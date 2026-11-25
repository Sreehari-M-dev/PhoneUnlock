using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace BtUnlockManager;

/// <summary>
/// Installs, repairs and removes Phone Unlock without PowerShell.
///
/// The release build embeds the published service + credential provider as
/// <c>payload.zip</c>, so <c>PhoneUnlock.exe</c> is a single file that can put
/// the whole product back on a fresh machine. It mirrors scripts/install.ps1
/// step for step (service, ACLs, DisableCAD, firewall, credential provider,
/// Start Menu shortcut).
/// </summary>
internal static class SetupEngine
{
    private const string ProviderGuid = "E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C";
    private const string ServiceDisplayName = "Bluetooth Unlock Service";
    private const string ServiceDescription =
        "Unlocks this PC for a paired phone over Wi-Fi (phone hotspot) or Bluetooth.";

    private static readonly string[] FirewallRules =
    {
        "BtUnlock TCP 9735",
        "BtUnlock UDP 9736",
        "BtUnlock UDP 9737"
    };

    /// <summary>True when this build carries the service payload inside the exe.</summary>
    public static bool HasInstallerPayload =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Contains("payload.zip", StringComparer.OrdinalIgnoreCase);

    public static bool HasLocalPayload =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "payload"));

    public static bool CanInstall => HasInstallerPayload || HasLocalPayload ||
                                     AppInfo.FindFile("install.ps1") != null;

    // -----------------------------------------------------------------
    // Install
    // -----------------------------------------------------------------

    public static string Install(IProgress<string> log)
    {
        string? extractedPayload = null;

        try
        {
            string source = ResolvePayload(log, out extractedPayload);

            log.Report("Checking the .NET 8 runtime…");
            EnsureDotNetRuntime(log, source);

            log.Report("Stopping any running service…");
            StopService(log);
            KillStaleProcesses(log);

            log.Report($"Copying files to {AppInfo.InstallDir} …");
            Directory.CreateDirectory(AppInfo.InstallDir);
            int copied = CopyTree(source, AppInfo.InstallDir);
            log.Report($"  {copied} file(s) copied.");

            string serviceExe = Path.Combine(AppInfo.InstallDir, "BluetoothUnlockService.exe");
            string comHost = Path.Combine(AppInfo.InstallDir, "BtUnlockCredProvider.comhost.dll");

            if (!File.Exists(serviceExe))
            {
                throw new FileNotFoundException(
                    "The installer payload does not contain BluetoothUnlockService.exe.", serviceExe);
            }

            log.Report("Configuring the sign-in policy (DisableCAD = 1)…");
            SetCadPolicy();
            log.Report("  Ctrl+Alt+Del is no longer required at sign-in.");

            log.Report("Securing the data folder…");
            SecureDataFolder(log);

            log.Report("Registering the Windows service…");
            RegisterService(serviceExe, log);

            log.Report("Adding firewall rules (local subnet only)…");
            ConfigureFirewall(log);

            log.Report("Registering the credential provider…");
            RegisterCredentialProvider(comHost, log);

            log.Report("Installing the control panel and Start Menu shortcut…");
            CopyManagerToInstallDir(log);
            CreateShortcut(log);

            log.Report("Starting the service…");
            StartService(log);

            log.Report("Installation complete.");
            return "Installed successfully.";
        }
        finally
        {
            if (extractedPayload != null)
            {
                try
                {
                    Directory.Delete(extractedPayload, recursive: true);
                }
                catch
                {
                    // Temp cleanup must not hide a successful install.
                }
            }
        }
    }

    // -----------------------------------------------------------------
    // Uninstall
    // -----------------------------------------------------------------

    public static string Uninstall(IProgress<string> log, bool deleteData)
    {
        log.Report("Stopping and removing the Windows service…");
        StopService(log);
        KillStaleProcesses(log);
        DeleteService(log);

        log.Report("Unregistering the credential provider…");
        RemoveCredentialProvider(log);

        log.Report("Removing firewall rules…");
        RemoveFirewall(log);

        log.Report("Removing the Start Menu shortcut…");
        DeleteShortcut(log);

        if (deleteData)
        {
            log.Report($"Deleting {AppInfo.DataDir} (credentials, pairing secret, logs)…");
            try
            {
                if (Directory.Exists(AppInfo.DataDir))
                {
                    Directory.Delete(AppInfo.DataDir, recursive: true);
                }

                log.Report("  Data folder deleted.");
            }
            catch (Exception ex)
            {
                log.Report($"  Could not delete the data folder: {ex.Message}");
            }
        }
        else
        {
            log.Report($"Keeping {AppInfo.DataDir} (credentials and pairing secret).");
        }

        log.Report($"Removing installed files from {AppInfo.InstallDir}…");
        RemoveInstallDir(log);

        log.Report("Uninstallation complete.");
        return "Uninstalled. Sign out or restart for the lock-screen change to take effect.";
    }

    /// <summary>Removes the install folder after this process exits.</summary>
    public static void ScheduleInstallDirRemoval()
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(
            $"timeout /t 3 /nobreak >nul & rmdir /s /q \"{AppInfo.InstallDir}\"");

        try
        {
            Process.Start(start);
        }
        catch
        {
            // The caller still exits; a leftover exe is cosmetic.
        }
    }

    // -----------------------------------------------------------------
    // Policy used by the "Fix sign-in policy" button
    // -----------------------------------------------------------------

    public static void SetCadPolicy()
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System")
            ?? throw new InvalidOperationException("Could not open the system policy key.");

        // DisableCAD = 1  ->  "Interactive logon: Do not require CTRL+ALT+DEL"
        key.SetValue("DisableCAD", 1, RegistryValueKind.DWord);

        // Only ever existed to let the old service send the SAS. It is obsolete.
        try
        {
            key.DeleteValue("SoftwareSASGeneration", throwOnMissingValue: false);
        }
        catch
        {
            // Value absent or protected: ignore.
        }
    }

    // -----------------------------------------------------------------
    // Steps
    // -----------------------------------------------------------------

    private static string ResolvePayload(IProgress<string> log, out string? extracted)
    {
        extracted = null;

        using (Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        {
            if (stream != null)
            {
                extracted = Path.Combine(
                    Path.GetTempPath(),
                    "PhoneUnlock-payload-" + Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(extracted);
                log.Report("Extracting the embedded installer payload…");
                ZipFile.ExtractToDirectory(stream, extracted);
                return extracted;
            }
        }

        string local = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(local))
        {
            log.Report($"Using the payload folder: {local}");
            return local;
        }

        throw new InvalidOperationException(
            "This build has no installer payload. Download PhoneUnlock.exe from GitHub, " +
            "or run scripts\\install.ps1 from the source tree.");
    }

    private static int CopyTree(string source, string destination)
    {
        string sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        string destFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(sourceFull, destFull, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int count = 0;

        foreach (string file in Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories))
        {
            // The runtime installer is consumed by EnsureDotNetRuntime; it does
            // not belong in the install directory.
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith("dotnet-runtime-", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = Path.GetRelativePath(sourceFull, file);
            string target = Path.Combine(destFull, relative);

            string? targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            try
            {
                File.Copy(file, target, overwrite: true);
                count++;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"Could not overwrite {target}. Sign out (or restart) so LogonUI releases " +
                    $"the credential provider files, then try again. {ex.Message}", ex);
            }
        }

        return count;
    }

    private static void EnsureDotNetRuntime(IProgress<string> log, string payloadDirectory)
    {
        if (IsDotNet8RuntimeInstalled())
        {
            log.Report("  .NET 8 runtime is already installed.");
            return;
        }

        string? installer = Directory
            .EnumerateFiles(payloadDirectory, "dotnet-runtime-*.exe", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (installer == null)
        {
            throw new InvalidOperationException(
                "The .NET 8 runtime is missing and this package does not contain its installer.\n\n" +
                "Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run Install again.");
        }

        log.Report("  Installing the .NET 8 runtime (one time, silent)…");

        (int code, string output) = RunRaw(installer, "/install /quiet /norestart");

        // 0 = success, 3010 = success but a reboot is pending, 1638 = a
        // different .NET 8 build is already installed.
        if (code is not (0 or 3010 or 1638))
        {
            throw new InvalidOperationException(
                $"The .NET 8 runtime installer failed (exit code {code}).\n{output}");
        }

        log.Report("  .NET 8 runtime installed.");
    }

    private static bool IsDotNet8RuntimeInstalled()
    {
        try
        {
            string shared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.NETCore.App");

            return Directory.Exists(shared) &&
                   Directory.GetDirectories(shared)
                       .Any(path => Path.GetFileName(path)
                           .StartsWith("8.", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static void StopService(IProgress<string> log)
    {
        try
        {
            using var service = new ServiceController(AppInfo.ServiceName);

            if (service.Status != ServiceControllerStatus.Stopped)
            {
                log.Report($"  Stopping {AppInfo.ServiceName}…");
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            // Not installed yet.
        }
        catch (Exception ex)
        {
            log.Report($"  Warning while stopping the service: {ex.Message}");
        }
    }

    private static void StartService(IProgress<string> log)
    {
        try
        {
            using var service = new ServiceController(AppInfo.ServiceName);
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            log.Report("  Service is running.");
        }
        catch (Exception ex)
        {
            string tail = string.Join(Environment.NewLine,
                AppInfo.TailLines(AppInfo.ServiceLogPath, 12));

            throw new InvalidOperationException(
                $"The service was installed but did not start: {ex.Message}" +
                (string.IsNullOrWhiteSpace(tail) ? string.Empty : Environment.NewLine + tail), ex);
        }
    }

    private static void KillStaleProcesses(IProgress<string> log)
    {
        foreach (Process process in Process.GetProcessesByName("BluetoothUnlockService"))
        {
            try
            {
                log.Report($"  Stopping stray process {process.Id}…");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // It may exit on its own between GetProcessesByName and Kill.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void DeleteService(IProgress<string> log)
    {
        if (!AppInfo.IsServiceInstalled())
        {
            return;
        }

        (int code, string output) = RunRaw("sc.exe", $"delete {AppInfo.ServiceName}");
        if (code != 0 && code != 1060)
        {
            log.Report($"  Warning: sc delete returned {code}: {output}");
        }

        Thread.Sleep(1500);
    }

    private static void RegisterService(string serviceExe, IProgress<string> log)
    {
        // A previous install may exist (repair, upgrade).
        (int deleteCode, _) = RunRaw("sc.exe", $"delete {AppInfo.ServiceName}");
        if (deleteCode == 0)
        {
            Thread.Sleep(1500);
        }

        // The quoted path is required: C:\Program Files contains a space.
        (int createCode, string createOutput) = RunRaw(
            "sc.exe",
            $"create {AppInfo.ServiceName} binPath= \"{serviceExe}\" start= auto " +
            $"obj= LocalSystem DisplayName= \"{ServiceDisplayName}\"");

        if (createCode != 0)
        {
            throw new InvalidOperationException(
                $"sc create failed ({createCode}): {createOutput}");
        }

        log.Report("  Service created (auto-start, LocalSystem).");

        RunRaw("sc.exe", $"description {AppInfo.ServiceName} \"{ServiceDescription}\"");
        RunRaw(
            "sc.exe",
            $"failure {AppInfo.ServiceName} reset= 60 " +
            "actions= restart/5000/restart/10000/restart/30000");
    }

    private static void SecureDataFolder(IProgress<string> log)
    {
        Directory.CreateDirectory(AppInfo.DataDir);

        (int code, string output) = RunRaw(
            "icacls.exe",
            $"\"{AppInfo.DataDir}\" /inheritance:r " +
            "/grant:r \"SYSTEM:(OI)(CI)F\" \"Administrators:(OI)(CI)F\" \"Users:(OI)(CI)RX\" /C /Q");

        if (code == 0)
        {
            log.Report("  SYSTEM/Administrators write, Users read.");
        }
        else
        {
            log.Report($"  Warning: icacls returned {code}: {output}");
        }
    }

    private static void ConfigureFirewall(IProgress<string> log)
    {
        foreach (string rule in FirewallRules)
        {
            RunRaw("netsh.exe", $"advfirewall firewall delete rule name=\"{rule}\"");
        }

        string[][] rules =
        {
            new[] { "BtUnlock TCP 9735", "in", "TCP", "9735" },
            new[] { "BtUnlock UDP 9736", "in", "UDP", "9736" },
            new[] { "BtUnlock UDP 9737", "out", "UDP", "9737" }
        };

        foreach (string[] rule in rules)
        {
            (int code, string output) = RunRaw(
                "netsh.exe",
                $"advfirewall firewall add rule name=\"{rule[0]}\" dir={rule[1]} " +
                $"action=allow protocol={rule[2]} localport={rule[3]} " +
                "remoteip=localsubnet profile=any");

            if (code != 0)
            {
                log.Report($"  Warning: firewall rule \"{rule[0]}\" failed: {output}");
            }
        }
    }

    private static void RegisterCredentialProvider(string comHostPath, IProgress<string> log)
    {
        if (!File.Exists(comHostPath))
        {
            throw new FileNotFoundException(
                "The credential provider (BtUnlockCredProvider.comhost.dll) is missing from the payload.",
                comHostPath);
        }

        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{{{ProviderGuid}}}")
            ?? throw new InvalidOperationException("Could not register the credential provider."))
        {
            key.SetValue(null, "BtUnlockCredProvider");
        }

        using (RegistryKey clsid = Registry.LocalMachine.CreateSubKey(
            $@"SOFTWARE\Classes\CLSID\{{{ProviderGuid}}}")
            ?? throw new InvalidOperationException("Could not create the provider CLSID."))
        {
            clsid.SetValue(null, "BtUnlockCredProvider");
        }

        using (RegistryKey inproc = Registry.LocalMachine.CreateSubKey(
            $@"SOFTWARE\Classes\CLSID\{{{ProviderGuid}}}\InprocServer32")
            ?? throw new InvalidOperationException("Could not create InprocServer32."))
        {
            inproc.SetValue(null, comHostPath);
            inproc.SetValue("ThreadingModel", "Both");
        }

        log.Report("  Credential provider registered (comhost).");
    }

    private static void RemoveCredentialProvider(IProgress<string> log)
    {
        TryDeleteKey(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{{{ProviderGuid}}}",
            log);

        TryDeleteKey($@"SOFTWARE\Classes\CLSID\{{{ProviderGuid}}}", log);
    }

    private static void TryDeleteKey(string path, IProgress<string> log)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            log.Report($"  Warning: could not delete HKLM\\{path}: {ex.Message}");
        }
    }

    private static void RemoveFirewall(IProgress<string> log)
    {
        foreach (string rule in FirewallRules)
        {
            (int code, string output) = RunRaw(
                "netsh.exe", $"advfirewall firewall delete rule name=\"{rule}\"");

            if (code != 0)
            {
                log.Report($"  Note: \"{rule}\" was not present ({output}).");
            }
        }
    }

    private static void CopyManagerToInstallDir(IProgress<string> log)
    {
        string? running = Environment.ProcessPath;
        if (string.IsNullOrEmpty(running))
        {
            return;
        }

        string target = Path.Combine(AppInfo.InstallDir, AppInfo.ManagerExeName);

        try
        {
            if (string.Equals(
                    Path.GetFullPath(running),
                    Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Copy(running, target, overwrite: true);
            log.Report($"  Control panel copied to {target}.");
        }
        catch (Exception ex)
        {
            log.Report($"  Warning: could not copy the control panel: {ex.Message}");
        }
    }

    private static void CreateShortcut(IProgress<string> log)
    {
        try
        {
            string manager = Path.Combine(AppInfo.InstallDir, AppInfo.ManagerExeName);
            if (!File.Exists(manager))
            {
                return;
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                log.Report("  Warning: WScript.Shell is unavailable; skipping the shortcut.");
                return;
            }

            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string linkPath = Path.Combine(startMenu, "Phone Unlock.lnk");

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return;
            }

            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = manager;
            link.WorkingDirectory = AppInfo.InstallDir;
            link.IconLocation = manager + ",0";
            link.Description = "Phone Unlock — service, pairing and logs";
            link.Save();

            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);

            log.Report("  Start Menu shortcut created.");
        }
        catch (Exception ex)
        {
            log.Report($"  Warning: could not create the Start Menu shortcut: {ex.Message}");
        }
    }

    private static void DeleteShortcut(IProgress<string> log)
    {
        try
        {
            string linkPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                "Phone Unlock.lnk");

            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }
        }
        catch (Exception ex)
        {
            log.Report($"  Warning: could not delete the shortcut: {ex.Message}");
        }
    }

    private static void RemoveInstallDir(IProgress<string> log)
    {
        if (!Directory.Exists(AppInfo.InstallDir))
        {
            return;
        }

        if (!AppInfo.IsRunningFromInstallDir())
        {
            try
            {
                Directory.Delete(AppInfo.InstallDir, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                log.Report($"  Warning: {ex.Message}");
                return;
            }
        }

        // We are running from the install folder: delete everything except this
        // exe. The caller closes the app and a detached rmdir removes the rest.
        foreach (string file in Directory.EnumerateFiles(
                     AppInfo.InstallDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (string.Equals(
                        Path.GetFullPath(file),
                        Path.GetFullPath(Environment.ProcessPath!),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(file);
            }
            catch
            {
                // Locked file: leave it for the rmdir after exit.
            }
        }

        foreach (string directory in Directory
                     .EnumerateDirectories(AppInfo.InstallDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                Directory.Delete(directory);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    // -----------------------------------------------------------------
    // Process helpers
    // -----------------------------------------------------------------

    private static (int ExitCode, string Output) RunRaw(string fileName, string arguments)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore.
            }

            return (-1, $"{fileName} timed out.");
        }

        string output = (stdout.GetAwaiter().GetResult() + "\n" +
                         stderr.GetAwaiter().GetResult()).Trim();

        return (process.ExitCode, output);
    }
}
