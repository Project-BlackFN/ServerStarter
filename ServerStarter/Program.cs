using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Utilities;
using ServerStarter.Utilities;

class BlackFN
{
    static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackFN_Server");
    static string DllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dll");
    static List<int> runningInstances = new List<int>();
    static Dictionary<int, string> instanceAccounts = new Dictionary<int, string>();
    static Dictionary<int, ProcessOutputHandler> instanceOutputHandlers = new Dictionary<int, ProcessOutputHandler>();
    static Dictionary<int, bool> instanceBackendInjected = new Dictionary<int, bool>();
    static Dictionary<int, bool> instanceSetupComplete = new Dictionary<int, bool>();
    static Dictionary<int, DateTime> instanceSetupStartTime = new Dictionary<int, DateTime>();
    static bool shouldMonitor = false;
    static bool s4LoginEnabled = false;
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    static HttpClient httpClient = new HttpClient();

    static async Task Main()
    {
        Console.WriteLine("[DEBUG] Starting application...");

        AppDomain.CurrentDomain.ProcessExit += async (sender, e) => await OnProgramExit();
        Console.CancelKeyPress += async (sender, e) =>
        {
            e.Cancel = true;
            await OnProgramExit();
            Environment.Exit(0);
        };

        Directory.CreateDirectory(AppDataPath);
        if (!CheckRequiredFiles())
        {
            Console.WriteLine("[DEBUG] Required DLLs missing.");
            Console.WriteLine("A few DLLs were not found, please add them..");
            Console.ReadKey();
            return;
        }

        Console.WriteLine("[DEBUG] All required files are present.");
        await StartMenu();
    }

    static async Task OnProgramExit()
    {
        Console.WriteLine("[DEBUG] Program closing, cleaning up...");

        if (s4LoginEnabled)
        {
            Console.WriteLine("[DEBUG] Stopping S4Login proxy server...");
            var result = await S4Login.StopServerAsync();
            if (result.ContainsKey("success") && (bool)result["success"])
            {
                Console.WriteLine("[DEBUG] S4Login proxy server stopped successfully.");
            }
            else
            {
                Console.WriteLine("[DEBUG] Failed to stop S4Login proxy server: " + (result.ContainsKey("error") ? result["error"] : "Unknown error"));
            }
        }

        if (runningInstances.Count > 0)
        {
            Console.WriteLine("[DEBUG] Stopping all running instances...");
            foreach (int pid in runningInstances)
            {
                try
                {
                    Process.GetProcessById(pid).Kill();
                    CleanupInstance(pid);
                }
                catch { }
            }
            runningInstances.Clear();
        }

        Console.WriteLine("[DEBUG] Cleanup complete.");
    }

    static bool CheckRequiredFiles()
    {
        string backendDllPath = Path.Combine(DllPath, "Backend.dll");
        string memoryDllPath = Path.Combine(DllPath, "memory.dll");
        string serverDllPath = Path.Combine(DllPath, "server.dll");

        Console.WriteLine("[DEBUG] Checking DLLs in " + DllPath);

        if (!Directory.Exists(DllPath)) return false;

        bool allFilesFound = true;

        if (!File.Exists(backendDllPath)) { allFilesFound = false; Console.WriteLine("[DEBUG] Backend.dll missing"); }
        if (!File.Exists(memoryDllPath)) { allFilesFound = false; Console.WriteLine("[DEBUG] memory.dll missing"); }
        if (!File.Exists(serverDllPath)) { allFilesFound = false; Console.WriteLine("[DEBUG] server.dll missing"); }

        return allFilesFound;
    }

    static async Task StartMenu()
    {
        Console.WriteLine("-----------------------------");
        Console.WriteLine("-> 1 - Settings");
        Console.WriteLine("-> 2 - Start Fortnite Server");
        Console.WriteLine("-----------------------------");
        Console.Write("What is your Choice: ");
        string option = Console.ReadLine();

        if (option == "1") await Settings();
        else if (option == "2") await StartFNServer();
        else await StartMenu();
    }

