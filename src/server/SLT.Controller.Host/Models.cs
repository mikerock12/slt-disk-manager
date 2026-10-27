using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Slt.Controller;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("JSON vazio.");
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Id() => Guid.NewGuid().ToString();
}
public record PartitionInfo(string Name, string Path, long SizeBytes, string FileSystem, string MountPoint);
public record DiskInfo(string Id, string Path, string Model, string Serial, long SizeBytes, int LogicalSectorBytes,
    int PhysicalSectorBytes, string Fingerprint, bool IsSystem, bool IsReadOnly, bool Mounted, string Health, PartitionInfo[] Partitions)
{
    public string ComputeFingerprint() => Wire.Hash($"{Model}|{Serial}|{SizeBytes}|{LogicalSectorBytes}|{Path}");
}
public record Inventory(string DeviceId, string Hostname, string Os, string Mode, string AgentVersion,
    string[] Capabilities, DiskInfo[] Disks, DateTimeOffset CapturedAtUtc, string[] Notes);
public record Device(string Id, string Hostname, string Mode, string TrustStatus, DateTimeOffset LastSeenAtUtc, Inventory? Inventory);
public record EnrollmentRequest(string Hostname, string Mode, string CsrPem, string Secret);
public record Enrollment(string Id, string Hostname, string Mode, string CsrPem, string SecretHash, string Code,
    DateTimeOffset ExpiresAtUtc, string Status, string? DeviceId = null, string? CertificatePem = null);
public record CodeRequest(string Code);
public record ApprovalRequest(string Challenge);
public record PlanRequest(string Kind, string DeviceId, string DiskFingerprint, string? ImageId, bool Consent);
public record JobPlan(string Id, string Kind, string DeviceId, string DiskFingerprint, string DiskPath,
    long DiskSizeBytes, int LogicalSectorBytes, string ImageId, string Challenge, DateTimeOffset ExpiresAtUtc, bool Consent);
public record SignedAssignment(string Payload, string Signature);
public record Progress(string Phase, long BytesProcessed, long BytesTotal);
public record JobResult(bool Success, string Message, string Details);
public record Job(string Id, JobPlan Plan, string State, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc = null, Progress? Progress = null, JobResult? Result = null, bool CancelRequested = false);
public record ImageChunk(int Sequence, long OffsetBytes, int SizeBytes, string Sha256);
public record ImageManifest(int SchemaVersion, string Id, string Name, string State, string SourceDeviceId,
    string SourceFingerprint, long LogicalSizeBytes, int LogicalSectorBytes, DateTimeOffset CreatedAtUtc, List<ImageChunk> Chunks);
public record AuditEvent(long Id, DateTimeOffset TimestampUtc, string Actor, string Action, string Details, string PreviousHash, string EventHash);
public sealed class ApiException(int status, string message) : Exception(message) { public int Status { get; } = status; }
