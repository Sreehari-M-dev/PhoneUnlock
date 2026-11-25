using System.Runtime.InteropServices;

namespace BluetoothUnlockService;

/// <summary>
/// Token privilege helpers.
///
/// LocalSystem holds these privileges but they are disabled in the token by
/// default, so they must be enabled explicitly. Only the display-wake helper
/// needs them now: <c>WTSQueryUserToken</c> requires SeTcbPrivilege, and
/// <c>CreateProcessAsUser</c> requires SeAssignPrimaryToken/SeIncreaseQuota.
///
/// This is deliberately separate from the old <c>SecureDesktopHelper</c>: the
/// Ctrl+Alt+Del / SendSAS path is gone because <c>DisableCAD = 1</c> removes
/// the requirement, and dead SAS code that silently "succeeds" is worse than
/// no SAS code at all.
/// </summary>
public static class Privileges
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TokenPrivileges newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Enables a named privilege in the current process token. Returns false
    /// when the privilege is not held; <paramref name="error"/> then carries
    /// ERROR_NOT_ALL_ASSIGNED (1300).
    /// </summary>
    public static bool EnablePrivilege(string name, out int error)
    {
        error = 0;

        if (!OpenProcessToken(
                GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out Luid luid))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            var state = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            // AdjustTokenPrivileges does not clear the last error on success.
            Marshal.SetLastPInvokeError(0);

            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            // Success is reported even when the privilege is not assigned, so
            // ERROR_NOT_ALL_ASSIGNED is the real failure signal.
            error = Marshal.GetLastWin32Error();
            return error == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Enables everything the wake helper needs. Best-effort: a failure only
    /// means the monitor may stay asleep, never that the unlock fails.
    /// </summary>
    public static void EnableWakePrivileges()
    {
        EnablePrivilege("SeTcbPrivilege", out _);
        EnablePrivilege("SeAssignPrimaryTokenPrivilege", out _);
        EnablePrivilege("SeIncreaseQuotaPrivilege", out _);
    }
}