    static async Task StartFNServer()
    {
        Console.WriteLine("[DEBUG] Starting FN Server...");
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        if (!File.Exists(filePath))
        {
            Console.WriteLine("[DEBUG] Settings file not found.");
            Console.WriteLine("Settings not found! Please configure first.");
            await StartMenu();
            return;
        }

        string[] settings = File.ReadAllLines(filePath);
        Console.WriteLine("[DEBUG] Loaded settings:");
        for (int i = 0; i < settings.Length; i++)
            Console.WriteLine($"[DEBUG] Line {i}: {settings[i]}");

        string fortnitePath = settings[2];

        if (settings.Length > 3 && settings[3].Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            s4LoginEnabled = true;
            Console.WriteLine("[DEBUG] S4Login is enabled, verifying required files...");

            string paksPath = Path.Combine(fortnitePath, "FortniteGame", "Content", "Paks");
            string loginFixPak = Path.Combine(paksPath, "pakchunkLoginFix-WindowsClient.pak");
            string loginFixSig = Path.Combine(paksPath, "pakchunkLoginFix-WindowsClient.sig");

            bool filesOk = true;

            if (!File.Exists(loginFixPak))
            {
                Console.WriteLine("[ERROR] S4Login is enabled but pakchunkLoginFix-WindowsClient.pak not found!");
                Console.WriteLine($"[ERROR] Expected location: {loginFixPak}");
                filesOk = false;
            }

            if (!File.Exists(loginFixSig))
            {
                Console.WriteLine("[ERROR] S4Login is enabled but pakchunkLoginFix-WindowsClient.sig not found!");
                Console.WriteLine($"[ERROR] Expected location: {loginFixSig}");
                filesOk = false;
            }

            if (!filesOk)
            {
                Console.WriteLine("Please add the LoginFix pak and sig files to continue.");
                Console.WriteLine("Press any key to return to menu...");
                Console.ReadKey();
                await StartMenu();
                return;
            }

            Console.WriteLine("[DEBUG] LoginFix pak and sig files found.");
            Console.WriteLine("[DEBUG] Starting S4Login proxy server...");

            var result = await S4Login.StartServerAsync();
            if (result.ContainsKey("success") && (bool)result["success"])
            {
                Console.WriteLine("[DEBUG] S4Login proxy server started successfully on 127.0.0.1:8010");
            }
            else
            {
                Console.WriteLine("[DEBUG] Failed to start S4Login proxy server: " + (result.ContainsKey("error") ? result["error"] : "Unknown error"));
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
                await StartMenu();
                return;
            }
        }
        else
        {
            s4LoginEnabled = false;
            Console.WriteLine("[DEBUG] S4Login is disabled.");
        }

        shouldMonitor = true;

        _ = MonitorScalingAsync(cancellationTokenSource.Token).ContinueWith(t =>
        {
            if (t.Exception != null)
                Console.WriteLine("[DEBUG] Error in MonitorScalingAsync: " + t.Exception.Flatten().InnerException);
        });

        await InstanceManagementMenu();
    }

