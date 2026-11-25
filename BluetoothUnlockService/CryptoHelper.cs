using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BluetoothUnlockService;

/// <summary>
/// Validates commands from the phone.
///
/// Two wire formats are accepted:
///
///   v2  "v2." + Base64(nonce[12] || ciphertext || tag[16])
///       AES-256-GCM with the per-device secret from pairing.json. Authenticated
///       and nonce-replay protected. This is what a freshly paired phone uses.
///
///   v1  Base64(AES-128-CBC(PKCS7, "COMMAND|timestamp"))
///       The original format with the hardcoded key. Still accepted so an
///       already-paired phone keeps working until it is re-paired; it has no
///       integrity check, so it is a migration path, not a destination.
///
/// Both formats are timestamp-checked (±60 s) and v2 additionally rejects a
/// nonce it has seen before.
/// </summary>
public static class CryptoHelper
{
    // ---- v1 (legacy) ----------------------------------------------------
    private static readonly byte[] LegacyKey = Encoding.UTF8.GetBytes("1234567890123456");
    private static readonly byte[] LegacyIv = Encoding.UTF8.GetBytes("1234567890123456");

    // ---- v2 -------------------------------------------------------------
    private const string V2Prefix = "v2.";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private const string UnlockPrefix = "UNLOCK_REQUEST|";
    private const string LockPrefix = "LOCK_REQUEST|";
    private const string PairPrefix = "PAIR_CONFIRM|";

    private const long MaxAgeMs = 60_000;
    private static readonly TimeSpan NonceMemory = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, long> SeenNonces = new();

    public enum CommandType { Unknown, Unlock, Lock, Pair }

    /// <summary>Details carried by a PAIR_CONFIRM frame.</summary>
    public sealed record PairingConfirmation(string PhoneId, string PhoneName);

    public static bool TryValidateUnlockRequest(string payload, out string? errorReason)
        => TryValidateCommand(payload, out _, out _, out errorReason);

    public static bool TryValidateCommand(
        string payload, out CommandType command, out string? errorReason)
        => TryValidateCommand(payload, out command, out _, out errorReason);

    public static bool TryValidateCommand(
        string payload,
        out CommandType command,
        out PairingConfirmation? pairing,
        out string? errorReason)
    {
        command = CommandType.Unknown;
        pairing = null;
        errorReason = null;

        string trimmed = payload.Trim();

        if (!TryDecrypt(trimmed, out string plaintext, out errorReason))
        {
            return false;
        }

        if (plaintext.StartsWith(UnlockPrefix, StringComparison.Ordinal))
        {
            command = CommandType.Unlock;
            return CheckTimestamp(plaintext[UnlockPrefix.Length..], out errorReason);
        }

        if (plaintext.StartsWith(LockPrefix, StringComparison.Ordinal))
        {
            command = CommandType.Lock;
            return CheckTimestamp(plaintext[LockPrefix.Length..], out errorReason);
        }

        if (plaintext.StartsWith(PairPrefix, StringComparison.Ordinal))
        {
            // PAIR_CONFIRM|<timestamp>|<phoneId>|<phoneName>
            string[] parts = plaintext.Split('|', 4);

            if (parts.Length < 4)
            {
                errorReason = "Malformed PAIR_CONFIRM";
                return false;
            }

            if (!CheckTimestamp(parts[1], out errorReason))
            {
                return false;
            }

            string phoneId = Sanitise(parts[2], 64);
            string phoneName = Sanitise(parts[3], 64);

            if (phoneId.Length == 0 || phoneName.Length == 0)
            {
                errorReason = "PAIR_CONFIRM is missing the phone identity";
                return false;
            }

            command = CommandType.Pair;
            pairing = new PairingConfirmation(phoneId, phoneName);
            return true;
        }

        errorReason = "Unknown command";
        return false;
    }

    /// <summary>
    /// Decrypts either format. v2 is preferred when a secret exists.
    /// </summary>
    private static bool TryDecrypt(string payload, out string plaintext, out string? errorReason)
    {
        plaintext = string.Empty;

        if (payload.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            if (!PairingStore.TryGetSecret(out byte[] secret))
            {
                errorReason = "v2 payload received but this PC has no pairing secret";
                return false;
            }

            return TryDecryptV2(payload[V2Prefix.Length..], secret, out plaintext, out errorReason);
        }

        return TryDecryptLegacy(payload, out plaintext, out errorReason);
    }

