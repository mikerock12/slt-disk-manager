using System.Net;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using Slt.Controller;

internal static class FeatureTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); } catch (ApiException) { check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        const long MiB = 1024 * 1024;
        DiskInfo Disk(string serial, long bytes, DiskLayout? layout = null)
        {
            var disk = new DiskInfo(serial, serial + ".img", "Synthetic", serial, bytes, 512, 512, "", false, false, false, "FIXTURE", [], layout);
            return disk with { Fingerprint = disk.ComputeFingerprint() };
        }
        var layout = new DiskLayout("GPT", 512, Guid.NewGuid().ToString(), 34, 256 * MiB / 512 - 34,
            [new(1, 2048, 16 * MiB / 512, "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", Guid.NewGuid().ToString(), "EFI", "", false, "vfat"),
             new(2, 18 * MiB / 512, 200 * MiB / 512, "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", Guid.NewGuid().ToString(), "Windows", "", false, "ntfs", 100 * MiB),
             new(3, 220 * MiB / 512, 16 * MiB / 512, "de94bba4-06d1-4d40-a16a-bfd50179d6ac", Guid.NewGuid().ToString(), "WinRE", "0x8000000000000001", false, "ntfs")]);
        var source = Disk("SOURCE", 256 * MiB, layout); var smaller = Disk("SMALLER", 192 * MiB);
        var fit = ClonePlanning.Preview(source, smaller, "FIT");
        check(fit.Blockers.Length == 0 && fit.ResizePartitionNumber == 2, "FIT selects supported OS partition on smaller destination");
        check(fit.TargetLayout!.Partitions.Length == 3 && fit.TargetLayout!.TableType == "GPT" && fit.TargetLayout!.DiskIdentifier == layout.DiskIdentifier, "FIT preserves all partition identities and GPT table");
        check(fit.TargetLayout!.Partitions[0] == layout.Partitions[0] && fit.TargetLayout!.Partitions[2].Uuid == layout.Partitions[2].Uuid && fit.TargetLayout!.Partitions[2].StartLba == layout.Partitions[2].StartLba - 64 * MiB / 512, "FIT preserves EFI and shifts complete WinRE suffix");
        check(layout.Partitions[1].LengthLba == 200 * MiB / 512 && fit.ScratchBytesRequired >= source.SizeBytes + smaller.SizeBytes * 2, "FIT leaves source immutable and reserves workspace");
        check(ClonePlanning.Preview(source, Disk("TOOSMALL", 128 * MiB), "FIT").Blockers.Length > 0, "FIT rejects target below estimated filesystem minimum");
        check(ClonePlanning.Preview(source, smaller, "EXACT_RAW").Blockers.Length > 0 && ClonePlanning.Preview(source, smaller, "PRESERVE_LAYOUT").Blockers.Length > 0, "nonadaptive modes reject smaller destination");
        var larger = ClonePlanning.Preview(source, Disk("LARGER", 320 * MiB), "PRESERVE_LAYOUT");
        check(larger.Blockers.Length == 0 && larger.TargetLayout!.Partitions.SequenceEqual(layout.Partitions) && larger.TargetLayout!.LastUsableLba > layout.LastUsableLba, "larger preserve mode relocates GPT boundary without dropping partitions");
        check(ClonePlanning.Preview(source, source, "FIT").Blockers.Length > 0 && ClonePlanning.Preview(source, smaller with { Mounted = true }, "FIT").Blockers.Length > 0, "clone rejects same identity and mounted target");
        check(ClonePlanning.Preview(source, smaller with { LogicalSectorBytes = 4096 }, "FIT").Blockers.Length > 0, "clone rejects logical sector mismatch");
        Reject(() => ClonePlanning.ValidateLayout(layout with { Partitions = [layout.Partitions[0], layout.Partitions[1] with { StartLba = 2048 }] }, source.SizeBytes), "overlapping source partitions rejected");
        foreach (var path in new[] { "../secret", "a/../../b", "C:/data", "dir\\file", "a:stream", "CON.txt", "dir./file", "a//b" })
            Reject(() => FileRepository.Relative(path), "portable file path rejected: " + path);
        check(FileRepository.Relative("Documentos/ação.txt") == "Documentos/ação.txt", "portable UTF8 filename accepted");
        byte[] Wim(string xml, ushort part = 1, ushort parts = 1, uint imageCount = 1)
        {
            var encoded = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
            var bytes = new byte[208 + encoded.Length]; "MSWIM\0\0\0"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 208);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(40), part); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(42), parts);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), imageCount);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72), (ulong)(uint)encoded.Length | 2UL << 56);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(80), 208); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(88), encoded.Length);
            encoded.CopyTo(bytes.AsSpan(208)); return bytes;
        }
        const string xml = "<WIM><IMAGE INDEX=\"1\"><NAME>Synthetic Windows</NAME><DESCRIPTION>Metadata fixture only</DESCRIPTION><TOTALBYTES>1000</TOTALBYTES><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>19045</BUILD><SPBUILD>1</SPBUILD></VERSION></WINDOWS></IMAGE></WIM>";
        var mediaBytes = Wim(xml);
        var editions = MediaRepository.ReadImages(new MemoryStream(mediaBytes));
        check(editions.Length == 1 && editions[0].Architecture == "AMD64" && editions[0].Index == 1 && editions[0].Version == "10.0.19045.1", "WIM metadata reads bounded image index architecture and Windows version");
        Reject(() => MediaRepository.ReadImages(new MemoryStream(Wim(xml, parts: 2))), "split WIM rejected before deployment");
        Reject(() => MediaRepository.ReadImages(new MemoryStream(Wim(xml, imageCount: 2))), "WIM image count mismatch rejected");
        var invalidOffset = mediaBytes.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(invalidOffset.AsSpan(80), mediaBytes.Length + 1);
        Reject(() => MediaRepository.ReadImages(new MemoryStream(invalidOffset)), "WIM XML offset beyond file rejected");
        var compressedXml = mediaBytes.ToArray(); compressedXml[79] = 4;
        Reject(() => MediaRepository.ReadImages(new MemoryStream(compressedXml)), "compressed unsupported XML resource rejected");
        try
        {
            MediaRepository.ReadImages(new MemoryStream(Wim("<!DOCTYPE WIM [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><WIM>&x;</WIM>")));
            throw new Exception("WIM DTD should be prohibited.");
        }
        catch (System.Xml.XmlException) { check(true, "WIM external XML entities and DTD are prohibited"); }

        var root = Path.Combine(Path.GetTempPath(), "slt-controller-tests-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var certificates = new Certificates(root, IPAddress.Loopback);
            using var db = new Store(root); var controller = new Controller(db, certificates, Path.Combine(root, "repository"));
            FileBundle AwaitBundle(string id)
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    FileBundle bundle; lock (controller.Gate) bundle = controller.Files.Get(id);
                    if (bundle.State is "AVAILABLE" or "FAILED") return bundle;
                    Thread.Sleep(20);
                }
                throw new Exception("Local file import did not finish.");
            }
            Device Add(string mode, DiskInfo disk, string[] capabilities, FileRootInfo[]? roots = null)
            {
                var id = Wire.Id(); var inventory = new Inventory(id, "fixture-" + id[..8], mode, mode, "0.1.0", capabilities, [disk], DateTimeOffset.UtcNow, ["SYNTHETIC_FIXTURE"], roots);
                var device = new Device(id, inventory.Hostname, mode, "TRUSTED", DateTimeOffset.UtcNow, inventory); db.Put("device", id, device); return device;
            }
            var a = Add("RESCUE", source, ["CAPTURE", "RESTORE", "SMART"]);
            var b = Add("RESCUE", smaller, ["CAPTURE", "RESTORE", "RESTORE_FIT", "RESTORE_LAYOUT", "SMART"]);
            Reject(() => controller.PlanClone(new(a.Id, source.Fingerprint, b.Id, smaller.Fingerprint, "FIT", false)), "clone requires consent");
            controller.PlanClone(new(a.Id, source.Fingerprint, b.Id, smaller.Fingerprint, "FIT", true));
            var session = db.All<CloneSession>("clone").Single();
            check(controller.CloneState(session).State == "AWAITING_SOURCE_APPROVAL" && session.TargetJobId == null, "clone plans capture before destination authorization");
            Reject(() => controller.PrepareCloneTarget(session.Id), "clone cannot prepare target before complete capture");
            controller.Approve(session.CaptureJobId, controller.GetJob(session.CaptureJobId).Plan.Challenge);
            var secondJob = controller.Plan(new("SMART", a.Id, source.Fingerprint, null, true));
            Reject(() => controller.Approve(secondJob.Id, secondJob.Plan.Challenge), "same machine jobs cannot overlap clone capture");
            var parallel = controller.Plan(new("SMART", b.Id, smaller.Fingerprint, null, true)); controller.Approve(parallel.Id, parallel.Plan.Challenge);
            check(controller.GetJob(parallel.Id).State == "QUEUED" && controller.GetJob(session.CaptureJobId).State == "QUEUED", "distinct customer machines execute concurrently");
            if (OperatingSystem.IsWindows())
            {
                var mediaPath = Path.Combine(root, "synthetic.wim"); File.WriteAllBytes(mediaPath, mediaBytes);
                var media = controller.Media.Import(new(mediaPath, "Synthetic metadata fixture"));
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (media.State == "IMPORTING" && DateTime.UtcNow < deadline) { Thread.Sleep(20); lock (controller.Gate) media = controller.Media.Get(media.Id); }
                check(media.State == "AVAILABLE" && media.Sha256 == Convert.ToHexStringLower(SHA256.HashData(mediaBytes)), "local WIM import verifies full media hash before publishing catalog");
                var peDisk = Disk("WINPE_FIXTURE", 64L * 1024 * MiB) with { Path = @"\\.\PhysicalDrive250" }; peDisk = peDisk with { Fingerprint = peDisk.ComputeFingerprint() };
                var pe = Add("WINPE", peDisk, ["INVENTORY", "DEPLOY_WINDOWS"]);
                var options = new DeploymentOptions(media.Id, 1, "GPT_UEFI");
                Reject(() => controller.Plan(new("DEPLOY_WINDOWS", pe.Id, peDisk.Fingerprint, null, false, Deployment: options)), "Windows installation requires explicit consent");
                Reject(() => controller.Plan(new("DEPLOY_WINDOWS", pe.Id, peDisk.Fingerprint, null, true, Deployment: options with { ImageIndex = 999 })), "Windows installation rejects nonexistent edition");
                Reject(() => controller.Plan(new("DEPLOY_WINDOWS", a.Id, source.Fingerprint, null, true, Deployment: options)), "Windows installation cannot be scheduled on Linux Rescue");
                var installation = controller.Plan(new("DEPLOY_WINDOWS", pe.Id, peDisk.Fingerprint, null, true, Deployment: options));
                Reject(() => controller.Approve(installation.Id, "wrong"), "Windows installation requires destination challenge");
                var changedDisk = peDisk with { Serial = "SWAPPED_FIXTURE" }; changedDisk = changedDisk with { Fingerprint = changedDisk.ComputeFingerprint() };
                db.Put("device", pe.Id, pe with { Inventory = pe.Inventory! with { Disks = [changedDisk] } });
                Reject(() => controller.Approve(installation.Id, installation.Plan.Challenge), "Windows installation revalidates destination after disk swap");
                db.Put("device", pe.Id, pe);
                controller.Approve(installation.Id, installation.Plan.Challenge);
                check(controller.GetJob(installation.Id).State == "QUEUED" && controller.GetJob(session.CaptureJobId).State == "QUEUED" && controller.GetJob(parallel.Id).State == "QUEUED", "client3 Windows installation can queue while clients1 and2 run other jobs");
                controller.Next(pe.Id);
                check(controller.Media.Manifest(pe.Id, installation.Id).Chunks.Count == 1 && controller.Media.Download(pe.Id, installation.Id, 0).SequenceEqual(mediaBytes), "WinPE receives only assigned verified deployment media bytes");
                Reject(() => controller.Media.Download(a.Id, installation.Id, 0), "deployment media restricted to assigned WinPE client");
                File.WriteAllBytes(Path.Combine(controller.Repository, "media", media.Id, media.FileName), new byte[mediaBytes.Length]);
                Reject(() => controller.Media.Download(pe.Id, installation.Id, 0), "corrupted deployment media blocked before transfer");
                controller.Cancel(installation.Id);
                controller.Result(pe.Id, installation.Id, new(false, "fixture cancellation", ""));
            }
            controller.Cancel(parallel.Id); controller.Cancel(session.CaptureJobId);
            var unavailable = b with { Inventory = b.Inventory! with { Capabilities = ["RESTORE"] } }; db.Put("device", b.Id, unavailable);
            Reject(() => controller.PlanClone(new(a.Id, source.Fingerprint, b.Id, smaller.Fingerprint, "FIT", true)), "clone refuses destination without adaptive engine capability");

            var shrinkPreview = ClonePlanning.Preview(source, smaller, "FIT", stream: true);
            check(shrinkPreview.Blockers.Length == 0 && shrinkPreview.ScratchBytesRequired == 0 && shrinkPreview.TargetLayout!.Partitions[1].LengthLba == 136 * MiB / 512, "batch FIT shrinks the OS partition without Rescue scratch");
            var shrinkTarget = Add("RESCUE", Disk("SHRINK_TARGET", 192 * MiB), ["CAPTURE", "RESTORE", "CLONE_STREAM", "STREAM_FIT"]);
            var shrinkSource = Add("RESCUE", source, ["CAPTURE", "RESTORE", "CLONE_STREAM"]);
            Reject(() => controller.PlanClone(new(shrinkSource.Id, source.Fingerprint, shrinkTarget.Id, shrinkTarget.Inventory!.Disks[0].Fingerprint, "FIT", true, "STREAM")), "batch shrink requires STREAM_SHRINK on the destination");
            var grow = ClonePlanning.Preview(source, Disk("GROW", 320 * MiB), "FIT", stream: true);
            check(grow.Blockers.Length == 0 && grow.ScratchBytesRequired == 0 && grow.ServerBytesRequired == 0 && ClonePlanning.Preview(source, Disk("GROW", 320 * MiB), "FIT").ServerBytesRequired == source.SizeBytes, "batch FIT to larger disk needs neither Rescue scratch nor a full server image");
            var streamSource = Disk("STREAM_SOURCE", 3 * Controller.ChunkSize + 512); var streamTarget = Disk("STREAM_TARGET", streamSource.SizeBytes);
            var sa = Add("RESCUE", streamSource, ["CAPTURE", "RESTORE", "CLONE_STREAM"]); var sb = Add("RESCUE", streamTarget, ["CAPTURE", "RESTORE", "CLONE_STREAM"]);
            var legacy = Add("RESCUE", Disk("LEGACY_TARGET", streamSource.SizeBytes), ["CAPTURE", "RESTORE"]);
            Reject(() => controller.PlanClone(new(sa.Id, streamSource.Fingerprint, legacy.Id, legacy.Inventory!.Disks[0].Fingerprint, "EXACT_RAW", true, "STREAM")), "batch clone requires CLONE_STREAM on both Rescue agents");
            controller.StreamWindowChunks = 2;
            var imagesBefore = db.All<ImageManifest>("image").Count;
            controller.PlanClone(new(sa.Id, streamSource.Fingerprint, sb.Id, streamTarget.Fingerprint, "EXACT_RAW", true, "STREAM"));
            var ss = db.All<CloneSession>("clone").Single(c => c.Transfer == "STREAM"); string cap = ss.CaptureJobId, tgt = ss.TargetJobId!, sid = ss.StreamId!;
            check(controller.CloneState(ss).State == "AWAITING_SOURCE_APPROVAL" && db.All<ImageManifest>("image").Count == imagesBefore && ss.Preview.ServerBytesRequired == 2L * Controller.ChunkSize, "batch clone plans both jobs and reserves only the window, not a full image");
            Reject(() => controller.PrepareCloneTarget(ss.Id), "batch clone has no separate target preparation");
            controller.Approve(cap, controller.GetJob(cap).Plan.Challenge);
            check(controller.CloneState(ss).State == "AWAITING_TARGET_APPROVAL", "batch clone asks for destination authorization after source");
            Reject(() => controller.Approve(tgt, "AUTORIZAR"), "batch destination still requires its APAGAR challenge");
            controller.Approve(tgt, controller.GetJob(tgt).Plan.Challenge); controller.Next(sa.Id); controller.Next(sb.Id);
            check(controller.CloneState(ss).State == "STREAMING", "source and destination run at the same time");
            var data = RandomNumberGenerator.GetBytes((int)streamSource.SizeBytes); Array.Clear(data, 3 * Controller.ChunkSize, 512);
            byte[] Part(int seq) => data.AsSpan(seq * Controller.ChunkSize, (int)Math.Min(Controller.ChunkSize, data.Length - (long)seq * Controller.ChunkSize)).ToArray();
            string H(byte[] x) => Convert.ToHexStringLower(SHA256.HashData(x));
            Reject(() => controller.Upload(sa.Id, cap, 0, Part(0), H(Part(0))), "batch source must announce verified layout before data");
            controller.StreamSourceStart(sa.Id, cap, new());
            controller.Upload(sa.Id, cap, 0, Part(0), H(Part(0))); controller.Upload(sa.Id, cap, 1, Part(1), H(Part(1)));
            try { controller.Upload(sa.Id, cap, 2, Part(2), H(Part(2))); throw new Exception("Batch window not enforced."); }
            catch (ApiException e) when (e.Status == 429) { check(true, "server holds at most the configured window of batches"); }
            var status = controller.StreamStatusFor(sb.Id, tgt);
            check(status.TotalChunks == 4 && status.Pending.Select(p => p.Sequence).SequenceEqual([0, 1]) && controller.Download(sb.Id, tgt, 0).SequenceEqual(Part(0)), "destination receives verified pending batches");
            Reject(() => controller.Download(sa.Id, tgt, 0), "batch download restricted to assigned destination");
            Reject(() => controller.StreamAcknowledge(sb.Id, tgt, new(3)), "destination cannot acknowledge batches not received");
            controller.StreamAcknowledge(sb.Id, tgt, new(0));
            check(!File.Exists(Path.Combine(controller.StreamDir(sid), "00000000.bin")) && File.Exists(Path.Combine(controller.StreamDir(sid), "00000001.bin")), "acknowledged batch deleted from server while pending batch is kept");
            Reject(() => controller.Download(sb.Id, tgt, 0), "discarded batch cannot be requested again");
            Reject(() => controller.Upload(sa.Id, cap, 2, [], H(Part(2)), zero: true), "zero batch marker refused unless its hash is the all-zero hash");
            controller.Upload(sa.Id, cap, 2, Part(2), H(Part(2)));
            try { controller.Upload(sa.Id, cap, 3, Part(3), H(Part(3))); throw new Exception("Batch window not enforced after ack."); }
            catch (ApiException e) when (e.Status == 429) { }
            controller.StreamAcknowledge(sb.Id, tgt, new(2)); controller.Upload(sa.Id, cap, 3, [], Controller.ZeroHash(512), zero: true);
            check(!File.Exists(Path.Combine(controller.StreamDir(sid), "00000003.bin")) && controller.StreamStatusFor(sb.Id, tgt).Pending.Single().Sha256 == Controller.ZeroHash(512), "all-zero batch crosses without bytes and keeps its full-size hash");
            Reject(() => controller.Result(sa.Id, cap, new(true, "fixture", "")), "source cannot finish before declaring all batches sent");
            controller.Complete(sa.Id, cap); controller.Result(sa.Id, cap, new(true, "fixture", ""));
            check(controller.GetJob(cap).State == "COMPLETED" && controller.CloneState(ss).State == "STREAMING", "source machine is released while destination drains last batches");
            Reject(() => controller.Result(sb.Id, tgt, new(true, "fixture", "")), "destination cannot complete before acknowledging every batch");
            check(controller.Download(sb.Id, tgt, 3).SequenceEqual(Part(3)), "last partial batch delivered");
            controller.StreamAcknowledge(sb.Id, tgt, new(3)); controller.Result(sb.Id, tgt, new(true, "fixture", ""));
            check(controller.CloneState(ss).State == "COMPLETED" && controller.GetStream(sid).State == "COMPLETED" && !Directory.Exists(controller.StreamDir(sid)) && db.Chunks(sid, "stream").Count == 4, "completed batch clone leaves no disk data on server and keeps only hashes");

            controller.PlanClone(new(sa.Id, streamSource.Fingerprint, sb.Id, streamTarget.Fingerprint, "EXACT_RAW", true, "STREAM"));
            var failing = db.All<CloneSession>("clone").Single(c => c.Transfer == "STREAM" && c.Id != ss.Id);
            controller.Approve(failing.CaptureJobId, controller.GetJob(failing.CaptureJobId).Plan.Challenge); controller.Approve(failing.TargetJobId!, controller.GetJob(failing.TargetJobId!).Plan.Challenge);
            controller.Next(sa.Id); controller.Next(sb.Id); controller.StreamSourceStart(sa.Id, failing.CaptureJobId, new()); controller.Upload(sa.Id, failing.CaptureJobId, 0, Part(0), H(Part(0)));
            controller.Result(sb.Id, failing.TargetJobId!, new(false, "falha sintética no destino", ""));
            check(controller.GetStream(failing.StreamId!).State == "FAILED" && controller.GetJob(failing.CaptureJobId).CancelRequested && !Directory.Exists(controller.StreamDir(failing.StreamId!)) && controller.CloneState(failing).State == "FAILED", "destination failure stops the source and discards pending batches");
            Reject(() => controller.Upload(sa.Id, failing.CaptureJobId, 1, Part(1), H(Part(1))), "source cannot keep sending after destination failure");
            controller.Result(sa.Id, failing.CaptureJobId, new(false, "cancelado", ""));

            controller.PlanClone(new(sa.Id, streamSource.Fingerprint, sb.Id, streamTarget.Fingerprint, "EXACT_RAW", true, "STREAM"));
            var restarted = db.All<CloneSession>("clone").Single(c => c.Transfer == "STREAM" && c.Id != ss.Id && c.Id != failing.Id);
            controller.Approve(restarted.CaptureJobId, controller.GetJob(restarted.CaptureJobId).Plan.Challenge);
            _ = new Controller(db, certificates, controller.Repository);
            check(controller.GetStream(restarted.StreamId!).State == "FAILED" && controller.GetJob(restarted.CaptureJobId).State == "INTERRUPTED" && !Directory.Exists(controller.StreamDir(restarted.StreamId!)), "server restart fails open batch clones and discards their data");
            controller.Cancel(restarted.TargetJobId!);

            check(Provisioning.Sha512Crypt("Hello world!", "saltstring") == "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1", "SHA-512-crypt matches the reference vector");
            var temporary = Provisioning.TemporaryPassword();
            check(temporary.Length == 14 && temporary.Any(char.IsAsciiLetterUpper) && temporary.Any(char.IsAsciiLetterLower) && temporary.Any(char.IsAsciiDigit), "temporary password meets Windows complexity");
            var linuxMedia = new DeploymentMedia(Wire.Id(), "Xubuntu fixture", "media.iso", 4096, new string('a', 64), [], [new(1, "Xubuntu 26.04", "fixture", "AMD64", "26.04", 16L * 1024 * MiB, "pt-BR")], "AVAILABLE", DateTimeOffset.UtcNow, Kind: "LINUX");
            db.Put("media", linuxMedia.Id, linuxMedia);
            var linuxDisk = Disk("LINUX_TARGET", 64L * 1024 * MiB) with { Path = "/dev/sda" }; linuxDisk = linuxDisk with { Fingerprint = linuxDisk.ComputeFingerprint() };
            var linuxClient = Add("RESCUE", linuxDisk, ["INVENTORY", "CAPTURE", "RESTORE", "DEPLOY_LINUX"]);
            var linuxOptions = new DeploymentOptions(linuxMedia.Id, 1, "GPT_UEFI", false, new("cliente1", "tecnico"));
            Reject(() => controller.Plan(new("DEPLOY_LINUX", linuxClient.Id, linuxDisk.Fingerprint, null, true, Deployment: linuxOptions with { Account = null })), "Linux installation requires an initial account");
            Reject(() => controller.Plan(new("DEPLOY_LINUX", linuxClient.Id, linuxDisk.Fingerprint, null, true, Deployment: linuxOptions with { Account = new("cliente1", "root") })), "reserved account names rejected");
            Reject(() => controller.Plan(new("DEPLOY_LINUX", linuxClient.Id, linuxDisk.Fingerprint, null, true, Deployment: linuxOptions with { Account = new("cliente1", "tecnico", Password: "chosen") })), "operator cannot inject a password; the server generates it");
            var linuxJob = controller.Plan(new("DEPLOY_LINUX", linuxClient.Id, linuxDisk.Fingerprint, null, true, Deployment: linuxOptions));
            var stored = controller.GetJob(linuxJob.Id);
            check(linuxJob.TemporaryPassword is { Length: 14 } && stored.TemporaryPassword == null && stored.Plan.Deployment!.Account!.Password == null && stored.Plan.Deployment.Account.PasswordHash!.StartsWith("$6$") && stored.Plan.Deployment.Account.TimeZone == "America/Sao_Paulo", "Linux plan carries only the password hash; the password is returned once");
            check(!db.Audit().Any(e => e.Details.Contains(linuxJob.TemporaryPassword!) || e.Details.Contains(stored.Plan.Deployment.Account.PasswordHash!)), "temporary password and hash never reach the audit log");
            check(stored.Plan.Challenge.StartsWith("APAGAR ") && stored.Plan.Kind == "DEPLOY_LINUX", "Linux installation requires destination APAGAR challenge");
            Reject(() => controller.Plan(new("DEPLOY_WINDOWS", linuxClient.Id, linuxDisk.Fingerprint, null, true, Deployment: linuxOptions)), "Linux media cannot be planned as Windows installation");
            Reject(() => controller.Plan(new("CONVERT_GPT", linuxClient.Id, linuxDisk.Fingerprint, null, true)), "MBR to GPT conversion requires a UEFI WinPE agent");
            var uefiDisk = Disk("UEFI_TARGET", 64L * 1024 * MiB) with { Path = @"\\.\PhysicalDrive0" }; uefiDisk = uefiDisk with { Fingerprint = uefiDisk.ComputeFingerprint() };
            var uefiPe = Add("WINPE", uefiDisk, ["INVENTORY", "CONVERT_GPT"]);
            Reject(() => controller.Plan(new("CONVERT_GPT", uefiPe.Id, uefiDisk.Fingerprint, null, false)), "conversion requires explicit consent");
            var winMedia = new DeploymentMedia(Wire.Id(), "Windows fixture", "install.esd", 4096, new string('b', 64), [], [new(1, "Windows 10 Pro", "fixture", "AMD64", "10.0", 32L * 1024 * MiB, "pt-BR")], "AVAILABLE", DateTimeOffset.UtcNow);
            db.Put("media", winMedia.Id, winMedia);
            Reject(() => controller.Plan(new("CONVERT_GPT", uefiPe.Id, uefiDisk.Fingerprint, null, true)), "conversion requires Windows media that provides MBR2GPT");
            var toolDir = Path.Combine(controller.Repository, "media", winMedia.Id, "tools", "1"); Directory.CreateDirectory(toolDir);
            var toolBytes = RandomNumberGenerator.GetBytes(2048); File.WriteAllBytes(Path.Combine(toolDir, "MBR2GPT.EXE"), toolBytes);
            Reject(() => controller.Plan(new("CONVERT_GPT", uefiPe.Id, uefiDisk.Fingerprint, null, true, Deployment: new(linuxMedia.Id, 1, "GPT_UEFI"))), "conversion tool cannot come from Linux media");
            var conversion = controller.Plan(new("CONVERT_GPT", uefiPe.Id, uefiDisk.Fingerprint, null, true, Deployment: new(winMedia.Id, 1, "GPT_UEFI")));
            check(conversion.Plan.Deployment!.ToolSha256 == Convert.ToHexStringLower(SHA256.HashData(toolBytes)), "signed conversion plan binds the MBR2GPT hash from operator media");
            Reject(() => controller.Approve(conversion.Id, "APAGAR"), "conversion requires its own CONVERTER challenge");
            controller.Approve(conversion.Id, conversion.Plan.Challenge);
            check(conversion.Plan.Challenge.StartsWith("CONVERTER ") && controller.Next(uefiPe.Id) != null && controller.GetJob(conversion.Id).State == "RUNNING", "approved CONVERT_GPT dispatches as signed typed job");
            check(controller.Media.ToolDownload(uefiPe.Id, conversion.Id).SequenceEqual(toolBytes), "WinPE receives the exact planned MBR2GPT");
            Reject(() => controller.Media.ToolDownload(linuxClient.Id, conversion.Id), "conversion tool restricted to the assigned WinPE agent");
            File.WriteAllBytes(Path.Combine(toolDir, "MBR2GPT.EXE"), new byte[2048]);
            Reject(() => controller.Media.ToolDownload(uefiPe.Id, conversion.Id), "tool changed after planning is blocked");
            controller.Result(uefiPe.Id, conversion.Id, new(true, "fixture", ""));
            controller.Cancel(linuxJob.Id);
            check(controller.GetJob(linuxJob.Id).Plan.Deployment!.Account!.PasswordHash == "***", "account secret erased from the stored plan once the job ends");
            var windowsAccount = new DeploymentAccount("CLIENTE2", "tecnico", Password: temporary);
            var windowsJob = stored with { Plan = stored.Plan with { Kind = "DEPLOY_WINDOWS", Deployment = stored.Plan.Deployment with { Account = windowsAccount } } };
            var shown = Provisioning.WithoutSecrets(windowsJob);
            check(shown.Plan.Deployment!.Account!.Password == "***" && windowsJob.Plan.Deployment!.Account!.Password == temporary && !Wire.Serialize(shown).Contains(temporary), "admin job listings mask the Windows temporary password");

            var fileDevice = Add("WINDOWS", Disk("FILES", 8 * MiB), ["FILE_LIST", "FILE_BACKUP", "FILE_RESTORE"], [new("source", "Origem", true, false), new("destination", "Destino", false, true)]);
            if (OperatingSystem.IsWindows())
            {
                var localPath = Path.Combine(root, "local-selected"); Directory.CreateDirectory(Path.Combine(localPath, "empty"));
                var localBytes = RandomNumberGenerator.GetBytes(Controller.ChunkSize + 512); File.WriteAllBytes(Path.Combine(localPath, "ação.bin"), localBytes); File.WriteAllBytes(Path.Combine(localPath, "zero.bin"), []);
                Reject(() => controller.Files.Import(new(localPath, "Local selected", false)), "server local file import requires consent");
                Reject(() => controller.Files.Import(new(controller.Repository, "Forbidden repository", true)), "server local file import cannot expose operational repository");
                var imported = AwaitBundle(controller.Files.Import(new(localPath, "Local selected", true)).Id);
                check(imported.State == "AVAILABLE" && imported.TotalBytes == localBytes.Length && imported.Entries.Count == 4 && imported.Entries.Any(e => e.IsDirectory && e.RelativePath.EndsWith("/empty")), "server local folder import preserves UTF8 files empty directories and multi-chunk data");
                var catalog=db.FileCatalog().Single(item=>item.Id==imported.Id);
                check(catalog.EntryCount==4 && catalog.Entries.Count==0 && catalog.TotalBytes==localBytes.Length,"file catalog preserves counts without retransmitting whole manifests");
                var localRestore = controller.Plan(new("FILE_RESTORE", fileDevice.Id, "", null, true, new("destination", "server-delivery", BundleId: imported.Id))); controller.Approve(localRestore.Id, localRestore.Plan.Challenge); controller.Next(fileDevice.Id);
                var entry = imported.Entries.Single(e => e.RelativePath.EndsWith("/ação.bin"));
                var delivered = entry.Chunks.SelectMany(chunk => controller.Files.Download(fileDevice.Id, localRestore.Id, entry.Id, chunk.Sequence)).ToArray();
                check(delivered.SequenceEqual(localBytes), "imported server files delivered byte-exact to a selected client");
                controller.Result(fileDevice.Id, localRestore.Id, new(true, "fixture", ""));
            }
            Reject(() => controller.Files.List(new(fileDevice.Id, "missing", "", null, 100, true)), "file access outside local declared roots rejected");
            Reject(() => controller.Files.List(new(fileDevice.Id, "source", "", null, 201, true)), "file page bound enforced");
            Reject(() => controller.Plan(new("FILE_BACKUP", fileDevice.Id, "", null, true, new("source", Paths: ["docs", "docs/a.txt"], Name: "backup"))), "overlapping backup selection rejected");
            var backup = controller.Plan(new("FILE_BACKUP", fileDevice.Id, "", null, true, new("source", Paths: ["docs"], Name: "Fixture")));
            Reject(() => controller.Approve(backup.Id, "wrong"), "selected backup requires contextual approval");
            controller.Approve(backup.Id, backup.Plan.Challenge); controller.Next(fileDevice.Id);
            var directory = new BundleEntry(Wire.Id(), "docs", true, 0, DateTimeOffset.UtcNow, []);
            var file = new BundleEntry(Wire.Id(), "docs/ação.txt", false, 512, DateTimeOffset.UtcNow, []);
            Reject(() => controller.Files.Begin(fileDevice.Id, backup.Id, new([directory, file with { RelativePath = "other.txt" }], 512)), "agent cannot add files outside approved selection");
            Reject(() => controller.Files.Begin(fileDevice.Id, backup.Id, new([directory, file, file with { Id = Wire.Id(), RelativePath = "DOCS/AÇÃO.TXT" }], 1024)), "case collision in portable bundle rejected");
            controller.Files.Begin(fileDevice.Id, backup.Id, new([directory, file], 512));
            Reject(() => controller.Files.Complete(fileDevice.Id, backup.Id), "incomplete file bundle cannot become available");
            var bytes = RandomNumberGenerator.GetBytes(512); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Reject(() => controller.Files.Upload(fileDevice.Id, backup.Id, file.Id, 0, bytes, new string('0', 64)), "file transfer corrupted hash rejected");
            controller.Files.Upload(fileDevice.Id, backup.Id, file.Id, 0, bytes, hash); controller.Files.Upload(fileDevice.Id, backup.Id, file.Id, 0, bytes, hash);
            var completed = controller.Files.Complete(fileDevice.Id, backup.Id); controller.Result(fileDevice.Id, backup.Id, new(true, "fixture", ""));
            check(completed.State == "AVAILABLE" && completed.Entries.Single(e => !e.IsDirectory).Chunks.Count == 1, "verified bundle promoted with idempotent durable chunk receipt");
            Reject(() => controller.Plan(new("FILE_RESTORE", fileDevice.Id, "", null, true, new("destination", BundleId: completed.Id))), "file restore requires explicit new destination folder");
            Reject(() => controller.Plan(new("FILE_RESTORE", fileDevice.Id, "", null, true, new("source", "new-folder", BundleId: completed.Id))), "read-only root cannot receive restore");
            var restore = controller.Plan(new("FILE_RESTORE", fileDevice.Id, "", null, true, new("destination", "new-folder", BundleId: completed.Id))); controller.Approve(restore.Id, restore.Plan.Challenge); controller.Next(fileDevice.Id);
            check(controller.Files.Download(fileDevice.Id, restore.Id, file.Id, 0).SequenceEqual(bytes), "server sends verified selected files byte-exact to client");
            Reject(() => controller.Files.Download(a.Id, restore.Id, file.Id, 0), "file download restricted to assigned client");
            Reject(() => controller.Files.Verify(completed.Id), "verification cannot race active file restore");
            controller.Result(fileDevice.Id, restore.Id, new(true, "fixture", ""));
            var corruptPath = Path.Combine(controller.Files.BundlePath(completed.Id), file.Id, "00000000.bin"); File.WriteAllBytes(corruptPath, new byte[512]);
            var nextRestore = controller.Plan(new("FILE_RESTORE", fileDevice.Id, "", null, true, new("destination", "another-folder", BundleId: completed.Id))); controller.Approve(nextRestore.Id, nextRestore.Plan.Challenge); controller.Next(fileDevice.Id);
            Reject(() => controller.Files.Download(fileDevice.Id, nextRestore.Id, file.Id, 0), "at-rest corrupted file blocked before sending to client");
        }
        finally { TestCleanup.TryRemove(root, warning => Console.Error.WriteLine("WARN " + warning)); }
    }
}
