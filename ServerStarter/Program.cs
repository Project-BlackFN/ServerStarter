using System;
using System.Diagnostics;
using System.IO;
using Utilities;

class BlackFN
{
    static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackFN_Server");
    static string DllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dll");

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
        Console.WriteLine("-> 2 - Start Fortnite");
        Console.WriteLine("-----------------------------");
        Console.Write("What is your Choice: ");
        string option = Console.ReadLine();

        if (option == "1") Settings();
        else if (option == "2") StartFortnite();
        else
        {
            Console.WriteLine("Please enter a valid number");
            StartMenu();
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

    static void StartFortnite()
    {
        string filePath = Path.Combine(AppDataPath, "blackfn_inf.txt");
        string backendDll = Path.Combine(DllPath, "Backend.dll");
        string serverDll = Path.Combine(DllPath, "server.dll");

        if (!File.Exists(filePath))
        {
            Console.WriteLine("Settings not found! Please configure first.");
            StartMenu();
            return;
        }

        var lines = File.ReadAllLines(filePath);
        string email = lines[0];
        string password = lines[1];
        string fortnitePath = lines[2];

        string exePath = Path.Combine(fortnitePath, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");
        if (!File.Exists(exePath))
        {
            Console.WriteLine($"Error: Fortnite executable not found at {exePath}");
            StartMenu();
            return;
        }

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exePath)
            {
                Arguments = $"-epicapp=Fortnite -epicenv=Prod -epiclocale=en-us -epicportal -skippatchcheck -nobe -fromfl=eac -fltoken=3db3ba5dcbd2e16703f3978d -caldera=eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2NvdW50X2lkIjoiYmU5ZGE1YzJmYmVhNDQwN2IyZjQwZWJhYWQ4NTlhZDQiLCJnZW5lcmF0ZWQiOjE2Mzg3MTcyNzgsImNhbGRlcmFHdWlkIjoiMzgxMGI4NjMtMmE2NS00NDU3LTliNTgtNGRhYjNiNDgyYTg2IiwiYWNQcm92aWRlciI6IkVhc3lBbnRpQ2hlYXQiLCJub3RlcyI6IiIsImZhbGxiYWNrIjpmYWxzZX0.VAWQB67RTxhiWOxx7DBjnzDnXyyEnX7OljJm-j2d88G_WgwQ9wrE6lwMEHZHjBd1ISJdUO1UVUqkfLdU5nofBQ -AUTH_LOGIN={email} -AUTH_PASSWORD={password} -AUTH_TYPE=epic -nosplash -nosound -nullrhi",
                UseShellExecute = true
            };
            Process process = Process.Start(psi);
            FakeAC.Start(fortnitePath, "FortniteClient-Win64-Shipping_EAC.exe");
            FakeAC.Start(fortnitePath, "FortniteLauncher.exe");
            Console.WriteLine("Fortnite Server is starting up...");
            Injector.Inject(process.Id, backendDll);
            Thread.Sleep(60000);
            Injector.Inject(process.Id, serverDll);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error while Starting Fortnite Server: {e.Message}");
        }
        StartMenu();
    }
}