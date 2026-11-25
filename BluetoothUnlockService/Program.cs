using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using BluetoothUnlockService;

// =====================================================
// Bluetooth Unlock Service — Entry Point
// =====================================================
// Usage:
//   BluetoothUnlockService.exe            → Run as Windows Service
//   BluetoothUnlockService.exe --setup    → Configure credentials
//   BluetoothUnlockService.exe --test     → Test encryption/decryption
//   BluetoothUnlockService.exe --console  → Run in console mode (for debugging)
//   BluetoothUnlockService.exe --diagnose → Print environment diagnostics
//   BluetoothUnlockService.exe --verify   → Check the stored Windows password
//   BluetoothUnlockService.exe --pair     → Pair a phone by scanning a QR code
// =====================================================

// Handle command-line modes
if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "--setup":
            RunSetup();
            return;

        case "--test":
            RunTest();
            return;

        case "--diagnose":
            RunDiagnose();
            return;

        case "--verify":
            RunVerify();
            return;

        case "--wake":
            // Internal helper, launched in the user's session by the service.
            MonitorWaker.WakeInCurrentSession();
            return;

        case "--lock":
            // Internal helper, launched in the user's session by the service.
            // Session 0 cannot lock an interactive desktop, so this runs there.
            SessionLocker.TryLockHere(out _);
            return;

        case "--pair":
            // --json / --compact print the payload instead of the QR code.
            RunPair(args.Contains("--json") ? "json"
                : args.Contains("--compact") ? "compact"
                : null);
            return;

        case "--console":
            // Fall through to run as console app (for debugging)
            break;

        default:
            Console.WriteLine("Bluetooth Unlock Service");
            Console.WriteLine("========================");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  BluetoothUnlockService.exe            Run as Windows Service");
            Console.WriteLine("  BluetoothUnlockService.exe --setup    Configure Windows credentials");
            Console.WriteLine("  BluetoothUnlockService.exe --test     Test encryption/decryption");
            Console.WriteLine("  BluetoothUnlockService.exe --console  Run in console mode (debugging)");
            Console.WriteLine("  BluetoothUnlockService.exe --diagnose Print environment diagnostics");
            Console.WriteLine("  BluetoothUnlockService.exe --verify   Check the stored Windows password");
            Console.WriteLine("  BluetoothUnlockService.exe --pair     Pair a phone by scanning a QR code");
            Console.WriteLine("  BluetoothUnlockService.exe --pair --compact   Print the QR payload text only");
            Console.WriteLine("  BluetoothUnlockService.exe --pair --json      Print the verbose JSON payload");
            return;
    }
}

// Only one copy may run at a time. A stale console copy holding TCP 9735 is
// exactly what stopped the installed service from starting before.
Mutex? singleInstance = null;
if (!args.Contains("--console"))
{
    singleInstance = new Mutex(true, @"Global\BtUnlockServiceMutex", out bool createdNew);
    if (!createdNew)
    {
        FileLog.Write("Another instance is already running. Exiting.");
        Console.Error.WriteLine("Another BluetoothUnlockService instance is already running.");
        return;
    }
}

// Build and run the host (either as service or console)
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<Worker>();

if (!args.Contains("--console"))
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "BluetoothUnlockService";
    });
}

var host = builder.Build();
host.Run();

singleInstance?.Dispose();


// =====================================================
// Setup Mode — Store Windows credentials
// =====================================================
static void RunSetup()
{
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║  Bluetooth Unlock — Credential Setup ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.WriteLine();

    if (CredentialStore.HasCredentials())
    {
        Console.Write("Credentials already stored. Overwrite? (y/N): ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled.");
            return;
        }
    }

    // Get username
    Console.Write("Windows username (e.g., DESKTOP-ABC\\John or John): ");
    string? username = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(username))
    {
        Console.WriteLine("Error: Username cannot be empty.");
        return;
    }

    // Get password/PIN
    Console.Write("Windows password or PIN: ");
    string password = ReadPasswordMasked();
    Console.WriteLine();

    if (string.IsNullOrEmpty(password))
    {
        Console.WriteLine("Error: Password cannot be empty.");
        return;
    }

    // Confirm
    Console.Write("Confirm password/PIN: ");
    string confirm = ReadPasswordMasked();
    Console.WriteLine();

    if (password != confirm)
    {
        Console.WriteLine("Error: Passwords do not match.");
        return;
    }

    try
    {
        CredentialStore.Store(username, password);
        Console.WriteLine();
        Console.WriteLine("✓ Credentials stored successfully (DPAPI encrypted).");
        Console.WriteLine($"  Storage: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BtUnlock", "credentials.dat")}");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine("  1. Install the Credential Provider: Run scripts\\install.ps1 as Admin");
        Console.WriteLine("  2. Start the service: sc start BluetoothUnlockService");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error storing credentials: {ex.Message}");
        Console.WriteLine("Try running this command as Administrator.");
    }
}

