using System.Text.Json;
using System.Text.Json.Serialization;

namespace SLT.Desktop;

public sealed record Dashboard(int Devices, int Online, int ActiveJobs, int Images, long RepositoryFreeBytes);
public sealed record PartitionInfo(string Name, string Path, long SizeBytes, string FileSystem, string MountPoint)
{
    public string Size => Display.Bytes(SizeBytes);
}
public sealed record DiskInfo(string Id, string Path, string Model, string Serial, long SizeBytes,
    int LogicalSectorBytes, int PhysicalSectorBytes, string Fingerprint, bool IsSystem, bool IsReadOnly,
    bool Mounted, string Health, PartitionInfo[] Partitions, DiskLayout? Layout = null)
{
    public string Size => Display.Bytes(SizeBytes);
    public string Flags => $"{(IsSystem ? "Sistema • " : "")}{(Mounted ? "Montado • " : "")}{(IsReadOnly ? "Somente leitura" : "Leitura/gravação")}";
    public string Label => $"{Model} • {Serial} • {Size} • {Path}";
}
public sealed record Inventory(string DeviceId, string Hostname, string Os, string Mode, string AgentVersion,
    string[] Capabilities, DiskInfo[] Disks, DateTimeOffset CapturedAtUtc, string[] Notes, FileRootInfo[]? FileRoots = null);
public sealed record Device(string Id, string Hostname, string Mode, string TrustStatus,
    DateTimeOffset LastSeenAtUtc, Inventory? Inventory)
{
    public string Status => TrustStatus == "REVOKED" ? "REVOGADO" : DateTimeOffset.UtcNow - LastSeenAtUtc < TimeSpan.FromSeconds(45) ? $"ONLINE / {Mode}" : "OFFLINE";
    public string LastSeen => LastSeenAtUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string OperatingSystem => Inventory?.Os ?? "Aguardando inventário";
    public string Label => $"{Hostname} • {Mode} • {Status}";
}
public sealed record Enrollment(string Id, string Hostname, string Mode, string Code,
    DateTimeOffset ExpiresAtUtc, string Status)
{
    public string Expires => ExpiresAtUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
}
public sealed record JobPlan(string Id, string Kind, string DeviceId, string DiskFingerprint, string DiskPath,
    long DiskSizeBytes, int LogicalSectorBytes, string ImageId, string Challenge, DateTimeOffset ExpiresAtUtc, bool Consent,
    [property: JsonPropertyName("clone")] CloneOptions? CloneOptions = null, FileRequest? File = null, DeploymentOptions? Deployment = null);
public sealed record Job(string Id, JobPlan Plan, string State, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc, JsonElement? Progress, JsonElement? Result, string? TemporaryPassword = null)
{
    public string Kind => Plan.Kind;
    public string DiskPath => Plan.File is { } files
        ? $"{files.RootId}/{files.RelativePath}{(Plan.Kind == "FILE_BACKUP" ? $" • {files.Paths.Length} caminhos" : "")}" : Plan.DiskPath;
    public string Created => CreatedAtUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string ProgressText => Progress is { ValueKind: JsonValueKind.Object } p
        ? $"{ReadString(p, "phase")} • {Display.Bytes(ReadLong(p, "bytesProcessed"))} / {Display.Bytes(ReadLong(p, "bytesTotal"))}" : "—";
    private static string ReadString(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
    private static long ReadLong(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
public sealed record ImageChunk(int Sequence, long OffsetBytes, int SizeBytes, string Sha256);
public sealed record ImageManifest(int SchemaVersion, string Id, string Name, string State,
    string SourceDeviceId, string SourceFingerprint, long LogicalSizeBytes, int LogicalSectorBytes,
    DateTimeOffset CreatedAtUtc, ImageChunk[] Chunks, DiskLayout? Layout = null)
{
    public string Size => Display.Bytes(LogicalSizeBytes);
    public string Created => CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string Label => $"{Name} • {Size} • {State} • {Id}";
}
public sealed record AuditEvent(long Id, DateTimeOffset TimestampUtc, string Actor, string Action,
    JsonElement Details, string PreviousHash, string EventHash)
{
    public string When => TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}
public static class Display
{
    public static string Bytes(long value)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var n = (double)value;
        var i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
}
