using System.Runtime.InteropServices;

namespace BluetoothUnlockService;

/// <summary>
/// Publishes the RFCOMM listener in the local Bluetooth SDP database.
///
/// Binding a Winsock RFCOMM socket is not enough on its own: without an SDP
/// record the phone has no way to translate our service UUID into the dynamic
/// RFCOMM channel Windows picked, and Android's
/// createRfcommSocketToServiceRecord() fails with "service discovery failed".
///
/// WSASetService(RNRSERVICE_REGISTER) with NS_BTH is the documented way to add
/// that record. Verified on this adapter: registration returns 0 and the
/// channel assigned by the stack is reported back through getsockname().
/// </summary>
internal static class RfcommSdp
{
    private const int AfBth = 32;
    private const int SockStream = 1;
    private const int BthProtoRfcomm = 3;
    private const int NsBth = 16;
    private const int RnrServiceRegister = 0;
    private const int RnrServiceDeregister = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockaddrBth
    {
        public ushort AddressFamily;
        public ulong BtAddr;
        public Guid ServiceClassId;
        public uint Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SocketAddress
    {
        public IntPtr Sockaddr;
        public int SockaddrLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CsaddrInfo
    {
        public SocketAddress LocalAddr;
        public SocketAddress RemoteAddr;
        public int SocketType;
        public int Protocol;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WsaQuerySetW
    {
        public int Size;
        public IntPtr ServiceInstanceName;
        public IntPtr ServiceClassId;
        public IntPtr Version;
        public IntPtr Comment;
        public int NameSpace;
        public IntPtr NsProviderId;
        public IntPtr Context;
        public int NumberOfProtocols;
        public IntPtr AfpProtocols;
        public IntPtr QueryString;
        public int NumberOfCsAddrs;
        public IntPtr CsaBuffer;
        public int OutputFlags;
        public IntPtr Blob;
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int getsockname(IntPtr socket, IntPtr name, ref int nameLength);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSASetService(IntPtr querySet, int operation, uint flags);

    /// <summary>
    /// Registers the SDP record for <paramref name="socket"/>. Returns false when
    /// the stack refused (the listener still works, but the phone may have to
    /// fall back to a channel scan).
    /// </summary>
    public static bool TryRegister(IntPtr socket, Guid serviceUuid, string displayName)
    {
        try
        {
            if (!TryGetLocalAddress(socket, out ulong address, out uint channel))
            {
                FileLog.Write("BT: SDP registration skipped — could not read the local socket address.");
                return false;
            }

            bool registered = SetService(socket, serviceUuid, displayName, address, channel, RnrServiceRegister);

            FileLog.Write(
                registered
                    ? $"BT: SDP record published on channel {channel} for {serviceUuid}."
                    : "BT: SDP registration failed; the phone will fall back to a channel scan.");

            return registered;
        }
        catch (Exception ex)
        {
            FileLog.Write($"BT: SDP registration threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void Deregister(IntPtr socket, Guid serviceUuid, string displayName)
    {
        try
        {
            if (TryGetLocalAddress(socket, out ulong address, out uint channel))
            {
                SetService(socket, serviceUuid, displayName, address, channel, RnrServiceDeregister);
            }
        }
        catch
        {
            // Shutdown path — nothing useful to do.
        }
    }

    /// <summary>Reads the bound socket's local radio address and RFCOMM channel.</summary>
    public static bool TryGetLocalAddress(IntPtr socket, out ulong address, out uint channel)
    {
        address = 0;
        channel = 0;

        int size = Marshal.SizeOf<SockaddrBth>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            int length = size;
            if (getsockname(socket, buffer, ref length) != 0 || length < size)
            {
                return false;
            }

            var sockaddr = Marshal.PtrToStructure<SockaddrBth>(buffer);
            address = sockaddr.BtAddr;
            channel = sockaddr.Port;
            return address != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool SetService(
        IntPtr socket, Guid serviceUuid, string displayName,
        ulong address, uint channel, int operation)
    {
        IntPtr localSockaddr = IntPtr.Zero;
        IntPtr remoteSockaddr = IntPtr.Zero;
        IntPtr csaddr = IntPtr.Zero;
        IntPtr name = IntPtr.Zero;
        IntPtr classId = IntPtr.Zero;
        IntPtr querySet = IntPtr.Zero;

        try
        {
            localSockaddr = AllocSockaddr(address, channel, serviceUuid);
            remoteSockaddr = AllocSockaddr(address, 0, serviceUuid);

            var cs = new CsaddrInfo
            {
                LocalAddr = new SocketAddress
                {
                    Sockaddr = localSockaddr,
                    SockaddrLength = Marshal.SizeOf<SockaddrBth>()
                },
                RemoteAddr = new SocketAddress
                {
                    Sockaddr = remoteSockaddr,
                    SockaddrLength = Marshal.SizeOf<SockaddrBth>()
                },
                SocketType = SockStream,
                Protocol = BthProtoRfcomm
            };

            csaddr = Marshal.AllocHGlobal(Marshal.SizeOf<CsaddrInfo>());
            Marshal.StructureToPtr(cs, csaddr, false);

            name = Marshal.StringToHGlobalUni(displayName);
            classId = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(serviceUuid, classId, false);

            var qs = new WsaQuerySetW
            {
                Size = Marshal.SizeOf<WsaQuerySetW>(),
                ServiceInstanceName = name,
                ServiceClassId = classId,
                NameSpace = NsBth,
                NumberOfCsAddrs = 1,
                CsaBuffer = csaddr
            };

            querySet = Marshal.AllocHGlobal(Marshal.SizeOf<WsaQuerySetW>());
            Marshal.StructureToPtr(qs, querySet, false);

            return WSASetService(querySet, operation, 0) == 0;
        }
        finally
        {
            if (querySet != IntPtr.Zero) Marshal.FreeHGlobal(querySet);
            if (classId != IntPtr.Zero) Marshal.FreeHGlobal(classId);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
            if (csaddr != IntPtr.Zero) Marshal.FreeHGlobal(csaddr);
            if (remoteSockaddr != IntPtr.Zero) Marshal.FreeHGlobal(remoteSockaddr);
            if (localSockaddr != IntPtr.Zero) Marshal.FreeHGlobal(localSockaddr);
        }
    }

    private static IntPtr AllocSockaddr(ulong address, uint port, Guid serviceUuid)
    {
        var sockaddr = new SockaddrBth
        {
            AddressFamily = AfBth,
            BtAddr = address,
            ServiceClassId = serviceUuid,
            Port = port
        };

        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf<SockaddrBth>());
        Marshal.StructureToPtr(sockaddr, memory, false);
        return memory;
    }
}
