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
    static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServerStarter");
    static string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServerStarter", "log");
    static string DllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dll");
    static List<int> runningInstances = new List<int>();
    static Dictionary<int, ProcessOutputHandler> instanceOutputHandlers = new Dictionary<int, ProcessOutputHandler>();
    static Dictionary<int, bool> instanceBackendInjected = new Dictionary<int, bool>();
    static Dictionary<int, bool> instanceSetupComplete = new Dictionary<int, bool>();
    static Dictionary<int, bool> instanceListeningDetected = new Dictionary<int, bool>();
    static Dictionary<int, DateTime> instanceSetupStartTime = new Dictionary<int, DateTime>();
    static Dictionary<int, string> instanceLogFiles = new Dictionary<int, string>();
    static Dictionary<int, (string email, string password)> instanceCredentials = new Dictionary<int, (string, string)>();
    static bool shouldMonitor = false;
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    static HttpClient httpClient = new HttpClient();
    static int logCounter = 1;
    static bool debugMode = false;
    static int maxServerInstances = 5;

    static async Task Main(string[] args)
    {
        debugMode = args.Length > 0 && args[0] == "-debug";

        if (debugMode)
            Console.WriteLine("[DEBUG] Starting application...");

        AppDomain.CurrentDomain.ProcessExit += async (sender, e) => await OnProgramExit();
        Console.CancelKeyPress += async (sender, e) =>
        {
            e.Cancel = true;
            await OnProgramExit();
            Environment.Exit(0);
        };

        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(LogPath);

        if (!CheckRequiredFiles())
        {
            Console.WriteLine("Required DLLs missing. Please add them and restart.");
            Console.ReadKey();
            return;
        }

        if (debugMode)
            Console.WriteLine("[DEBUG] All required files are present.");

        await StartMenu();
    }

    static string GetNextLogFileName()
    {
        while (true)
        {
            string logFile = Path.Combine(LogPath, $"log-{logCounter}.txt");
            if (!File.Exists(logFile))
            {
                logCounter++;
                return logFile;
            }
            logCounter++;
        }
    }

    static async Task OnProgramExit()
    {
        if (debugMode)
            Console.WriteLine("[DEBUG] Program closing, cleaning up...");

        if (runningInstances.Count > 0)
        {
            if (debugMode)
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

        if (debugMode)
            Console.WriteLine("[DEBUG] Cleanup complete.");
    }

    static bool CheckRequiredFiles()
    {
        string backendDllPath = Path.Combine(DllPath, "backend.dll");
        string memoryDllPath = Path.Combine(DllPath, "memory.dll");
        string serverDllPath = Path.Combine(DllPath, "server.dll");

        if (debugMode)
            Console.WriteLine("[DEBUG] Checking DLLs in " + DllPath);

        if (!Directory.Exists(DllPath)) return false;

        bool allFilesFound = true;

        if (!File.Exists(backendDllPath))
        {
            allFilesFound = false;
            if (debugMode)
                Console.WriteLine("[DEBUG] backend.dll missing");
        }
        if (!File.Exists(memoryDllPath))
        {
            allFilesFound = false;
            if (debugMode)
                Console.WriteLine("[DEBUG] memory.dll missing");
        }
        if (!File.Exists(serverDllPath))
        {
            allFilesFound = false;
            if (debugMode)
                Console.WriteLine("[DEBUG] server.dll missing");
        }

        return allFilesFound;
    }

    static async Task StartMenu()
    {
        Console.WriteLine("-----------------------------");
        Console.WriteLine("-> 1 - Settings");
        Console.WriteLine("-> 2 - Start ServerStarter Manager");
        Console.WriteLine("-----------------------------");
        Console.Write("What is your Choice: ");
        string option = Console.ReadLine();

        if (option == "1") await Settings();
        else if (option == "2") await StartFNServer();
        else await StartMenu();
    }

    static async Task<string> DetectApiUrlWithPort(string baseUrl)
    {
        string[] protocols = { "https://", "http://" };
        int[] ports = { 3551, 443 };

        foreach (string protocol in protocols)
        {
            foreach (int port in ports)
            {
                string testUrl = $"{protocol}{baseUrl}:{port}";
                try
                {
                    if (debugMode)
                        Console.WriteLine($"[DEBUG] Testing connection to {testUrl}");

                    var response = await httpClient.GetAsync($"{testUrl}/bettermomentum/matchmaker/serverInfo",
                        new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

                    if (response.IsSuccessStatusCode)
                    {
                        if (debugMode)
                            Console.WriteLine($"[DEBUG] Successfully connected to {testUrl}");
                        return testUrl;
                    }
                }
                catch (Exception ex)
                {
                    if (debugMode)
                        Console.WriteLine($"[DEBUG] Failed to connect to {testUrl}: {ex.Message}");
                }
            }
        }

        return null;
    }

    static async Task StartFNServer()
    {
        if (debugMode)
            Console.WriteLine("[DEBUG] Starting FN Server...");

        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        string configPath = Path.Combine(AppDataPath, "config.txt");

        if (!File.Exists(filePath))
        {
            Console.WriteLine("Settings not found! Please configure first.");
            await StartMenu();
            return;
        }

        if (File.Exists(configPath))
        {
            string maxServersLine = File.ReadAllText(configPath);
            if (int.TryParse(maxServersLine, out int maxServers))
            {
                maxServerInstances = maxServers;
                if (debugMode)
                    Console.WriteLine($"[DEBUG] Max server instances: {maxServerInstances}");
            }
        }

        string[] settings = File.ReadAllLines(filePath);

        if (debugMode)
        {
            Console.WriteLine("[DEBUG] Loaded settings:");
            for (int i = 0; i < settings.Length; i++)
                Console.WriteLine($"[DEBUG] Line {i}: {settings[i]}");
        }

        shouldMonitor = true;

        _ = MonitorScalingAsync(cancellationTokenSource.Token).ContinueWith(t =>
        {
            if (t.Exception != null && debugMode)
                Console.WriteLine("[DEBUG] Error in MonitorScalingAsync: " + t.Exception.Flatten().InnerException);
        });

        await InstanceManagementMenu();
    }

    static async Task MonitorScalingAsync(CancellationToken token)
    {
        if (debugMode)
            Console.WriteLine("[DEBUG] MonitorScalingAsync started.");

        string[] settings = File.ReadAllLines(Path.Combine(AppDataPath, "blackfn_inf.txt"));
        string apiUrl = settings[0];
        string secretToken = settings[1];

        while (shouldMonitor && !token.IsCancellationRequested)
        {
            try
            {
                bool anyInstanceInSetup = false;
                bool anyInstanceInWaitPeriod = false;

                foreach (var instance in instanceSetupStartTime)
                {
                    if (!instanceSetupComplete.ContainsKey(instance.Key) || !instanceSetupComplete[instance.Key])
                    {
                        anyInstanceInSetup = true;
                        break;
                    }
                    else if (instanceSetupComplete[instance.Key])
                    {
                        var timeSinceSetup = DateTime.Now - instanceSetupStartTime[instance.Key];
                        if (timeSinceSetup.TotalSeconds < 70)
                        {
                            anyInstanceInWaitPeriod = true;
                        }
                    }
                }

                if (!anyInstanceInSetup && !anyInstanceInWaitPeriod && runningInstances.Count < maxServerInstances)
                {
                    if (debugMode)
                        Console.WriteLine("[DEBUG] No instances in setup or wait period, checking scaling...");

                    var response = await httpClient.GetAsync($"{apiUrl}/bettermomentum/matchmaker/serverInfo");
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync();

                    if (debugMode)
                        Console.WriteLine("[DEBUG] Received server info: " + json);

                    using JsonDocument doc = JsonDocument.Parse(json);
                    bool scalingRequired = doc.RootElement.GetProperty("server_scaling_required").GetBoolean();

                    if (debugMode)
                        Console.WriteLine("[DEBUG] Scaling required? " + scalingRequired);

                    if (scalingRequired)
                    {
                        var account = await CreateServerAccount(apiUrl, secretToken);
                        if (account.HasValue)
                        {
                            Console.WriteLine($"Server account created: {account.Value.email}");

                            int pid = StartFortniteInstance(account.Value.email, account.Value.password);
                            if (pid != -1)
                            {
                                runningInstances.Add(pid);
                                instanceBackendInjected[pid] = false;
                                instanceSetupComplete[pid] = false;
                                instanceListeningDetected[pid] = false;
                                instanceSetupStartTime[pid] = DateTime.Now;
                                instanceLogFiles[pid] = GetNextLogFileName();
                                instanceCredentials[pid] = (account.Value.email, account.Value.password);

                                Console.WriteLine($"Server started with PID: {pid}");

                                if (debugMode)
                                    Console.WriteLine("[DEBUG] Log file: " + instanceLogFiles[pid]);
                            }
                            else
                            {
                                Console.WriteLine("Failed to start server instance.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Failed to create server account.");
                        }
                    }
                }
                else if (runningInstances.Count >= maxServerInstances)
                {
                    if (debugMode)
                        Console.WriteLine($"[DEBUG] Max instances ({maxServerInstances}) reached, skipping scaling check.");
                }
                else
                {
                    if (anyInstanceInSetup && debugMode)
                    {
                        Console.WriteLine("[DEBUG] Instances in setup phase, skipping scaling check.");
                    }
                    else if (anyInstanceInWaitPeriod && debugMode)
                    {
                        Console.WriteLine("[DEBUG] Instances in 70 second wait period, skipping scaling check.");
                    }
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
                            if (debugMode)
                                Console.WriteLine($"[DEBUG] Injecting Backend.dll into PID {pid} on startup...");

                            Injector.Inject(pid, Path.Combine(DllPath, "Backend.dll"));
                            instanceBackendInjected[pid] = true;

                            if (debugMode)
                                Console.WriteLine($"[DEBUG] Backend.dll successfully injected into PID {pid}");
                        }

                        if (instanceSetupComplete.ContainsKey(pid) && instanceSetupComplete[pid])
                        {
                            if (instanceSetupStartTime.ContainsKey(pid))
                            {
                                var timeSinceSetup = DateTime.Now - instanceSetupStartTime[pid];
                                if (timeSinceSetup.TotalSeconds >= 70)
                                {
                                    if (debugMode)
                                        Console.WriteLine($"[DEBUG] 70 seconds passed since setup completion for PID {pid}, marking as ready.");

                                    instanceSetupComplete[pid] = false;
                                    instanceSetupStartTime.Remove(pid);
                                }
                            }
                        }
                    }
                    catch
                    {
                        Console.WriteLine($"Server instance PID {pid} stopped.");

                        if (instanceListeningDetected.ContainsKey(pid) && !instanceListeningDetected[pid])
                        {
                            Console.WriteLine($"Server PID {pid} crashed before ready - restarting...");

                            if (instanceCredentials.ContainsKey(pid))
                            {
                                var credentials = instanceCredentials[pid];
                                CleanupInstance(pid);
                                runningInstances.RemoveAt(i);

                                int newPid = StartFortniteInstance(credentials.email, credentials.password);
                                if (newPid != -1)
                                {
                                    runningInstances.Add(newPid);
                                    instanceBackendInjected[newPid] = false;
                                    instanceSetupComplete[newPid] = false;
                                    instanceListeningDetected[newPid] = false;
                                    instanceSetupStartTime[newPid] = DateTime.Now;
                                    instanceLogFiles[newPid] = GetNextLogFileName();
                                    instanceCredentials[newPid] = credentials;
                                    Console.WriteLine($"Server restarted with PID: {newPid}");
                                }
                                else
                                {
                                    Console.WriteLine("Failed to restart server instance.");
                                }
                            }
                            else
                            {
                                if (debugMode)
                                    Console.WriteLine($"[DEBUG] No credentials found for PID {pid}, cannot restart.");

                                CleanupInstance(pid);
                                runningInstances.RemoveAt(i);
                            }
                        }
                        else
                        {
                            if (debugMode)
                                Console.WriteLine($"[DEBUG] Instance PID {pid} exited normally after 'Listening on port'.");

                            CleanupInstance(pid);
                            runningInstances.RemoveAt(i);
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                if (debugMode)
                    Console.WriteLine("[DEBUG] Exception in MonitorScalingAsync: " + ex);
            }

            await Task.Delay(35000, token);
        }

        if (debugMode)
            Console.WriteLine("[DEBUG] MonitorScalingAsync stopped.");
    }

    static void CleanupInstance(int pid)
    {
        if (instanceOutputHandlers.TryGetValue(pid, out ProcessOutputHandler handler))
        {
            handler.Stop();
            instanceOutputHandlers.Remove(pid);
        }
        instanceBackendInjected.Remove(pid);
        instanceSetupComplete.Remove(pid);
        instanceListeningDetected.Remove(pid);
        instanceSetupStartTime.Remove(pid);
        instanceLogFiles.Remove(pid);
        instanceCredentials.Remove(pid);
    }

    static async Task<(string username, string email, string password)?> CreateServerAccount(string apiUrl, string serverKey)
    {
        try
        {
            if (debugMode)
            {
                Console.WriteLine("[DEBUG] Creating server account...");
                Console.WriteLine("[DEBUG] Using serverKey: " + serverKey);
            }

            var payload = new { serverKey };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{apiUrl}/bettermomentum/serveraccount/create", content);

            if (debugMode)
                Console.WriteLine("[DEBUG] Server account creation response: " + response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                if (debugMode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("[DEBUG] Error response: " + errorContent);
                }
                Console.WriteLine("Failed to create server account");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            string username = doc.RootElement.GetProperty("username").GetString();
            string email = doc.RootElement.GetProperty("email").GetString();
            string password = doc.RootElement.GetProperty("password").GetString();

            if (debugMode)
                Console.WriteLine("[DEBUG] Server account details: " + username + ", " + email);

            return (username, email, password);
        }
        catch (Exception ex)
        {
            if (debugMode)
                Console.WriteLine("[DEBUG] Exception in CreateServerAccount: " + ex);
            return null;
        }
    }

    static int StartFortniteInstance(string email, string password)
    {
        if (debugMode)
            Console.WriteLine("[DEBUG] Starting Fortnite instance for " + email);

        string[] settings = File.ReadAllLines(Path.Combine(AppDataPath, "blackfn_inf.txt"));
        string fortnitePath = settings[2];

        string exePath = Path.Combine(fortnitePath, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");

        if (debugMode)
            Console.WriteLine("[DEBUG] Fortnite EXE Path: " + exePath);

        if (!File.Exists(exePath))
        {
            if (debugMode)
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
                Console.WriteLine("Starting Fortnite Server (this could take a while)...");

                string logFile = GetNextLogFileName();
                var outputHandler = new ProcessOutputHandler(process, logFile, debugMode);
                outputHandler.LoginDetected += (sender, e) => OnLoginDetected(process.Id);
                outputHandler.ListeningDetected += (sender, e) => OnListeningDetected(process.Id);
                instanceOutputHandlers[process.Id] = outputHandler;
                instanceLogFiles[process.Id] = logFile;
                outputHandler.Start();

                FakeAC.Start(fortnitePath, "FortniteClient-Win64-Shipping_EAC.exe");
                FakeAC.Start(fortnitePath, "FortniteLauncher.exe");

                if (debugMode)
                {
                    Console.WriteLine("[DEBUG] Server started! PID: " + process.Id);
                    Console.WriteLine("[DEBUG] Log file: " + logFile);
                    Console.WriteLine("[DEBUG] Backend.dll will be injected on startup, memory.dll and server.dll after login detection...");
                }

                return process.Id;
            }

            return -1;
        }
        catch (Exception ex)
        {
            if (debugMode)
                Console.WriteLine("[DEBUG] Exception in StartFortniteInstance: " + ex);
            return -1;
        }
    }

    static async void OnListeningDetected(int processId)
    {
        Console.WriteLine($"Server PID {processId} is now listening - starting 70 second wait period...");

        if (instanceSetupStartTime.ContainsKey(processId))
        {
            instanceSetupStartTime[processId] = DateTime.Now;
            instanceSetupComplete[processId] = true;
            instanceListeningDetected[processId] = true;

            if (debugMode)
                Console.WriteLine($"[DEBUG] PID {processId} entered 70 second wait period. Scaling checks will resume after.");
        }
    }

    static async void OnLoginDetected(int processId)
    {
        Console.WriteLine($"Login detected for PID {processId} - injecting DLLs...");

        try
        {
            if (debugMode)
                Console.WriteLine($"[DEBUG] Injecting server.dll into PID {processId}...");

            Injector.Inject(processId, Path.Combine(DllPath, "server.dll"));

            if (debugMode)
                Console.WriteLine($"[DEBUG] server.dll successfully injected into PID {processId}");

            if (instanceSetupComplete.ContainsKey(processId))
            {
                instanceSetupComplete[processId] = true;

                if (debugMode)
                    Console.WriteLine($"[DEBUG] Setup completed for PID {processId}. Waiting 70 seconds before next scaling check...");
            }

            Console.WriteLine($"All DLLs successfully injected into PID {processId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error injecting DLLs into PID {processId}");

            if (debugMode)
                Console.WriteLine($"[DEBUG] Error details: {ex}");
        }
    }

    static async Task InstanceManagementMenu()
    {
        while (shouldMonitor)
        {
            Console.Clear();
            Console.WriteLine("============================");
            Console.WriteLine("SERVER STARTER V3.2");
            Console.WriteLine($"Running instances: {runningInstances.Count}/{maxServerInstances}");

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
        if (debugMode)
            Console.WriteLine("[DEBUG] Showing instance status:");

        for (int i = 0; i < runningInstances.Count; i++)
        {
            int pid = runningInstances[i];
            try
            {
                var process = Process.GetProcessById(pid);
                string backendStatus = instanceBackendInjected.ContainsKey(pid) && instanceBackendInjected[pid] ? "Backend ✓" : "Backend ✗";
                string setupStatus = instanceSetupComplete.ContainsKey(pid) && instanceSetupComplete[pid] ? "Setup ✓" : "Setup ✗";
                string listeningStatus = instanceListeningDetected.ContainsKey(pid) && instanceListeningDetected[pid] ? "Listening ✓" : "Listening ✗";
                string logFile = instanceLogFiles.ContainsKey(pid) ? Path.GetFileName(instanceLogFiles[pid]) : "N/A";

                if (instanceSetupStartTime.ContainsKey(pid) && instanceSetupComplete.ContainsKey(pid) && instanceSetupComplete[pid])
                {
                    var timeSinceSetup = DateTime.Now - instanceSetupStartTime[pid];
                    var timeRemaining = 70 - (int)timeSinceSetup.TotalSeconds;
                    setupStatus += $" ({timeRemaining}s remaining)";
                }

                Console.WriteLine($"PID {pid}: {backendStatus} | {setupStatus} | {listeningStatus} | Log: {logFile}");
            }
            catch
            {
                Console.WriteLine($"PID {pid}: Not found");
            }
        }
    }

    static async Task StopAllInstances()
    {
        Console.WriteLine("Stopping all instances...");
        shouldMonitor = false;
        cancellationTokenSource.Cancel();

        foreach (int pid in runningInstances)
        {
            try
            {
                Process.GetProcessById(pid).Kill();
                Console.WriteLine($"Stopped server PID {pid}");
                CleanupInstance(pid);
            }
            catch (Exception ex)
            {
                if (debugMode)
                    Console.WriteLine("[DEBUG] Exception while stopping PID " + pid + ": " + ex);
            }
        }

        runningInstances.Clear();
        Console.WriteLine("All instances stopped.");
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

        Console.WriteLine("Detecting API URL and port...");
        string detectedApiUrl = await DetectApiUrlWithPort(apiHost);

        if (detectedApiUrl == null)
        {
            Console.WriteLine("Could not connect to API server on any port (3551, 443) with http or https!");
            Console.WriteLine("Please check if the server is running and accessible.");
            Console.WriteLine("Press any key to try again...");
            Console.ReadKey();
            await Settings();
            return;
        }

        string apiUrl = detectedApiUrl;
        Console.WriteLine($"Successfully connected to API at: {apiUrl}");

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

        int maxServers;
        while (true)
        {
            Console.Write("Enter maximum server instances (1-75): ");
            if (int.TryParse(Console.ReadLine(), out maxServers) && maxServers >= 1 && maxServers <= 75)
                break;
            Console.WriteLine("Please enter a number between 1 and 75!");
        }

        File.WriteAllLines(filePath, new[] { apiUrl, secretToken, fortnitePath });
        File.WriteAllText(configPath, maxServers.ToString());

        Console.WriteLine("Settings saved successfully!");

        if (debugMode)
        {
            Console.WriteLine("[DEBUG] Settings saved to " + filePath);
            Console.WriteLine($"[DEBUG] Saved - API URL: {apiUrl}");
            Console.WriteLine($"[DEBUG] Saved - Secret Token: {secretToken}");
            Console.WriteLine($"[DEBUG] Saved - Fortnite Path: {fortnitePath}");
            Console.WriteLine($"[DEBUG] Saved - Max Instances: {maxServers}");
        }

        await StartMenu();
    }
}

public class ProcessOutputHandler
{
    private Process _process;
    private Thread _outputThread;
    private Thread _errorThread;
    private bool _isRunning;
    private string _logFilePath;
    private StreamWriter _logWriter;
    private object _logLock = new object();
    private bool _debugMode;

    public event EventHandler LoginDetected;
    public event EventHandler ListeningDetected;

    public ProcessOutputHandler(Process process, string logFilePath, bool debugMode = false)
    {
        _process = process;
        _logFilePath = logFilePath;
        _debugMode = debugMode;
        _logWriter = new StreamWriter(_logFilePath, true) { AutoFlush = true };
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

        lock (_logLock)
        {
            if (_logWriter != null)
            {
                _logWriter.Close();
                _logWriter.Dispose();
                _logWriter = null;
            }
        }
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
            if (_debugMode)
                Console.WriteLine($"[{timestamp}] [GAME] Login detected");
            LoginDetected?.Invoke(this, EventArgs.Empty);
        }
        else if (line.Contains("Listening on port"))
        {
            if (_debugMode)
                Console.WriteLine($"[{timestamp}] [GAME] Listening on port detected");
            ListeningDetected?.Invoke(this, EventArgs.Empty);
        }

        lock (_logLock)
        {
            if (_logWriter != null)
            {
                _logWriter.WriteLine($"[{timestamp}] {line}");
            }
        }
    }
}