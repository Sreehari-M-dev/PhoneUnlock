using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace BluetoothUnlockService;

/// <summary>
/// Wi-Fi transport: TCP for commands (port 9735), UDP for discovery (9736) and
/// for periodic presence announcements (9737).
///
/// The phone's hotspot is the network — no home router is involved.
///
/// Commands are newline-terminated so the server can answer as soon as the
/// payload arrives. The old code waited for the client to close before replying,
/// which deadlocked: the phone was waiting for the reply while the PC was
/// waiting for the phone to hang up.
/// </summary>
public sealed class WifiUnlockListener : IDisposable
{
    public const int TcpPort = 9735;
    public const int DiscoveryPort = 9736;
    public const int AnnouncementPort = 9737;

    private const string DiscoveryRequest = "DISCOVER_UNLOCK_SERVER";
    private const string StatusQuery = "STATUS_QUERY";
    private const string ResponsePrefix = "UNLOCK_SERVER|";
    private const int MaxPayloadBytes = 4096;
    private const int ClientReadTimeoutMs = 5000;

    private readonly UnlockCoordinator _coordinator;
    private readonly ILogger<WifiUnlockListener> _logger;

    private TcpListener? _tcpListener;
    private UdpClient? _discoveryUdp;
    private UdpClient? _announceUdp;
    private CancellationTokenSource? _cts;

