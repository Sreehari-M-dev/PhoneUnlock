namespace BluetoothUnlockService;

/// <summary>
/// IPC signaling between the Bluetooth service and the Credential Provider.
/// Uses BOTH a signal file AND a named event for maximum reliability across sessions.
/// </summary>
public static class UnlockSignaler
{
    public const string EVENT_NAME = @"Global\BtUnlockEvent";

    private static readonly string SignalFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BtUnlock", "unlock.signal");

    /// <summary>
    /// Signals the credential provider to perform the unlock.
    /// Creates a signal file (primary) and sets a named event (fallback).
    /// </summary>
    public static void Signal()
    {
        // Method 1: Create a signal file (most reliable across sessions)
        try
        {
            string dir = Path.GetDirectoryName(SignalFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SignalFile, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            Console.WriteLine($"[UnlockSignaler] Signal file created: {SignalFile}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UnlockSignaler] Failed to create signal file: {ex.Message}");
        }

        // Method 2: Named event (may work if credential provider is in same session)
        try
        {
            using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EVENT_NAME);
            evt.Set();
            Console.WriteLine("[UnlockSignaler] Named event signaled.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UnlockSignaler] Failed to signal event: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears any pending signal file. Called when the PC successfully unlocks
    /// to prevent stale signals from auto-triggering on next lock.
    /// </summary>
    public static void ClearSignal()
    {
        try
        {
            if (File.Exists(SignalFile))
            {
                File.Delete(SignalFile);
                Console.WriteLine("[UnlockSignaler] Signal file cleared.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UnlockSignaler] Failed to clear signal: {ex.Message}");
        }
    }
}
