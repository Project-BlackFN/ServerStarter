using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Utilities;

class BlackFN
{
    static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackFN_Server");
    static string DllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dll");
    static List<int> runningInstances = new List<int>();
    static bool shouldMonitor = false;
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

    static void Main()
    {
        Directory.CreateDirectory(AppDataPath);
        if (!CheckRequiredFiles())
        {
            Console.WriteLine("a few dlls were not found please add them..");
            Console.ReadKey();
            return;
        }
        StartMenu();
    }

    static bool CheckRequiredFiles()
    {
        string backendDllPath = Path.Combine(DllPath, "Backend.dll");
        string serverDllPath = Path.Combine(DllPath, "server.dll");

        if (!Directory.Exists(DllPath))
        {
            Console.WriteLine($"DLL-Ordner nicht gefunden: {DllPath}");
            return false;
        }

        bool allFilesFound = true;

        if (!File.Exists(backendDllPath))
        {
            Console.WriteLine($"Backend.dll was not found in: {backendDllPath}");
            allFilesFound = false;
        }

        if (!File.Exists(serverDllPath))
        {
            Console.WriteLine($"server.dll was not found in: {serverDllPath}");
            allFilesFound = false;
        }

        if (allFilesFound)
        {
            Console.WriteLine("All files found.");
        }

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
        else
        {
            Console.WriteLine("Please enter a valid number");
            StartMenu();
        }
    }

    static void StartFNServer()
    {
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        if (!File.Exists(filePath))
        {
            Console.WriteLine("Settings not found! Please configure first.");
            StartMenu();
            return;
        }

        int instanceCount;
        while (true)
        {
            Console.Write("How many instances do you want to start? ");
            string input = Console.ReadLine();
            if (int.TryParse(input, out instanceCount) && instanceCount > 0 && instanceCount <= 25) // max. 25 but if u have a beefy Server u can do more
                break;
            Console.WriteLine("Please enter a valid number between 1 and 25");
        }

        Console.WriteLine($"Starting {instanceCount} instances...");
        for (int i = 1; i <= instanceCount; i++)
        {
            Console.WriteLine($"Starting instance {i}/{instanceCount}...");
            int processId = StartFortniteInstance();
            if (processId != -1)
            {
                runningInstances.Add(processId);
                Console.WriteLine($"Instance {i} started with PID: {processId}");
            }
            else
            {
                Console.WriteLine($"Failed to start instance {i}");
            }

            if (i < instanceCount)
                Thread.Sleep(5000);
        }

        if (runningInstances.Count > 0)
        {
            Console.WriteLine($"\n{runningInstances.Count} instances started successfully!");
            shouldMonitor = true;

            Task.Run(() => MonitorInstances(cancellationTokenSource.Token));

            InstanceManagementMenu();
        }
        else
        {
            Console.WriteLine("No instances were started successfully.");
            Console.ReadKey();
            StartMenu();
        }
    }

    static void InstanceManagementMenu()
    {
        while (shouldMonitor)
        {
            Console.Clear();
            Console.WriteLine("============================");
            Console.WriteLine("SERVER STARTER V2");
            Console.WriteLine($"Running instances: {runningInstances.Count}");
            Console.WriteLine("============================");
            Console.WriteLine("-> 1 - Stop all Instances");
            Console.WriteLine("-> 2 - Show Instance Status");
            Console.WriteLine("============================");
            Console.Write("Your choice: ");

            string choice = Console.ReadLine();

            if (choice == "1")
            {
                StopAllInstances();
                break;
            }
            else if (choice == "2")
            {
                ShowInstanceStatus();
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }

            Thread.Sleep(1000);
        }

        StartMenu();
    }

    static void ShowInstanceStatus()
    {
        Console.WriteLine("\nInstance Status:");
        Console.WriteLine("================");

        for (int i = 0; i < runningInstances.Count; i++)
        {
            int pid = runningInstances[i];
            try
            {
                Process process = Process.GetProcessById(pid);
                Console.WriteLine($"Instance {i + 1}: PID {pid} - Running ({process.ProcessName})");
            }
            catch
            {
                Console.WriteLine($"Instance {i + 1}: PID {pid} - Not Running");
            }
        }
    }

    static void StopAllInstances()
    {
        Console.WriteLine("Stopping all instances...");
        shouldMonitor = false;
        cancellationTokenSource.Cancel();

        foreach (int pid in runningInstances)
        {
            try
            {
                Process process = Process.GetProcessById(pid);
                process.Kill();
                Console.WriteLine($"Stopped instance with PID: {pid}");
            }
            catch
            {
                Console.WriteLine($"Instance with PID {pid} was already stopped");
            }
        }

        runningInstances.Clear();
        Console.WriteLine("All instances stopped.");
        Console.ReadKey();
    }

