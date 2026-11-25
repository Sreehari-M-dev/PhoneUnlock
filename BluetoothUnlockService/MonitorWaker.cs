using System.Runtime.InteropServices;

namespace BluetoothUnlockService;

/// <summary>
/// Best-effort monitor wake.
///
/// This must never block the unlock path. The service runs in session 0, and a
/// synchronous broadcast (SendMessage) can wait forever on a window owned by
/// another session — which is exactly what used to freeze the unlock after the
/// SAS was sent. PostMessage is asynchronous, and the SAS (not this helper) is
/// what actually moves Windows to the sign-in screen.
/// </summary>
public static class MonitorWaker
{
    private const int HWND_BROADCAST = 0xFFFF;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MONITOR_ON = -1;

    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public MouseInput mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    // ---- Power requests -------------------------------------------------
    // PostMessage(HWND_BROADCAST) only reaches windows in the caller's session,
    // so a session-0 service can never light the monitor that way. Power
    // requests are kernel-wide: PowerRequestDisplayRequired turns the display
    // back on regardless of which session created it. This is the reliable
    // half of the wake path; the --wake helper is the user-session half.

    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;

    /// <summary>POWER_REQUEST_CONTEXT_SIMPLE_STRING. 0 is invalid → error 87.</summary>
    private const uint PowerRequestContextSimpleString = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Holds a system-wide "display required" request until disposed, which
    /// forces the monitor on and keeps it on while the unlock completes.
    /// Returns null if the request could not be created (never fatal).
    /// </summary>
    public static IDisposable? RequestDisplayOn()
    {
        IntPtr reason = IntPtr.Zero;
        IntPtr request = IntPtr.Zero;

        try
        {
            reason = Marshal.StringToHGlobalUni("Bluetooth unlock is waking the display");

            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                Reason = reason
            };

            request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == new IntPtr(-1))
            {
                FileLog.Write(
                    $"Wake: PowerCreateRequest failed ({Marshal.GetLastWin32Error()}).");
                return null;
            }

            if (!PowerSetRequest(request, PowerRequestDisplayRequired))
            {
                FileLog.Write(
                    $"Wake: PowerSetRequest(display) failed ({Marshal.GetLastWin32Error()}).");
            }

            PowerSetRequest(request, PowerRequestSystemRequired);

            return new DisplayRequest(request, reason);
        }
        catch (Exception ex)
        {
            FileLog.Write($"Wake: power request failed: {ex.Message}");

            if (request != IntPtr.Zero)
            {
                CloseHandle(request);
            }

            if (reason != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(reason);
            }

            return null;
        }
    }

    private sealed class DisplayRequest : IDisposable
    {
        private IntPtr _request;
        private IntPtr _reason;

        public DisplayRequest(IntPtr request, IntPtr reason)
        {
            _request = request;
            _reason = reason;
        }

        public void Dispose()
        {
            if (_request == IntPtr.Zero)
            {
                return;
            }

            try
            {
                PowerClearRequest(_request, PowerRequestDisplayRequired);
                PowerClearRequest(_request, PowerRequestSystemRequired);
                CloseHandle(_request);
            }
            catch
            {
                // Best-effort.
            }
            finally
            {
                _request = IntPtr.Zero;

                if (_reason != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_reason);
                    _reason = IntPtr.Zero;
                }
            }
        }
    }

    /// <summary>
    /// Runs inside the user's session (started with <c>--wake</c>). Resets the
    /// display idle timer and nudges the mouse so the monitor comes back on.
    /// Holds the display awake briefly, then releases it.
    /// </summary>
    public static void WakeInCurrentSession()
    {
        try
        {
            // Runs in the user's session, so this broadcast can reach the
            // desktop windows (unlike the same call from session 0).
            PostMessage(
                (IntPtr)HWND_BROADCAST,
                WM_SYSCOMMAND,
                (IntPtr)SC_MONITORPOWER,
                (IntPtr)MONITOR_ON);

            SetThreadExecutionState(
                EXECUTION_STATE.ES_CONTINUOUS |
                EXECUTION_STATE.ES_DISPLAY_REQUIRED |
                EXECUTION_STATE.ES_SYSTEM_REQUIRED);

            var inputs = new[]
            {
                new Input
                {
                    type = InputMouse,
                    mi = new MouseInput { dx = 1, dy = 1, dwFlags = MouseEventMove }
                },
                new Input
                {
                    type = InputMouse,
                    mi = new MouseInput { dx = -1, dy = -1, dwFlags = MouseEventMove }
                }
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

            // Keep the display on long enough for the unlock to finish.
            Thread.Sleep(2500);

            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
        catch
        {
            // Best-effort only.
        }
    }

    /// <summary>
    /// Asks the display to turn on. Returns immediately; failures are ignored.
    /// </summary>
    public static void Wake()
    {
        try
        {
            PostMessage(
                (IntPtr)HWND_BROADCAST,
                WM_SYSCOMMAND,
                (IntPtr)SC_MONITORPOWER,
                (IntPtr)MONITOR_ON);

            SetThreadExecutionState(
                EXECUTION_STATE.ES_CONTINUOUS |
                EXECUTION_STATE.ES_DISPLAY_REQUIRED |
                EXECUTION_STATE.ES_SYSTEM_REQUIRED);
        }
        catch (Exception ex)
        {
            FileLog.Write($"Wake: could not request display wake: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases the execution state, allowing normal sleep behavior again.
    /// </summary>
    public static void AllowSleep()
    {
        try
        {
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
        catch
        {
            // Ignore.
        }
    }
}
