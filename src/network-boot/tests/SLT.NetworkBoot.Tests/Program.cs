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
    Check(NetworkTopologyValidator.Validate(c with { Mode = "PROXY_DHCP" }, true) is { Valid: true } proxyValidation && proxyValidation.Warnings[0].Contains("nunca oferece endereço IP"), "ProxyDHCP is an explicit mode with its own warning");
    Check(!NetworkTopologyValidator.Validate(c with { Mode = "FULL_DHCP" }, true).Valid && new NetworkBootConfig().Mode == "EXTERNAL_DHCP", "unknown DHCP mode refused; default stays external DHCP");
    byte[] Dhcp(byte messageType, string? vendor, int architecture, byte[]? uuid = null, bool relayed = false, IPAddress? client = null)
    {
        var p = new byte[400]; p[0] = 1; p[1] = 1; p[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), 0x5a17c0deu); BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), 0x8000);
        (client ?? IPAddress.Any).GetAddressBytes().CopyTo(p, 12); if (relayed) p[24] = 10;
        new byte[] { 0x52, 0x54, 0x00, 0x77, 0x00, 0x09 }.CopyTo(p, 28); new byte[] { 99, 130, 83, 99 }.CopyTo(p, 236);
        var i = 240; void Option(byte code, byte[] value) { p[i++] = code; p[i++] = (byte)value.Length; value.CopyTo(p, i); i += value.Length; }
        Option(53, [messageType]); if (vendor != null) Option(60, Encoding.ASCII.GetBytes(vendor));
        if (architecture >= 0) Option(93, [(byte)(architecture >> 8), (byte)architecture]);
        if (uuid != null) Option(97, uuid); p[i] = 255; return p;
    }
    var proxyServer = IPAddress.Parse("10.77.0.1"); var machineUuid = new byte[17]; machineUuid[16] = 0x42;
    var discover = ProxyDhcpServer.Parse(Dhcp(1, "PXEClient:Arch:00000:UNDI:002001", 0, machineUuid))!;
    Check(discover.BootFile == "undionly.kpxe" && discover.TransactionId == 0x5a17c0deu && !discover.Relayed, "ProxyDHCP recognises a BIOS PXE discover");
    Check(ProxyDhcpServer.Parse(Dhcp(1, "PXEClient:Arch:00007:UNDI:003016", 7))!.BootFile == "ipxe.efi" && ProxyDhcpServer.Parse(Dhcp(1, "PXEClient:Arch:00009:UNDI:003016", 9))!.BootFile == "ipxe.efi" && ProxyDhcpServer.Parse(Dhcp(1, "PXEClient:Arch:00007:UNDI:003016", -1))!.BootFile == "ipxe.efi", "ProxyDHCP maps x86-64 UEFI (option 93 or vendor class) to the UEFI loader");
    foreach (var (packet, name) in new[] { (Dhcp(1, null, -1), "ordinary DHCP client"), (Dhcp(1, "MSFT 5.0", -1), "Windows DHCP client"), (Dhcp(1, "PXEClient:Arch:00011:UNDI:003000", 11), "ARM64 PXE client"), (Dhcp(5, "PXEClient:Arch:00000", 0), "server-side message type"), (Dhcp(1, "PXEClient:Arch:00000", 0)[..200], "truncated packet"), (new byte[300], "empty packet") })
        Check(ProxyDhcpServer.Parse(packet) == null, "ProxyDHCP stays silent for " + name);
    Check(ProxyDhcpServer.Parse(Dhcp(1, "PXEClient:Arch:00000", 0, relayed: true))!.Relayed, "relayed request is identified so it can be ignored");
    var offer = ProxyDhcpServer.BuildReply(discover, proxyServer);
    Check(offer[0] == 2 && BinaryPrimitives.ReadUInt32BigEndian(offer.AsSpan(4)) == 0x5a17c0deu && offer.AsSpan(16, 4).SequenceEqual(new byte[4]) && offer.AsSpan(20, 4).SequenceEqual(proxyServer.GetAddressBytes()) && offer.AsSpan(28, 6).SequenceEqual(new byte[] { 0x52, 0x54, 0x00, 0x77, 0x00, 0x09 }), "ProxyDHCP offer echoes the transaction, names this server and offers NO address (yiaddr 0.0.0.0)");
    Check(Encoding.ASCII.GetString(offer, 108, 13) == "undionly.kpxe" && offer[121] == 0, "ProxyDHCP offer carries the boot file name");
    Dictionary<byte, byte[]> Options(byte[] reply) { var map = new Dictionary<byte, byte[]>(); for (int i = 240; reply[i] != 255;) { var code = reply[i++]; int length = reply[i++]; map[code] = reply[i..(i + length)]; i += length; } return map; }
    var offerOptions = Options(offer);
    Check(offerOptions[53].SequenceEqual(new byte[] { 2 }) && offerOptions[54].SequenceEqual(proxyServer.GetAddressBytes()) && Encoding.ASCII.GetString(offerOptions[60]) == "PXEClient" && offerOptions[97].SequenceEqual(machineUuid) && offerOptions[43].SequenceEqual(new byte[] { 6, 1, 8, 255 }), "ProxyDHCP offer: type OFFER, server id, PXEClient class, client UUID echoed, discovery control 8");
    Check(!offerOptions.ContainsKey(1) && !offerOptions.ContainsKey(3) && !offerOptions.ContainsKey(6) && !offerOptions.ContainsKey(51), "ProxyDHCP offer has no netmask, router, DNS or lease time");
    var bootRequest = ProxyDhcpServer.Parse(Dhcp(3, "PXEClient:Arch:00007:UNDI:003016", 7, client: IPAddress.Parse("10.77.0.25")))!;
    var acknowledgement = ProxyDhcpServer.BuildReply(bootRequest, proxyServer);
    Check(Options(acknowledgement)[53].SequenceEqual(new byte[] { 5 }) && acknowledgement.AsSpan(12, 4).SequenceEqual(new byte[] { 10, 77, 0, 25 }) && acknowledgement.AsSpan(16, 4).SequenceEqual(new byte[4]) && Encoding.ASCII.GetString(acknowledgement, 108, 8) == "ipxe.efi", "port 4011 request is acknowledged with the UEFI loader and still no offered address");
    // Real sockets on loopback and unprivileged ports: a PXE discover is answered, an ordinary DHCP discover is not.
    await using (var proxy = new ProxyDhcpServer(IPAddress.Loopback, IPNetwork.Parse("127.0.0.0/8"), null, 0, 0, 0))
    {
        proxy.Start(); using var pxe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); pxe.Client.ReceiveTimeout = 5000;
        await pxe.SendAsync(Dhcp(1, "PXEClient:Arch:00000:UNDI:002001", 0, machineUuid), new IPEndPoint(IPAddress.Loopback, proxy.Port));
        using var proxyDeadline = new CancellationTokenSource(5000);
        var answer = (await pxe.ReceiveAsync(proxyDeadline.Token)).Buffer;
        Check(answer[0] == 2 && answer.AsSpan(16, 4).SequenceEqual(new byte[4]) && Options(answer)[53][0] == 2, "live ProxyDHCP socket answers a PXE discover without offering an address");
        await pxe.SendAsync(Dhcp(3, "PXEClient:Arch:00007:UNDI:003016", 7, client: IPAddress.Loopback), new IPEndPoint(IPAddress.Loopback, proxy.BootPort));
        Check(Options((await pxe.ReceiveAsync(proxyDeadline.Token)).Buffer)[53][0] == 5, "live boot-server port acknowledges the boot file request");
        // The address request that follows a discover is for the network's DHCP server, never for the proxy.
        await pxe.SendAsync(Dhcp(3, "PXEClient:Arch:00007:UNDI:003016", 7, machineUuid), new IPEndPoint(IPAddress.Loopback, proxy.Port));
        await pxe.SendAsync(Dhcp(1, "PXEClient:Arch:00007:UNDI:003016", 7, machineUuid), new IPEndPoint(IPAddress.Loopback, proxy.BootPort));
        using var requestSilence = new CancellationTokenSource(700); var ignoredRequest = false;
        try { await pxe.ReceiveAsync(requestSilence.Token); } catch (OperationCanceledException) { ignoredRequest = true; }
        Check(ignoredRequest, "ProxyDHCP never acknowledges an address request on the DHCP port, nor a discover on the boot-server port");
        await pxe.SendAsync(Dhcp(1, "MSFT 5.0", -1), new IPEndPoint(IPAddress.Loopback, proxy.Port));
        using var silence = new CancellationTokenSource(700); var silent = false;
        try { await pxe.ReceiveAsync(silence.Token); } catch (OperationCanceledException) { silent = true; }
        Check(silent, "live ProxyDHCP socket ignores an ordinary DHCP client");
    }
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
    var trustFile = Path.Combine(root, "trusted-boot-profiles.json");
    File.WriteAllText(trustFile, BootJson.Serialize(new TrustedBootRegistry(1, new string('0', 40), [new(imported.Id, TrustedBootProfiles.Fingerprint(imported)), new("bootstrap-fixture", new string('1', 64))])));
    var trust = new TrustedBootProfiles(trustFile);
    Check(new TrustedBootProfiles().Classify(imported).ValidationStatus == "NETWORK_BOOT_DEV", "no installed catalog cannot promote a profile");
    Check(trust.Classify(imported).ValidationStatus == "NETWORK_BOOT_LAB", "installed exact payload fingerprint recognizes lab profile");
    Check(trust.Classify(imported with { Version = "changed" }).ValidationStatus == "NETWORK_BOOT_DEV", "lab registry binds version");
    Check(trust.Classify(imported with { Artifacts = imported.Artifacts.Select(a => a with { Sha256 = new string('0', 64) }).ToArray() }).ValidationStatus == "NETWORK_BOOT_DEV", "lab registry binds every artifact hash");
    Check(trust.Classify(imported with { ValidationStatus = "NETWORK_BOOT_HARDWARE_VERIFIED", SecureBootStatus = "VERIFIED" }).SecureBootStatus == "UNSUPPORTED", "installed lab registry never promotes Secure Boot/hardware");
    BootArtifactStore.VerifyAll(store, [imported]); Check(true, "copy verified after import");
    Reject(() => BootArtifactStore.Import(source, store), "immutable versioned import");
    foreach (var path in new[] { "../kernel", "/etc/passwd", "C:/secret", "sub/../../secret", "kernel:secret", "sub\\kernel", "sub//kernel" }) Reject(() => BootArtifactStore.SafeFile(source, path), "path sandbox " + path);
    File.AppendAllText(Path.Combine(source, "kernel"), "tampered");
    Reject(() => BootArtifactStore.Import(source, Path.Combine(root, "tamper-store")), "corrupt import fails before publication");
    Check(!Directory.Exists(Path.Combine(root, "tamper-store", profile.Id)), "failed import is not published");
    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), store);
        Reject(() => BootArtifactStore.SafeFile(source, "linked/rescue-test/rootfs"), "symlink parent rejected on Linux");
    }
    Reject(() => BootArtifactStore.Validate(profile with { Artifacts = [null!, profile.Artifacts[1], profile.Artifacts[2]] }), "null artifact metadata rejected");
    BootArtifact Loader(string id) => new(id, id, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "synthetic");
    var bootstrapProfile = new BootProfile(1, "ipxe-test", "iPXE", "IPXE_BOOTSTRAP", "X64", ["BIOS", "UEFI"], "UNSUPPORTED", "NETWORK_BOOT_DEV", "test", [Loader("bios"), Loader("uefi")]);
    BootArtifactStore.Validate(bootstrapProfile); Check(true, "bootstrap profile without the SLT-mode loader stays valid");
    BootArtifactStore.Validate(bootstrapProfile with { Artifacts = [.. bootstrapProfile.Artifacts, Loader("uefi-local")] }); Check(true, "bootstrap profile may carry the optional SLT-mode loader");
    Reject(() => BootArtifactStore.Validate(bootstrapProfile with { Artifacts = [.. bootstrapProfile.Artifacts, Loader("extra")] }), "unknown bootstrap artifact rejected");
    Reject(() => BootArtifactStore.Validate(bootstrapProfile with { Artifacts = [.. bootstrapProfile.Artifacts, Loader("uefi-local"), Loader("uefi-local")] }), "duplicate SLT-mode loader rejected");
    Reject(() => BootArtifactStore.Validate(profile with { Artifacts = [.. profile.Artifacts, Loader("uefi-local")] }), "Rescue profile cannot carry a loader");
    Check(TrustedBootProfiles.Fingerprint(bootstrapProfile) != TrustedBootProfiles.Fingerprint(bootstrapProfile with { Artifacts = [.. bootstrapProfile.Artifacts, Loader("uefi-local")] }), "laboratory fingerprint covers the SLT-mode loader");
    var menu = BootScriptRenderer.Menu(c, [imported]);
    Check(menu.Contains("--default exit") && !menu.Contains("${shell}") && !menu.Contains("install.wim"), "generated menu no imported commands and safe default");
    Check(menu.Contains("initrd=initrd.img") && menu.Contains("fetch=http://10.77.0.1:8088/boot/v1/assets/rescue-test-rootfs.squashfs"), "live-boot network-only renderer with mandatory decoder extension");
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
        Check((await client.GetStringAsync("/boot/v1/menu.ipxe")).Contains("--default exit"), "actual HTTP menu safe timeout " + cycle);
        Check((await client.GetByteArrayAsync("/boot/v1/assets/rescue-test-rootfs.squashfs")).SequenceEqual(bytes), "exact rootfs decoder alias " + cycle);
        var tasks = Enumerable.Range(0, 8).Select(_ => client.GetByteArrayAsync("/boot/v1/assets/rescue-test-rootfs"));
        Check((await Task.WhenAll(tasks)).All(a => a.SequenceEqual(bytes)), "concurrent positional streaming exact " + cycle);
        using var range = new HttpRequestMessage(HttpMethod.Get, "/boot/v1/assets/rescue-test-rootfs"); range.Headers.Range = new RangeHeaderValue(123, 200);
        using var response = await client.SendAsync(range); Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[123..201]), "HTTP bounded range " + cycle);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/boot/v1/assets/rescue-test-rootfs.squashfs"));
        Check(head.Content.Headers.ContentLength == bytes.Length && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD no payload " + cycle);
        foreach (var path in new[] { "/api/v1/devices", "/agent/v1/jobs", "/boot/v1/assets/admin.token", "/boot/v1/assets/%2e%2e%2fadmin.token" }) Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, "public listener denies " + path);
    }
    // Stop with a slow consumer while a large synthetic file is in flight.
    var largePath = Path.Combine(store, imported.Id, "rootfs");
    using (var large = File.Open(largePath, FileMode.Create, FileAccess.Write)) large.SetLength(32 * 1024 * 1024);
    string largeHash; using (var large = File.OpenRead(largePath)) largeHash = Convert.ToHexStringLower(SHA256.HashData(large));
    var largeProfile = imported with { Artifacts = imported.Artifacts.Select(a => a.Id == "rootfs" ? a with { SizeBytes = 32 * 1024 * 1024, Sha256 = largeHash } : a).ToArray() };
    var slowServer = await BootHttpServer.Start(launch with { Profiles = [largeProfile] });
    using (var slow = new TcpClient())
    {
        slow.ReceiveBufferSize = 1024; await slow.ConnectAsync(IPAddress.Loopback, port);
        await slow.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /boot/v1/assets/rescue-test-rootfs.squashfs HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"));
        await Task.Delay(100); var timer = System.Diagnostics.Stopwatch.StartNew();
        await slowServer.DisposeAsync(); Check(timer.Elapsed < TimeSpan.FromSeconds(8), "stop bounds active slow HTTP transfer and releases listener");
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
