using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SLT.Desktop;

// Contracts of /api/v1/server-local (Controller LocalHost/*). Only the fields the panel shows.
public sealed record ServerPartition(int Number, long OffsetBytes, long SizeBytes, string Type, string GptType, bool IsActive, bool IsBoot, bool IsSystem,
    string DriveLetter, string FileSystem, string Label, long FreeBytes, bool HasWindows, string[] Protections)
{
    public string Letter => DriveLetter.Length == 1 ? DriveLetter + ":" : "—";
    public string Size => Display.Bytes(SizeBytes);
    public string Free => FileSystem.Length > 0 ? Display.Bytes(FreeBytes) : "";
    public string Kind => Type switch { "System" => "EFI", "Reserved" => "MSR", "Recovery" => "Recuperação", "Basic" or "IFS" => "Dados", _ => Type };
    public string WindowsLabel => HasWindows ? "Sim" : "";
    public string Protection => Protections.Length == 0 ? "" : string.Join(", ", Protections);
    public override string ToString() => $"{Number} • {Letter} {FileSystem} {Size}";
}
public sealed record ServerDisk(int Number, string Model, string Serial, long SizeBytes, int LogicalSectorBytes, int PhysicalSectorBytes, string BusType,
    string PartitionStyle, bool IsSystem, bool IsBoot, bool IsOffline, bool IsReadOnly, string UniqueId, string Health, string MediaType, string Location,
    ServerPartition[] Partitions, string Fingerprint, string[] Protections, bool IsVirtual)
{
    public string Size => Display.Bytes(SizeBytes);
    public string State => IsOffline ? "Offline" : IsReadOnly ? "Somente leitura" : "Online";
    public string Protection => Protections.Length == 0 ? "Livre para gravar" : string.Join(", ", Protections);
    public string Bus => IsVirtual ? "Virtual (VHD)" : BusType;
    public string Table => PartitionStyle == "RAW" ? "Sem tabela" : PartitionStyle;
    public override string ToString() => $"Disco {Number} • {Model} • {Size}";
}
public sealed record ServerDiskInventory(ServerDisk[] Disks, string SystemDrive, string[] PagefileDrives, bool VirtualOnly, DateTimeOffset CapturedAtUtc);
public sealed record ServerDiskRequest(string Kind, int DiskNumber, string Fingerprint, int? PartitionNumber = null, int? TargetDiskNumber = null,
    string? TargetFingerprint = null, string? TableType = null, string? FileSystem = null, string? Label = null, string? Mode = null, long? SizeBytes = null, bool ValidateOnly = false);
public sealed record ServerDiskOperation(string Id, string Kind, ServerDiskRequest Request, string Title, string[] Warnings, string Challenge, bool RequiresTyping,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, string State, long BytesDone, long BytesTotal, string Log, string? Message, DateTimeOffset? FinishedAtUtc, string? ResultJson)
{
    public string When => CreatedAtUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string StateLabel => State switch
    {
        "AWAITING_CONFIRMATION" => "Aguardando confirmação", "RUNNING" => "Em andamento", "COMPLETED" => "Concluída", "FAILED" => "Falhou",
        "CANCELLED" => "Cancelada", "EXPIRED" => "Expirada", "INTERRUPTED" => "Interrompida", _ => State
    };
    public string ProgressLabel => BytesTotal > 0 ? $"{BytesDone * 100 / BytesTotal}% ({Display.Bytes(BytesDone)} de {Display.Bytes(BytesTotal)})" : State == "RUNNING" ? "…" : "";
}
public sealed record ConfirmRequest(string Challenge);

public sealed record VmDrive(string Path, int DiskNumber);
public sealed record VmMachine(string Name, string State, int Generation, long MemoryBytes, int ProcessorCount, int CpuMaximumPercent, long UptimeSeconds, int CpuUsagePercent, string Iso, VmDrive[] Drives, string SecureBoot)
{
    public string Firmware => Generation == 2 ? "UEFI (geração 2)" : "Legacy/BIOS (geração 1)";
    public string Memory => Display.Bytes(MemoryBytes);
    public string StateLabel => State switch { "Running" => "Ligada", "Off" => "Desligada", "Saved" => "Salva", "Paused" => "Pausada", "Starting" => "Ligando", "Stopping" => "Desligando", _ => State };
    public string Cpu => $"{ProcessorCount} vCPU • limite {CpuMaximumPercent}% • uso {CpuUsagePercent}%";
    public string Disks => string.Join("; ", Drives.Select(d => d.DiskNumber >= 0 ? $"disco físico {d.DiskNumber}" : System.IO.Path.GetFileName(d.Path)));
    public string IsoName => string.IsNullOrEmpty(Iso) ? "—" : System.IO.Path.GetFileName(Iso);
}
public sealed record VmStatus(bool Available, string? Reason, int LogicalProcessors, long MemoryBytes, string[]? Switches, VmMachine[]? Machines, bool VmConnect);
public sealed record VmDiskRequest(string Kind, string? Folder = null, int SizeGiB = 0, string? Path = null, int? DiskNumber = null, string? Fingerprint = null);
public sealed record VmCreateRequest(string Name, int Generation, int MemoryMiB, int CpuCount, int CpuPercent, bool SecureBoot, string? SecureBootTemplate,
    bool Tpm, VmDiskRequest Disk, string? IsoPath, string? SwitchName);
