using System.Security;
using System.Security.Cryptography;
using System.Text;
using Lithnet.CredentialProvider;

namespace BtUnlockCredProvider;

/// <summary>
/// The credential tile that appears on the Windows lock screen.
/// Shows connection status and auto-unlocks when a fresh signal arrives.
///
/// Status flow on lock screen:
///   "Waiting for phone connection..."  → Service running, no phone connected
///   "Phone connected! Waiting..."      → Phone connected, awaiting signal
///   "Unlocking..."                     → Signal received, submitting credentials
///
/// A signal only counts when it is fresh (see <see cref="SignalMaxAge"/>). The
/// service deletes the file when an unlock finishes, but if it ever dies
/// mid-unlock a leftover file would otherwise auto-submit credentials on the
/// next lock — which is exactly the kind of surprise this app must not have.
/// </summary>
public class BtUnlockTile : CredentialTile2
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BtUnlock");

    private static readonly string SignalFile = Path.Combine(DataDir, "unlock.signal");
    private static readonly string CredFile = Path.Combine(DataDir, "credentials.dat");
    private static readonly string LogFile = Path.Combine(DataDir, "credprovider.log");
    private static readonly string StatusFile = Path.Combine(DataDir, "service.status");

    /// <summary>How long an unlock signal stays valid.</summary>
    private static readonly TimeSpan SignalMaxAge = TimeSpan.FromSeconds(90);

    private SmallLabelControl? _statusLabel;
    private SmallLabelControl? _connLabel;
    private readonly CredentialProviderBase _provider;

    /// <summary>
    /// Persists across tile re-creation (ReloadUserTiles builds a new instance).
    /// Stores the signal timestamp rather than a bare bool so a stale flag can
    /// never survive a service restart.
    /// </summary>
    private static long s_signalTimestamp;

    private Thread? _waitThread;
    private CancellationTokenSource? _cts;

    public BtUnlockTile(CredentialProviderBase provider, CredentialProviderUser user)
        : base(provider, user)
    {
        _provider = provider;
    }

    public override void Initialize()
    {
        Log("=== BtUnlockTile.Initialize() called ===");

        _statusLabel = Controls.GetControl<SmallLabelControl>("StatusLabel");
        _connLabel = Controls.GetControl<SmallLabelControl>("ConnLabel");

        if (!File.Exists(CredFile))
        {
            Log("No credentials file found at: " + CredFile);
            UpdateStatus("No credentials stored. Run setup first.");
            UpdateConn("");
            return;
        }

        Log("Credentials file found.");

        // A signal may already be waiting from the service.
        if (TryGetFreshSignalTimestamp(out long timestamp))
        {
            Log($"Fresh signal already present ({timestamp}). Setting auto-logon flag.");
            s_signalTimestamp = timestamp;
            ShowUnlocking();

            // Do NOT return here. The watcher thread below is what asks LogonUI
            // to select this tile. Returning relied on the user clicking the
            // tile, which is exactly why the unlock only worked manually.
        }
        else
        {
            // Read initial service status.
            UpdateServiceStatus();
        }

        // Start the background thread that polls for the signal and status.
        _cts = new CancellationTokenSource();
        _waitThread = new Thread(WatcherThread)
        {
            IsBackground = true,
            Name = "BtUnlockWaiter"
        };
        _waitThread.Start();
        Log("Watcher thread started.");
    }

    /// <summary>
    /// Called by Windows when this tile is selected.
    /// </summary>
    protected override bool OnSelectedShouldAutoLogon()
    {
        bool signaled = HasFreshSignal();
        Log($"OnSelectedShouldAutoLogon called. signaled={signaled}");

        if (signaled)
        {
            ShowUnlocking();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Background thread that:
    /// 1. Polls the service status file to show connection state
    /// 2. Watches for a fresh unlock signal
    /// 3. Triggers auto-logon, retrying the reload until Windows reacts
    /// </summary>
    private void WatcherThread()
    {
        Log("WatcherThread running. Polling for signal and status updates.");
        string lastStatus = "";

        try
        {
            while (_cts != null && !_cts.Token.IsCancellationRequested)
            {
                // ── Check for a fresh unlock signal ──
                if (HasFreshSignal())
                {
                    Log("*** FRESH SIGNAL DETECTED ***");
                    ShowUnlocking();

                    // Keep trying: LogonUI ignores a reload that arrives while it
                    // is still building the tile. The old code tried once and
                    // then stopped, so the unlock silently never happened.
                    for (int attempt = 1; attempt <= 10; attempt++)
                    {
                        if (TryTriggerReload())
                        {
                            Log($"Reload accepted on attempt {attempt}.");
                            break;
                        }

                        if (_cts == null || _cts.Token.IsCancellationRequested)
                        {
                            return;
                        }

                        Thread.Sleep(700);
                    }

                    return;
                }

                // Also honour the named event, which avoids filesystem polling
                // latency right after the phone sends its request.
                if (TryConsumeNamedEvent())
                {
                    Log("*** NAMED EVENT DETECTED ***");
                    ShowUnlocking();

                    for (int attempt = 1; attempt <= 10; attempt++)
                    {
                        if (TryTriggerReload())
                        {
                            Log($"Reload accepted on attempt {attempt}.");
                            break;
                        }

                        if (_cts == null || _cts.Token.IsCancellationRequested)
                        {
                            return;
                        }

                        Thread.Sleep(700);
                    }

                    return;
                }

                // ── Update status display from service status file ──
                string currentStatus = ReadServiceStatus();
                if (currentStatus != lastStatus)
                {
                    lastStatus = currentStatus;
                    ApplyServiceStatus(currentStatus);
                }

                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            Log("WatcherThread ERROR: " + ex);
            UpdateStatus("Error: " + ex.Message);
        }

        Log("WatcherThread exiting.");
    }

    /// <summary>
    /// True when a signal file exists and was written recently. Stale files are
    /// deleted so they cannot trigger an unlock later.
    /// </summary>
    private static bool HasFreshSignal()
    {
        if (TryGetFreshSignalTimestamp(out long timestamp))
        {
            s_signalTimestamp = timestamp;
            return true;
        }

        s_signalTimestamp = 0;
        return false;
    }

    private static bool TryGetFreshSignalTimestamp(out long timestamp)
    {
        timestamp = 0;

        try
        {
            if (!File.Exists(SignalFile))
            {
                return false;
            }

            DateTime writtenUtc = File.GetLastWriteTimeUtc(SignalFile);

            // The service writes a Unix-ms timestamp; fall back to the file
            // timestamp if the content is unreadable.
            try
            {
                string text = File.ReadAllText(SignalFile).Trim();
                if (long.TryParse(text, out long parsed) && parsed > 0)
                {
                    writtenUtc = DateTimeOffset.FromUnixTimeMilliseconds(parsed).UtcDateTime;
                }
            }
            catch
            {
                // Use the filesystem timestamp.
            }

            if (DateTime.UtcNow - writtenUtc > SignalMaxAge)
            {
                Log($"Ignoring stale signal from {writtenUtc:HH:mm:ss} (age {(DateTime.UtcNow - writtenUtc).TotalSeconds:F0}s).");
                TryDeleteSignalFile();
                return false;
            }

            timestamp = new DateTimeOffset(writtenUtc).ToUnixTimeMilliseconds();
            return true;
        }
        catch (Exception ex)
        {
            Log("TryGetFreshSignalTimestamp ERROR: " + ex.Message);
            return false;
        }
    }

    private static bool TryConsumeNamedEvent()
    {
        try
        {
            using var evt = new EventWaitHandle(
                false, EventResetMode.AutoReset, @"Global\BtUnlockEvent");

            if (!evt.WaitOne(0))
            {
                return false;
            }

            // The event alone is not enough — require a fresh signal file so a
            // stray event cannot unlock the PC.
            return HasFreshSignal();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the service status written by the listeners.
    /// </summary>
    private static string ReadServiceStatus()
    {
        try
        {
            if (File.Exists(StatusFile))
                return File.ReadAllText(StatusFile).Trim();
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Updates the UI labels based on the service status string.
    /// </summary>
    private void ApplyServiceStatus(string status)
    {
        Log($"Service status: '{status}'");

        if (status.StartsWith("UNLOCKING"))
        {
            ShowUnlocking();
        }
        else if (status.StartsWith("LOCKING"))
        {
            UpdateStatus("Locking...");
            UpdateConn("");
        }
        else if (status.StartsWith("CONNECTED|"))
        {
            string via = status.Substring("CONNECTED|".Length);
            UpdateStatus("Phone connected! Waiting for signal...");
            UpdateConn($"Via {via}");
        }
        else if (status == "LISTENING")
        {
            UpdateStatus("Waiting for phone connection...");
            UpdateConn("Service is ready");
        }
        else if (string.IsNullOrEmpty(status))
        {
            UpdateStatus("Waiting for service to start...");
            UpdateConn("");
        }
        else
        {
            UpdateStatus($"Status: {status}");
            UpdateConn("");
        }
    }

    /// <summary>
    /// Reads and applies the current service status to the UI.
    /// </summary>
    private void UpdateServiceStatus()
    {
        string status = ReadServiceStatus();
        ApplyServiceStatus(status);
    }

    private void ShowUnlocking()
    {
        UpdateStatus("Unlocking...");
        UpdateConn("Submitting credentials to Windows");
    }

    /// <summary>
    /// Asks LogonUI to re-enumerate tiles so OnSelectedShouldAutoLogon is
    /// called again. Returns false when the provider refused the request.
    /// </summary>
    private bool TryTriggerReload()
    {
        try
        {
            _provider.SetDefaultTile(this, true);
            return true;
        }
        catch (Exception ex)
        {
            Log("SetDefaultTile failed: " + ex.Message);
        }

        try
        {
            _provider.ReloadUserTiles();
            return true;
        }
        catch (Exception ex)
        {
            Log("ReloadUserTiles failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Called by Windows when auto-logon is triggered.
    /// </summary>
    protected override CredentialResponseBase GetCredentials()
    {
        Log("GetCredentials() called!");

        try
        {
            if (!TryReadCredentials(out string? username, out string? password))
            {
                Log("GetCredentials: Failed to read credentials!");
                return new CredentialResponseSecure { IsSuccess = false };
            }

            string domain;
            string user;

            if (username!.Contains('\\'))
            {
                var parts = username.Split('\\', 2);
                domain = parts[0];
                user = parts[1];
            }
            else
            {
                domain = Environment.MachineName;
                user = username;
            }

            Log($"GetCredentials: Submitting credentials for {domain}\\{user}");

            var securePassword = new SecureString();
            foreach (char c in password!)
            {
                securePassword.AppendChar(c);
            }

            // Reset flags and clean up so this signal can never be reused.
            s_signalTimestamp = 0;
            TryDeleteSignalFile();
            Log("Signal flags reset.");

            return new CredentialResponseSecure
            {
                IsSuccess = true,
                Domain = domain,
                Username = user,
                Password = securePassword
            };
        }
        catch (Exception ex)
        {
            Log("GetCredentials ERROR: " + ex);
            return new CredentialResponseSecure { IsSuccess = false };
        }
    }

    private static bool TryReadCredentials(out string? username, out string? password)
    {
        username = null;
        password = null;

        try
        {
            if (!File.Exists(CredFile))
                return false;

            byte[] encrypted = File.ReadAllBytes(CredFile);
            byte[] entropy = Encoding.UTF8.GetBytes("BtUnlock_v1");

            byte[] plainBytes = ProtectedData.Unprotect(
                encrypted,
                entropy,
                DataProtectionScope.LocalMachine);

            string combined = Encoding.UTF8.GetString(plainBytes);
            const string separator = "|||";
            int sepIndex = combined.IndexOf(separator);

            if (sepIndex < 0)
                return false;

            username = combined.Substring(0, sepIndex);
            password = combined.Substring(sepIndex + separator.Length);
            return true;
        }
        catch (Exception ex)
        {
            Log("TryReadCredentials ERROR: " + ex.Message);
            return false;
        }
    }

    private void UpdateStatus(string text)
    {
        if (_statusLabel != null)
        {
            try { _statusLabel.Label = text; } catch { }
        }
    }

    private void UpdateConn(string text)
    {
        if (_connLabel != null)
        {
            try { _connLabel.Label = text; } catch { }
        }
    }

    private static void TryDeleteSignalFile()
    {
        try
        {
            if (File.Exists(SignalFile))
                File.Delete(SignalFile);
        }
        catch { }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
            File.AppendAllText(LogFile, line);
        }
        catch { }
    }

    ~BtUnlockTile()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
