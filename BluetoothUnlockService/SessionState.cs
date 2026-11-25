using System.Runtime.InteropServices;

namespace BluetoothUnlockService;

/// <summary>
/// Authoritative lock/unlock state for the active console session.
///
/// The old service trusted SessionSwitch events only. If the service started (or
/// restarted) while the PC was already locked it never saw the lock event, kept
/// believing the workstation was unlocked, and rejected every unlock request as
/// ALREADY_UNLOCKED. Querying Windows directly removes that failure mode.
/// </summary>
public static class SessionState
{
    private const int WtsSessionInfoEx = 25;
    private const uint WtsCurrentServerHandle = 0;

    // WTS_SESSIONSTATE values. UNKNOWN is 0xFFFFFFFF and must never be read as
    // "unlocked" — that false positive made the service report success while
    // the PC was still sitting on the lock screen.
    private const int WtsSessionStateLock = 0;
    private const int WtsSessionStateUnlock = 1;
    private const int WtsSessionStateUnknown = unchecked((int)0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags; // WTS_SESSIONSTATE_LOCK = 0, WTS_SESSIONSTATE_UNLOCK = 1

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
        public string WinStationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
        public string DomainName;

        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;

        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);

    /// <summary>
    /// Tries to read the current session state.
    /// Returns false when Windows could not be queried or reports UNKNOWN.
    /// </summary>
    public static bool TryGetState(out bool isLocked, out string userName)
        => TryGetState(out isLocked, out userName, out _);

    /// <summary>
    /// Overload that also returns the raw WTS_SESSIONSTATE value, for logging.
    /// </summary>
    public static bool TryGetState(out bool isLocked, out string userName, out int sessionFlags)
    {
        isLocked = false;
        userName = string.Empty;
        sessionFlags = WtsSessionStateUnknown;

        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                // No console session (e.g. before logon) — treat as unknown.
                return false;
            }

            if (!WTSQuerySessionInformation(
                    (IntPtr)WtsCurrentServerHandle, sessionId, WtsSessionInfoEx,
                    out IntPtr buffer, out _))
            {
                return false;
            }

            try
            {
                // WTSINFOEX: DWORD Level + padding, then the level payload at offset 8.
                if (Marshal.ReadInt32(buffer) != 1)
                {
                    return false;
                }

                var info = Marshal.PtrToStructure<WtsInfoExLevel1>(IntPtr.Add(buffer, 8));
                userName = info.UserName ?? string.Empty;
                sessionFlags = info.SessionFlags;

                switch (sessionFlags)
                {
                    case WtsSessionStateLock:
                        isLocked = true;
                        return true;

                    case WtsSessionStateUnlock:
                        isLocked = false;
                        return true;

                    default:
                        // UNKNOWN or an unexpected value. Do not claim the
                        // session is unlocked; callers treat false as unknown.
                        return false;
                }
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Convenience wrapper — false when the state is unknown.</summary>
    public static bool IsLocked() => TryGetState(out bool locked, out _) && locked;
}
