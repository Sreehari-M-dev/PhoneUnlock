using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BluetoothUnlockService;

/// <summary>
/// Single place that owns the unlock/lock state machine.
///
/// Both the Wi-Fi and Bluetooth listeners call into this, so the two transports
/// can never run two unlocks at once. It also fixes the old state tracking:
/// lock state comes from Windows itself (SessionState), not from a flag that
/// started as "unlocked".
/// </summary>
public sealed class UnlockCoordinator
{
    public const string ProviderGuid = "{E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C}";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BtUnlock");

    public static readonly string SignalFile = Path.Combine(DataDir, "unlock.signal");
    public static readonly string StatusFile = Path.Combine(DataDir, "service.status");

    /// <summary>How long to let the panel light up after waking.</summary>
    private const int WakeSettleDelayMs = 400;

    private readonly object _gate = new();
    private bool _unlockInProgress;
    private DateTime _unlockStartedUtc;

    /// <summary>
    /// A stuck attempt must not block every future unlock forever. Anything
    /// older than this is treated as abandoned and cleared on the next request.
    /// </summary>
    private static readonly TimeSpan StaleAttemptAge = TimeSpan.FromSeconds(30);

    public bool IsUnlockInProgress
    {
        get { lock (_gate) return _unlockInProgress; }
    }

