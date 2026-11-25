using Microsoft.Win32;

namespace BluetoothUnlockService;

/// <summary>
/// Reads the logon policy that decides whether Windows gates the sign-in UI
/// behind Ctrl+Alt+Del.
///
/// The service no longer sends the SAS. That only works when
/// <c>DisableCAD = 1</c> ("Interactive logon: Do not require CTRL+ALT+DEL"),
/// which is what the installer now enforces. If somebody sets it back to 0 the
/// unlock would silently fail, so the service logs a loud warning instead.
/// </summary>
public static class LogonPolicy
{
    private const string SystemPolicyKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>
    /// 0 = Ctrl+Alt+Del required, 1 = not required, null = not configured.
    /// </summary>
    public static int? DisableCad()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemPolicyKey);
            return key?.GetValue("DisableCAD") as int?;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when Windows will demand Ctrl+Alt+Del before showing the sign-in
    /// UI. Not configured means Windows' default, which is "required".
    /// </summary>
    public static bool IsCadRequired()
    {
        int? value = DisableCad();
        return value is null or 0;
    }

    /// <summary>Human-readable policy state for the logs.</summary>
    public static string Describe()
    {
        int? value = DisableCad();
        return value switch
        {
            1 => "DisableCAD=1 (Ctrl+Alt+Del not required)",
            0 => "DisableCAD=0 (Ctrl+Alt+Del REQUIRED — unlock will fail)",
            _ => "DisableCAD unset (Windows default: Ctrl+Alt+Del required)"
        };
    }
}
