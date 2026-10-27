using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SLT.Desktop;

public partial class DeploymentPanel : UserControl
{
    private Func<ControllerClient>? _client;
    private Func<Job, Task>? _review;
    private Func<Task>? _refresh;
    private bool _updating;
    private bool _busy;
    public DeploymentPanel() => InitializeComponent();
    public void Configure(Func<ControllerClient> client, Func<Job, Task> review, Func<Task> refresh)
    { _client = client; _review = review; _refresh = refresh; }
    public void UpdateData(Device[] devices, DeploymentMedia[] media)
    {
        _updating = true;
        try
        {
            var deviceId = (TargetDevice.SelectedItem as Device)?.Id;
            var fingerprint = (TargetDisk.SelectedItem as DiskInfo)?.Fingerprint;
            var mediaId = (Media.SelectedItem as DeploymentMedia)?.Id;
            var edition = (Edition.SelectedItem as WindowsImage)?.Index;
            var eligible = devices.Where(IsDeploymentDevice).ToArray();
            TargetDevice.ItemsSource = eligible;
            TargetDevice.SelectedItem = eligible.FirstOrDefault(d => d.Id == deviceId);
            FillDisks(fingerprint);
            Media.ItemsSource = media;
            Media.SelectedItem = media.FirstOrDefault(m => m.Id == mediaId);
            FillEditions(edition);
        }
        finally { _updating = false; }
        UpdateButtons();
    }
    private static bool IsDeploymentDevice(Device d) => d.TrustStatus == "TRUSTED" && d.Mode == "WINPE" && d.Inventory?.Capabilities.Contains("DEPLOY_WINDOWS") == true;
    private void FillDisks(string? fingerprint)
    {
        var disks = (TargetDevice.SelectedItem as Device)?.Inventory?.Disks ?? [];
        TargetDisk.ItemsSource = disks;
        TargetDisk.SelectedItem = disks.FirstOrDefault(d => d.Fingerprint == fingerprint);
    }
    private void FillEditions(int? index)
    {
        var editions = (Media.SelectedItem as DeploymentMedia)?.Images ?? [];
        Edition.ItemsSource = editions;
        Edition.SelectedItem = editions.FirstOrDefault(i => i.Index == index);
    }
    private void TargetDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updating && TargetDisk is not null) { FillDisks(null); Consent.IsChecked = false; UpdateButtons(); } }
    private void Media_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updating && Edition is not null) { FillEditions(null); Consent.IsChecked = false; UpdateButtons(); } }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updating && PlanButton is not null) { Consent.IsChecked = false; UpdateButtons(); } }
    private void UpdateButtons()
    {
        if (PlanButton is null) return;
        var disk = TargetDisk.SelectedItem as DiskInfo;
        DiskIdentity.Text = disk is null ? "" : $"{disk.Flags}\nSetor lógico: {disk.LogicalSectorBytes} B\nFingerprint: {disk.Fingerprint}";
        PlanButton.IsEnabled = !_busy && TargetDevice.SelectedItem is Device d && IsDeploymentDevice(d) && disk is { IsSystem: false, IsReadOnly: false } && Media.SelectedItem is DeploymentMedia { State: "AVAILABLE" } && Edition.SelectedItem is WindowsImage i && IsAmd64(i.Architecture);
        ImportButton.IsEnabled = !_busy;
    }
    private static bool IsAmd64(string value) => value.Equals("AMD64", StringComparison.OrdinalIgnoreCase) || value.Equals("x64", StringComparison.OrdinalIgnoreCase) || value == "9";
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao controlador.");
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Instalar Windows", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; UpdateButtons(); }
    }
    private void PickMedia_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Selecionar mídia Windows no servidor", Filter = "Mídia Windows|*.iso;*.wim;*.esd" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) { MediaPath.Text = picker.FileName; MediaName.Text = Path.GetFileNameWithoutExtension(picker.FileName); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = MediaPath.Text.Trim();
        if (!Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(MediaName.Text)) throw new InvalidOperationException("Escolha a mídia local e informe um nome.");
        var imported = await Client.PostAsync<DeploymentMedia>("/api/v1/media/import", new { path, name = MediaName.Text.Trim() });
        if (_refresh is not null) await _refresh();
        Status.Text = $"Mídia {imported.Name}: {imported.State}. O catálogo atualiza automaticamente; importações grandes podem levar alguns minutos.";
    });
    private async void Plan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Consent.IsChecked != true) throw new InvalidOperationException("Confirme a autorização para apagar e instalar Windows no disco selecionado.");
        var device = TargetDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione o cliente WinPE.");
        var disk = TargetDisk.SelectedItem as DiskInfo ?? throw new InvalidOperationException("Selecione o disco.");
        var media = Media.SelectedItem as DeploymentMedia ?? throw new InvalidOperationException("Selecione a mídia.");
        var edition = Edition.SelectedItem as WindowsImage ?? throw new InvalidOperationException("Selecione a edição.");
        if (!IsDeploymentDevice(device) || disk.IsSystem || disk.IsReadOnly || media.State != "AVAILABLE" || !IsAmd64(edition.Architecture)) throw new InvalidOperationException("Seleção incompatível com instalação WinPE x64.");
        var firmware = ((ComboBoxItem)Firmware.SelectedItem).Tag.ToString()!;
        var deployment = new DeploymentOptions(media.Id, edition.Index, firmware, Reboot.IsChecked == true);
        var job = await Client.PostAsync<Job>("/api/v1/jobs/plan", new { kind = "DEPLOY_WINDOWS", deviceId = device.Id, diskFingerprint = disk.Fingerprint, consent = true, deployment });
        Consent.IsChecked = false;
        if (_refresh is not null) await _refresh();
        if (_review is not null) await _review(job);
        Status.Text = $"Plano {job.Id} criado. Acompanhe Tarefas e logs. A confirmação local no cliente continua obrigatória.";
    });
}
