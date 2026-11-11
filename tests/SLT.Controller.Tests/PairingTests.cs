using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Slt.Controller;

// Discovery, short-code pairing, SLT-mode reboot ticket and FORMAT_DISK planning. Synthetic data only.
internal static class PairingTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); } catch (ApiException) { check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        // Same vector as the Windows agent and Go agent tests: the three implementations must agree.
        const string vectorCsr = "-----BEGIN CERTIFICATE REQUEST-----\nSLT\n-----END CERTIFICATE REQUEST-----\n";
        var vectorPin = string.Concat(Enumerable.Repeat("ab", 32)); var vectorClient = string.Concat(Enumerable.Repeat("01", 32)); var vectorServer = string.Concat(Enumerable.Repeat("fe", 32));
        check(Pairing.Code(vectorPin, vectorCsr, vectorClient, vectorServer) == "07111918" && Pairing.Commit(vectorClient) == "72cd6e8422c407fb6d098690f1130b7ded7ec2f7f5e1d30bd9d521f015363793", "pairing code and commitment match the shared test vector");
        check(Pairing.Code(vectorPin.ToUpperInvariant(), vectorCsr, vectorClient.ToUpperInvariant(), vectorServer) == "07111918", "pairing code ignores hexadecimal case");

        check(DiscoveryResponder.ParseRequest("""{"slt":"discover","v":1,"nonce":"00112233AABBCCDD"}"""u8) == "00112233aabbccdd", "discovery accepts a well-formed probe");
        foreach (var bad in new[] { """{"slt":"discover","v":2,"nonce":"00112233aabbccdd"}""", """{"slt":"server","v":1,"nonce":"00112233aabbccdd"}""", """{"slt":"discover","v":1,"nonce":"zz112233aabbccdd"}""", """{"slt":"discover","v":1,"nonce":"0011"}""", "[1,2,3,4,5,6,7,8,9,10,11,12]", new string('x', 300) })
            check(DiscoveryResponder.ParseRequest(Encoding.UTF8.GetBytes(bad)) == null, "discovery ignores malformed probe: " + bad[..Math.Min(28, bad.Length)]);

