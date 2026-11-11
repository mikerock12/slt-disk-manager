using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Slt.NetworkBoot;

// ProxyDHCP (PXE 2.1, section 2.2.3): tells PXE clients which boot file to fetch while the
// network's own DHCP server keeps handing out addresses. This server NEVER offers an address
// (yiaddr stays 0.0.0.0), keeps no lease and answers only requests that identify a PXE client
// of a supported architecture on the selected interface. Everything else is ignored.
public sealed class ProxyDhcpServer : IAsyncDisposable
{
    public const int ServerPort = 67, ClientPort = 68, BootServerPort = 4011;
    private static readonly byte[] Cookie = [99, 130, 83, 99];
    private readonly IPAddress bind;
    private readonly IPNetwork network;
    private readonly int interfaceIndex;
    private readonly Action<BootEvent>? report;
    private readonly Socket listener, sender, bootServer;
    private readonly CancellationTokenSource stop = new();
    private readonly int serverPort, clientPort;
    private Task[] loops = [];
    private int answered; private long window = Environment.TickCount64;

    public sealed record Request(byte MessageType, uint TransactionId, ushort Flags, IPAddress ClientAddress, byte[] HardwareAddress, byte HardwareType, byte HardwareLength, string BootFile, byte[]? ClientUuid, bool Relayed);

