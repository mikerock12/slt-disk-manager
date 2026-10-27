using System.Text.Json.Serialization;

namespace SLT.Desktop;

public sealed record DiskLayout(string TableType, int SectorSize, string DiskIdentifier,
    long FirstUsableLba, long LastUsableLba, LayoutPartition[] Partitions);
public sealed record LayoutPartition(int Number, long StartLba, long LengthLba, string Type,
    string Uuid, string Name, string Attributes, bool Bootable, string FileSystem, long? MinimumSizeBytes)
{
    public string Boot => Bootable ? "Sim" : "—";
}
public sealed record CloneOptions(string Strategy, int ResizePartitionNumber);
public sealed record DeploymentOptions(string MediaId, int ImageIndex, string Firmware, bool RebootAfterInstall);
public sealed record WindowsImage(int Index, string Name, string Description, string Architecture, string Version, long MinimumSizeBytes)
{
    public string Label => $"{Index} • {Name} • {Architecture} • mínimo {Display.Bytes(MinimumSizeBytes)}";
}
public sealed record DeploymentMedia(string Id, string Name, string FileName, long SizeBytes, string Sha256,
    ImageChunk[] Chunks, WindowsImage[] Images, string State, DateTimeOffset CreatedAtUtc)
{
    public string Size => Display.Bytes(SizeBytes);
    public string Label => $"{Name} • {Size} • {State}";
}
public sealed record ClonePreview(string[] Blockers, string[] Warnings, string[] Actions,
    DiskLayout? SourceLayout, DiskLayout? TargetLayout, int ResizePartitionNumber, long ScratchBytesRequired);
public sealed record CloneSession(string Id, string SourceDeviceId, string SourceDiskFingerprint,
    string TargetDeviceId, string TargetDiskFingerprint, string Strategy, string CaptureJobId,
    string? TargetJobId, string ImageId, string State, DateTimeOffset CreatedAtUtc, ClonePreview Preview)
{
    public string Created => CreatedAtUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
    [JsonIgnore] public string SourceName { get; set; } = "";
    [JsonIgnore] public string TargetName { get; set; } = "";
}
public sealed record ClonePlanResponse(CloneSession Session, Job CaptureJob, ClonePreview Preview);
public sealed record FileRootInfo(string Id, string Label, bool CanRead, bool CanWrite)
{
    public string DisplayLabel => $"{Label} • {(CanRead ? "leitura" : "")}{(CanRead && CanWrite ? " / " : "")}{(CanWrite ? "gravação" : "")}";
}
public sealed record FileRequest(string RootId, string RelativePath, string[] Paths, string BundleId,
    string Name, string? Cursor, int PageSize);
public sealed record FileEntry(string Name, string RelativePath, bool IsDirectory, long SizeBytes,
    DateTimeOffset LastWriteTimeUtc, bool IsLink)
{
    public string Type => IsLink ? "Link bloqueado" : IsDirectory ? "Pasta" : "Arquivo";
    public string Size => IsDirectory ? "—" : Display.Bytes(SizeBytes);
    public string Modified => LastWriteTimeUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}
public sealed record FilePage(string RootId, string RelativePath, FileEntry[] Entries,
    string? NextCursor, string DirectoryStamp);
public sealed record BundleEntry(string Id, string RelativePath, bool IsDirectory, long SizeBytes,
    DateTimeOffset LastWriteTimeUtc, ImageChunk[] Chunks);
public sealed record FileBundle(int SchemaVersion, string Id, string Name, string State, string SourceDeviceId,
    string RootId, DateTimeOffset CreatedAtUtc, long TotalBytes, BundleEntry[] Entries)
{
    public string Size => Display.Bytes(TotalBytes);
    public int EntryCount => Entries.Length;
    public string Created => CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string Label => $"{Name} • {Size} • {EntryCount} entradas • {State}";
}
