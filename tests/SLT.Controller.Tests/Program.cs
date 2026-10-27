using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Slt.Controller;

var root = Path.Combine(Path.GetTempPath(), "slt-controller-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
void Reject(Action action, string message)
{
    try { action(); } catch (ApiException) { Check(true, message); return; } throw new Exception("Expected rejection: " + message);
}
try
{
    FeatureTests.Run(Check);
    var certs = new Certificates(root, IPAddress.Loopback);
    using var db = new Store(root); var c = new Controller(db, certs, Path.Combine(root, "repo"));
    using var key = RSA.Create(2048);
    var csr = new CertificateRequest("CN=Untrusted", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
    var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    Reject(() => c.Enroll(new("pc", "SHELL", csr, secret)), "reject unknown agent mode");
    var e = c.Enroll(new("fixture-source", "RESCUE", csr, secret));
    Reject(() => c.GetEnrollment(e.Id, "bad"), "enrollment polling requires secret");
    Reject(() => c.ApproveEnrollment(e.Id, "bad"), "enrollment approval requires physical code");
    var approved = c.ApproveEnrollment(e.Id, e.Code); var id = approved.DeviceId!;
    Reject(() => c.ApproveEnrollment(e.Id, e.Code), "one-time enrollment approval");
    using var client = X509Certificate2.CreateFromPem(approved.CertificatePem!);
    Check(c.Authenticate(client) == id, "issued certificate authenticates registered device");
    using var unknownKey = RSA.Create(2048);
    using var unknown = new CertificateRequest("CN=unknown", unknownKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    Reject(() => c.Authenticate(unknown), "untrusted certificate rejected");
    var disk = new DiskInfo("fixture", "fixture.img", "Synthetic", "SOURCE", Controller.ChunkSize + 512, 512, 512, "", false, false, false, "UNKNOWN", []);
    disk = disk with { Fingerprint = disk.ComputeFingerprint() };
    var inventory = new Inventory(id, "fixture-source", "Linux", "RESCUE", "0.1.0", ["INVENTORY", "SMART", "CAPTURE", "RESTORE"], [disk], DateTimeOffset.UtcNow, ["SYNTHETIC_FIXTURE"]);
    Reject(() => c.Inventory(id, inventory with { DeviceId = Wire.Id() }), "certificate identity binds inventory");
    Reject(() => c.Inventory(id, inventory with { Disks = [disk with { Fingerprint = "forged" }] }), "fingerprint checked against geometry");
    c.Inventory(id, inventory);
    Reject(() => c.Plan(new("FORMAT", id, disk.Fingerprint, null, true)), "unsupported commands rejected");
    Reject(() => c.Plan(new("CAPTURE", id, disk.Fingerprint, null, false)), "consent required");
    var j = c.Plan(new("CAPTURE", id, disk.Fingerprint, null, true));
    Reject(() => c.Approve(j.Id, "bad"), "contextual confirmation required");
    c.Inventory(id, inventory with { Disks = [disk with { Mounted = true }] });
    Reject(() => c.Approve(j.Id, j.Plan.Challenge), "mounted disk rejected after plan");
    c.Inventory(id, inventory); c.Approve(j.Id, j.Plan.Challenge);
    var another = c.Plan(new("SMART", id, disk.Fingerprint, null, true));
    Reject(() => c.Approve(another.Id, another.Plan.Challenge), "durable per-device conflict lock");
    var assignment = c.Next(id)!;
    using var signingKey = certs.Server.GetRSAPublicKey()!;
    Check(signingKey.VerifyData(Convert.FromBase64String(assignment.Payload), Convert.FromBase64String(assignment.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "job signature verifies byte-exact payload");
    Check(c.Next(id) == null, "assignment not replayed after dispatch");
    Reject(() => c.Complete(id, j.Id), "partial image cannot become available");
    var bytes = RandomNumberGenerator.GetBytes(Controller.ChunkSize);
    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    Reject(() => c.Upload(id, j.Id, 0, bytes, "00"), "corrupt transfer hash rejected");
    Reject(() => c.Upload(id, j.Id, 1, new byte[512], Wire.Hash("wrong")), "out-of-order chunk rejected");
    c.Upload(id, j.Id, 0, bytes, hash); c.Upload(id, j.Id, 0, bytes, hash);
    Check(c.GetImage(j.Plan.ImageId).Chunks.Count == 1, "duplicate identical chunk is idempotent");
    Reject(() => c.Complete(id, j.Id), "missing final chunk rejected");
    var tail = RandomNumberGenerator.GetBytes(512); c.Upload(id, j.Id, 1, tail, Convert.ToHexStringLower(SHA256.HashData(tail)));
    var manifest = c.Complete(id, j.Id);
    Check(manifest.State == "AVAILABLE" && manifest.Chunks.Sum(x => (long)x.SizeBytes) == disk.SizeBytes, "capture promoted only after complete integrity validation");
    c.Result(id, j.Id, new(true, "captured", ""));
    Reject(() => c.Result(id, j.Id, new(true, "duplicate", "")), "repeated result rejected");
    Reject(() => c.Plan(new("RESTORE", id, disk.Fingerprint, manifest.Id, true)), "restore to same source disk rejected");
    var target = disk with { Path = "target.img", Serial = "TARGET", SizeBytes = 512 };
    target = target with { Fingerprint = target.ComputeFingerprint() };
    c.Inventory(id, inventory with { Disks = [disk, target] });
    Reject(() => c.Plan(new("RESTORE", id, target.Fingerprint, manifest.Id, true)), "smaller restore target rejected");
    target = target with { SizeBytes = disk.SizeBytes, LogicalSectorBytes = 4096 }; target = target with { Fingerprint = target.ComputeFingerprint() };
    c.Inventory(id, inventory with { Disks = [disk, target] });
    Reject(() => c.Plan(new("RESTORE", id, target.Fingerprint, manifest.Id, true)), "sector mismatch rejected");
    target = target with { LogicalSectorBytes = 512 }; target = target with { Fingerprint = target.ComputeFingerprint() };
    c.Inventory(id, inventory with { Disks = [disk, target] });
    var restore = c.Plan(new("RESTORE", id, target.Fingerprint, manifest.Id, true)); c.Approve(restore.Id, restore.Plan.Challenge); c.Next(id);
    Check(c.Download(id, restore.Id, 0).SequenceEqual(bytes), "restore downloads exact verified bytes");
    Reject(() => c.StartVerification(manifest.Id), "verification cannot interrupt an active restore");
    Reject(() => c.Download(Wire.Id(), restore.Id, 0), "image transfer restricted to assigned agent");
    var secondController = new Controller(db, certs, c.Repository);
    Check(secondController.GetJob(restore.Id).State == "INTERRUPTED" && secondController.Next(id) == null, "server restart interrupts without replaying writes");
    File.WriteAllText(Path.Combine(c.ImageDir(manifest.Id), "00000000.bin"), "corrupted");
    Reject(() => c.Verify(manifest.Id), "at-rest corruption invalidates image");
    Check(c.GetImage(manifest.Id).State == "FAILED", "corrupt image unavailable for restore");
    c.Revoke(id); Reject(() => c.Authenticate(client), "revoked certificate rejected immediately");
    var audit = db.Audit();
    Check(audit.Count > 5 && audit[0].PreviousHash == "GENESIS", "persistent audit trail exists");
    for (int i = 0; i < audit.Count; i++)
    {
        var a = audit[i]; if (a.EventHash != Wire.Hash(Wire.Serialize(a with { EventHash = "" })) || (i > 0 && a.PreviousHash != audit[i - 1].EventHash)) throw new Exception("Audit chain invalid");
    }
    Check(true, "audit hash chain validates");
    db.Backup(Path.Combine(root, "snapshot.db")); Check(File.Exists(Path.Combine(root, "snapshot.db")), "consistent SQLite backup succeeds");
    if (OperatingSystem.IsWindows())
    {
        var cleanupProbe = Path.Combine(Path.GetTempPath(), "slt-controller-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(cleanupProbe);
        string? warning = null;
        using (var locked = new FileStream(Path.Combine(cleanupProbe, "locked.db"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Check(!TestCleanup.TryRemove(cleanupProbe, message => warning = message, attempts: 1) &&
                warning != null && Directory.Exists(cleanupProbe), "locked cleanup reports warning without throwing");
        }
        Check(TestCleanup.TryRemove(cleanupProbe, Console.Error.WriteLine) && !Directory.Exists(cleanupProbe), "cleanup succeeds after file handle is released");
    }
    Console.WriteLine($"{passed}/{passed} controller checks passed (synthetic files only).");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally { TestCleanup.TryRemove(root, warning => Console.Error.WriteLine("WARN " + warning)); }
