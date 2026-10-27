using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SLT.Desktop;

public partial class ClonePanel : UserControl
{
    private Func<ControllerClient>? _client;
    private Func<Job, Task>? _review;
    private Func<Task>? _refresh;
    private Job[] _jobs = [];
    private bool _updating;
    private bool _busy;
    public ClonePanel() => InitializeComponent();
    public void Configure(Func<ControllerClient> client, Func<Job, Task> review, Func<Task> refresh)
    { _client = client; _review = review; _refresh = refresh; }
    public void UpdateData(Device[] devices, CloneSession[] sessions, Job[] jobs)
    {
        _updating = true;
        try
        {
            _jobs = jobs;
            foreach (var session in sessions)
            {
                session.SourceName = devices.FirstOrDefault(d => d.Id == session.SourceDeviceId)?.Hostname ?? session.SourceDeviceId;
                session.TargetName = devices.FirstOrDefault(d => d.Id == session.TargetDeviceId)?.Hostname ?? session.TargetDeviceId;
            }
            var sourceId = (SourceDevice.SelectedItem as Device)?.Id;
            var targetId = (TargetDevice.SelectedItem as Device)?.Id;
            var sourceFingerprint = (SourceDisk.SelectedItem as DiskInfo)?.Fingerprint;
            var targetFingerprint = (TargetDisk.SelectedItem as DiskInfo)?.Fingerprint;
            var sessionId = (Sessions.SelectedItem as CloneSession)?.Id;
            var rescue = devices.Where(x => x.Mode == "RESCUE" && x.TrustStatus == "TRUSTED").ToArray();
            SourceDevice.ItemsSource = rescue;
            TargetDevice.ItemsSource = rescue;
            SourceDevice.SelectedItem = rescue.FirstOrDefault(x => x.Id == sourceId);
            TargetDevice.SelectedItem = rescue.FirstOrDefault(x => x.Id == targetId);
            FillDisks(SourceDevice, SourceDisk, sourceFingerprint);
            FillDisks(TargetDevice, TargetDisk, targetFingerprint);
            Sessions.ItemsSource = sessions;
            Sessions.SelectedItem = sessions.FirstOrDefault(x => x.Id == sessionId);
        }
        finally { _updating = false; }
        UpdateDiskSummary();
        UpdateSession();
    }
    private static void FillDisks(ComboBox deviceBox, ComboBox diskBox, string? fingerprint)
    {
        var disks = (deviceBox.SelectedItem as Device)?.Inventory?.Disks ?? [];
        diskBox.ItemsSource = disks;
        diskBox.SelectedItem = disks.FirstOrDefault(x => x.Fingerprint == fingerprint);
    }
    private void SourceDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_updating || SourceDisk is null) return; Sessions.SelectedItem = null; FillDisks(SourceDevice, SourceDisk, null); UpdateDiskSummary(); }
    private void TargetDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_updating || TargetDisk is null) return; Sessions.SelectedItem = null; FillDisks(TargetDevice, TargetDisk, null); UpdateDiskSummary(); }
    private void Disk_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && SourceSummary is not null) { Sessions.SelectedItem = null; UpdateDiskSummary(); } }
    private void Strategy_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && PreviewSummary is not null) { Sessions.SelectedItem = null; UpdateDiskSummary(); } }
    private string SelectedStrategy => ((ComboBoxItem)Strategy.SelectedItem).Tag.ToString()!;
    private void UpdateDiskSummary()
    {
        if (SourceSummary is null) return;
        var source = SourceDisk.SelectedItem as DiskInfo;
        var target = TargetDisk.SelectedItem as DiskInfo;
        SourceSummary.Text = Describe(source);
        TargetSummary.Text = Describe(target);
        SourcePartitions.ItemsSource = source?.Layout?.Partitions ?? [];
        TargetPartitions.ItemsSource = target?.Layout?.Partitions ?? [];
        var targetDevice = TargetDevice.SelectedItem as Device;
        var required = SelectedStrategy switch { "FIT" => "RESTORE_FIT", "PRESERVE_LAYOUT" when source?.SizeBytes != target?.SizeBytes => "RESTORE_LAYOUT", _ => "RESTORE" };
        var capable = targetDevice?.Inventory?.Capabilities.Contains(required) == true;
        PlanButton.IsEnabled = !_busy && source is not null && target is not null && capable;
        PreviewSummary.Text = !capable && targetDevice is not null ? $"Destino não anuncia a capacidade {required}. Selecione outra estratégia ou configure o Rescue adequado." :
            "A origem será capturada inteira, incluindo tabela e todas as partições. FIT adapta apenas em cópia de trabalho; o preview do servidor registra a partição escolhida, deslocamentos e espaço temporário.";
        TargetPartitionNameColumn.Header = "Destino atual — apagar";
        if (Sessions.SelectedItem is CloneSession selected) ShowPreview(selected.Preview);
    }
    private static string Describe(DiskInfo? disk) => disk is null ? "Selecione um disco." :
        $"{disk.Size} • setor {disk.LogicalSectorBytes} B • {disk.Layout?.TableType ?? "layout não informado"} • {disk.Flags}\nFingerprint: {disk.Fingerprint}";
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; UpdateSession(); UpdateDiskSummary();
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Clonagem", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; UpdateSession(); UpdateDiskSummary(); }
    }
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao controlador.");
    private async void Plan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Consent.IsChecked != true) throw new InvalidOperationException("Registre a autorização do responsável para esta clonagem.");
        var sourceDevice = SourceDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione o computador de origem.");
        var sourceDisk = SourceDisk.SelectedItem as DiskInfo ?? throw new InvalidOperationException("Selecione o disco de origem.");
        var targetDevice = TargetDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione o computador de destino.");
        var targetDisk = TargetDisk.SelectedItem as DiskInfo ?? throw new InvalidOperationException("Selecione o disco de destino.");
        if (sourceDevice.Id == targetDevice.Id && sourceDisk.Fingerprint == targetDisk.Fingerprint) throw new InvalidOperationException("Origem e destino são o mesmo disco.");
        var response = await Client.PostAsync<ClonePlanResponse>("/api/v1/clones/plan", new
        { sourceDeviceId = sourceDevice.Id, sourceDiskFingerprint = sourceDisk.Fingerprint, targetDeviceId = targetDevice.Id, targetDiskFingerprint = targetDisk.Fingerprint, strategy = SelectedStrategy, consent = true });
        if (_refresh is not null) await _refresh();
        if (Sessions.ItemsSource is CloneSession[] all) Sessions.SelectedItem = all.FirstOrDefault(x => x.Id == response.Session.Id);
        ShowPreview(response.Preview);
        Consent.IsChecked = false;
        // Approval is a separate operator action after inspecting the returned preview.
    });
    private void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating) UpdateSession(); }
    private void UpdateSession()
    {
        if (ApproveSource is null) return;
        var session = Sessions.SelectedItem as CloneSession;
        ApproveSource.IsEnabled = !_busy && session?.State == "AWAITING_SOURCE_APPROVAL";
        PrepareTarget.IsEnabled = !_busy && session?.State is "IMAGE_READY" or "AWAITING_TARGET_APPROVAL";
        if (session is null) { SessionProgress.Text = "Selecione uma sessão para acompanhar e autorizar as etapas."; return; }
        var wasUpdating = _updating; _updating = true;
        try
        {
            SourceDevice.SelectedItem = SourceDevice.Items.OfType<Device>().FirstOrDefault(d => d.Id == session.SourceDeviceId);
            TargetDevice.SelectedItem = TargetDevice.Items.OfType<Device>().FirstOrDefault(d => d.Id == session.TargetDeviceId);
            FillDisks(SourceDevice, SourceDisk, session.SourceDiskFingerprint);
            FillDisks(TargetDevice, TargetDisk, session.TargetDiskFingerprint);
            Strategy.SelectedItem = Strategy.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, session.Strategy));
            SourceSummary.Text = Describe(SourceDisk.SelectedItem as DiskInfo);
            TargetSummary.Text = Describe(TargetDisk.SelectedItem as DiskInfo);
        }
        finally { _updating = wasUpdating; }
        var capture = _jobs.FirstOrDefault(j => j.Id == session.CaptureJobId);
        var restore = _jobs.FirstOrDefault(j => j.Id == session.TargetJobId);
        SessionProgress.Text = $"Captura: {capture?.State ?? "—"} • {capture?.ProgressText ?? "—"}\nDestino: {restore?.State ?? "aguardando imagem validada e nova autorização"} • {restore?.ProgressText ?? "—"}";
        ShowPreview(session.Preview);
    }
    private void ShowPreview(ClonePreview preview)
    {
        var lines = new List<string>();
        if (preview.Blockers.Length > 0) lines.Add("BLOQUEIOS: " + string.Join("; ", preview.Blockers));
        if (preview.Actions.Length > 0) lines.Add("Plano: " + string.Join("; ", preview.Actions));
        if (preview.Warnings.Length > 0) lines.Add("Observações: " + string.Join("; ", preview.Warnings));
        lines.Add($"Partição a adaptar: {(preview.ResizePartitionNumber > 0 ? preview.ResizePartitionNumber.ToString() : "nenhuma")} • espaço temporário requerido: {Display.Bytes(preview.ScratchBytesRequired)}");
        lines.Add("A tabela à direita mostra o layout previsto após o clone. Confira os novos inícios e tamanhos em setores.");
        PreviewSummary.Text = string.Join("\n", lines);
        SourcePartitions.ItemsSource = preview.SourceLayout?.Partitions ?? [];
        TargetPartitions.ItemsSource = preview.TargetLayout?.Partitions ?? [];
        TargetPartitionNameColumn.Header = "Destino previsto após clone";
    }
    private async void ApproveSource_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var session = Sessions.SelectedItem as CloneSession ?? throw new InvalidOperationException("Selecione uma sessão.");
        var job = await Client.GetAsync<Job>($"/api/v1/jobs/{Uri.EscapeDataString(session.CaptureJobId)}");
        if (_review is not null) await _review(job);
    });
    private async void PrepareTarget_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var session = Sessions.SelectedItem as CloneSession ?? throw new InvalidOperationException("Selecione uma sessão.");
        var job = session.State == "AWAITING_TARGET_APPROVAL" && session.TargetJobId is not null
            ? await Client.GetAsync<Job>($"/api/v1/jobs/{Uri.EscapeDataString(session.TargetJobId)}")
            : await Client.PostAsync<Job>($"/api/v1/clones/{Uri.EscapeDataString(session.Id)}/prepare-target", new { });
        if (_refresh is not null) await _refresh();
        if (_review is not null) await _review(job);
    });
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is CloneSession session) new DetailsWindow("Plano de clonagem integral", JsonSerializer.Serialize(session, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
