using System.Diagnostics;

namespace BluetoothUnlockService;

/// <summary>
/// Reports whether LogonUI.exe is running and in which session.
///
/// This is the missing piece of evidence when Ctrl+Alt+Del "does nothing":
/// before the SAS, LogonUI owns the secure desktop and shows the
/// "Press Ctrl+Alt+Del" screen. If LogonUI is absent, no SAS can be consumed.
/// After a successful SAS, LogonUI re-instantiates the credential provider —
/// which is what makes our tile appear.
/// </summary>
public static class LogonUiProbe
{
    public static string Describe()
    {
        try
        {
            Process[] processes = Process.GetProcessesByName("LogonUI");

            if (processes.Length == 0)
            {
                return "LogonUI: not running";
            }

            var parts = new List<string>(processes.Length);

            foreach (Process process in processes)
            {
                try
                {
                    parts.Add($"pid {process.Id} session {process.SessionId}");
                }
                catch
                {
                    parts.Add($"pid {process.Id}");
                }
                finally
                {
                    process.Dispose();
                }
            }

            return "LogonUI: " + string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return $"LogonUI: probe failed ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// True when a process named <paramref name="name"/> is running in the
    /// given session. Used to confirm the credential provider host is alive.
    /// </summary>
    public static bool IsRunningInSession(string name, int sessionId)
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.SessionId == sessionId)
                    {
                        return true;
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Treat as unknown.
        }

        return false;
    }
}
