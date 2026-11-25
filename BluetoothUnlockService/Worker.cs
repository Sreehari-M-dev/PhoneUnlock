using System.Security.Principal;

namespace BluetoothUnlockService;

/// <summary>
/// Background worker that runs both transports:
///   • Wi-Fi  (TCP 9735 / UDP 9736 + 9737) — used when the phone hotspot is on
///   • Bluetooth RFCOMM — used when Wi-Fi is unavailable
///
/// Startup deliberately yields to the SCM first. The old code did file/DNS work
/// synchronously before its first await, which is what produced the
/// "service did not respond in a timely fashion" errors.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILogger<WifiUnlockListener> _wifiLogger;

    public Worker(ILogger<Worker> logger, ILogger<WifiUnlockListener> wifiLogger)
    {
        _logger = logger;
        _wifiLogger = wifiLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Report "started" to the Service Control Manager immediately.
        await Task.Yield();

        FileLog.Write("=== Bluetooth Unlock service starting ===");
        FileLog.Write(
            $"Running as SYSTEM={IsSystem()}, " +
            $"credentials={CredentialStore.HasCredentials()}, " +
            $"pairing={PairingStore.Describe()}");

        // The unlock depends on Windows showing the sign-in UI immediately.
        // With DisableCAD = 0 the tile never appears without Ctrl+Alt+Del, and
        // we no longer send the SAS, so say so loudly at startup.
        FileLog.Write($"Logon policy: {LogonPolicy.Describe()}");

        if (LogonPolicy.IsCadRequired())
        {
            FileLog.Write(
                "WARNING: Ctrl+Alt+Del is required, so automatic unlock cannot work. " +
                "Run scripts/set-cad-requirement.ps1 -Mode not-required.");
        }

        if (!CredentialStore.HasCredentials())
        {
            FileLog.Write(
                "WARNING: no stored credentials. Run " +
                "\"BluetoothUnlockService.exe --setup\" as Administrator.");
        }

        var coordinator = new UnlockCoordinator();
        coordinator.WriteStatus("LISTENING");

        _ = Task.Run(() => RunWifiAsync(coordinator, stoppingToken), stoppingToken);
        _ = Task.Run(() => RunBluetoothAsync(coordinator, stoppingToken), stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        FileLog.Write("=== Bluetooth Unlock service stopped ===");
    }

    private async Task RunWifiAsync(UnlockCoordinator coordinator, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var listener = new WifiUnlockListener(coordinator, _wifiLogger);

            try
            {
                await listener.RunAsync(token);
                break; // cancelled cleanly
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLog.Write($"WiFi: listener failed: {ex.Message}. Retrying in 10s.");

                try
                {
                    await Task.Delay(10_000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunBluetoothAsync(UnlockCoordinator coordinator, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var listener = new BluetoothUnlockListener(coordinator);

            if (!listener.Start())
            {
                // Radio off, driver not ready, etc. Try again shortly.
                try
                {
                    await Task.Delay(15_000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // Keep the listener alive until it reports a fatal error or shutdown.
            while (listener.IsRunning && !token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2_000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!token.IsCancellationRequested)
            {
                FileLog.Write("BT: listener stopped unexpectedly. Restarting in 10s.");
                try
                {
                    await Task.Delay(10_000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static bool IsSystem()
    {
        try
        {
            return WindowsIdentity.GetCurrent().IsSystem;
        }
        catch
        {
            return false;
        }
    }
}