    static async Task MonitorScalingAsync(CancellationToken token)
    {
        Console.WriteLine("[DEBUG] MonitorScalingAsync started.");
        string[] settings = File.ReadAllLines(Path.Combine(AppDataPath, "blackfn_inf.txt"));
        string apiUrl = settings[0];
        string secretToken = settings[1];

        while (shouldMonitor && !token.IsCancellationRequested)
        {
            try
            {
                bool anyInstanceInSetup = false;
                foreach (var instance in instanceSetupStartTime)
                {
                    if (!instanceSetupComplete.ContainsKey(instance.Key) || !instanceSetupComplete[instance.Key])
                    {
                        anyInstanceInSetup = true;
                        break;
                    }
                }

                if (!anyInstanceInSetup)
                {
                    Console.WriteLine("[DEBUG] No instances in setup, checking scaling...");
                    var response = await httpClient.GetAsync($"{apiUrl}/bettermomentum/matchmaker/serverInfo");

                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("[DEBUG] Received server info: " + json);

                    using JsonDocument doc = JsonDocument.Parse(json);
                    bool scalingRequired = doc.RootElement.GetProperty("server_scaling_required").GetBoolean();
                    Console.WriteLine("[DEBUG] Scaling required? " + scalingRequired);

                    if (scalingRequired)
                    {
                        var account = await CreateServerAccount(apiUrl, secretToken);
                        if (account.HasValue)
                        {
                            Console.WriteLine("[DEBUG] Server account created: " + account.Value.email);
                            int pid = StartFortniteInstance(account.Value.email, account.Value.password);
                            if (pid != -1)
                            {
                                runningInstances.Add(pid);
                                instanceAccounts[pid] = account.Value.deleteToken;
                                instanceBackendInjected[pid] = false;
                                instanceSetupComplete[pid] = false;
                                instanceSetupStartTime[pid] = DateTime.Now;
                                Console.WriteLine("[DEBUG] Fortnite instance started, PID: " + pid);
                            }
                            else
                            {
                                Console.WriteLine("[DEBUG] Failed to start Fortnite instance.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("[DEBUG] Failed to create server account.");
                        }
                    }
                }
                else
                {
                    Console.WriteLine("[DEBUG] Instances in setup phase, skipping scaling check.");
                }

                for (int i = runningInstances.Count - 1; i >= 0; i--)
                {
                    int pid = runningInstances[i];
                    try
                    {
                        var process = Process.GetProcessById(pid);
                        if (process.HasExited) throw new Exception("Process stopped");

                        if (!instanceBackendInjected[pid])
                        {
                            Console.WriteLine($"[DEBUG] Injecting Backend.dll into PID {pid} on startup...");
                            Injector.Inject(pid, Path.Combine(DllPath, "Backend.dll"));
                            instanceBackendInjected[pid] = true;
                            Console.WriteLine($"[DEBUG] Backend.dll successfully injected into PID {pid}");
                        }

                        if (instanceSetupComplete.ContainsKey(pid) && instanceSetupComplete[pid])
                        {
                            if (instanceSetupStartTime.ContainsKey(pid))
                            {
                                var timeSinceSetup = DateTime.Now - instanceSetupStartTime[pid];
                                if (timeSinceSetup.TotalSeconds >= 70)
                                {
                                    Console.WriteLine($"[DEBUG] 70 seconds passed since setup completion for PID {pid}, marking as ready.");
                                    instanceSetupComplete[pid] = false;
                                    instanceSetupStartTime.Remove(pid);
                                }
                            }
                        }
                    }
                    catch
                    {
                        Console.WriteLine("[DEBUG] Instance PID " + pid + " exited.");
                        CleanupInstance(pid);
                        runningInstances.RemoveAt(i);
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("[DEBUG] Exception in MonitorScalingAsync: " + ex);
            }

            await Task.Delay(35000, token);
        }

        Console.WriteLine("[DEBUG] MonitorScalingAsync stopped.");
    }

    static void CleanupInstance(int pid)
    {
        if (instanceAccounts.TryGetValue(pid, out string deleteToken))
        {
            string[] settings = File.ReadAllLines(Path.Combine(AppDataPath, "blackfn_inf.txt"));
            string apiUrl = settings[0];
            _ = DeleteServerAccount(apiUrl, deleteToken);
            instanceAccounts.Remove(pid);
            Console.WriteLine("[DEBUG] Deleted server account for PID " + pid);
        }
        if (instanceOutputHandlers.TryGetValue(pid, out ProcessOutputHandler handler))
        {
            handler.Stop();
            instanceOutputHandlers.Remove(pid);
        }
        instanceBackendInjected.Remove(pid);
        instanceSetupComplete.Remove(pid);
        instanceSetupStartTime.Remove(pid);
    }

    static async Task<(string username, string email, string password, string deleteToken)?> CreateServerAccount(string apiUrl, string serverKey)
    {
        try
        {
            Console.WriteLine("[DEBUG] Creating server account...");
            Console.WriteLine("[DEBUG] Using serverKey: " + serverKey);
            var payload = new { serverKey };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{apiUrl}/bettermomentum/serveraccount/create", content);
            Console.WriteLine("[DEBUG] Server account creation response: " + response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine("[DEBUG] Error response: " + errorContent);
                Console.WriteLine("[DEBUG] Failed to create server account");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            string username = doc.RootElement.GetProperty("username").GetString();
            string email = doc.RootElement.GetProperty("email").GetString();
            string password = doc.RootElement.GetProperty("password").GetString();
            string deleteToken = doc.RootElement.GetProperty("deleteToken").GetString();

            Console.WriteLine("[DEBUG] Server account details: " + username + ", " + email);
            return (username, email, password, deleteToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DEBUG] Exception in CreateServerAccount: " + ex);
            return null;
        }
    }

    static async Task DeleteServerAccount(string apiUrl, string deleteToken)
    {
        try
        {
            Console.WriteLine("[DEBUG] Deleting server account with token: " + deleteToken);
            var payload = new { deleteToken };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{apiUrl}/bettermomentum/serveraccount/delete", content);
            Console.WriteLine("[DEBUG] Delete server account response: " + response.StatusCode);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DEBUG] Exception in DeleteServerAccount: " + ex);
        }
    }

    static int StartFortniteInstance(string email, string password)
    {
        Console.WriteLine("[DEBUG] Starting Fortnite instance for " + email);
        string[] settings = File.ReadAllLines(Path.Combine(AppDataPath, "blackfn_inf.txt"));
        string fortnitePath = settings[2];

        string exePath = Path.Combine(fortnitePath, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");

        Console.WriteLine("[DEBUG] Fortnite EXE Path: " + exePath);

        if (!File.Exists(exePath))
        {
            Console.WriteLine("[DEBUG] EXE does not exist!");
            return -1;
        }

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exePath)
            {
                Arguments = $"-epicapp=Fortnite -epicenv=Prod -epiclocale=en-us -epicportal -skippatchcheck -nobe -fromfl=eac -fltoken=3db3ba5dcbd2e16703f3978d -caldera=eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2NvdW50X2lkIjoiYmU5ZGE1YzJmYmVhNDQwN2IyZjQwZWJhYWQ4NTlhZDQiLCJnZW5lcmF0ZWQiOjE2Mzg3MTcyNzgsImNhbGRlcmFHdWlkIjoiMzgxMGI4NjMtMmE2NS00NDU3LTliNTgtNGRhYjNiNDgyYTg2IiwiYWNQcm92aWRlciI6IkVhc3lBbnRpQ2hlYXQiLCJub3RlcyI6IiIsImZhbGxiYWNrIjpmYWxzZX0.VAWQB67RTxhiWOxx7DBjnzDnXyyEnX7OljJm-j2d88G_WgwQ9wrE6lwMEHZHjBd1ISJdUO1UVUqkfLdU5nofBQ -AUTH_LOGIN={email} -AUTH_PASSWORD={password} -AUTH_TYPE=epic -nosplash -nosound -nullrhi",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);

            if (process != null)
            {
                Console.WriteLine("[DEBUG] Starting Fortnite Server (this could take a while)...");

                var outputHandler = new ProcessOutputHandler(process);
                outputHandler.LoginDetected += (sender, e) => OnLoginDetected(process.Id);
                instanceOutputHandlers[process.Id] = outputHandler;
                outputHandler.Start();

                FakeAC.Start(fortnitePath, "FortniteClient-Win64-Shipping_EAC.exe");
                FakeAC.Start(fortnitePath, "FortniteLauncher.exe");

                Console.WriteLine("[DEBUG] Server started! PID: " + process.Id);
                Console.WriteLine("[DEBUG] Backend.dll will be injected on startup, memory.dll and server.dll after login detection...");

                return process.Id;
            }

            return -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DEBUG] Exception in StartFortniteInstance: " + ex);
            return -1;
        }
    }

    static async void OnLoginDetected(int processId)
    {
        Console.WriteLine($"[DEBUG] Login detected for PID {processId}! Injecting memory.dll and server.dll...");

        try
        {
            Console.WriteLine($"[DEBUG] Injecting server.dll into PID {processId}...");
            Injector.Inject(processId, Path.Combine(DllPath, "server.dll"));
            Console.WriteLine($"[DEBUG] server.dll successfully injected into PID {processId}");

            if (instanceSetupComplete.ContainsKey(processId))
            {
                instanceSetupComplete[processId] = true;
                Console.WriteLine($"[DEBUG] Setup completed for PID {processId}. Waiting 70 seconds before next scaling check...");
            }

            Console.WriteLine($"[DEBUG] All DLLs successfully injected into PID {processId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error injecting DLLs into PID {processId}: {ex}");
        }
    }

    static async Task InstanceManagementMenu()
    {
        while (shouldMonitor)
        {
            Console.Clear();
            Console.WriteLine("============================");
            Console.WriteLine("SERVER STARTER V3");
            Console.WriteLine($"Running instances: {runningInstances.Count}");
            Console.WriteLine($"S4Login Proxy: {(s4LoginEnabled ? "Enabled" : "Disabled")}");

            int instancesInSetup = 0;
            foreach (var instance in instanceSetupComplete)
            {
                if (instance.Value) instancesInSetup++;
            }
            Console.WriteLine($"Instances in setup: {instancesInSetup}");

            Console.WriteLine("============================");
            Console.WriteLine("-> 1 - Stop all Instances");
            Console.WriteLine("-> 2 - Show Instance Status");
            Console.WriteLine("============================");
            Console.Write("Your choice: ");

            string choice = Console.ReadLine();

            if (choice == "1") { await StopAllInstances(); break; }
            else if (choice == "2") { ShowInstanceStatus(); Console.ReadKey(); }

            Thread.Sleep(1000);
        }

        await StartMenu();
    }

    static void ShowInstanceStatus()
    {
        Console.WriteLine("[DEBUG] Showing instance status:");
        for (int i = 0; i < runningInstances.Count; i++)
        {
            int pid = runningInstances[i];
            try
            {
                var process = Process.GetProcessById(pid);
                string backendStatus = instanceBackendInjected.ContainsKey(pid) && instanceBackendInjected[pid] ? "Backend.dll ✓" : "Backend.dll ✗";
                string setupStatus = instanceSetupComplete.ContainsKey(pid) && instanceSetupComplete[pid] ? "Setup ✓" : "Setup ✗";

                if (instanceSetupStartTime.ContainsKey(pid) && instanceSetupComplete.ContainsKey(pid) && instanceSetupComplete[pid])
                {
                    var timeSinceSetup = DateTime.Now - instanceSetupStartTime[pid];
                    var timeRemaining = 70 - (int)timeSinceSetup.TotalSeconds;
                    setupStatus += $" ({timeRemaining}s remaining)";
                }

                Console.WriteLine($"[DEBUG] PID {pid} is running. {backendStatus} | {setupStatus}");
            }
            catch
            {
                Console.WriteLine($"[DEBUG] PID {pid} not found.");
            }
        }
    }

    static async Task StopAllInstances()
    {
        Console.WriteLine("[DEBUG] Stopping all instances...");
        shouldMonitor = false;
        cancellationTokenSource.Cancel();

        foreach (int pid in runningInstances)
        {
            try
            {
                Process.GetProcessById(pid).Kill();
                Console.WriteLine("[DEBUG] Killed PID " + pid);
                CleanupInstance(pid);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DEBUG] Exception while stopping PID " + pid + ": " + ex);
            }
        }

        runningInstances.Clear();
        Console.WriteLine("[DEBUG] All instances stopped.");

        if (s4LoginEnabled)
        {
            Console.WriteLine("[DEBUG] Stopping S4Login proxy server...");
            var result = await S4Login.StopServerAsync();
            if (result.ContainsKey("success") && (bool)result["success"])
            {
                Console.WriteLine("[DEBUG] S4Login proxy server stopped successfully.");
            }
            else
            {
                Console.WriteLine("[DEBUG] Failed to stop S4Login proxy server: " + (result.ContainsKey("error") ? result["error"] : "Unknown error"));
            }
            s4LoginEnabled = false;
        }
    }

    static async Task Settings()
    {
        Directory.CreateDirectory(AppDataPath);
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        string configPath = Path.Combine(AppDataPath, "config.txt");

        if (File.Exists(filePath)) File.Delete(filePath);

        string apiHost;
        while (true)
        {
            Console.Write("Enter API Host (e.g., api.backend-mypro.dev): ");
            apiHost = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(apiHost)) break;
        }

        string apiUrl = $"https://{apiHost}";

        string secretToken;
        while (true)
        {
            Console.Write("Enter Secret Token (from .env): ");
            secretToken = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(secretToken)) break;
        }