// =====================================================
// Test Mode — Verify encryption/decryption works
// =====================================================
static void RunTest()
{
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║  Bluetooth Unlock — Crypto Test      ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.WriteLine();

    // Test encryption
    string payload = CryptoHelper.CreateTestPayload();
    Console.WriteLine($"Generated payload: {payload}");
    Console.WriteLine($"Payload length: {payload.Length} characters");
    Console.WriteLine();

    // Test decryption
    if (CryptoHelper.TryValidateUnlockRequest(payload, out string? errorReason))
    {
        Console.WriteLine("✓ Decryption and validation: PASSED");
    }
    else
    {
        Console.WriteLine($"✗ Validation failed: {errorReason}");
    }

    // Test credential store
    Console.WriteLine();
    if (CredentialStore.HasCredentials())
    {
        if (CredentialStore.TryRetrieve(out string? user, out string? pass))
        {
            Console.WriteLine($"✓ Credential store: OK (user: {user}, password: {"*".PadRight(pass?.Length ?? 0, '*')})");
        }
        else
        {
            Console.WriteLine("✗ Credential store: Could not decrypt stored credentials.");
        }
    }
    else
    {
        Console.WriteLine("⚠ Credential store: No credentials stored. Run --setup first.");
    }

    // Test signal
    Console.WriteLine();
    Console.Write("Send unlock signal now? (y/N): ");
    if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
    {
        MonitorWaker.Wake();
        Thread.Sleep(500);
        UnlockSignaler.Signal();
        Console.WriteLine("✓ Unlock signal sent!");
    }
}

// =====================================================
// Verify Mode — Validate the stored Windows password
// =====================================================
static void RunVerify()
{
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║  Bluetooth Unlock — Credential Check ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.WriteLine();

    if (!CredentialStore.HasCredentials())
    {
        Console.WriteLine("✗ No credentials stored. Run --setup first.");
        return;
    }

    if (CredentialStore.VerifyStored(out string account, out int error))
    {
        Console.WriteLine($"✓ Windows accepted the stored password for {account}.");
        Console.WriteLine("  If unlock still fails, the problem is the SAS/tile path, not the password.");
    }
    else
    {
        var message = new System.ComponentModel.Win32Exception(error).Message;
        Console.WriteLine(
            $"✗ Windows rejected the stored secret for {account} " +
            $"(error {error}: {message}).");
        Console.WriteLine();
        Console.WriteLine("Re-run --setup and enter the Windows ACCOUNT PASSWORD.");
        Console.WriteLine("A Windows Hello PIN is not a password and will be rejected.");
    }
}

