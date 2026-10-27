using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SLT.WindowsAgent;

if (args.Contains("--e2e-files", StringComparer.Ordinal)) return await WindowsFilesE2E.Run();

var passed = 0;
var total = 0;
void Check(string name, Action test)
{
    total++;
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); Environment.ExitCode = 1; }
}
void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
void Reject(Action action)
{
    try { action(); } catch (Exception) { return; }
    throw new InvalidOperationException("Unsafe input was accepted.");
}
var now = DateTimeOffset.UtcNow;
var deviceId = Guid.NewGuid().ToString();
var plan = new JobPlan(Guid.NewGuid().ToString(), "INVENTORY", deviceId, "", "", 0, 512, "", "CONFIRM", now.AddMinutes(10), true);
using var key = RSA.Create(2048);
var request = new CertificateRequest("CN=SLT-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var cert = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
SignedAssignment Sign(JobPlan value)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
    return new(Convert.ToBase64String(payload), Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
}
Check("signed valid inventory accepted", () => Assert(PinnedTransport.VerifySignedAssignment(Sign(plan), cert, deviceId, now).Id == plan.Id));
Check("tampered payload rejected", () =>
{
    var signed = Sign(plan);
    Reject(() => PinnedTransport.VerifySignedAssignment(signed with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(plan with { Kind = "SMART" }, Wire.Json)) }, cert, deviceId, now));
});
Check("foreign signer rejected", () =>
{
    using var otherKey = RSA.Create(2048);
    using var otherCert = new CertificateRequest("CN=other", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
    Reject(() => PinnedTransport.VerifySignedAssignment(Sign(plan), otherCert, deviceId, now));
});
Check("expired plan rejected", () => Reject(() => PinnedTransport.ValidatePlan(plan with { ExpiresAtUtc = now.AddSeconds(-1) }, deviceId, now)));
Check("unbounded future plan rejected", () => Reject(() => PinnedTransport.ValidatePlan(plan with { ExpiresAtUtc = now.AddDays(2) }, deviceId, now)));
Check("other device rejected", () => Reject(() => PinnedTransport.ValidatePlan(plan, Guid.NewGuid().ToString(), now)));
Check("absent consent rejected", () => Reject(() => PinnedTransport.ValidatePlan(plan with { Consent = false }, deviceId, now)));
Check("restore forbidden on Windows", () => Reject(() => PinnedTransport.ValidatePlan(plan with { Kind = "RESTORE" }, deviceId, now)));
Check("capture forbidden on active Windows", () => Reject(() => PinnedTransport.ValidatePlan(plan with { Kind = "CAPTURE" }, deviceId, now)));
Check("unknown job kind rejected", () => Reject(() => PinnedTransport.ValidatePlan(plan with { Kind = "SHELL" }, deviceId, now)));
Check("non UUID job rejected", () => Reject(() => PinnedTransport.ValidatePlan(plan with { Id = "../../target" }, deviceId, now)));
Check("matching cert DER pin accepted", () => Assert(PinnedTransport.VerifyPin(cert, Convert.ToHexStringLower(SHA256.HashData(cert.RawData)), now)));
Check("wrong TLS pin rejected", () => Assert(!PinnedTransport.VerifyPin(cert, new string('0', 64), now)));
Check("expired TLS certificate rejected", () => Assert(!PinnedTransport.VerifyPin(cert, Convert.ToHexStringLower(SHA256.HashData(cert.RawData)), now.AddDays(2))));
Check("private subnet accepted", () => { foreach (var ip in new[] { "10.1.2.3", "172.16.1.2", "192.168.3.4", "127.0.0.1", "::1", "fd12::1" }) Assert(AgentOptions.IsLocal(IPAddress.Parse(ip))); });
Check("public and wildcard addresses rejected", () => { foreach (var ip in new[] { "8.8.8.8", "172.15.1.2", "0.0.0.0", "::", "2001:4860:4860::8888" }) Assert(!AgentOptions.IsLocal(IPAddress.Parse(ip))); });
Check("plain HTTP URI rejected", () => Reject(() => AgentOptions.EnsureLocalServer(new Uri("http://192.168.1.2:7443"))));
Check("remote arbitrary URI path rejected", () => Reject(() => AgentOptions.EnsureLocalServer(new Uri("https://192.168.1.2:7443/evil"))));
Check("ambiguous DNS hostname rejected", () => Reject(() => AgentOptions.EnsureLocalServer(new Uri("https://example.com:7443"))));
Check("disk fingerprint changes after hardware or topology swap", () =>
{
    var disk = new DiskInfo("disk0", @"\\.\PhysicalDrive0", "Model", "serial", 1024, 512, 4096, "", true, false, true, "Healthy", []);
    Assert(Wire.Fingerprint(disk) == Wire.Fingerprint(disk));
    Assert(Wire.Fingerprint(disk) != Wire.Fingerprint(disk with { Serial = "other" }));
    Assert(Wire.Fingerprint(disk) != Wire.Fingerprint(disk with { Path = @"\\.\PhysicalDrive1" }));
    Assert(Wire.Fingerprint(disk) != Wire.Fingerprint(disk with { LogicalSectorBytes = 4096 }));
});
Check("durable claim prevents replay after process restart", () =>
{
    var directory = Path.Combine(Environment.CurrentDirectory, ".build", "agent-tests", Guid.NewGuid().ToString("N"));
    var state = new SecureState(directory);
    Assert(state.TryClaimJob(plan.Id));
    var restarted = new SecureState(directory);
    Assert(!restarted.TryClaimJob(plan.Id));
    Assert(restarted.TryClaimJob(Guid.NewGuid().ToString()));
    Reject(() => state.TryClaimJob("../bad"));
});
Check("smartctl physical drive mapping is deterministic", () =>
{
    Assert(InventoryCollector.SmartDevicePath(@"\\.\PhysicalDrive0") == "/dev/pd0");
    Assert(InventoryCollector.SmartDevicePath(@"\\.\PhysicalDrive255") == "/dev/pd255");
});
Check("smartctl arbitrary remote path rejected", () =>
{
    Reject(() => InventoryCollector.SmartDevicePath("/dev/pd0; echo injected"));
    Reject(() => InventoryCollector.SmartDevicePath(@"\\.\PhysicalDrive0 --test=long"));
});
Check("smartctl unsupported device range rejected", () => Reject(() => InventoryCollector.SmartDevicePath(@"\\.\PhysicalDrive256")));
FileFeatureTests.Run(Check, Assert, Reject);
Console.WriteLine($"WindowsAgent security checks passed: {passed}/{total}");
return Environment.ExitCode;
