using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BluetoothUnlockService;

/// <summary>
/// Classic Bluetooth RFCOMM server (no Wi-Fi or hotspot required).
///
/// Windows exposes RFCOMM through Winsock's AF_BTH address family. This is the
/// same protocol the phone uses over TCP, so the encrypted payload and the
/// response handling are shared with <see cref="WifiUnlockListener"/>.
///
/// The phone must be paired with the PC once; after that the link works even
/// while a headset is connected (Classic supports multiple simultaneous links).
/// </summary>
public sealed class BluetoothUnlockListener : IDisposable
{
    /// <summary>Must match the UUID in the Android app.</summary>
    public static readonly Guid ServiceUuid =
        Guid.Parse("E8F2B1C4-7A3D-4F5E-9B6C-1D2E3F4A5B6C");

    private const int AfBth = 32;
    private const int SockStream = 1;
    private const int BthProtoRfcomm = 3;
    private const int SolSocket = 0xFFFF;
    private const int SoRcvTimeo = 0x1006;
    private const int SoSndTimeo = 0x1005;
    private const int Fionbio = unchecked((int)0x8004667E);
    private const int WsaEwouldblock = 10035;
    private const uint BtPortAny = 0xFFFFFFFF;
    private const int MaxPayloadBytes = 4096;

    private static readonly IntPtr InvalidSocket = (IntPtr)(-1);
    private static bool _wsaStarted;

    private readonly UnlockCoordinator _coordinator;
    private IntPtr _listenSocket = InvalidSocket;
    private Thread? _acceptThread;
    private volatile bool _running;
    private bool _sdpRegistered;

