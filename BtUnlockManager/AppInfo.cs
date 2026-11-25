using System.Net.NetworkInformation;
using System.ServiceProcess;
using BluetoothUnlockService;

namespace BtUnlockManager;

/// <summary>
/// Small helpers shared by the control panel: where things live, what the
/// service is doing, and safe reads of the data folder.
/// </summary>
internal static class AppInfo
{
    public const string ServiceName = "BluetoothUnlockService";
    public const string ProductName = "Phone Unlock";
    public const string InstallDir = @"C:\Program Files\BtUnlock";
    public const string ManagerExeName = "PhoneUnlock.exe";
    public const int TcpPort = 9735;

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BtUnlock");

    public static string ServiceLogPath => FileLog.FilePath;

    public static string CredProviderLogPath => Path.Combine(DataDir, "credprovider.log");

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Null when the service is not installed.</summary>
    public static ServiceControllerStatus? ServiceStatus()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // not installed
        }
        catch
        {
            return null;
        }
    }

    public static bool IsServiceInstalled() => ServiceStatus() != null;

    public static bool IsPortListening(int port = TcpPort)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Locates BluetoothUnlockService.exe (installed, release payload, or dev tree).</summary>
    public static string? FindServiceExe()
    {
        string[] candidates =
        {
            Path.Combine(InstallDir, "BluetoothUnlockService.exe"),
            Path.Combine(AppContext.BaseDirectory, "BluetoothUnlockService.exe"),
            Path.Combine(AppContext.BaseDirectory, "payload", "BluetoothUnlockService.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "payload", "BluetoothUnlockService.exe"))
        };

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Bad path: try the next one.
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a script either next to the exe, in a sibling scripts/ folder, or
    /// in the install directory (also walks a few parents for dev builds).
    /// </summary>
    public static string? FindFile(string fileName)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, InstallDir })
        {
            var directory = new DirectoryInfo(start);

            for (int depth = 0; depth < 7 && directory != null; depth++, directory = directory.Parent)
            {
                string direct = Path.Combine(directory.FullName, fileName);
                if (File.Exists(direct))
                {
                    return direct;
                }

                string nested = Path.Combine(directory.FullName, "scripts", fileName);
                if (File.Exists(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>Reads the radio address the running service logged at startup.</summary>
    public static string? ReadLastBluetoothAddress()
    {
        const string marker = "BT: local radio address ";

        try
        {
            if (!File.Exists(ServiceLogPath))
            {
                return null;
            }

            foreach (string line in File.ReadLines(ServiceLogPath).Reverse())
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
            // Diagnostics may not fail because the log is busy.
        }

        return null;
    }

    /// <summary>Reads a file another process (the service) may be writing.</summary>
    public static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Last <paramref name="count"/> lines of a file, safely.</summary>
    public static IReadOnlyList<string> TailLines(string path, int count)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var buffer = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                buffer.Enqueue(line);
                if (buffer.Count > count)
                {
                    buffer.Dequeue();
                }
            }

            return buffer.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static bool IsRunningFromInstallDir()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(exe);
            string install = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
            return full.StartsWith(install, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
