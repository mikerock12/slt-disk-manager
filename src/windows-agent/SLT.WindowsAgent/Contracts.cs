using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SLT.WindowsAgent;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Fingerprint(DiskInfo disk) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', disk.Model, disk.Serial, disk.SizeBytes.ToString(CultureInfo.InvariantCulture),
            disk.LogicalSectorBytes.ToString(CultureInfo.InvariantCulture), disk.Path))));
}

public sealed record PartitionInfo(string Name, string Path, long SizeBytes, string FileSystem, string MountPoint);
public sealed record DiskInfo(string Id, string Path, string Model, string Serial, long SizeBytes,
    int LogicalSectorBytes, int PhysicalSectorBytes, string Fingerprint, bool IsSystem, bool IsReadOnly,
    bool Mounted, string Health, PartitionInfo[] Partitions);
public sealed record Inventory(string DeviceId, string Hostname, string Os, string Mode, string AgentVersion,
    string[] Capabilities, DiskInfo[] Disks, DateTimeOffset CapturedAtUtc, string[] Notes, FileRootInfo[]? FileRoots = null);
public sealed record EnrollmentRequest(string Hostname, string Mode, string CsrPem, string Secret);
public sealed record EnrollmentCreated(string Id, string Code, DateTimeOffset ExpiresAtUtc);
public sealed record EnrollmentStatus(string Status, string? DeviceId, string? CertificatePem, string? CaPem);
public sealed record EnrollmentPending(string Id, string Secret, string Code, DateTimeOffset ExpiresAtUtc);
public sealed record Identity(string DeviceId);
public sealed record SignedAssignment(string Payload, string Signature);
public sealed record JobPlan(string Id, string Kind, string DeviceId, string DiskFingerprint, string DiskPath,
    long DiskSizeBytes, int LogicalSectorBytes, string ImageId, string Challenge, DateTimeOffset ExpiresAtUtc, bool Consent,
    FileRequest? File = null);
public sealed record JobResult(bool Success, string Message, string Details);
public sealed record JobProgress(string Phase, long BytesProcessed, long BytesTotal);
public sealed record ProgressResponse(bool CancelRequested);
public sealed record FileRootInfo(string Id, string Label, bool CanRead, bool CanWrite);
public sealed record FileRootConfiguration(string Id, string Label, string Path, bool AllowRead, bool AllowWrite);
public sealed record FileRequest(string RootId, string RelativePath, string[] Paths, string BundleId, string Name,
    string? Cursor, int PageSize);
public sealed record FilePage(string RootId, string RelativePath, FilePageEntry[] Entries, string? NextCursor, string DirectoryStamp);
public sealed record FilePageEntry(string Name, string RelativePath, bool IsDirectory, long SizeBytes, DateTimeOffset LastWriteTimeUtc, bool IsLink);
public sealed record FileBundle(int SchemaVersion, string Id, string Name, string State, string SourceDeviceId,
    string RootId, DateTimeOffset CreatedAtUtc, long TotalBytes, BundleEntry[] Entries);
public sealed record BundleEntry(string Id, string RelativePath, bool IsDirectory, long SizeBytes,
    DateTimeOffset LastWriteTimeUtc, FileChunk[] Chunks);
public sealed record FileChunk(int Sequence, long OffsetBytes, int SizeBytes, string Sha256);
