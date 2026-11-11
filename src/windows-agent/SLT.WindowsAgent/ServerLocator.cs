using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SLT.WindowsAgent;

public sealed record ServerEndpoint(string Name, string Url, string Pin, string Version);
public sealed record SavedServer(string Url, string Pin, string Name);

// LAN discovery. A reply only says where a server claims to be; nothing here is trusted until
// the pairing code typed on the server matches (Pairing.Code binds the certificate we saw).
public static class Discovery
{
    public const int DefaultPort = 7444;

    public static ServerEndpoint? ParseReply(ReadOnlySpan<byte> packet, string nonce, IPAddress source)
    {
        if (packet.Length is < 40 or > 1024) return null;
        try
        {
            var reader = new Utf8JsonReader(packet, new JsonReaderOptions { MaxDepth = 2 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (root.ValueKind != JsonValueKind.Object || Text("slt") != "server" || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1 ||
                !string.Equals(Text("nonce"), nonce, StringComparison.OrdinalIgnoreCase)) return null;
            var pin = Text("pin"); var url = Text("url");
            if (pin is not { Length: 64 } || !pin.All(Uri.IsHexDigit) || url is not { Length: <= 64 } || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            AgentOptions.EnsureLocalServer(uri);
            if (source.IsIPv4MappedToIPv6) source = source.MapToIPv4();
            // The advertised address must be the one that answered: a reply cannot redirect us elsewhere.
            if (!IPAddress.Parse(uri.Host).Equals(source)) return null;
            var name = new string((Text("name") ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' ').Take(48).ToArray());
            var version = new string((Text("version") ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.').Take(32).ToArray());
            return new(name.Length == 0 ? uri.Host : name, uri.GetLeftPart(UriPartial.Authority), pin.ToLowerInvariant(), version);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException) { return null; }
    }

    public static async Task<ServerEndpoint[]> Find(int port, IPEndPoint? target, TimeSpan wait, CancellationToken ct)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var probe = Encoding.UTF8.GetBytes($$"""{"slt":"discover","v":1,"nonce":"{{nonce}}"}""");
        var sockets = new List<(UdpClient Socket, IPEndPoint[] Targets)>();
        var found = new Dictionary<string, ServerEndpoint>(StringComparer.Ordinal);
        try
        {
            if (target is not null) sockets.Add((new UdpClient(new IPEndPoint(IPAddress.IsLoopback(target.Address) ? IPAddress.Loopback : IPAddress.Any, 0)), [target]));
            else
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && AgentOptions.IsLocal(a.Address) && a.PrefixLength is >= 8 and <= 30))
                    {
                        try
                        {
                            var socket = new UdpClient(new IPEndPoint(unicast.Address, 0)) { EnableBroadcast = true };
                            sockets.Add((socket, [new(IPAddress.Broadcast, port), new(DirectedBroadcast(unicast.Address, unicast.PrefixLength), port)]));
                        }
                        catch (SocketException) { /* adapter went away between enumeration and bind */ }
                    }
            if (sockets.Count == 0) return [];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(wait);
            var listeners = sockets.Select(async entry =>
            {
                try
                {
                    while (true)
                    {
                        var received = await entry.Socket.ReceiveAsync(deadline.Token);
                        if (ParseReply(received.Buffer, nonce, received.RemoteEndPoint.Address) is { } endpoint)
                            lock (found) found[endpoint.Url + "|" + endpoint.Pin] = endpoint;
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }).ToArray();
            for (var round = 0; round < 3 && !deadline.IsCancellationRequested; round++)
            {
                foreach (var (socket, targets) in sockets)
                    foreach (var endpoint in targets)
                        try { await socket.SendAsync(probe, endpoint, deadline.Token); } catch (Exception e) when (e is SocketException or OperationCanceledException) { }
                try { await Task.Delay(wait / 4, deadline.Token); } catch (OperationCanceledException) { }
            }
            await Task.WhenAll(listeners);
        }
        finally { foreach (var (socket, _) in sockets) socket.Dispose(); }
        ct.ThrowIfCancellationRequested();
        lock (found) return found.Values.OrderBy(e => e.Url, StringComparer.Ordinal).ToArray();
    }

    public static IPAddress DirectedBroadcast(IPAddress address, int prefix)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value | ~mask);
        return new IPAddress(bytes);
    }
}

// Short-code pairing, identical to the server and the Go agents (see docs/ADR/003).
public static class Pairing
{
    public static string Commit(string nonceHex) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(nonceHex)));
    public static string Code(string serverPin, string csrPem, string clientNonce, string serverNonce)
    {
        var csrHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(csrPem)));
        var text = string.Join('\n', "SLT-PAIR-2", serverPin.ToLowerInvariant(), csrHash, clientNonce.ToLowerInvariant(), serverNonce.ToLowerInvariant()) + "\n";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return (BinaryPrimitives.ReadUInt64BigEndian(digest) % 100_000_000UL).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string Spaced(string code) => code.Length == 8 ? code[..4] + " " + code[4..] : code;
}