// =====================================================
// Pair Mode — show a QR code the phone can scan
// =====================================================
static void RunPair(string? payloadOnly)
{
    QrRenderer.PrepareConsole();

    Console.WriteLine("╔══════════════════════════════════════════╗");
    Console.WriteLine("║  Bluetooth Unlock — Pair a phone         ║");
    Console.WriteLine("╚══════════════════════════════════════════╝");
    Console.WriteLine();

    // No elevation check on purpose: the ProgramData ACL is the real boundary.
    // If this account cannot read the config, the try/catch below explains why.
    PairingStore.PairingConfig config;

    try
    {
        config = PairingStore.Load();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ Could not read {PairingStore.ConfigPath}: {ex.Message}");
        Console.WriteLine("  That folder holds the shared secret, so only SYSTEM and");
        Console.WriteLine("  Administrators can read it. Re-run from an elevated prompt.");
        return;
    }

    string? ip = NetworkInfo.PrimaryIPv4();
    if (ip == null)
    {
        Console.WriteLine("✗ No usable IPv4 address (Wi-Fi off and no hotspot?).");
        Console.WriteLine("  Turn on the phone hotspot, then run --pair again.");
        return;
    }

    string? bluetoothMac = ReadLastBluetoothAddress();

    if (LogonPolicy.IsCadRequired())
    {
        Console.WriteLine("⚠ Ctrl+Alt+Del is still required at sign-in:");
        Console.WriteLine($"  {LogonPolicy.Describe()}");
        Console.WriteLine("  The unlock tile will not appear until that is disabled:");
        Console.WriteLine("  powershell -File .\\scripts\\set-cad-requirement.ps1 -Mode not-required");
        Console.WriteLine();
    }

    string payload;
    try
    {
        payload = payloadOnly == "json"
            ? PairingStore.BuildPayloadJson(ip, bluetoothMac)
            : PairingStore.BuildCompactPayload(ip, bluetoothMac);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ Could not build the pairing payload: {ex.Message}");
        return;
    }

    if (payloadOnly != null)
    {
        Console.WriteLine(payload);
        return;
    }

    // A stale result from an earlier attempt would report success instantly.
    // Deleting may fail (non-admins cannot), so the poll loop also checks the
    // file's timestamp against the moment this run started.
    DateTime startedUtc = DateTime.UtcNow;
    try
    {
        File.Delete(PairingStore.ResultFile);
    }
    catch
    {
        // Ignore; the timestamp check below covers it.
    }

    Console.WriteLine("Scan this with the Android app (Pair New PC):");
    Console.WriteLine();

    QrRenderer.Print(payload);

    Console.WriteLine();
    Console.WriteLine("  Device name:   " + config.Name);
    Console.WriteLine("  Operating sys: Windows");
    Console.WriteLine("  Address:       " + ip + ":" + config.Port);
    Console.WriteLine("  Bluetooth:     " + (bluetoothMac ?? "(no radio / service not running)"));
    Console.WriteLine("  Device ID:     " + config.Id);
    Console.WriteLine();
    Console.WriteLine("  Waiting up to 120 s for the phone to confirm...");
    Console.WriteLine("  (Ctrl+C to cancel; Ctrl+- shrinks the font if the code is too wide)");
    Console.WriteLine();

    DateTime deadline = DateTime.UtcNow.AddSeconds(120);

    while (DateTime.UtcNow < deadline)
    {
        try
        {
            if (File.Exists(PairingStore.ResultFile) &&
                File.GetLastWriteTimeUtc(PairingStore.ResultFile) >= startedUtc)
            {
                string result = File.ReadAllText(PairingStore.ResultFile).Trim();
                string[] parts = result.Split('|');

                Console.WriteLine();
                Console.WriteLine(
                    "✓ Paired successfully with " +
                    (parts.Length > 1 ? parts[1] : "your phone") + ".");

                try
                {
                    File.Delete(PairingStore.ResultFile);
                }
                catch
                {
                    // Ignore.
                }

                return;
            }
        }
        catch
        {
            // File is being written; try again next tick.
        }

        Thread.Sleep(400);
    }

    Console.WriteLine();
    Console.WriteLine("✗ Timed out — no confirmation from the phone.");
    Console.WriteLine("  Check that the phone is on the same network as " + ip + ".");
}

