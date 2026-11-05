using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Slt.NetworkBoot;

// RFC1350 read-only octet server. RFC2348/2349: bounded blksize and tsize only.
// No paths, uploads, leases, relay or filesystem access from client supplied input.
public sealed class TftpBootstrapServer : IAsyncDisposable
{
    private readonly UdpClient listener;
    private readonly IPAddress bind;
    private readonly IPNetwork network;
    private readonly IReadOnlyDictionary<string, byte[]> files;
    private readonly SemaphoreSlim slots;
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> transfers = [];
    private Task? loop;
    private int requests;
    private long window = Environment.TickCount64;
    public TftpBootstrapServer(IPAddress bind, IPNetwork network, IReadOnlyDictionary<string, byte[]> files, int concurrent, int port = 69)
    {
        this.bind = bind; this.network = network; this.files = files; slots = new(concurrent);
        listener = new UdpClient(new IPEndPoint(bind, port));
    }
    public int Port => ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    public void Start() => loop = Run();
    public static (string Name, int BlockSize, string[] Options) Parse(byte[] packet)
    {
        if (packet.Length is < 4 or > 1024 || packet[0] != 0 || packet[1] != 1 || packet[^1] != 0) throw new InvalidDataException("RRQ inválido.");
        if (packet.Skip(2).Any(b => b > 127)) throw new InvalidDataException("RRQ não ASCII.");
        var parts = Encoding.ASCII.GetString(packet, 2, packet.Length - 3).Split('\0');
        if (parts.Length < 2 || parts[1].ToLowerInvariant() != "octet" || (parts.Length - 2) % 2 != 0) throw new InvalidDataException("Modo inválido.");
        var size = 512; var options = new List<string>(); var seen = new HashSet<string>();
        for (int i = 2; i < parts.Length; i += 2)
        {
            var key = parts[i].ToLowerInvariant(); if (!seen.Add(key)) throw new InvalidDataException("Opção duplicada.");
            if (key == "blksize")
            {
                if (!int.TryParse(parts[i + 1], out var requested) || requested is < 8 or > 65464) throw new InvalidDataException("Blksize inválido.");
                size = Math.Min(requested, 1468); options.Add(key);
            }
            else if (key == "tsize") { if (parts[i + 1] != "0") throw new InvalidDataException("Tsize inválido."); options.Add(key); }
        }
        return (parts[0], size, options.ToArray());
    }
    private async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await listener.ReceiveAsync(stop.Token); } catch (OperationCanceledException) { break; }
            if (!network.Contains(r.RemoteEndPoint.Address)) continue;
            if (Environment.TickCount64 - window > 1000) { requests = 0; window = Environment.TickCount64; }
            if (++requests > 32 || !slots.Wait(0)) continue;
            var task = Transfer(r); lock (transfers) { transfers.RemoveAll(t => t.IsCompleted); transfers.Add(task); }
        }
    }
    private async Task Transfer(UdpReceiveResult r)
    {
        try
        {
            using var socket = new UdpClient(new IPEndPoint(bind, 0)); socket.Connect(r.RemoteEndPoint);
            (string name, int block, string[] options) parsed;
            try { parsed = Parse(r.Buffer); }
            catch (InvalidDataException) { await socket.SendAsync(new byte[] { 0, 5, 0, 4, 0 }, stop.Token); return; }
            if (!files.TryGetValue(parsed.name, out var data)) { await socket.SendAsync(new byte[] { 0, 5, 0, 1, 0 }, stop.Token); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            async Task<bool> SendAck(byte[] packet, ushort number)
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await socket.SendAsync(packet, timeout.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); deadline.CancelAfter(1000);
                    try
                    {
                        while (true)
                        {
                            var ack = (await socket.ReceiveAsync(deadline.Token)).Buffer;
                            if (ack.Length == 4 && ack[0] == 0 && ack[1] == 4 && BinaryPrimitives.ReadUInt16BigEndian(ack.AsSpan(2)) == number) return true;
                        }
                    }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                }
                return false;
            }
            if (parsed.options.Length != 0)
            {
                var body = string.Join('\0', parsed.options.SelectMany(k => new[] { k, k == "blksize" ? parsed.block.ToString(System.Globalization.CultureInfo.InvariantCulture) : data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) })) + "\0";
                if (!await SendAck([0, 6, .. Encoding.ASCII.GetBytes(body)], 0)) return;
            }
            int offset = 0; ushort sequence = 1;
            do
            {
                var count = Math.Min(parsed.block, data.Length - offset); var packet = new byte[count + 4];
                packet[1] = 3; BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++); data.AsSpan(offset, count).CopyTo(packet.AsSpan(4));
                if (!await SendAck(packet, (ushort)(sequence - 1))) return;
                offset += count; if (count < parsed.block) break;
            } while (!timeout.IsCancellationRequested);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { slots.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); if (loop != null) await loop;
        Task[] active; lock (transfers) active = transfers.ToArray(); await Task.WhenAll(active);
        listener.Dispose(); slots.Dispose(); stop.Dispose();
    }
}