        var root = Path.Combine(Path.GetTempPath(), "slt-controller-tests-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var certificates = new Certificates(root, IPAddress.Loopback);
            using var db = new Store(root); var controller = new Controller(db, certificates, Path.Combine(root, "repository")) { ServerUrl = "https://192.168.50.10:7443" };

            using (var responder = new DiscoveryResponder(IPAddress.Loopback, 7443, 0, certificates.Pin, "test", IPAddress.IsLoopback))
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                probe.Client.ReceiveTimeout = 5000; var endpoint = new IPEndPoint(IPAddress.Loopback, responder.Port);
                probe.Send(Encoding.UTF8.GetBytes("""{"slt":"discover","v":1,"nonce":"a1b2c3d4e5f60718"}"""), endpoint);
                IPEndPoint? from = null; using var answer = JsonDocument.Parse(probe.Receive(ref from));
                check(answer.RootElement.GetProperty("slt").GetString() == "server" && answer.RootElement.GetProperty("nonce").GetString() == "a1b2c3d4e5f60718" && answer.RootElement.GetProperty("pin").GetString() == certificates.Pin && answer.RootElement.GetProperty("url").GetString() == "https://127.0.0.1:7443", "discovery answers with address, pin and the probe nonce");
                check(!answer.RootElement.EnumerateObject().Any(p => p.Name.Contains("token", StringComparison.OrdinalIgnoreCase)), "discovery reply carries no administrative secret");
            }
            using (var refused = new DiscoveryResponder(IPAddress.Loopback, 7443, 0, certificates.Pin, "test", _ => false))
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                probe.Client.ReceiveTimeout = 400; probe.Send(Encoding.UTF8.GetBytes("""{"slt":"discover","v":1,"nonce":"a1b2c3d4e5f60718"}"""), new IPEndPoint(IPAddress.Loopback, refused.Port));
                var silent = false; try { IPEndPoint? from = null; probe.Receive(ref from); } catch (SocketException) { silent = true; }
                check(silent, "discovery stays silent outside the authorised subnet");
            }

            using var key = RSA.Create(2048);
            var csr = new CertificateRequest("CN=Pairing", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
            string Secret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var secret = Secret(); var nonce = Secret();
            Reject(() => controller.Enroll(new("pc", "WINDOWS", csr, secret, "not-hex")), "malformed pairing commitment rejected");
            var pending = controller.Enroll(new("pc-cliente", "WINDOWS", csr, secret, Pairing.Commit(nonce)));
            check(pending.Status == "COMMITTED" && pending.Code == "" && pending.ServerNonce is { Length: 64 }, "short-code request has no code before the client reveals its nonce");
            Reject(() => controller.ApproveEnrollment(pending.Id, "00000000"), "uncommitted request cannot be approved");
            Reject(() => controller.Reveal(pending.Id, "bad-secret", nonce), "reveal requires the enrollment secret");
            Reject(() => controller.Reveal(pending.Id, secret, Secret()), "reveal must open the earlier commitment");
            var revealed = controller.Reveal(pending.Id, secret, nonce);
            var expected = Pairing.Code(certificates.Pin, csr, nonce, pending.ServerNonce!);
            check(revealed.Status == "PENDING" && revealed.Code == expected, "server derives the same code the client computes");
            Reject(() => controller.Reveal(pending.Id, secret, nonce), "nonce can be revealed only once");
            // A machine impersonating the server presents another certificate, so the client shows another code.
            var impostor = Pairing.Code(Wire.Hash("impostor"), csr, nonce, pending.ServerNonce!);
            check(impostor != expected, "a different server certificate yields a different code");
            Reject(() => controller.ApproveEnrollment(pending.Id, impostor), "code shown by an impersonated server is refused");
            var approved = controller.ApproveEnrollment(pending.Id, expected);
            check(approved.Status == "APPROVED" && approved.DeviceId != null, "matching short code approves the client");

            var blockedSecret = Secret(); var blockedNonce = Secret();
            var blocked = controller.Enroll(new("pc-errado", "WINDOWS", csr, blockedSecret, Pairing.Commit(blockedNonce))); controller.Reveal(blocked.Id, blockedSecret, blockedNonce);
            for (var i = 0; i < 4; i++) try { controller.ApproveEnrollment(blocked.Id, "12345678"); } catch (ApiException) { }
            Reject(() => controller.ApproveEnrollment(blocked.Id, "12345678"), "fifth wrong code rejects the request");
            check(controller.GetEnrollment(blocked.Id, blockedSecret).Status == "REJECTED", "request stays rejected after repeated wrong codes");
            var legacy = controller.Enroll(new("pc-antigo", "RESCUE", csr, Secret()));
            check(legacy.Status == "PENDING" && legacy.Code.Length == 8 && controller.ApproveEnrollment(legacy.Id, legacy.Code).Status == "APPROVED", "legacy pairing with a typed pin still works");
            var rejected = controller.Enroll(new("pc-recusado", "RESCUE", csr, Secret())); controller.RejectEnrollment(rejected.Id);
            Reject(() => controller.ApproveEnrollment(rejected.Id, rejected.Code), "operator rejection is final");

            // SLT mode: a paired Windows client reboots into Rescue and enrols with a single-use ticket.
            DiskInfo Disk(string serial, long bytes)
            {
                var disk = new DiskInfo(serial, "/dev/" + serial.ToLowerInvariant(), "Synthetic", serial, bytes, 512, 512, "", false, false, false, "FIXTURE", []);
                return disk with { Fingerprint = disk.ComputeFingerprint() };
            }
            Device Add(string mode, string[] capabilities, DiskInfo[] disks, string[]? notes = null)
            {
                var id = Wire.Id(); var inventory = new Inventory(id, "pc-" + id[..6], mode, mode, "0.1.0", capabilities, disks, DateTimeOffset.UtcNow, notes ?? ["SYNTHETIC_FIXTURE"]);
                var device = new Device(id, inventory.Hostname, mode, "TRUSTED", DateTimeOffset.UtcNow, inventory); db.Put("device", id, device); return device;
            }
            var loader = Path.Combine(root, "ipxe-local.efi"); var loaderBytes = RandomNumberGenerator.GetBytes(4096); File.WriteAllBytes(loader, loaderBytes);
            var source = new RescueBootSource("http://192.168.50.10:8088", "rescue-test", loader, Convert.ToHexStringLower(SHA256.HashData(loaderBytes)), loaderBytes.Length);
            var windows = Add("WINDOWS", ["INVENTORY", "SMART", "REBOOT_RESCUE"], []);
            var legacyBios = Add("WINDOWS", ["INVENTORY", "SMART"], [], ["MODO_SLT: firmware BIOS/Legacy; use USB ou PXE."]);
            Reject(() => controller.Plan(new("REBOOT_RESCUE", windows.Id, "", null, true)), "SLT mode needs the boot service running");
            controller.RescueBootProvider = () => (source, "");
            Reject(() => controller.Plan(new("REBOOT_RESCUE", windows.Id, "", null, false)), "SLT mode requires consent");
            try { controller.Plan(new("REBOOT_RESCUE", legacyBios.Id, "", null, true)); throw new Exception("BIOS client accepted"); }
            catch (ApiException e) { check(e.Message.Contains("BIOS/Legacy", StringComparison.Ordinal), "SLT mode explains why a client cannot reboot into it"); }
            var reboot = controller.Plan(new("REBOOT_RESCUE", windows.Id, "", null, true, RescueBoot: new(true)));
            check(reboot.Plan.RescueBoot is { Ticket: "", Script: "" } && reboot.Plan.Challenge == $"REINICIAR {windows.Hostname} MODO SLT", "reboot plan carries no ticket before approval");
            Reject(() => controller.Approve(reboot.Id, "REINICIAR"), "reboot requires the contextual confirmation");
            controller.Approve(reboot.Id, reboot.Plan.Challenge);
            var stored = controller.GetJob(reboot.Id).Plan.RescueBoot!;
            check(stored.Ticket.Length == 64 && stored.Script.StartsWith("#!ipxe\n", StringComparison.Ordinal) && stored.Script.Contains("slt.ticket=" + stored.Ticket, StringComparison.Ordinal) && stored.Script.Contains("slt.pin=" + certificates.Pin, StringComparison.Ordinal) && stored.Script.Contains("slt.server=https://192.168.50.10:7443 ", StringComparison.Ordinal) && stored.Script.Contains("http://192.168.50.10:8088/boot/v1/assets/rescue-test-kernel ", StringComparison.Ordinal), "approved reboot plan renders the boot script with server, pin and ticket");
            check(stored.Script.All(ch => ch is '\n' or >= ' ' and < (char)127), "boot script is plain ASCII");
            var listed = Provisioning.WithoutSecrets(controller.GetJob(reboot.Id)).Plan.RescueBoot!;
            check(listed.Ticket == "***" && listed.Script == "***", "administrative listings never show the ticket");
            check(!db.Audit().Any(a => a.Details.Contains(stored.Ticket, StringComparison.Ordinal)), "audit trail never records the ticket");
            var assignment = controller.Next(windows.Id)!;
            check(Encoding.UTF8.GetString(Convert.FromBase64String(assignment.Payload)).Contains(stored.Ticket, StringComparison.Ordinal), "signed assignment delivers the ticket to the paired client only");
            check(controller.RescueLoader(windows.Id, reboot.Id).SequenceEqual(loaderBytes), "client downloads the loader named in the signed plan");
            Reject(() => controller.RescueLoader(legacyBios.Id, reboot.Id), "loader download restricted to the assigned client");
            File.WriteAllBytes(loader, RandomNumberGenerator.GetBytes(4096));
            Reject(() => controller.RescueLoader(windows.Id, reboot.Id), "changed loader is refused");
            File.WriteAllBytes(loader, loaderBytes);
            controller.Result(windows.Id, reboot.Id, new(true, "Reinício agendado.", "REBOOT_SCHEDULED"));
            check(controller.GetJob(reboot.Id).Plan.RescueBoot!.Ticket == "***", "ticket is erased from the finished job");

            Reject(() => controller.Enroll(new("slt-rescue", "RESCUE", csr, Secret(), Ticket: Secret())), "unknown ticket rejected");
            Reject(() => controller.Enroll(new("slt-rescue", "WINPE", csr, Secret(), Ticket: stored.Ticket)), "ticket only enrols a Rescue session");
            var ticketSecret = Secret(); var session = controller.Enroll(new("slt-rescue", "RESCUE", csr, ticketSecret, Ticket: stored.Ticket));
            var rescue = controller.Device(session.DeviceId!);
            check(session.Status == "APPROVED" && rescue.Mode == "RESCUE" && rescue.OriginDeviceId == windows.Id && rescue.UnattendedWrite && rescue.Hostname == windows.Hostname + " (modo SLT)", "ticket enrols the Rescue session linked to its Windows client");
            check(controller.GetEnrollment(session.Id, ticketSecret).CertificatePem != null, "ticket session receives its certificate through the usual polling");
            Reject(() => controller.Enroll(new("slt-rescue", "RESCUE", csr, Secret(), Ticket: stored.Ticket)), "ticket is single use");
            var target = Disk("FORMATME", 512L * 1024 * 1024);
            var rescueInventory = new Inventory(rescue.Id, "slt-rescue", "Linux", "RESCUE", "0.1.0", ["INVENTORY", "SMART", "CAPTURE", "RESTORE", "FORMAT_DISK"], [target], DateTimeOffset.UtcNow, ["SYNTHETIC_FIXTURE"]);
            controller.Inventory(rescue.Id, rescueInventory);
            check(controller.Device(rescue.Id).Hostname == windows.Hostname + " (modo SLT)", "SLT session keeps the client's name instead of the live hostname");

            using (var client = X509Certificate2.CreateFromPem(session.CertificatePem!))
            {
                var local = controller.LocalRescueBoot(windows.Id);
                var localPlan = JsonSerializer.Deserialize<JobPlan>(Convert.FromBase64String(local.Payload), Wire.Json)!;
                check(localPlan.Kind == "REBOOT_RESCUE" && localPlan.RescueBoot!.Ticket.Length == 64 && controller.GetJob(localPlan.Id).State == "RUNNING", "request made at the client keyboard is dispatched at once");
                Reject(() => controller.LocalRescueBoot(windows.Id), "client cannot stack SLT-mode requests");
                controller.Result(windows.Id, localPlan.Id, new(true, "Reinício agendado.", "REBOOT_SCHEDULED"));
                var second = controller.Enroll(new("slt-rescue", "RESCUE", csr, Secret(), Ticket: localPlan.RescueBoot!.Ticket));
                check(controller.Device(rescue.Id).TrustStatus == "REVOKED" && controller.Device(second.DeviceId!).TrustStatus == "TRUSTED", "a new SLT session retires the previous one of the same client");
                Reject(() => controller.Authenticate(client), "retired SLT session certificate no longer authenticates");
                rescue = controller.Device(second.DeviceId!);
                controller.Inventory(rescue.Id, rescueInventory with { DeviceId = rescue.Id });
            }
            controller.Revoke(windows.Id);
            var orphan = controller.Plan(new("INVENTORY", rescue.Id, "", null, true));
            check(orphan.State == "AWAITING_APPROVAL", "SLT session remains usable after its Windows identity is revoked");

            // FORMAT_DISK: typed, Rescue only, with its own challenge.
            Reject(() => controller.Plan(new("FORMAT_DISK", rescue.Id, target.Fingerprint, null, true)), "format requires table and filesystem");
            Reject(() => controller.Plan(new("FORMAT_DISK", rescue.Id, target.Fingerprint, null, false, Format: new("GPT", "NTFS"))), "format requires consent");
            Reject(() => controller.Plan(new("FORMAT_DISK", rescue.Id, target.Fingerprint, null, true, Format: new("GPT", "ZFS"))), "format rejects unsupported filesystem");
            Reject(() => controller.Plan(new("FORMAT_DISK", rescue.Id, target.Fingerprint, null, true, Format: new("GPT", "NTFS", "bad label;rm"))), "format rejects unsafe label");
            Reject(() => controller.Plan(new("FORMAT_DISK", rescue.Id, target.Fingerprint, null, true, Format: new("GPT", "FAT32", "ABCDEFGHIJKL"))), "format rejects label longer than the filesystem allows");
            Reject(() => controller.Plan(new("FORMAT_DISK", windows.Id, target.Fingerprint, null, true, Format: new("GPT", "NTFS"))), "format cannot be scheduled on a Windows client");
            var format = controller.Plan(new("FORMAT_DISK", rescue.Id, target.Fingerprint, null, true, Format: new("GPT", "NTFS", "DADOS")));
            check(format.Plan.Challenge == $"FORMATAR {rescue.Hostname} {target.Fingerprint[..12]}" && format.Plan.Format == new FormatOptions("GPT", "NTFS", "DADOS"), "format plan names the disk in its confirmation");
            controller.Inventory(rescue.Id, rescueInventory with { DeviceId = rescue.Id, Disks = [target with { Mounted = true }] });
            Reject(() => controller.Approve(format.Id, format.Plan.Challenge), "format revalidates the disk before dispatch");
            controller.Inventory(rescue.Id, rescueInventory with { DeviceId = rescue.Id });
            controller.Approve(format.Id, format.Plan.Challenge);
            check(controller.GetJob(format.Id).State == "QUEUED", "format is queued after the typed confirmation");
            var huge = Disk("HUGE", 3L * 1024 * 1024 * 1024 * 1024);
            try { Controller.ValidateFormat(new("MBR", "NTFS"), huge); throw new Exception("MBR above 2 TiB accepted"); } catch (ApiException) { check(true, "MBR refused above 2 TiB"); }
        }
        finally { TestCleanup.TryRemove(root, warning => Console.Error.WriteLine("WARN " + warning)); }
    }
}