        string fortnitePath;
        while (true)
        {
            Console.Write("Enter Fortnite Path: ");
            fortnitePath = Console.ReadLine();
            string exe = Path.Combine(fortnitePath, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");
            if (File.Exists(exe)) break;
            Console.WriteLine("Invalid path!");
        }

        string useS4Login;
        bool s4Enabled = false;
        while (true)
        {
            Console.Write("Use S4Login Proxy? (yes/no): ");
            useS4Login = Console.ReadLine()?.ToLower();
            if (useS4Login == "yes" || useS4Login == "y")
            {
                s4Enabled = true;

                string paksPath = Path.Combine(fortnitePath, "FortniteGame", "Content", "Paks");
                string loginFixPak = Path.Combine(paksPath, "pakchunkLoginFix-WindowsClient.pak");
                string loginFixSig = Path.Combine(paksPath, "pakchunkLoginFix-WindowsClient.sig");

                if (!File.Exists(loginFixPak) || !File.Exists(loginFixSig))
                {
                    Console.WriteLine("[WARNING] S4Login enabled but LoginFix files not found!");
                    Console.WriteLine($"[WARNING] pak location: {loginFixPak}");
                    Console.WriteLine($"[WARNING] sig location: {loginFixSig}");
                    Console.Write("Continue anyway? (yes/no): ");
                    string continueAnyway = Console.ReadLine()?.ToLower();
                    if (continueAnyway != "yes" && continueAnyway != "y")
                    {
                        Console.WriteLine("Please add the LoginFix files and try again.");
                        continue;
                    }
                }
                else
                {
                    Console.WriteLine("[SUCCESS] LoginFix pak and sig files found!");
                }
                break;
            }
            else if (useS4Login == "no" || useS4Login == "n")
            {
                s4Enabled = false;
                break;
            }
            Console.WriteLine("Please enter 'yes' or 'no'");
        }

        File.WriteAllLines(filePath, new[] { apiUrl, secretToken, fortnitePath, s4Enabled.ToString() });

        File.WriteAllText(configPath, $"UseS4Login={s4Enabled}");

        Console.WriteLine("[DEBUG] Settings saved to " + filePath);
        Console.WriteLine("[DEBUG] Config saved to " + configPath);
        Console.WriteLine($"[DEBUG] Saved - API URL: {apiUrl}");
        Console.WriteLine($"[DEBUG] Saved - Secret Token: {secretToken}");
        Console.WriteLine($"[DEBUG] Saved - Fortnite Path: {fortnitePath}");
        Console.WriteLine($"[DEBUG] Saved - S4Login Enabled: {s4Enabled}");

        await StartMenu();
    }
}

