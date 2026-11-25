using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BluetoothUnlockService;

/// <summary>
/// Stores and retrieves Windows credentials encrypted with DPAPI.
/// Credentials are stored in %ProgramData%\BtUnlock\credentials.dat
/// DPAPI LocalMachine scope = only decryptable on this machine.
/// </summary>
public static class CredentialStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BtUnlock");

    private static readonly string CredFile = Path.Combine(StorePath, "credentials.dat");

    // Separator between username and password in the stored blob
    private const string SEPARATOR = "|||";

    /// <summary>
    /// Stores a username and password encrypted with DPAPI.
    /// </summary>
    public static void Store(string username, string password)
    {
        Directory.CreateDirectory(StorePath);

        string combined = $"{username}{SEPARATOR}{password}";
        byte[] plainBytes = Encoding.UTF8.GetBytes(combined);

        // Encrypt with DPAPI (LocalMachine scope so the SYSTEM service can decrypt too)
        byte[] entropy = Encoding.UTF8.GetBytes("BtUnlock_v1");
        byte[] encrypted = ProtectedData.Protect(
            plainBytes,
            entropy,
            DataProtectionScope.LocalMachine);

        File.WriteAllBytes(CredFile, encrypted);
    }

    /// <summary>
    /// Retrieves the stored username and password.
    /// Returns false if no credentials are stored.
    /// </summary>
    public static bool TryRetrieve(out string? username, out string? password)
    {
        username = null;
        password = null;

        if (!File.Exists(CredFile))
            return false;

        try
        {
            byte[] encrypted = File.ReadAllBytes(CredFile);
            byte[] entropy = Encoding.UTF8.GetBytes("BtUnlock_v1");

            byte[] plainBytes = ProtectedData.Unprotect(
                encrypted,
                entropy,
                DataProtectionScope.LocalMachine);

            string combined = Encoding.UTF8.GetString(plainBytes);
            int sepIndex = combined.IndexOf(SEPARATOR);

            if (sepIndex < 0)
                return false;

            username = combined.Substring(0, sepIndex);
            password = combined.Substring(sepIndex + SEPARATOR.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if credentials are stored.
    /// </summary>
    public static bool HasCredentials() => File.Exists(CredFile);

    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string user, string domain, string password,
        int logonType, int logonProvider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Validates the stored secret against Windows without unlocking anything.
    /// Returns false when the blob is missing/undecryptable or the account
    /// rejects the password; <paramref name="error"/> carries the Win32 code.
    /// This distinguishes "wrong stored password" from "SAS failed".
    /// </summary>
    public static bool VerifyStored(out string account, out int error)
    {
        account = string.Empty;
        error = 0;

        if (!TryRetrieve(out string? username, out string? password) ||
            string.IsNullOrWhiteSpace(username) ||
            password == null)
        {
            error = 1326; // ERROR_LOGON_FAILURE
            return false;
        }

        string domain;
        string user;

        if (username.Contains('\\'))
        {
            string[] parts = username.Split('\\', 2);
            domain = parts[0];
            user = parts[1];
        }
        else
        {
            domain = Environment.MachineName;
            user = username;
        }

        account = $"{domain}\\{user}";

        if (LogonUser(
                user, domain, password,
                Logon32LogonInteractive, Logon32ProviderDefault,
                out IntPtr token))
        {
            CloseHandle(token);
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>
    /// Deletes stored credentials.
    /// </summary>
    public static void Delete()
    {
        if (File.Exists(CredFile))
            File.Delete(CredFile);
    }
}