    public BluetoothUnlockListener(UnlockCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>False once the accept loop has stopped (radio off, fatal error).</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Local radio address ("AA:BB:CC:DD:EE:FF"), published to the app over Wi-Fi
    /// so the phone can initiate the one-time pairing itself.
    /// Null until the listener has bound.
    /// </summary>
    public static string? LocalAddress { get; private set; }

    public static string FormatAddress(ulong address)
    {
        // The stack stores the BD_ADDR in the low 48 bits, already in display order.
        string hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockaddrBth
    {
        public ushort AddressFamily;
        public ulong BtAddr;
        public Guid ServiceClassId;
        public uint Port;
    }

    [DllImport("ws2_32.dll")]
    private static extern int WSAStartup(ushort version, IntPtr data);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr WSASocket(
        int addressFamily, int socketType, int protocol, IntPtr protocolInfo, uint group, uint flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int bind(IntPtr socket, IntPtr name, int nameLength);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int listen(IntPtr socket, int backlog);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr accept(IntPtr socket, IntPtr address, IntPtr addressLength);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(IntPtr socket, byte[] buffer, int length, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(IntPtr socket, byte[] buffer, int length, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(
        IntPtr socket, int level, int optionName, ref int optionValue, int optionLength);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int ioctlsocket(IntPtr socket, int command, ref uint argument);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(IntPtr socket);

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    /// <summary>Starts the RFCOMM listener. Returns false (and logs) on failure.</summary>
    public bool Start()
    {
        if (_running)
        {
            return true;
        }

        try
        {
            EnsureWsaStarted();

            _listenSocket = WSASocket(AfBth, SockStream, BthProtoRfcomm, IntPtr.Zero, 0, 0);
            if (_listenSocket == InvalidSocket)
            {
                throw new Win32Exception(WSAGetLastError());
            }

            var address = new SockaddrBth
            {
                AddressFamily = AfBth,
                BtAddr = 0, // BDADDR_ANY
                ServiceClassId = ServiceUuid,
                Port = BtPortAny
            };

            int size = Marshal.SizeOf<SockaddrBth>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(address, buffer, false);
                if (bind(_listenSocket, buffer, size) != 0)
                {
                    throw new Win32Exception(WSAGetLastError());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (listen(_listenSocket, 4) != 0)
            {
                throw new Win32Exception(WSAGetLastError());
            }

            // Publish the service so the phone can resolve the UUID to the
            // dynamic RFCOMM channel Windows assigned.
            _sdpRegistered = RfcommSdp.TryRegister(
                _listenSocket, ServiceUuid, "Phone Unlock");

            if (RfcommSdp.TryGetLocalAddress(_listenSocket, out ulong localAddress, out _))
            {
                LocalAddress = FormatAddress(localAddress);
                FileLog.Write($"BT: local radio address {LocalAddress}.");
            }

            // Non-blocking accept so Dispose() can stop the thread cleanly.
            uint nonBlocking = 1;
            ioctlsocket(_listenSocket, Fionbio, ref nonBlocking);

            _running = true;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "BtRfcommAccept"
            };
            _acceptThread.Start();

            FileLog.Write("BT: RFCOMM listener started (service UUID " + ServiceUuid + ").");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"BT: could not start RFCOMM listener: {ex.Message}");
            Dispose();
            return false;
        }
    }

    private void AcceptLoop()
    {
        int consecutiveFailures = 0;

        while (_running)
        {
            IntPtr client = accept(_listenSocket, IntPtr.Zero, IntPtr.Zero);

            if (client == InvalidSocket)
            {
                int error = WSAGetLastError();
                if (error == WsaEwouldblock)
                {
                    consecutiveFailures = 0;
                    Thread.Sleep(200);
                    continue;
                }

                if (!_running)
                {
                    break;
                }

                consecutiveFailures++;
                FileLog.Write($"BT: accept failed (error {error}, attempt {consecutiveFailures}).");

                if (consecutiveFailures >= 10)
                {
                    FileLog.Write("BT: too many accept failures — stopping listener so it can restart.");
                    break;
                }

                Thread.Sleep(500);
                continue;
            }

            consecutiveFailures = 0;
            _ = Task.Run(() => HandleClientAsync(client));
        }

        _running = false;
    }

    private async Task HandleClientAsync(IntPtr client)
    {
        try
        {
            // Accepted sockets are blocking; give reads/writes a bounded timeout.
            uint blocking = 0;
            ioctlsocket(client, Fionbio, ref blocking);
            SetTimeout(client, SoRcvTimeo, 12_000);
            SetTimeout(client, SoSndTimeo, 12_000);

            string payload = ReadLine(client);
            if (string.IsNullOrWhiteSpace(payload))
            {
                FileLog.Write("BT: empty payload — ignoring.");
                return;
            }

            FileLog.Write($"BT: received {payload.Length} bytes.");
            _coordinator.WriteStatus("CONNECTED|Bluetooth");

            if (!CryptoHelper.TryValidateCommand(
                    payload, out var command, out var pairing, out string? errorReason))
            {
                FileLog.Write($"BT: invalid request: {errorReason}");
                SendLine(client, $"FAIL|{errorReason}");
                return;
            }

            switch (command)
            {
                case CryptoHelper.CommandType.Pair:
                    PairingStore.CompletePairing(pairing!.PhoneId, pairing.PhoneName);
                    SendLine(client, $"PAIR_OK|{Environment.MachineName}");
                    break;

                case CryptoHelper.CommandType.Unlock:
                    if (!_coordinator.TryBeginUnlock(out string rejection))
                    {
                        FileLog.Write($"BT: unlock rejected ({rejection}).");
                        SendLine(client, $"UNLOCK_REJECTED|{rejection}");
                        return;
                    }

                    SendLine(client, "UNLOCK_ACCEPTED");
                    bool unlocked = await _coordinator.UnlockAsync(TimeSpan.FromSeconds(12));
                    SendLine(client, unlocked ? "UNLOCK_OK" : "FAIL|timeout");
                    break;

                case CryptoHelper.CommandType.Lock:
                    bool locked = await _coordinator.LockAsync(TimeSpan.FromSeconds(8));
                    SendLine(client, locked ? "LOCK_OK" : "LOCK_FAIL|timeout");
                    break;

                default:
                    SendLine(client, "FAIL|unknown command");
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"BT: client handling failed: {ex.Message}");
        }
        finally
        {
            closesocket(client);

            if (!_coordinator.IsUnlockInProgress)
            {
                _coordinator.WriteStatus("LISTENING");
            }
        }
    }

    private static string ReadLine(IntPtr socket)
    {
        var builder = new StringBuilder();
        var chunk = new byte[512];

        while (builder.Length < MaxPayloadBytes)
        {
            int read = recv(socket, chunk, chunk.Length, 0);
            if (read <= 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                char c = (char)chunk[i];
                if (c == '\n')
                {
                    return builder.ToString().Trim();
                }

                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    private static void SendLine(IntPtr socket, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
        send(socket, bytes, bytes.Length, 0);
    }

    private static void SetTimeout(IntPtr socket, int option, int milliseconds)
    {
        int value = milliseconds;
        setsockopt(socket, SolSocket, option, ref value, sizeof(int));
    }

    private static void EnsureWsaStarted()
    {
        if (_wsaStarted)
        {
            return;
        }

        IntPtr data = Marshal.AllocHGlobal(512);
        try
        {
            int result = WSAStartup(0x202, data);
            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            _wsaStarted = true;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public void Dispose()
    {
        _running = false;

        if (_listenSocket != InvalidSocket)
        {
            if (_sdpRegistered)
            {
                RfcommSdp.Deregister(_listenSocket, ServiceUuid, "Phone Unlock");
                _sdpRegistered = false;
            }

            closesocket(_listenSocket);
            _listenSocket = InvalidSocket;
        }

        try
        {
            _acceptThread?.Join(1500);
        }
        catch
        {
            // Ignore shutdown races.
        }

        _acceptThread = null;
    }
}
