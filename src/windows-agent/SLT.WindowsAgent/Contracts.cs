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
    string[] Capabilities, DiskInfo[] Disks, DateTimeOffset CapturedAtUtc, string[] Notes);
public sealed record EnrollmentRequest(string Hostname, string Mode, string CsrPem, string Secret);
public sealed record EnrollmentCreated(string Id, string Code, DateTimeOffset ExpiresAtUtc);
public sealed record EnrollmentStatus(string Status, string? DeviceId, string? CertificatePem, string? CaPem);
public sealed record EnrollmentPending(string Id, string Secret, string Code, DateTimeOffset ExpiresAtUtc);
public sealed record Identity(string DeviceId);
public sealed record SignedAssignment(string Payload, string Signature);
public sealed record JobPlan(string Id, string Kind, string DeviceId, string DiskFingerprint, string DiskPath,
    long DiskSizeBytes, int LogicalSectorBytes, string ImageId, string Challenge, DateTimeOffset ExpiresAtUtc, bool Consent);
public sealed record JobResult(bool Success, string Message, string Details);
public sealed record JobProgress(string Phase, long BytesProcessed, long BytesTotal);
public sealed record ProgressResponse(bool CancelRequested);
