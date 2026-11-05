using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Slt.Controller;
using Slt.NetworkBoot;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or SocketException) { Check(true, name); return; } throw new Exception("FAIL accepted " + name); }
var root = Path.Combine(Path.GetTempPath(), "slt-netboot-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var c = new NetworkBootConfig(1, "synthetic", "10.77.0.1", "10.77.0.0/24");
    Check(NetworkTopologyValidator.Validate(c, true).Valid, "isolated private topology");
    foreach (var ip in new[] { "0.0.0.0", "127.0.0.1", "8.8.8.8", "::1", "169.254.1.1" }) Check(!NetworkTopologyValidator.Validate(c with { BindIPv4 = ip }).Valid, "production refuses " + ip);
    foreach (var subnet in new[] { "0.0.0.0/0", "10.0.0.0/7", "172.0.0.0/8", "192.168.0.0/15", "10.78.0.0/24" }) Check(!NetworkTopologyValidator.Validate(c with { AllowedCidr = subnet }, true).Valid, "refuse CIDR " + subnet);
    Check(!NetworkTopologyValidator.Validate(c with { Mode = "PROXY_DHCP" }, true).Valid, "unimplemented proxy refuses leases");
    Check(!NetworkTopologyValidator.Validate(c with { InterfaceId = "does-not-exist" }).Valid, "missing physical interface");
    Check(!NetworkTopologyValidator.Validate(c with { HttpPort = 7443 }, true).Valid, "no API port collision");
    Check(!NetworkTopologyValidator.Validate(c with { MaxConcurrentTransfers = 100 }, true).Valid, "bounded concurrency");
    var source = Path.Combine(root, "source"); var store = Path.Combine(root, "store"); Directory.CreateDirectory(source);
    byte[] bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    BootArtifact Artifact(string id) { File.WriteAllBytes(Path.Combine(source, id), bytes); return new(id, id, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "synthetic"); }
    var profile = new BootProfile(1, "rescue-test", "Injected ${shell} is display-only", "RESCUE_LINUX", "X64", ["BIOS", "UEFI"], "VERIFIED", "NETWORK_BOOT_HARDWARE_VERIFIED", "test", [Artifact("kernel"), Artifact("initrd"), Artifact("rootfs")]);
    File.WriteAllText(Path.Combine(source, "netboot-manifest.json"), BootJson.Serialize(profile));
    var imported = BootArtifactStore.Import(source, store);
    Check(imported.ValidationStatus == "NETWORK_BOOT_DEV" && imported.SecureBootStatus == "UNSUPPORTED", "manifest cannot self-certify support");
    BootArtifactStore.VerifyAll(store, [imported]); Check(true, "copy verified after import");
    Reject(() => BootArtifactStore.Import(source, store), "immutable versioned import");
    foreach (var path in new[] { "../kernel", "/etc/passwd", "C:/secret", "sub/../../secret", "kernel:secret", "sub\\kernel", "sub//kernel" }) Reject(() => BootArtifactStore.SafeFile(source, path), "path sandbox " + path);
    File.AppendAllText(Path.Combine(source, "kernel"), "tampered");
    Reject(() => BootArtifactStore.Import(source, Path.Combine(root, "tamper-store")), "corrupt import fails before publication");
    Check(!Directory.Exists(Path.Combine(root, "tamper-store", profile.Id)), "failed import is not published");
    var menu = BootScriptRenderer.Menu(c, [imported]);
    Check(menu.Contains("--default exit") && !menu.Contains("${shell}") && !menu.Contains("install.wim"), "generated menu no imported commands and safe default");
    Check(menu.Contains("initrd=initrd.img") && menu.Contains("fetch=http://10.77.0.1:8088/boot/v1/assets/rescue-test-rootfs"), "live-boot network-only renderer");
    Check(!BootScriptRenderer.Autoexec(c).Contains("undionly") && BootScriptRenderer.Autoexec(c).Contains("menu.ipxe"), "autoexec second stage avoids chainload loop");
    foreach (var packet in new byte[][] { [0, 2, 1, 0], [0, 1, 65, 0], [0, 1, 255, 0] }) Reject(() => TftpBootstrapServer.Parse(packet), "invalid TFTP RRQ refuses");
    byte[] Rrq(string name, string suffix = "") => [0, 1, .. Encoding.ASCII.GetBytes(name + "\0octet\0" + suffix)];
    var request = TftpBootstrapServer.Parse(Rrq("undionly.kpxe", "blksize\065464\0tsize\00\0"));
    Check(request.BlockSize == 1468 && request.Options.Length == 2, "TFTP option negotiation bounds datagram");
    await using (var server = new TftpBootstrapServer(IPAddress.Loopback, IPNetwork.Parse("127.0.0.0/8"), new Dictionary<string, byte[]> { ["undionly.kpxe"] = bytes }, 2, 0))
    {
        server.Start(); using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await socket.SendAsync(Rrq("undionly.kpxe"), new IPEndPoint(IPAddress.Loopback, server.Port));
        using var deadline = new CancellationTokenSource(10000); using var result = new MemoryStream();
        ushort seq = 1;
        while (true)
        {
            var response = await socket.ReceiveAsync(deadline.Token); Check(response.Buffer[1] == 3 && BinaryPrimitives.ReadUInt16BigEndian(response.Buffer.AsSpan(2)) == seq, "TFTP block " + seq);
            result.Write(response.Buffer, 4, response.Buffer.Length - 4);
            var ack = new byte[] { 0, 4, (byte)(seq >> 8), (byte)seq }; await socket.SendAsync(ack, response.RemoteEndPoint); seq++;
            if (response.Buffer.Length < 516) break;
        }
        Check(result.ToArray().SequenceEqual(bytes), "actual UDP TFTP file exact including terminal empty block");
        await socket.SendAsync(Rrq("../admin.token"), new IPEndPoint(IPAddress.Loopback, server.Port));
        Check((await socket.ReceiveAsync(deadline.Token)).Buffer[1] == 5, "TFTP traversal gets error");
        await socket.SendAsync(new byte[] { 0, 2, 0, 0 }, new IPEndPoint(IPAddress.Loopback, server.Port));
        Check((await socket.ReceiveAsync(deadline.Token)).Buffer[1] == 5, "TFTP write refused");
        Reject(() => new TftpBootstrapServer(IPAddress.Loopback, IPNetwork.Parse("127.0.0.0/8"), new Dictionary<string, byte[]>(), 1, server.Port), "TFTP collision cannot reuse socket");
    }
    // Actual listener lifecycle on loopback only; no DHCP packets, NIC changes or real disks.
    var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
    var launch = new BootLaunch(c with { BindIPv4 = "127.0.0.1", AllowedCidr = "127.0.0.0/8", HttpPort = port }, store, [imported], true);
    for (int cycle = 0; cycle < 3; cycle++)
    {
        await using var http = await BootHttpServer.Start(launch);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Check((await client.GetStringAsync("/boot/v1/health")).Contains("running"), "HTTP lifecycle " + cycle);
        var tasks = Enumerable.Range(0, 8).Select(_ => client.GetByteArrayAsync("/boot/v1/assets/rescue-test-rootfs"));
        Check((await Task.WhenAll(tasks)).All(a => a.SequenceEqual(bytes)), "concurrent positional streaming exact " + cycle);
        using var range = new HttpRequestMessage(HttpMethod.Get, "/boot/v1/assets/rescue-test-rootfs"); range.Headers.Range = new RangeHeaderValue(123, 200);
        using var response = await client.SendAsync(range); Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[123..201]), "HTTP bounded range " + cycle);
        foreach (var path in new[] { "/api/v1/devices", "/agent/v1/jobs", "/boot/v1/assets/admin.token", "/boot/v1/assets/%2e%2e%2fadmin.token" }) Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, "public listener denies " + path);
    }
    await using (var management = new NetworkBootManagement(Path.Combine(root, "state"), Path.Combine(root, "absent.exe"), (_, _) => { }))
    {
        Check((await management.Status()).State == "STOPPED", "management always disabled initially");
        var v = await management.Validate(); Check(!v.Valid && v.Errors.Any(e => e.Contains("ausente")), "preflight reports missing binary/profile/NIC");
        try { await management.Start(false); throw new Exception("unexpected start"); } catch (ApiException e) { Check(e.Status == 400, "start requires explicit contextual network action"); }
        await management.Stop(); Check((await management.Status()).State == "STOPPED", "idempotent stop");
    }
    Console.WriteLine($"Network Boot {passed}/{passed} PASS (synthetic files, loopback sockets only)");
}
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
finally { Directory.Delete(root, true); }
