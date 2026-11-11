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
    DateTimeOffset LastSeenAtUtc, Inventory? Inventory, string? OriginDeviceId = null, bool UnattendedWrite = false)
{
    public string Status => TrustStatus == "REVOKED" ? "Desconectado" : DateTimeOffset.UtcNow - LastSeenAtUtc < TimeSpan.FromSeconds(45) ? "Online" : "Offline";
    public string ModeLabel => Display.Mode(Mode, OriginDeviceId is not null);
    public string LastSeen => LastSeenAtUtc == DateTimeOffset.MinValue ? "—" : LastSeenAtUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string OperatingSystem => Inventory?.Os ?? "Aguardando inventário";
    public string Label => $"{Hostname} • {ModeLabel} • {Status}";
}
public sealed record Enrollment(string Id, string Hostname, string Mode, string Code,
    DateTimeOffset ExpiresAtUtc, string Status, bool ShortCode = false)
{
    public string Expires => ExpiresAtUtc.ToLocalTime().ToString("dd/MM HH:mm");
    public string ModeLabel => Display.Mode(Mode, false);
    // After LAN discovery the code exists only on the client's screen: reading it there is the check.
    public string CodeLabel => Status != "PENDING" ? "—" : ShortCode || Code.Length == 0 ? "mostrado na tela do cliente" : Code;
    public string StatusLabel => Status switch { "PENDING" => "Aguardando código", "APPROVED" => "Conectado", "EXPIRED" => "Expirado", "REJECTED" => "Recusado", _ => Status };
}
public sealed record ServerInfo(string Name, string Url, string Pin, string Version, int? DiscoveryPort, string Repository);
public sealed record RescueBootOptions(string LoaderSha256, long LoaderSizeBytes, string Script, string Ticket, bool UnattendedWrite, int RebootDelaySeconds = 30);
public sealed record FormatOptions(string TableType, string FileSystem, string Label = "");
public sealed record JobPlan(string Id, string Kind, string DeviceId, string DiskFingerprint, string DiskPath,
    long DiskSizeBytes, int LogicalSectorBytes, string ImageId, string Challenge, DateTimeOffset ExpiresAtUtc, bool Consent,
    [property: JsonPropertyName("clone")] CloneOptions? CloneOptions = null, FileRequest? File = null, DeploymentOptions? Deployment = null,
    RescueBootOptions? RescueBoot = null, FormatOptions? Format = null);
public sealed record Job(string Id, JobPlan Plan, string State, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc, JsonElement? Progress, JsonElement? Result, string? TemporaryPassword = null)
{
    public string Kind => Plan.Kind;
    public string KindLabel => Display.JobKind(Plan.Kind);
    public string StateLabel => Display.JobState(State);
    public string DiskPath => Plan.Kind == "REBOOT_RESCUE" ? "computador inteiro" : Plan.File is { } files
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
    public static string Mode(string mode, bool sltSession) => mode switch
    {
        "WINDOWS" => "Windows (cliente)",
        "RESCUE" => sltSession ? "Modo SLT" : "SLT Rescue",
        "WINPE" => "WinPE (instalação)",
        _ => mode
    };
    public static string JobKind(string kind) => kind switch
    {
        "INVENTORY" => "Inventário", "SMART" => "Saúde do disco (SMART)", "CAPTURE" => "Capturar imagem", "RESTORE" => "Restaurar imagem",
        "FILE_LIST" => "Listar arquivos", "FILE_BACKUP" => "Copiar arquivos", "FILE_RESTORE" => "Enviar arquivos",
        "DEPLOY_WINDOWS" => "Instalar Windows", "DEPLOY_LINUX" => "Instalar Linux", "CONVERT_GPT" => "Converter MBR → GPT",
        "REBOOT_RESCUE" => "Reiniciar em modo SLT", "FORMAT_DISK" => "Formatar disco", _ => kind
    };
    public static string JobState(string state) => state switch
    {
        "AWAITING_APPROVAL" => "Aguardando aprovação", "QUEUED" => "Na fila", "RUNNING" => "Em andamento", "CANCELLING" => "Cancelando",
        "CANCELLED" => "Cancelada", "COMPLETED" => "Concluída", "FAILED" => "Falhou", "INTERRUPTED" => "Interrompida", _ => state
    };
    public static string Bytes(long value)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var n = (double)value;
        var i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
}