    public WifiUnlockListener(UnlockCoordinator coordinator, ILogger<WifiUnlockListener> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <summary>
    /// Binds the sockets and runs until cancellation. Throws when the port is
    /// already in use, so the worker can retry.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationToken token = _cts.Token;

        LogLocalAddresses();

        _tcpListener = new TcpListener(IPAddress.Any, TcpPort)
        {
            ExclusiveAddressUse = false
        };
        _tcpListener.Start();

        _discoveryUdp = new UdpClient();
        _discoveryUdp.Client.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _discoveryUdp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _discoveryUdp.EnableBroadcast = true;

        _announceUdp = new UdpClient { EnableBroadcast = true };

        _coordinator.WriteStatus("LISTENING");
        FileLog.Write($"WiFi: TCP {TcpPort}, UDP {DiscoveryPort}/{AnnouncementPort} ready.");

        _ = Task.Run(() => RunDiscoveryAsync(token), token);
        _ = Task.Run(() => RunAnnouncementLoopAsync(token), token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = await _tcpListener.AcceptTcpClientAsync(token);
                string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                FileLog.Write($"WiFi: TCP connection from {remote}.");
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunDiscoveryAsync(CancellationToken token)
    {
        if (_discoveryUdp == null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await _discoveryUdp.ReceiveAsync(token);
                string message = Encoding.UTF8
                    .GetString(result.Buffer)
                    .TrimEnd('\0')
                    .Trim();

                switch (message)
                {
                    case DiscoveryRequest:
                    {
                        string response = BuildDiscoveryResponse();
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        await _discoveryUdp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                        FileLog.Write($"WiFi: discovery reply to {result.RemoteEndPoint}: {response}");
                        break;
                    }

                    case StatusQuery:
                    {
                        string response = BuildStatusResponse();
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        await _discoveryUdp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                        FileLog.Write($"WiFi: status reply to {result.RemoteEndPoint}: {response}");
                        break;
                    }

                    default:
                        FileLog.Write($"WiFi: unknown UDP message from {result.RemoteEndPoint}: \"{message}\"");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLog.Write($"WiFi: discovery loop error: {ex.Message}");
                await Task.Delay(500, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Broadcasts our presence every few seconds. The app can then find the PC
    /// without having to win a single request/response race.
    /// </summary>
    private async Task RunAnnouncementLoopAsync(CancellationToken token)
    {
        if (_announceUdp == null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                string message = BuildDiscoveryResponse();
                byte[] bytes = Encoding.UTF8.GetBytes(message);

                foreach (IPAddress target in GetBroadcastAddresses())
                {
                    try
                    {
                        await _announceUdp.SendAsync(
                            bytes, bytes.Length, new IPEndPoint(target, AnnouncementPort));
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"WiFi: announcement to {target} failed: {ex.Message}");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLog.Write($"WiFi: announcement loop error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = ClientReadTimeoutMs;

                using NetworkStream stream = client.GetStream();
                string payload = ReadPayload(stream);

                if (string.IsNullOrWhiteSpace(payload))
                {
                    FileLog.Write("WiFi: empty payload — ignoring.");
                    return;
                }

                FileLog.Write($"WiFi: received {payload.Length} bytes.");
                _coordinator.WriteStatus("CONNECTED|Wi-Fi");

                if (!CryptoHelper.TryValidateCommand(
                        payload, out var command, out var pairing, out string? errorReason))
                {
                    FileLog.Write($"WiFi: invalid request: {errorReason}");
                    await WriteLineAsync(stream, $"FAIL|{errorReason}");
                    return;
                }

                switch (command)
                {
                    case CryptoHelper.CommandType.Pair:
                        PairingStore.CompletePairing(
                            pairing!.PhoneId, pairing.PhoneName);
                        await WriteLineAsync(
                            stream, $"PAIR_OK|{Environment.MachineName}");
                        break;

                    case CryptoHelper.CommandType.Unlock:
                        if (!_coordinator.TryBeginUnlock(out string rejection))
                        {
                            FileLog.Write($"WiFi: unlock rejected ({rejection}).");
                            await WriteLineAsync(stream, $"UNLOCK_REJECTED|{rejection}");
                            return;
                        }

                        bool unlocked = await _coordinator.UnlockAsync(TimeSpan.FromSeconds(12));
                        await WriteLineAsync(stream, unlocked ? "UNLOCK_OK" : "FAIL|timeout");
                        break;

                    case CryptoHelper.CommandType.Lock:
                        bool locked = await _coordinator.LockAsync(TimeSpan.FromSeconds(8));
                        await WriteLineAsync(stream, locked ? "LOCK_OK" : "LOCK_FAIL|timeout");
                        break;

                    default:
                        await WriteLineAsync(stream, "FAIL|unknown command");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"WiFi: client handling failed: {ex.Message}");
        }
        finally
        {
            // Back to idle so the lock screen stops saying "Phone connected".
            if (!_coordinator.IsUnlockInProgress)
            {
                _coordinator.WriteStatus("LISTENING");
            }
        }
    }

    private static string ReadPayload(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[512];

        while (builder.Length < MaxPayloadBytes)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                char c = (char)buffer[i];
                if (c == '\n')
                {
                    return builder.ToString().Trim();
                }

                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    private static async Task WriteLineAsync(NetworkStream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static string BuildDiscoveryResponse()
    {
        string state = SessionState.IsLocked() ? "LOCKED" : "UNLOCKED";
        string response = $"{ResponsePrefix}{Environment.MachineName}|{state}";

        // The app uses this to initiate the one-time Bluetooth pairing, which
        // avoids making the PC discoverable. Older app builds ignore it.
        string? bluetooth = BluetoothUnlockListener.LocalAddress;
        if (!string.IsNullOrEmpty(bluetooth))
        {
            response += $"|{bluetooth}";
        }

        return response;
    }

    private string BuildStatusResponse()
    {
        string state = SessionState.IsLocked() ? "LOCKED" : "UNLOCKED";
        string inProgress = _coordinator.IsUnlockInProgress ? "|UNLOCK_IN_PROGRESS" : string.Empty;
        return $"STATUS|{state}{inProgress}";
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        var results = new List<IPAddress> { IPAddress.Broadcast };

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    IPAddress? mask = unicast.IPv4Mask;
                    if (mask == null || mask.Equals(IPAddress.Any))
                    {
                        continue;
                    }

                    byte[] ip = unicast.Address.GetAddressBytes();
                    byte[] m = mask.GetAddressBytes();
                    var broadcast = new byte[4];

                    for (int i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(ip[i] | ~m[i]);
                    }

                    results.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"WiFi: could not enumerate broadcast addresses: {ex.Message}");
        }

        return results.Distinct();
    }

    private static void LogLocalAddresses()
    {
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        FileLog.Write($"WiFi: local IPv4 {unicast.Address} on {nic.Name}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"WiFi: could not enumerate local addresses: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _tcpListener?.Stop();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _discoveryUdp?.Close();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _announceUdp?.Close();
        }
        catch
        {
            // Ignore.
        }

        _cts?.Dispose();
    }
}
