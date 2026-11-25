using System.Runtime.InteropServices;

namespace BluetoothUnlockService;

/// <summary>
/// Locks the workstation.
///
/// <c>LockWorkStation</c> acts on the session of the calling process. The
/// service lives in session 0, which has no interactive desktop, so the call
/// fails with ERROR_ACCESS_DENIED (5) — exactly what the phone used to report
/// as "Lock failed". The reliable path is to run <c>--lock</c> inside the
/// user's own session through <see cref="UserSessionLauncher"/>.
/// </summary>
public static class SessionLocker
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    /// <summary>
    /// Locks the current process's session. Only meaningful inside the user's
    /// session; returns false (with the Win32 error) in session 0.
    /// </summary>
    public static bool TryLockHere(out int error)
    {
        bool ok = LockWorkStation();
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
}
