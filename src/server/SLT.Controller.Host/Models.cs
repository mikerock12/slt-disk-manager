using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    int PhysicalSectorBytes, string Fingerprint, bool IsSystem, bool IsReadOnly, bool Mounted, string Health, PartitionInfo[] Partitions, DiskLayout? Layout = null)
{
    public string ComputeFingerprint() => Wire.Hash($"{Model}|{Serial}|{SizeBytes}|{LogicalSectorBytes}|{Path}");
}
public record Inventory(string DeviceId, string Hostname, string Os, string Mode, string AgentVersion,
    string[] Capabilities, DiskInfo[] Disks, DateTimeOffset CapturedAtUtc, string[] Notes, FileRootInfo[]? FileRoots = null);
public record Device(string Id, string Hostname, string Mode, string TrustStatus, DateTimeOffset LastSeenAtUtc, Inventory? Inventory);
public record EnrollmentRequest(string Hostname, string Mode, string CsrPem, string Secret);
public record Enrollment(string Id, string Hostname, string Mode, string CsrPem, string SecretHash, string Code,
    DateTimeOffset ExpiresAtUtc, string Status, string? DeviceId = null, string? CertificatePem = null);
public record CodeRequest(string Code);
public record ApprovalRequest(string Challenge);
public record PlanRequest(string Kind, string DeviceId, string DiskFingerprint, string? ImageId, bool Consent, FileRequest? File = null, [property:JsonPropertyName("clone")] CloneOptions? CloneOptions = null, DeploymentOptions? Deployment = null);
public record JobPlan(string Id, string Kind, string DeviceId, string DiskFingerprint, string DiskPath,
    long DiskSizeBytes, int LogicalSectorBytes, string ImageId, string Challenge, DateTimeOffset ExpiresAtUtc, bool Consent, FileRequest? File = null, [property:JsonPropertyName("clone")] CloneOptions? CloneOptions = null, DeploymentOptions? Deployment = null, string? StreamId = null);
public record SignedAssignment(string Payload, string Signature);
public record Progress(string Phase, long BytesProcessed, long BytesTotal);
public record JobResult(bool Success, string Message, string Details);
public record Job(string Id, JobPlan Plan, string State, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc = null, Progress? Progress = null, JobResult? Result = null, bool CancelRequested = false, string? TemporaryPassword = null);
public record ImageChunk(int Sequence, long OffsetBytes, int SizeBytes, string Sha256);
public record ImageManifest(int SchemaVersion, string Id, string Name, string State, string SourceDeviceId,
    string SourceFingerprint, long LogicalSizeBytes, int LogicalSectorBytes, DateTimeOffset CreatedAtUtc, List<ImageChunk> Chunks, DiskLayout? Layout = null);
public record DiskLayout(string TableType, int SectorSize, string DiskIdentifier, long FirstUsableLba, long LastUsableLba, LayoutPartition[] Partitions);
public record LayoutPartition(int Number, long StartLba, long LengthLba, string Type, string Uuid, string Name, string Attributes, bool Bootable, string FileSystem, long? MinimumSizeBytes = null);
public record CloneOptions(string Strategy, int ResizePartitionNumber = 0);
public record CaptureCompletion(DiskLayout? Layout = null);
public record CloneRequest(string SourceDeviceId, string SourceDiskFingerprint, string TargetDeviceId, string TargetDiskFingerprint, string Strategy, bool Consent, string? Transfer = null);
public record ClonePreview(string[] Blockers, string[] Warnings, string[] Actions, DiskLayout? SourceLayout, DiskLayout? TargetLayout, int ResizePartitionNumber, long ScratchBytesRequired, long ServerBytesRequired = 0);
public record CloneSession(string Id, string SourceDeviceId, string SourceDiskFingerprint, string TargetDeviceId, string TargetDiskFingerprint,
    string Strategy, string CaptureJobId, string? TargetJobId, string ImageId, string State, DateTimeOffset CreatedAtUtc, ClonePreview Preview, string Transfer = "IMAGE", string? StreamId = null);
// Batch relay between two online Rescue agents. Chunk hashes stay in the catalog; chunk
// bytes are deleted as soon as the target acknowledges them durably written.
public record CloneStream(string Id, string SessionId, string SourceJobId, string TargetJobId, string SourceDeviceId, string SourceFingerprint,
    long LogicalSizeBytes, int LogicalSectorBytes, DiskLayout? Layout, string State, int WindowChunks, int AckedThrough, bool SourceStarted, bool SourceComplete, DateTimeOffset CreatedAtUtc, string? Error = null);
public record StreamStatus(string Id, string State, long LogicalSizeBytes, int LogicalSectorBytes, string SourceFingerprint, DiskLayout? Layout,
    int TotalChunks, int NextSequence, int AckedThrough, bool SourceStarted, bool SourceComplete, List<ImageChunk> Pending);
public record StreamStart(DiskLayout? Layout = null);
public record StreamAck(int Through);
public record FileRootInfo(string Id, string Label, bool CanRead, bool CanWrite);
public record FileRequest(string RootId, string RelativePath = "", string[]? Paths = null, string BundleId = "", string Name = "", string? Cursor = null, int PageSize = 100);
public record FileListRequest(string DeviceId, string RootId, string RelativePath, string? Cursor, int PageSize, bool Consent);
public record BundleEntry(string Id, string RelativePath, bool IsDirectory, long SizeBytes, DateTimeOffset LastWriteTimeUtc, List<ImageChunk> Chunks);
public record FileBundle(int SchemaVersion, string Id, string Name, string State, string SourceDeviceId, string RootId, DateTimeOffset CreatedAtUtc, long TotalBytes, List<BundleEntry> Entries, int? EntryCount = null);
public record BundleStart(List<BundleEntry> Entries, long TotalBytes);
public record LocalImport(string Path, string Name, bool Consent);
public record MediaImport(string Path, string Name);
public record DeploymentOptions(string MediaId, int ImageIndex, string Firmware, bool RebootAfterInstall = false, DeploymentAccount? Account = null, string? ToolSha256 = null);
// Initial local account. Windows unattend needs the temporary password itself; Linux receives
// only its SHA-512-crypt hash. The operator changes it after the first logon.
public record DeploymentAccount(string ComputerName, string UserName, string? Password = null, string? PasswordHash = null, string Locale = "pt-BR", string TimeZone = "");
public record WindowsImage(int Index, string Name, string Description, string Architecture, string Version, long MinimumSizeBytes, string Language = "");
public record DeploymentMedia(string Id, string Name, string FileName, long SizeBytes, string Sha256, List<ImageChunk> Chunks, WindowsImage[] Images, string State, DateTimeOffset CreatedAtUtc, string? Error = null, string Kind = "WINDOWS");
public record AuditEvent(long Id, DateTimeOffset TimestampUtc, string Actor, string Action, string Details, string PreviousHash, string EventHash);
public sealed class ApiException(int status, string message) : Exception(message) { public int Status { get; } = status; }