    private static bool TryDecryptV2(
        string base64, byte[] secret, out string plaintext, out string? errorReason)
    {
        plaintext = string.Empty;
        errorReason = null;

        byte[] frame;
        try
        {
            frame = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            errorReason = "Invalid Base64 encoding";
            return false;
        }

        // nonce[12] || ciphertext || tag[16]
        if (frame.Length < NonceBytes + TagBytes)
        {
            errorReason = "v2 frame is too short";
            return false;
        }

        ReadOnlySpan<byte> nonce = frame.AsSpan(0, NonceBytes);
        ReadOnlySpan<byte> ciphertext = frame.AsSpan(NonceBytes, frame.Length - NonceBytes - TagBytes);
        ReadOnlySpan<byte> tag = frame.AsSpan(frame.Length - TagBytes, TagBytes);

        // Replay guard: a GCM nonce must never repeat under the same key.
        string nonceKey = Convert.ToBase64String(nonce);
        PruneNonces();

        if (!SeenNonces.TryAdd(nonceKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            errorReason = "Replay detected (nonce already used)";
            return false;
        }

        try
        {
            byte[] buffer = new byte[ciphertext.Length];

            using (var aes = new AesGcm(secret, TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, buffer);
            }

            plaintext = Encoding.UTF8.GetString(buffer);
            return true;
        }
        catch (CryptographicException)
        {
            // Wrong secret or tampered frame. Forget the nonce so a legitimate
            // retry is not punished by our own bookkeeping.
            SeenNonces.TryRemove(nonceKey, out _);
            errorReason = "Decryption failed (wrong secret or tampered payload)";
            return false;
        }
    }

    private static bool TryDecryptLegacy(
        string base64, out string plaintext, out string? errorReason)
    {
        plaintext = string.Empty;
        errorReason = null;

        byte[] ciphertext;
        try
        {
            ciphertext = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            errorReason = "Invalid Base64 encoding";
            return false;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = LegacyKey;
            aes.IV = LegacyIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            plaintext = Encoding.UTF8.GetString(decrypted);
            return true;
        }
        catch (CryptographicException ex)
        {
            errorReason = $"Decryption failed: {ex.Message}";
            return false;
        }
    }

    private static bool CheckTimestamp(string raw, out string? errorReason)
    {
        errorReason = null;

        if (!long.TryParse(raw, out long timestampMs))
        {
            errorReason = $"Invalid timestamp: '{raw}'";
            return false;
        }

        long ageMs = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestampMs);
        if (ageMs > MaxAgeMs)
        {
            errorReason = $"Replay detected. Message age: {ageMs}ms (max: {MaxAgeMs}ms)";
            return false;
        }

        return true;
    }

    private static void PruneNonces()
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(NonceMemory).ToUnixTimeMilliseconds();

        foreach (KeyValuePair<string, long> entry in SeenNonces)
        {
            if (entry.Value < cutoff)
            {
                SeenNonces.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string Sanitise(string value, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));

        foreach (char c in value)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')')
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Builds a v2 payload for the self-test. Uses the real pairing secret when
    /// one exists, otherwise reports that the test can only cover v1.
    /// </summary>
    public static string CreateTestPayload()
    {
        if (PairingStore.TryGetSecret(out byte[] secret))
        {
            return CreateV2Payload("UNLOCK_REQUEST", secret);
        }

        string legacyPlaintext = $"UNLOCK_REQUEST|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        using var aes = Aes.Create();
        aes.Key = LegacyKey;
        aes.IV = LegacyIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        byte[] plain = Encoding.UTF8.GetBytes(legacyPlaintext);
        using var encryptor = aes.CreateEncryptor();
        byte[] encrypted = encryptor.TransformFinalBlock(plain, 0, plain.Length);

        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Encrypts a command with AES-256-GCM using the pairing secret.</summary>
    public static string CreateV2Payload(string command, byte[] secret)
    {
        string plaintext = $"{command}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] ciphertext = new byte[plain.Length];
        byte[] tag = new byte[TagBytes];

        using (var aes = new AesGcm(secret, TagBytes))
        {
            aes.Encrypt(nonce, plain, ciphertext, tag);
        }

        byte[] frame = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(frame, 0);
        ciphertext.CopyTo(frame, NonceBytes);
        tag.CopyTo(frame, NonceBytes + ciphertext.Length);

        return V2Prefix + Convert.ToBase64String(frame);
    }
}