    // Null for anything that is not a PXE DISCOVER/REQUEST from a supported client.
    public static Request? Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 240 or > 1500 || packet[0] != 1 || packet[1] != 1 || packet[2] != 6 || !packet.Slice(236, 4).SequenceEqual(Cookie)) return null;
        byte type = 0; string? vendor = null; int architecture = -1; byte[]? uuid = null;
        for (int i = 240; i < packet.Length;)
        {
            var code = packet[i++];
            if (code == 255) break;
            if (code == 0) continue;
            if (i >= packet.Length) return null;
            int length = packet[i++];
            if (i + length > packet.Length) return null;
            var value = packet.Slice(i, length); i += length;
            switch (code)
            {
                case 53 when length == 1: type = value[0]; break;
                case 60: vendor = Encoding.ASCII.GetString(value); break;
                case 93 when length >= 2: architecture = BinaryPrimitives.ReadUInt16BigEndian(value); break;
                case 97 when length == 17: uuid = value.ToArray(); break;
            }
        }
        if (type is not (1 or 3) || vendor == null || !vendor.StartsWith("PXEClient", StringComparison.Ordinal)) return null;
        if (architecture < 0)
        {
            // "PXEClient:Arch:00007:UNDI:003016"
            var marker = vendor.IndexOf(":Arch:", StringComparison.Ordinal);
            if (marker < 0 || vendor.Length < marker + 11 || !int.TryParse(vendor.AsSpan(marker + 6, 5), out architecture)) return null;
        }
        // 0 = BIOS; 7 and 9 = x86-64 UEFI. Other architectures have no SLT loader: stay silent.
        var file = architecture switch { 0 => "undionly.kpxe", 7 or 9 => "ipxe.efi", _ => null };
        if (file == null) return null;
        return new(type, BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(10, 2)),
            new IPAddress(packet.Slice(12, 4)), packet.Slice(28, 16).ToArray(), packet[1], packet[2], file, uuid, !packet.Slice(24, 4).SequenceEqual(stackalloc byte[4]));
    }

    // OFFER (to a DISCOVER) or ACK (to a REQUEST). yiaddr is always zero: this is not an address offer.
    public static byte[] BuildReply(Request request, IPAddress server)
    {
        var reply = new byte[300];
        reply[0] = 2; reply[1] = request.HardwareType; reply[2] = request.HardwareLength;
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), request.TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), request.Flags);
        request.ClientAddress.GetAddressBytes().CopyTo(reply, 12);
        server.GetAddressBytes().CopyTo(reply, 20);
        request.HardwareAddress.CopyTo(reply, 28);
        Encoding.ASCII.GetBytes(request.BootFile).CopyTo(reply, 108);
        Cookie.CopyTo(reply, 236);
        var i = 240;
        void Option(byte code, ReadOnlySpan<byte> value) { reply[i++] = code; reply[i++] = (byte)value.Length; value.CopyTo(reply.AsSpan(i)); i += value.Length; }
        Option(53, [request.MessageType == 1 ? (byte)2 : (byte)5]);
        Option(54, server.GetAddressBytes());
        Option(60, "PXEClient"u8);
        if (request.ClientUuid != null) Option(97, request.ClientUuid);
        // PXE_DISCOVERY_CONTROL bit 3: use the boot file name above, no boot server discovery menu.
        Option(43, [6, 1, 8, 255]);
        reply[i] = 255;
        return reply;
    }

    public ProxyDhcpServer(IPAddress bind, IPNetwork network, Action<BootEvent>? report = null, int serverPort = ServerPort, int clientPort = ClientPort, int bootServerPort = BootServerPort)
    {
        this.bind = bind; this.network = network; this.report = report; this.serverPort = serverPort; this.clientPort = clientPort;
        interfaceIndex = IPAddress.IsLoopback(bind) ? -1 : NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(bind)))
            .Select(n => n.GetIPProperties().GetIPv4Properties()?.Index ?? -1).DefaultIfEmpty(-1).First();
        if (interfaceIndex < 0 && !IPAddress.IsLoopback(bind)) throw new InvalidDataException("Interface do ProxyDHCP não encontrada.");
        Socket Udp(IPAddress address, int port, bool broadcast)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // Listener (all addresses, to receive broadcasts) and sender (the bench address, so the
                // broadcast answer leaves through the right interface) share the DHCP server port.
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (broadcast) socket.EnableBroadcast = true;
                socket.Bind(new IPEndPoint(address, port)); return socket;
            }
            catch { socket.Dispose(); throw; }
        }
        listener = Udp(IPAddress.IsLoopback(bind) ? bind : IPAddress.Any, serverPort, true);
        try
        {
            listener.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            sender = IPAddress.IsLoopback(bind) ? listener : Udp(bind, serverPort, true);
            bootServer = Udp(bind, bootServerPort, false);
        }
        catch { listener.Dispose(); sender?.Dispose(); throw; }
    }
    public int Port => ((IPEndPoint)listener.LocalEndPoint!).Port;
    public int BootPort => ((IPEndPoint)bootServer.LocalEndPoint!).Port;
    public Task Completion => loops.Length == 0 ? Task.CompletedTask : Task.WhenAny(loops);
    public void Start() => loops = [Serve(listener, true), Serve(bootServer, false)];

    private bool Allow()
    {
        if (Environment.TickCount64 - window > 1000) { answered = 0; window = Environment.TickCount64; }
        return ++answered <= 40;
    }
    private async Task Serve(Socket socket, bool discovery)
    {
        var buffer = new byte[1500];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token);
                var remote = (IPEndPoint)received.RemoteEndPoint;
                if (discovery && interfaceIndex >= 0 && received.PacketInformation.Interface != interfaceIndex) continue;
                var request = Parse(buffer.AsSpan(0, received.ReceivedBytes));
                // Relayed requests (another subnet) and clients outside the bench network are not served.
                if (request == null || request.Relayed) continue;
                // On the DHCP port only a DISCOVER is answered. The client's later REQUEST belongs to the
                // network's DHCP server: acknowledging it here (without an address) breaks UEFI firmware,
                // which treats it as the lease answer. The boot-server port answers REQUEST only.
                if (request.MessageType != (discovery ? 1 : 3) || !Allow()) continue;
                if (!remote.Address.Equals(IPAddress.Any) && !network.Contains(remote.Address) && !IPAddress.IsLoopback(remote.Address)) continue;
                var reply = BuildReply(request, bind);
                var mac = Convert.ToHexString(request.HardwareAddress.AsSpan(0, 6));
                if (discovery)
                {
                    // The client has no address yet (or asked for broadcast): answer to the local broadcast.
                    var target = remote.Address.Equals(IPAddress.Any) || (request.Flags & 0x8000) != 0 ? new IPEndPoint(IPAddress.Broadcast, clientPort) : new IPEndPoint(remote.Address, clientPort);
                    if (IPAddress.IsLoopback(bind)) target = remote;
                    await sender.SendToAsync(reply, SocketFlags.None, target, stop.Token);
                    report?.Invoke(new(DateTimeOffset.UtcNow, "PROXY", $"{mac}: arquivo de boot {request.BootFile} informado; nenhum endereço IP oferecido."));
                }
                else
                {
                    await socket.SendToAsync(reply, SocketFlags.None, remote, stop.Token);
                    report?.Invoke(new(DateTimeOffset.UtcNow, "PROXY", $"{mac} ({remote.Address}): arquivo de boot {request.BootFile} confirmado na porta {BootPort}."));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { /* a client vanished or sent a truncated datagram */ }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { await Task.WhenAll(loops); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        listener.Dispose(); if (!ReferenceEquals(sender, listener)) sender.Dispose(); bootServer.Dispose(); stop.Dispose();
    }
}
