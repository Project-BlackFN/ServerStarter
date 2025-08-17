using System.Diagnostics;
using static Utilities.Freeze;

namespace Utilities;

public class FakeAC
{
    public static Process FNLauncherProcess;
    public static Process FNAntiCheatProcess;

    public static void Start(string FNPath, string FileName, string args = "", string t = "r")
    {
        try
        {
            FNLauncherProcess = new Process(); // Initialize FNLauncherProcess

            if (File.Exists(Path.Combine(FNPath, "FortniteGame\\Binaries\\Win64\\", FileName)))
            {
                ProcessStartInfo process = new ProcessStartInfo()
                {
                    FileName = Path.Combine(FNPath, "FortniteGame\\Binaries\\Win64\\", FileName),
                    Arguments = args,
                    CreateNoWindow = true,
                };

                if (t == "r")
                {
                    FNAntiCheatProcess = Process.Start(process);

                    if (FNAntiCheatProcess.Id == 0)
                    {

                    }
                    else
                    {
                        Freezeproc(FNAntiCheatProcess);
                    }
                }
                else
                {
                    FNLauncherProcess = Process.Start(process);

                    if (FNLauncherProcess.Id == 0)
                    {

                    }
                    else
                    {
                        Freezeproc(FNLauncherProcess);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[red]ERROR: " + ex.Message + "[/]");
            Console.ReadKey();
        }
    }


}