public class ProcessOutputHandler
{
    private Process _process;
    private Thread _outputThread;
    private Thread _errorThread;
    private bool _isRunning;

    public event EventHandler LoginDetected;

    public ProcessOutputHandler(Process process)
    {
        _process = process;
    }

    public void Start()
    {
        _isRunning = true;

        _outputThread = new Thread(ReadOutput);
        _outputThread.IsBackground = true;
        _outputThread.Start();

        _errorThread = new Thread(ReadError);
        _errorThread.IsBackground = true;
        _errorThread.Start();
    }

    public void Stop()
    {
        _isRunning = false;
    }

    private void ReadOutput()
    {
        while (_isRunning && !_process.HasExited)
        {
            try
            {
                string line = _process.StandardOutput.ReadLine();
                if (line != null)
                {
                    HandleOutputLine(line);
                }
            }
            catch
            {
                break;
            }
        }
    }

    private void ReadError()
    {
        while (_isRunning && !_process.HasExited)
        {
            try
            {
                string line = _process.StandardError.ReadLine();
                if (line != null)
                {
                    HandleOutputLine(line);
                }
            }
            catch
            {
                break;
            }
        }
    }

    private void HandleOutputLine(string line)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (line.Contains("Successfully logged in user"))
        {
            Console.WriteLine($"[{timestamp}] [GAME] Login detected");
            LoginDetected?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Console.WriteLine($"[GAME OUTPUT] {line}");
        }
    }
}