// =====================================================
// Diagnose Mode — Print everything needed to debug
// =====================================================
static void RunDiagnose()
{
    Console.WriteLine("╔══════════════════════════════════════════════╗");
    Console.WriteLine("║  Bluetooth Unlock — Diagnostics              ║");
    Console.WriteLine("╚══════════════════════════════════════════════╝");
    Console.WriteLine();

    try
    {
        var identity = WindowsIdentity.GetCurrent();
        Console.WriteLine($"User:                 {identity.Name}");
        Console.WriteLine($"Running as SYSTEM:    {identity.IsSystem}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"User:                 (unknown: {ex.Message})");
    }

    Console.WriteLine($"Credentials stored:   {CredentialStore.HasCredentials()}");
    Console.WriteLine($"Pairing:              {PairingStore.Describe()}");

    int? disableCad = LogonPolicy.DisableCad();
    string cadState = disableCad switch
    {
        0 => "REQUIRED (unlock will fail — run set-cad-requirement.ps1 -Mode not-required)",
        1 => "not required (correct)",
        _ => "not configured (Windows default = required)"
    };
    Console.WriteLine($"Require Ctrl+Alt+Del: {cadState}");

    if (SessionState.TryGetState(out bool isLocked, out string user, out int flags))
    {
        Console.WriteLine(
            $"Session state:        {(isLocked ? "LOCKED" : "UNLOCKED")} " +
            $"(user: {user}, flags={flags})");
    }
    else
    {
        Console.WriteLine($"Session state:        unknown (flags=0x{flags:X8})");
    }

    // Is an instance already listening? If so it owns TCP 9735 and has already
    // published the Bluetooth SDP record. Starting our own RFCOMM listener here
    // would publish a duplicate record and can send the phone to the wrong
    // channel, so only probe when nothing else is running.
    bool serviceRunning;
    try
    {
        var probe = new TcpListener(IPAddress.Any, WifiUnlockListener.TcpPort);
        probe.Start();
        probe.Stop();
        serviceRunning = false;
        Console.WriteLine($"TCP {WifiUnlockListener.TcpPort}:              free (no instance listening)");
    }
    catch
    {
        serviceRunning = true;
        Console.WriteLine($"TCP {WifiUnlockListener.TcpPort}:              in use — a service instance is listening (expected)");
    }

    bool isSystem;
    try
    {
        isSystem = WindowsIdentity.GetCurrent().IsSystem;
    }
    catch
    {
        isSystem = false;
    }

    if (isSystem)
    {
        Console.WriteLine(
            Privileges.EnablePrivilege("SeTcbPrivilege", out int privilegeError)
                ? "SeTcbPrivilege:       enabled (needed only for --wake)"
                : $"SeTcbPrivilege:       NOT available (error {privilegeError})");
    }
    else
    {
        Console.WriteLine("SeTcbPrivilege:       n/a in this shell (service runs as SYSTEM)");
    }

    if (serviceRunning)
    {
        string? address = ReadLastBluetoothAddress();
        Console.WriteLine(
            "Bluetooth RFCOMM:     published by the running service" +
            (address != null ? $" ({address})" : ""));
    }
    else
    {
        using var bluetooth = new BluetoothUnlockListener(new UnlockCoordinator());
        Console.WriteLine(
            bluetooth.Start()
                ? "Bluetooth RFCOMM:     OK"
                : "Bluetooth RFCOMM:     failed (see service.log)");

        if (BluetoothUnlockListener.LocalAddress != null)
        {
            Console.WriteLine($"Bluetooth address:    {BluetoothUnlockListener.LocalAddress}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Log file:             {FileLog.FilePath}");
    Console.WriteLine("Done.");
}

/// <summary>
/// Reads the radio address the running service logged, so --diagnose can show
/// it without opening its own RFCOMM socket.
/// </summary>
static string? ReadLastBluetoothAddress()
{
    try
    {
        if (!File.Exists(FileLog.FilePath))
        {
            return null;
        }

        const string marker = "BT: local radio address ";
        foreach (string line in File.ReadLines(FileLog.FilePath).Reverse())
        {
            int index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                return line[(index + marker.Length)..].Trim().TrimEnd('.');
            }
        }
    }
    catch
    {
        // Diagnostics must never fail because the log is busy.
    }

    return null;
}

// =====================================================
// Helper: Read password with masking
// =====================================================
static string ReadPasswordMasked()
{
    var password = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
        {
            password.Remove(password.Length - 1, 1);
            Console.Write("\b \b");
        }
        else if (key.Key != ConsoleKey.Backspace)
        {
            password.Append(key.KeyChar);
            Console.Write("*");
        }
    }
    return password.ToString();
}