    public void WriteStatus(string status)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StatusFile, status);
        }
        catch
        {
            // Non-fatal: the lock-screen tile just keeps its previous text.
        }
    }

    /// <summary>
    /// Returns true when an unlock may start. When false, <paramref name="rejection"/>
    /// is ALREADY_UNLOCKED or IN_PROGRESS.
    /// </summary>
    public bool TryBeginUnlock(out string rejection)
    {
        rejection = string.Empty;

        lock (_gate)
        {
            // If Windows cannot tell us the state, allow the attempt rather than
            // rejecting a real unlock.
            if (SessionState.TryGetState(out bool isLocked, out _) && !isLocked)
            {
                rejection = "ALREADY_UNLOCKED";
                return false;
            }

            if (_unlockInProgress &&
                DateTime.UtcNow - _unlockStartedUtc > StaleAttemptAge)
            {
                FileLog.Write(
                    $"Unlock: clearing a stuck attempt " +
                    $"({(DateTime.UtcNow - _unlockStartedUtc).TotalSeconds:F0}s old).");
                _unlockInProgress = false;
            }

            if (_unlockInProgress)
            {
                rejection = "IN_PROGRESS";
                return false;
            }

            _unlockInProgress = true;
            _unlockStartedUtc = DateTime.UtcNow;
            return true;
        }
    }

    public void EndUnlock()
    {
        lock (_gate)
        {
            _unlockInProgress = false;
            _unlockStartedUtc = default;
        }
    }

    /// <summary>
    /// Drops a fresh signal for the credential provider. The file contains a
    /// Unix-ms timestamp so the provider can ignore stale requests.
    /// </summary>
    public void SignalUnlock()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(
                SignalFile,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            FileLog.Write("Signal: wrote unlock.signal");
        }
        catch (Exception ex)
        {
            FileLog.Write($"Signal: could not write signal file: {ex.Message}");
        }

        try
        {
            using var evt = new EventWaitHandle(
                false, EventResetMode.AutoReset, @"Global\BtUnlockEvent");
            evt.Set();
        }
        catch (Exception ex)
        {
            FileLog.Write($"Signal: could not set named event: {ex.Message}");
        }
    }

    public void ClearSignal()
    {
        try
        {
            if (File.Exists(SignalFile))
            {
                File.Delete(SignalFile);
                FileLog.Write("Signal: cleared.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"Signal: could not clear signal file: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes our credential provider the one LogonUI prefers, so the user does
    /// not have to click "Sign-in options" first.
    /// </summary>
    public void SetDefaultProvider()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI", true);
            if (key == null)
            {
                FileLog.Write("Provider: LogonUI registry key not found.");
                return;
            }

            key.SetValue("LastLoggedOnProvider", ProviderGuid);
            FileLog.Write("Provider: set as the default logon provider.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"Provider: could not set default provider: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the full unlock sequence and waits until Windows actually reports the
    /// session as unlocked. The SAS is retried because the logon UI sometimes
    /// needs a second nudge while the monitor is waking.
    /// </summary>
    public async Task<bool> UnlockAsync(TimeSpan timeout)
    {
        WriteStatus("UNLOCKING");
        SetDefaultProvider();

        DateTime deadline = DateTime.UtcNow + timeout;
        int attempt = 0;

        // System-wide display request: honoured by the kernel, so it works from
        // session 0 and turns the monitor back on before the SAS lands.
        using IDisposable? displayRequest = MonitorWaker.RequestDisplayOn();

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                attempt++;

                SessionState.TryGetState(out bool startLocked, out _, out int startFlags);
                FileLog.Write(
                    $"Unlock: attempt {attempt} starting " +
                    $"(locked={startLocked}, flags={startFlags}, {LogonUiProbe.Describe()}).");

                SignalUnlock();

                // Wake from inside the user's session too (monitor-on broadcast
                // + mouse nudge), then give the panel a moment to light up.
                UserSessionLauncher.WakeActiveSession();
                await Task.Delay(WakeSettleDelayMs);

                // No SAS here: with DisableCAD = 1 Windows shows the sign-in UI
                // (and therefore our tile) as soon as the session locks, so the
                // credential provider can submit without Ctrl+Alt+Del.
                if (LogonPolicy.IsCadRequired())
                {
                    FileLog.Write(
                        "Unlock: WARNING — " + LogonPolicy.Describe() +
                        ". The tile cannot appear until Ctrl+Alt+Del is pressed. " +
                        "Run scripts/set-cad-requirement.ps1 -Mode not-required.");
                }

                SessionState.TryGetState(out bool nowLocked, out _, out int nowFlags);
                FileLog.Write(
                    $"Unlock: attempt {attempt} (locked={nowLocked}, " +
                    $"flags={nowFlags}, {LogonUiProbe.Describe()}).");

                // Give Windows a moment, then check the real session state.
                for (int i = 0; i < 10 && DateTime.UtcNow < deadline; i++)
                {
                    await Task.Delay(500);

                    if (SessionState.TryGetState(out bool isLocked, out _, out int flags) && !isLocked)
                    {
                        ClearSignal();
                        WriteStatus("LISTENING");
                        FileLog.Write(
                            $"Unlock: session reports UNLOCKED (flags={flags}). " +
                            $"Succeeded on attempt {attempt}.");
                        return true;
                    }
                }
            }

            ClearSignal();
            WriteStatus("LISTENING");
            FileLog.Write("Unlock: timed out waiting for the session to unlock.");
            return false;
        }
        finally
        {
            EndUnlock();
        }
    }

    /// <summary>
    /// Locks the workstation and waits until Windows reports the session as
    /// locked, so the phone gets an honest answer instead of "LockWorkStation
    /// failed".
    /// </summary>
    public async Task<bool> LockAsync(TimeSpan timeout)
    {
        WriteStatus("LOCKING");

        // Session 0 has no interactive desktop: this usually fails with
        // ERROR_ACCESS_DENIED, so the real work happens in the user session.
        if (SessionLocker.TryLockHere(out int error))
        {
            FileLog.Write("Lock: LockWorkStation succeeded in this session.");
        }
        else
        {
            FileLog.Write(
                $"Lock: LockWorkStation failed here (error {error}); " +
                "asking the user session to lock.");
            UserSessionLauncher.LockActiveSession();
        }

        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (SessionState.TryGetState(out bool isLocked, out _, out _) && isLocked)
            {
                WriteStatus("LISTENING");
                FileLog.Write("Lock: session reports LOCKED.");
                return true;
            }

            await Task.Delay(200);
        }

        WriteStatus("LISTENING");
        FileLog.Write("Lock: timed out waiting for the session to lock.");
        return false;
    }
}
