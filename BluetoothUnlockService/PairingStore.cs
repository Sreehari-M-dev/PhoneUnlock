using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BluetoothUnlockService;

/// <summary>
/// Per-machine pairing identity: a stable device id plus a 256-bit shared
/// secret that is delivered to the phone once, inside the <c>--pair</c> QR
/// code.
///
/// The secret replaces the old hardcoded AES key (<c>"1234567890123456"</c>),
/// which was identical on every install and readable from the APK. Messages
/// are now AES-256-GCM with a random nonce, so a captured frame cannot be
/// replayed or forged.
///
/// Stored in <c>%ProgramData%\BtUnlock\pairing.json</c>; the folder is
/// SYSTEM/Administrators only, so only an elevated process can read it.
/// </summary>
public static class PairingStore
{
    public const int ProtocolVersion = 1;
    public const int SecretBytes = 32;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BtUnlock");

    public static readonly string ConfigPath = Path.Combine(DataDir, "pairing.json");

    /// <summary>Written by <c>--pair</c> to ask the running service for a QR payload.</summary>
    public static readonly string RequestFile = Path.Combine(DataDir, "pair.request");

    /// <summary>Written by the service when a phone confirms a pairing.</summary>
    public static readonly string ResultFile = Path.Combine(DataDir, "pair.result");

    private static readonly object Gate = new();
    private static PairingConfig? _cached;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed class PairingConfig
    {
        public int V { get; set; } = ProtocolVersion;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Os { get; set; } = "windows";
        public int Port { get; set; } = WifiUnlockListener.TcpPort;
        public string Secret { get; set; } = string.Empty;
        public List<PairedPhone> Paired { get; set; } = new();
    }

