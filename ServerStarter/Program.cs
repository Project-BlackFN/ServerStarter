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

class BlackFN
{
    static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackFN_Server");
    static string DllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dll");
    static List<int> runningInstances = new List<int>();
    static Dictionary<int, string> instanceAccounts = new Dictionary<int, string>();
    static bool shouldMonitor = false;
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    static HttpClient httpClient = new HttpClient();

    static void Main()
    {
        Console.WriteLine("[DEBUG] Starting application...");
        Directory.CreateDirectory(AppDataPath);
        if (!CheckRequiredFiles())
        {
            Console.WriteLine("[DEBUG] Required DLLs missing.");
            Console.WriteLine("A few DLLs were not found, please add them..");
            Console.ReadKey();
            return;
        }

        Console.WriteLine("[DEBUG] All required files are present.");
        StartMenu();
    }

    static bool CheckRequiredFiles()
    {
        string backendDllPath = Path.Combine(DllPath, "Backend.dll");
        string serverDllPath = Path.Combine(DllPath, "server.dll");

        Console.WriteLine("[DEBUG] Checking DLLs in " + DllPath);

        if (!Directory.Exists(DllPath)) return false;

        bool allFilesFound = true;

        if (!File.Exists(backendDllPath)) { allFilesFound = false; Console.WriteLine("[DEBUG] Backend.dll missing"); }
        if (!File.Exists(serverDllPath)) { allFilesFound = false; Console.WriteLine("[DEBUG] server.dll missing"); }

        return allFilesFound;
    }

    static void StartMenu()
    {
        Console.WriteLine("-----------------------------");
        Console.WriteLine("-> 1 - Settings");
        Console.WriteLine("-> 2 - Start Fortnite Server");
        Console.WriteLine("-----------------------------");
        Console.Write("What is your Choice: ");
        string option = Console.ReadLine();

        if (option == "1") Settings();
        else if (option == "2") StartFNServer();
        else StartMenu();
    }

    static void StartFNServer()
    {
        Console.WriteLine("[DEBUG] Starting FN Server...");
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        if (!File.Exists(filePath))
        {
            Console.WriteLine("[DEBUG] Settings file not found.");
            Console.WriteLine("Settings not found! Please configure first.");
            StartMenu();
            return;
        }

        string[] settings = File.ReadAllLines(filePath);
        Console.WriteLine("[DEBUG] Loaded settings:");
        for (int i = 0; i < settings.Length; i++)
            Console.WriteLine($"[DEBUG] Line {i}: {settings[i]}");

        shouldMonitor = true;

        _ = MonitorScalingAsync(cancellationTokenSource.Token).ContinueWith(t =>
        {
            if (t.Exception != null)
                Console.WriteLine("[DEBUG] Error in MonitorScalingAsync: " + t.Exception.Flatten().InnerException);
        });

        InstanceManagementMenu();
    }

