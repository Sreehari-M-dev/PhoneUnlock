using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BluetoothUnlockService;

/// <summary>
/// Launches a tiny helper inside the active console session to wake the display.
///
/// A service lives in session 0, so it cannot turn on the user's monitor or
/// generate input there. The unlock itself succeeds without this, but the user
/// sees a black screen and assumes nothing happened. WTSQueryUserToken +
/// CreateProcessAsUser lets us run the same executable with <c>--wake</c> in the
/// user's session, where SetThreadExecutionState and SendInput do work.
/// </summary>
public static class UserSessionLauncher
{
    /// <summary>Wake the display (monitor-on + mouse nudge).</summary>
    public static void WakeActiveSession() => RunInUserSession("--wake", "Wake");

    /// <summary>Lock the workstation from inside the user's session.</summary>
    public static void LockActiveSession() => RunInUserSession("--lock", "Lock");

    private const uint TokenAllAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    /// <summary>
    /// Starts <c>BluetoothUnlockService.exe --wake</c> in the active user
    /// session. Best-effort: any failure is logged and ignored.
    /// </summary>
    public static void RunInUserSession(string arguments, string label)
    {
        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                return;
            }

            // WTSQueryUserToken needs SeTcbPrivilege; CreateProcessAsUser needs
            // the token/quota privileges. LocalSystem has them, disabled by default.
            Privileges.EnableWakePrivileges();

            if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            {
                FileLog.Write(
                    $"{label}: WTSQueryUserToken failed for session {sessionId} " +
                    $"({Marshal.GetLastWin32Error()}).");
                return;
            }

            try
            {
                if (!DuplicateTokenEx(
                        userToken, TokenAllAccess, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out IntPtr primaryToken))
                {
                    FileLog.Write(
                        $"{label}: DuplicateTokenEx failed ({Marshal.GetLastWin32Error()}).");
                    return;
                }

                try
                {
                    string exe = Environment.ProcessPath ?? string.Empty;
                    if (string.IsNullOrEmpty(exe))
                    {
                        return;
                    }

                    var startup = new StartupInfo
                    {
                        cb = Marshal.SizeOf<StartupInfo>(),
                        lpDesktop = @"winsta0\default"
                    };

                    IntPtr environment = IntPtr.Zero;
                    CreateEnvironmentBlock(out environment, primaryToken, false);

                    try
                    {
                        string commandLine = $"\"{exe}\" {arguments}";
                        bool started = CreateProcessAsUser(
                            primaryToken,
                            exe,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            CreateUnicodeEnvironment | CreateNoWindow,
                            environment,
                            Path.GetDirectoryName(exe),
                            ref startup,
                            out ProcessInformation process);

                        if (started)
                        {
                            CloseHandle(process.hProcess);
                            CloseHandle(process.hThread);
                            FileLog.Write($"{label}: helper started in session {sessionId}.");
                        }
                        else
                        {
                            int error = Marshal.GetLastWin32Error();
                            FileLog.Write(
                                $"{label}: CreateProcessAsUser failed " +
                                $"({error}: {new Win32Exception(error).Message}).");
                        }
                    }
                    finally
                    {
                        if (environment != IntPtr.Zero)
                        {
                            DestroyEnvironmentBlock(environment);
                        }
                    }
                }
                finally
                {
                    CloseHandle(primaryToken);
                }
            }
            finally
            {
                CloseHandle(userToken);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"Wake/Lock: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