public sealed record VmPlan(string Id, VmCreateRequest Request, string Title, string[] Warnings, string Challenge, bool RequiresTyping, DateTimeOffset ExpiresAtUtc, string State);
public sealed record VmNameRequest(string? IsoPath = null, bool DeleteFiles = false, bool OnlineDisks = false);

public sealed record KamikazeDiskView(int Number, string Identity, string Model, string Serial, long SizeBytes, int LogicalSectorBytes, string PartitionStyle, string[] Notes, string Volumes)
{
    public string Size => Display.Bytes(SizeBytes);
    public string NotesLabel => string.Join(", ", Notes);
    public override string ToString() => $"Disco {Number} • {Model} • {Size}{(Notes.Length > 0 ? " • " + Notes[0] : "")}";
}
public sealed record KamikazeBootImage(string Source, string Path, string Detail);
public sealed record KamikazeSession(string Id, string State, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, string Challenge, string[] Summary, string[] Warnings,
    string BundleRoot, bool RebootNow, string Log, string? BootEntry, DateTimeOffset? StagedAtUtc, DateTimeOffset? FinishedAtUtc, string? ResultJson, string? Message, string[]? ConsoleChallenges)
{
    public string When => CreatedAtUtc.ToLocalTime().ToString("dd/MM HH:mm");
    public string StateLabel => State switch
    {
        "AWAITING_CONFIRMATION" => "Aguardando confirmação", "STAGING" => "Preparando", "STAGED" => "Pronto para reiniciar", "REBOOTING" => "Reiniciando",
        "COMPLETED" => "Concluído", "FAILED" => "Falhou", "CANCELLED" => "Cancelado", "INTERRUPTED" => "Interrompido", "NOT_RUN" => "Não executou", _ => State
    };
    public string SummaryLabel => string.Join(" | ", Summary);
}
public sealed record KamikazeStatus(string Firmware, bool? SecureBoot, KamikazeBootImage BootImage, bool Agent, KamikazeDiskView[] Disks, string? DiskError, KamikazeSession[] Sessions, string RepositoryRoot);
public sealed record KamikazeOperationRequest(string Kind, string TargetIdentity, string? SourceIdentity = null, string? TableType = null, string? FileSystem = null,
    string? Label = null, string? MediaId = null, int ImageIndex = 0)
{
    public string Description { get; init; } = "";
}
public sealed record KamikazePlanRequest(KamikazeOperationRequest[] Operations, bool RebootNow);

// Typed confirmation for local operations: the operator reads the context and types the phrase.
public sealed class TypedConfirmWindow : Window
{
    public TypedConfirmWindow(string title, IEnumerable<string> lines, IEnumerable<string> warnings, string challenge, bool requiresTyping, string consentText, string approveText)
    {
        Title = title;
        Style = (Style)Application.Current.FindResource("AppWindow");
        Width = 760; Height = 600; MinWidth = 600; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        void Text(string text, double size = 15, bool bold = false, Brush? color = null) =>
            content.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = color ?? Brushes.WhiteSmoke });
        Text(title, 22, true);
        foreach (var line in lines) Text(line);
        foreach (var warning in warnings) Text("• " + warning, 15, false, Brushes.SandyBrown);
        var box = new TextBox { MinHeight = 36, Margin = new Thickness(0, 0, 0, 12), MaxLength = challenge.Length + 20 };
        if (requiresTyping)
        {
            Text("Digite exatamente para confirmar:", 15, true);
            Text(challenge, 19, true, Brushes.SandyBrown);
            content.Children.Add(box);
        }
        var consent = new CheckBox { Content = consentText, Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(consent);
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Voltar sem executar", Style = (Style)Application.Current.FindResource("SecondaryButton"), IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var approve = new Button { Content = approveText, IsEnabled = false };
        void Update() => approve.IsEnabled = consent.IsChecked == true && (!requiresTyping || string.Equals(box.Text.Trim(), challenge, StringComparison.OrdinalIgnoreCase));
        box.TextChanged += (_, _) => Update(); consent.Checked += (_, _) => Update(); consent.Unchecked += (_, _) => Update();
        approve.Click += (_, _) => { Update(); if (approve.IsEnabled) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(approve);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);
        Content = layout;
    }
}

public static class JsonPretty
{
    public static string Format(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try { return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true }); } catch (JsonException) { return json; }
    }
}