    static async Task MonitorScalingAsync(CancellationToken token)
    {
        Console.WriteLine("[DEBUG] MonitorScalingAsync started.");
        string[] settings = File.ReadAllLines(Path.Combine(AppDataPath, "blackfn_inf.txt"));
        string apiUrl = settings[0];
        string secretToken = settings[1]; // FIXED: Changed from settings[2] to settings[1]

        while (shouldMonitor && !token.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("[DEBUG] Sending GET request to API: " + apiUrl);
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

                for (int i = runningInstances.Count - 1; i >= 0; i--)
                {
                    int pid = runningInstances[i];
                    try
                    {
                        var process = Process.GetProcessById(pid);
                        if (process.HasExited) throw new Exception("Process stopped");
                    }
                    catch
                    {
                        Console.WriteLine("[DEBUG] Instance PID " + pid + " exited.");
                        if (instanceAccounts.TryGetValue(pid, out string deleteToken))
                        {
                            await DeleteServerAccount(apiUrl, deleteToken);
                            instanceAccounts.Remove(pid);
                            Console.WriteLine("[DEBUG] Deleted server account for PID " + pid);
                        }
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

    static async Task<(string username, string email, string password, string deleteToken)?> CreateServerAccount(string apiUrl, string serverKey)
    {
        try
        {
            Console.WriteLine("[DEBUG] Creating server account...");
            Console.WriteLine("[DEBUG] Using serverKey: " + serverKey); // Added for debugging
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
        string fortnitePath = settings[2]; // FIXED: Changed from settings[3] to settings[2]

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
                Arguments = $"-epicapp=Fortnite -epicenv=Prod -epiclocale=en-us -epicportal -skippatchcheck -nobe -fromfl=eac -fltoken=3db3ba5dcbd2e16703f3978d -AUTH_LOGIN={email} -AUTH_PASSWORD={password} -AUTH_TYPE=epic -nosplash -nosound -nullrhi",
                UseShellExecute = true
            };
            var process = Process.Start(psi);

            if (process != null)
            {
                Console.WriteLine("[DEBUG] Starting Fortnite Server (this could take a while)...");
                FakeAC.Start(fortnitePath, "FortniteClient-Win64-Shipping_BE.exe", $"-epicapp=Fortnite -epicenv=Prod -epiclocale=en-us -epicportal -noeac -fromfl=be -fltoken=h1cdhchd10150221h130eB56 -skippatchcheck", "r");
                FakeAC.Start(fortnitePath, "FortniteClient-Win64-Shipping_EAC.exe");
                FakeAC.Start(fortnitePath, "FortniteLauncher.exe", $"-epicapp=Fortnite -epicenv=Prod -epiclocale=en-us -epicportal -noeac -fromfl=be -fltoken=h1cdhchd10150221h130eB56 -skippatchcheck", "dsf");
                Injector.Inject(process.Id, Path.Combine(DllPath, "Backend.dll"));
                Thread.Sleep(60000);
                Injector.Inject(process.Id, Path.Combine(DllPath, "server.dll"));
                Console.WriteLine("[DEBUG] Server started! PID: " + process.Id);
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

    static void InstanceManagementMenu()
    {
        while (shouldMonitor)
        {
            Console.Clear();
            Console.WriteLine("============================");
            Console.WriteLine("SERVER STARTER V3");
            Console.WriteLine($"Running instances: {runningInstances.Count}");
            Console.WriteLine("============================");
            Console.WriteLine("-> 1 - Stop all Instances");
            Console.WriteLine("-> 2 - Show Instance Status");
            Console.WriteLine("============================");
            Console.Write("Your choice: ");

            string choice = Console.ReadLine();

            if (choice == "1") { StopAllInstances(); break; }
            else if (choice == "2") { ShowInstanceStatus(); Console.ReadKey(); }

            Thread.Sleep(1000);
        }

        StartMenu();
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
                Console.WriteLine($"[DEBUG] PID {pid} is running.");
            }
            catch
            {
                Console.WriteLine($"[DEBUG] PID {pid} not found.");
            }
        }
    }

    static void StopAllInstances()
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
                if (instanceAccounts.TryGetValue(pid, out string deleteToken))
                    DeleteServerAccount(File.ReadAllLines(Path.Combine(AppDataPath, "blackfn_inf.txt"))[0], deleteToken).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DEBUG] Exception while stopping PID " + pid + ": " + ex);
            }
        }

        runningInstances.Clear();
        instanceAccounts.Clear();
        Console.WriteLine("[DEBUG] All instances stopped.");
    }

    static void Settings()
    {
        Directory.CreateDirectory(AppDataPath);
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        if (File.Exists(filePath)) File.Delete(filePath);

        string apiHost;
        while (true)
        {
            Console.Write("Enter API Url: ");
            apiHost = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(apiHost)) break;
        }

        string apiUrl = $"{apiHost}";

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

        File.WriteAllLines(filePath, new[] { apiUrl, secretToken, fortnitePath });
        Console.WriteLine("[DEBUG] Settings saved to " + filePath);
        Console.WriteLine($"[DEBUG] Saved - API URL: {apiUrl}");
        Console.WriteLine($"[DEBUG] Saved - Secret Token: {secretToken}");
        Console.WriteLine($"[DEBUG] Saved - Fortnite Path: {fortnitePath}");
    }
}