public static class ServerLocator
{
    public const string SavedFile = "server.json";

    // Order: explicit address and pin; server confirmed by an earlier pairing; explicit address whose
    // certificate is learned now; LAN discovery. The last two leave PinVerified false, so only the
    // short-code pairing can complete them.
    public static async Task<AgentOptions> Resolve(AgentOptions options, SecureState state, Action<string> log, CancellationToken ct,
        Func<ServerEndpoint[], ServerEndpoint?>? choose = null)
    {
        if (options.Server is not null && options.ServerPin.Length == 64) return options;
        var saved = state.Read<SavedServer>(SavedFile);
        if (saved is not null && saved.Pin is { Length: 64 } && Uri.TryCreate(saved.Url, UriKind.Absolute, out var savedUri))
        {
            AgentOptions.EnsureLocalServer(savedUri);
            return options with { Server = options.Server ?? savedUri, ServerPin = saved.Pin.ToLowerInvariant(), PinVerified = true };
        }
        if (options.Server is not null)
        {
            var pin = await PinnedTransport.ObservePin(options.Server, ct);
            log($"Servidor {options.Server.GetLeftPart(UriPartial.Authority)} respondeu. O código de pareamento confirmará que é o servidor certo.");
            return options with { ServerPin = pin, PinVerified = false };
        }
        for (var attempt = 1; ; attempt++)
        {
            log(attempt == 1 ? "Procurando o servidor SLT Disk Manager na rede local..." : "Ainda procurando o servidor na rede local...");
            var servers = await Discovery.Find(options.DiscoveryPort, options.DiscoveryTarget, TimeSpan.FromSeconds(3), ct);
            var selected = servers.Length == 1 ? servers[0] : servers.Length > 1 ? (choose ?? ChooseOnConsole)(servers) : null;
            if (selected is not null)
            {
                log($"Servidor encontrado: {selected.Name} ({selected.Url}).");
                return options with { Server = new Uri(selected.Url), ServerPin = selected.Pin, PinVerified = false };
            }
            if (servers.Length > 1) throw new InvalidOperationException("Mais de um servidor SLT respondeu na rede. Informe o endereço com --server https://IP:7443.");
            if (options.Once && attempt >= 5 || !Environment.UserInteractive && attempt >= 100)
                throw new InvalidOperationException("Nenhum servidor SLT Disk Manager respondeu na rede local. Confira se o servidor está ligado, na mesma rede e com a porta UDP " + options.DiscoveryPort + " liberada, ou informe --server https://IP:7443.");
            if (attempt == 3) log("Nada ainda. Confira: servidor ligado, mesma rede (cabo ou Wi-Fi da assistência) e firewall do servidor.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 2 * attempt)), ct);
        }
    }

    private static ServerEndpoint? ChooseOnConsole(ServerEndpoint[] servers)
    {
        if (Console.IsInputRedirected || !Environment.UserInteractive) return null;
        Console.WriteLine("\nMais de um servidor SLT Disk Manager respondeu:");
        for (var i = 0; i < servers.Length; i++) Console.WriteLine($"  {i + 1} - {servers[i].Name} ({servers[i].Url})");
        Console.Write("Digite o número do servidor desta bancada: ");
        return int.TryParse(Console.ReadLine(), out var index) && index >= 1 && index <= servers.Length ? servers[index - 1] : null;
    }

    public static void Save(SecureState state, AgentOptions options, string name) =>
        state.Write(SavedFile, new SavedServer(options.Server!.GetLeftPart(UriPartial.Authority), options.ServerPin, name));

    // The server kept its certificate but changed address (DHCP): follow it. Same pin, so no new trust.
    public static async Task<Uri?> Relocate(AgentOptions options, CancellationToken ct)
    {
        var servers = await Discovery.Find(options.DiscoveryPort, options.DiscoveryTarget, TimeSpan.FromSeconds(3), ct);
        var match = servers.Where(s => s.Pin == options.ServerPin).Select(s => new Uri(s.Url)).ToArray();
        return match.Length == 1 && match[0] != options.Server ? match[0] : null;
    }
}