    public sealed class PairedPhone
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PairedAt { get; set; } = string.Empty;
    }

    /// <summary>
    /// Loads the config, creating it (with a fresh device id and secret) when
    /// it does not exist yet.
    /// </summary>
    public static PairingConfig Load()
    {
        lock (Gate)
        {
            if (_cached != null)
            {
                return _cached;
            }

            PairingConfig config = ReadFromDisk() ?? CreateNew();
            _cached = config;
            return config;
        }
    }

    /// <summary>Forces the next <see cref="Load"/> to re-read the file.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
        }
    }

    /// <summary>
    /// Returns the shared secret, or false when it is missing/corrupt. Callers
    /// then fall back to the legacy protocol so an existing phone keeps working.
    /// </summary>
    public static bool TryGetSecret(out byte[] secret)
    {
        secret = Array.Empty<byte>();

        try
        {
            string encoded = Load().Secret;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return false;
            }

            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length != SecretBytes)
            {
                return false;
            }

            secret = bytes;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records (or refreshes) a phone that confirmed the pairing.</summary>
    public static void RecordPairedPhone(string phoneId, string phoneName)
    {
        lock (Gate)
        {
            PairingConfig config = Load();
            PairedPhone? existing = config.Paired.FirstOrDefault(
                p => string.Equals(p.Id, phoneId, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                config.Paired.Add(new PairedPhone
                {
                    Id = phoneId,
                    Name = phoneName,
                    PairedAt = DateTimeOffset.UtcNow.ToString("O")
                });
            }
            else
            {
                existing.Name = phoneName;
                existing.PairedAt = DateTimeOffset.UtcNow.ToString("O");
            }

            Save(config);
            _cached = config;
        }
    }

    /// <summary>
    /// Handles a PAIR_CONFIRM frame: records the phone and drops the result
    /// file that a waiting <c>--pair</c> process is polling.
    /// </summary>
    public static void CompletePairing(string phoneId, string phoneName)
    {
        RecordPairedPhone(phoneId, phoneName);

        try
        {
            File.WriteAllText(
                ResultFile,
                $"PAIRED|{phoneName}|{DateTimeOffset.UtcNow:O}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"Pairing: could not write {ResultFile}: {ex.Message}");
        }

        FileLog.Write($"Pairing: confirmed by \"{phoneName}\" ({phoneId}).");
    }

    /// <summary>Short status string for the startup log.</summary>
    public static string Describe()
    {
        try
        {
            PairingConfig config = Load();
            int phones = config.Paired.Count;
            return phones == 0
                ? $"device {ShortId(config.Id)}, no phone paired yet"
                : $"device {ShortId(config.Id)}, {phones} phone(s) paired";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    /// <summary>
    /// Compact binary QR payload: <c>p1.</c> + unpadded Base64Url of
    /// <c>version(1) flags(1) ipv4(4) port(2 BE) id(16) secret(32) [mac(6)] name(utf8)</c>.
    ///
    /// JSON costs 60 bytes of punctuation alone; this keeps the code at QR
    /// version 6 (~41 modules) instead of version 12 (~73 modules), which is
    /// the difference between a 12 cm and a 7 cm code on a terminal.
    /// </summary>
    public const string CompactPrefix = "p1.";

    private const byte FlagHasMac = 0x01;

    /// <summary>
    /// Upper bound on the machine name inside the QR code. Hostnames are
    /// normally ≤15 characters; the cap only stops an edited `pairing.json`
    /// from turning the code into a wall of modules.
    /// </summary>
    private const int MaxNameBytes = 32;

    public static string BuildCompactPayload(string ip, string? bluetoothMac)
    {
        PairingConfig config = Load();

        if (!IPAddress.TryParse(ip, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Not an IPv4 address: {ip}", nameof(ip));
        }

        byte[] secret = Convert.FromBase64String(config.Secret);
        if (secret.Length != SecretBytes)
        {
            throw new InvalidOperationException(
                $"Secret in {ConfigPath} is {secret.Length} bytes, expected {SecretBytes}.");
        }

        byte[] mac = Array.Empty<byte>();
        if (!string.IsNullOrWhiteSpace(bluetoothMac) &&
            TryParseMac(bluetoothMac, out byte[]? parsedMac))
        {
            mac = parsedMac;
        }

        byte flags = 0;
        if (mac.Length == 6)
        {
            flags |= FlagHasMac;
        }

        string name = string.IsNullOrWhiteSpace(config.Name)
            ? Environment.MachineName
            : config.Name;
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > MaxNameBytes)
        {
            // Cut on a character boundary: 0b10xxxxxx marks a UTF-8
            // continuation byte, so back off until the next char starts.
            int length = MaxNameBytes;
            while (length > 0 && (nameBytes[length] & 0xC0) == 0x80)
            {
                length--;
            }

            nameBytes = nameBytes[..length];
        }

        using var buffer = new MemoryStream(64 + nameBytes.Length);
        buffer.WriteByte(ProtocolVersion);
        buffer.WriteByte(flags);

        byte[] octets = address.GetAddressBytes();
        buffer.Write(octets, 0, 4);

        buffer.WriteByte((byte)(config.Port >> 8));
        buffer.WriteByte((byte)(config.Port & 0xFF));

        buffer.Write(Convert.FromHexString(Guid.Parse(config.Id).ToString("N")), 0, 16);
        buffer.Write(secret, 0, secret.Length);

        if ((flags & FlagHasMac) != 0)
        {
            buffer.Write(mac, 0, mac.Length);
        }

        buffer.Write(nameBytes, 0, nameBytes.Length);

        return CompactPrefix + Base64Url(buffer.ToArray());
    }

    /// <summary>Minified JSON QR payload (kept for compatibility with older builds).</summary>
    public static string BuildPayloadJson(string ip, string? bluetoothMac)
    {
        PairingConfig config = Load();

        var payload = new Dictionary<string, object?>
        {
            ["v"] = ProtocolVersion,
            ["id"] = config.Id,
            ["name"] = config.Name,
            ["os"] = config.Os,
            ["ip"] = ip,
            ["port"] = config.Port,
            ["bt_mac"] = string.IsNullOrWhiteSpace(bluetoothMac) ? null : bluetoothMac,
            ["secret"] = config.Secret
        };

        return JsonSerializer.Serialize(payload);
    }

    private static PairingConfig? ReadFromDisk()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            PairingConfig? config = JsonSerializer.Deserialize<PairingConfig>(
                File.ReadAllText(ConfigPath), JsonOptions);

            if (config == null || string.IsNullOrWhiteSpace(config.Id) ||
                string.IsNullOrWhiteSpace(config.Secret))
            {
                return null;
            }

            config.Name = string.IsNullOrWhiteSpace(config.Name)
                ? Environment.MachineName
                : config.Name;
            config.Os = "windows";
            config.Paired ??= new List<PairedPhone>();
            return config;
        }
        catch
        {
            return null;
        }
    }

    private static PairingConfig CreateNew()
    {
        var config = new PairingConfig
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = Environment.MachineName,
            Os = "windows",
            Port = WifiUnlockListener.TcpPort,
            Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
        };

        // Deliberately not swallowed: a secret that exists only in memory would
        // produce a QR code the service cannot honour.
        Save(config);

        return config;
    }

    private static void Save(PairingConfig config)
    {
        Directory.CreateDirectory(DataDir);

        // Write then move: a half-written config would strand the phone with a
        // secret the PC no longer has.
        string temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, ConfigPath, true);
    }

    private static string ShortId(string id)
        => string.IsNullOrEmpty(id) ? "(none)" : id[..Math.Min(8, id.Length)];

    /// <summary>Unpadded Base64Url: no '+' '/' '=', so it survives any QR reader.</summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Accepts "AA:BB:CC:DD:EE:FF", "AABBCCDDEEFF" or "AA-BB-...".</summary>
    private static bool TryParseMac(string value, out byte[] mac)
    {
        mac = Array.Empty<byte>();

        string hex = value
            .Replace(":", string.Empty)
            .Replace("-", string.Empty)
            .Replace(".", string.Empty)
            .Trim();

        if (hex.Length != 12)
        {
            return false;
        }

        try
        {
            mac = Convert.FromHexString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
