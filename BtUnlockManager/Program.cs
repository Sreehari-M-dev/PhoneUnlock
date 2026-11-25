using System.Security.Principal;

namespace BtUnlockManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The manifest already requests Administrator. This fallback keeps the
        // app usable if the manifest is ever removed (for example when running
        // the DLL directly during development).
        if (!IsElevated() && !args.Contains("--no-elevate"))
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe != null)
                {
                    var start = new System.Diagnostics.ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = "--elevated",
                        WorkingDirectory = AppContext.BaseDirectory
                    };

                    System.Diagnostics.Process.Start(start);
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user declined the UAC prompt. Continue without elevation;
                // the controls that need Administrator will report the error.
            }
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\PhoneUnlockManager", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "The Phone Unlock control panel is already running.",
                "Phone Unlock",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
