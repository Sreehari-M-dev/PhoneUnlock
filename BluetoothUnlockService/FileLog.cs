using System.Text;

namespace BluetoothUnlockService;

/// <summary>
/// Minimal thread-safe rolling log in %ProgramData%\BtUnlock\service.log.
///
/// A Windows Service has no console, so without this there is no way to see why
/// an unlock failed. Every important step in the service writes here.
/// </summary>
public static class FileLog
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BtUnlock");

    public static readonly string FilePath = Path.Combine(Dir, "service.log");

    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                RollIfNeeded();
                File.AppendAllText(
                    FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the service down.
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(FilePath, FilePath + ".1", overwrite: true);
            }
        }
        catch
        {
            // Ignore — the next write will try again.
        }
    }
}
