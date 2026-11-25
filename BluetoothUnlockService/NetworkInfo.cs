using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BluetoothUnlockService;

/// <summary>
/// Picks the address the phone should connect to and reports it in the pairing
/// QR code.
///
/// The phone hotspot (192.168.43.x), a home LAN (192.168.x.x / 10.x.x.x) and
/// Bluetooth all end up here. Virtual adapters are deprioritised because they
/// are reachable from the PC but not from the phone.
/// </summary>
public static class NetworkInfo
{
    private static readonly string[] VirtualAdapterHints =
    {
        "vethernet", "virtualbox", "vmware", "hyper-v", "loopback",
        "tap-", "tun", "npcap", "bluetooth network"
    };

    public sealed record Candidate(string Address, string Adapter, int Score);

    /// <summary>
    /// Best guess at the address the phone can reach, or null when the PC has
    /// no usable IPv4 address (Wi-Fi off and no hotspot).
    /// </summary>
    public static string? PrimaryIPv4()
    {
        Candidate? best = Candidates().FirstOrDefault();
        return best?.Address;
    }

    /// <summary>All usable IPv4 addresses, best first (used by --diagnose).</summary>
    public static IReadOnlyList<Candidate> Candidates()
    {
        var results = new List<Candidate>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                int adapterPenalty = IsVirtual(nic) ? 50 : 0;

                foreach (UnicastIPAddressInformation unicast in
                         nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    byte[] bytes = unicast.Address.GetAddressBytes();

                    // 169.254.x.x means DHCP failed; the phone cannot use it.
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    results.Add(new Candidate(
                        unicast.Address.ToString(),
                        nic.Name,
                        adapterPenalty + AddressScore(bytes)));
                }
            }
        }
        catch
        {
            // Fall through with whatever was collected.
        }

        return results
            .OrderBy(c => c.Score)
            .ThenBy(c => c.Adapter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int AddressScore(byte[] ip)
    {
        // Phone hotspots and home LANs first, then anything else.
        if (ip[0] == 192 && ip[1] == 168)
        {
            return ip[2] == 43 ? 0 : 1; // Android hotspot is 192.168.43.x
        }

        if (ip[0] == 10)
        {
            return 2;
        }

        if (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31)
        {
            return 3;
        }

        return 4;
    }

    private static bool IsVirtual(NetworkInterface nic)
    {
        string name = nic.Name.ToLowerInvariant();
        string description = nic.Description.ToLowerInvariant();

        foreach (string hint in VirtualAdapterHints)
        {
            if (name.Contains(hint) || description.Contains(hint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the address is a private (RFC1918) one.</summary>
    public static bool IsPrivate(string address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ip))
        {
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }
}