    static async Task MonitorInstances(CancellationToken cancellationToken)
    {
        while (shouldMonitor && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                for (int i = runningInstances.Count - 1; i >= 0; i--)
                {
                    int pid = runningInstances[i];

                    try
                    {
                        Process process = Process.GetProcessById(pid);
                        if (process.HasExited)
                        {
                            throw new ArgumentException("Process has exited");
                        }
                    }
                    catch
                    {
                        Console.WriteLine($"Instance with PID {pid} has stopped. Restarting...");
                        runningInstances.RemoveAt(i);

                        if (shouldMonitor)
                        {
                            int newPid = StartFortniteInstance();
                            if (newPid != -1)
                            {
                                runningInstances.Add(newPid);
                                Console.WriteLine($"Instance restarted with new PID: {newPid}");
                            }
                            else
                            {
                                Console.WriteLine("Failed to restart instance");
                            }
                        }
                    }
                }

                await Task.Delay(10000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in monitoring: {ex.Message}");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    static int StartFortniteInstance()
    {
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        string backendDll = Path.Combine(DllPath, "Backend.dll");
        string serverDll = Path.Combine(DllPath, "server.dll");

        if (!File.Exists(filePath))
        {
            Console.WriteLine("Settings not found! Please configure first.");
            return -1;
        }

        var lines = File.ReadAllLines(filePath);
        string email = lines[0];
        string password = lines[1];
        string fortnitePath = lines[2];

        string exePath = Path.Combine(fortnitePath, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");
        if (!File.Exists(exePath))
        {
            Console.WriteLine($"Error: Fortnite executable not found at {exePath}");
            return -1;
        }

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exePath)
            {
                Arguments = $"-epicapp=Fortnite -epicenv=Prod -epiclocale=en-us -epicportal -skippatchcheck -nobe -fromfl=eac -fltoken=3db3ba5dcbd2e16703f3978d -caldera=eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2NvdW50X2lkIjoiYmU5ZGE1YzJmYmVhNDQwN2IyZjQwZWJhYWQ4NTlhZDQiLCJnZW5lcmF0ZWQiOjE2Mzg3MTcyNzgsImNhbGRlcmFHdWlkIjoiMzgxMGI4NjMtMmE2NS00NDU3LTliNTgtNGRhYjNiNDgyYTg2IiwiYWNQcm92aWRlciI6IkVhc3lBbnRpQ2hlYXQiLCJub3RlcyI6IiIsImZhbGxiYWNrIjpmYWxzZX0.VAWQB67RTxhiWOxx7DBjnzDnXyyEnX7OljJm-j2d88G_WgwQ9wrE6lwMEHZHjBd1ISJdUO1UVUqkfLdU5nofBQ -AUTH_LOGIN={email} -AUTH_PASSWORD={password} -AUTH_TYPE=epic -nosplash -nosound -nullrhi",
                UseShellExecute = true
            };
            Process process = Process.Start(psi);

            if (process != null)
            {
                // FakeAC.Start(fortnitePath, "FortniteClient-Win64-Shipping_EAC.exe");
                // FakeAC.Start(fortnitePath, "FortniteLauncher.exe");

                Injector.Inject(process.Id, backendDll);
                Thread.Sleep(60000);
                Injector.Inject(process.Id, serverDll);

                return process.Id;
            }
            return -1;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error while Starting Fortnite Instance: {e.Message}");
            return -1;
        }
    }

    static void Settings()
    {
        Directory.CreateDirectory(AppDataPath);
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        if (File.Exists(filePath)) File.Delete(filePath);

        string email;
        while (true)
        {
            Console.Write("Enter your E-Mail: ");
            email = Console.ReadLine();
            if (email.Contains("@")) break;
            Console.WriteLine("Invalid E-Mail.");
        }

        Console.Write("Enter your Password: ");
        string password = Console.ReadLine();

        string fortnitePath;
        while (true)
        {
            Console.Write("Enter the File Path of Fortnite: ");
            fortnitePath = Console.ReadLine();
            string expectedFile = Path.Combine(fortnitePath, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");
            if (File.Exists(expectedFile)) break;
            Console.WriteLine($"Error: File not found at {expectedFile}");
        }

        File.WriteAllLines(filePath, new[] { email, password, fortnitePath });
        Console.WriteLine("Saved!");
        StartMenu();
    }
}