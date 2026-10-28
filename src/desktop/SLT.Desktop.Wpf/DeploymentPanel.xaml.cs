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
            _devices = devices;
            var eligible = devices.Where(d => IsDeploymentDevice(d, Media.SelectedItem as DeploymentMedia ?? media.FirstOrDefault(m => m.Id == mediaId)) || IsConversionDevice(d)).ToArray();
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
    private Device[] _devices = [];
    private static bool IsDeploymentDevice(Device d, DeploymentMedia? media) => d.TrustStatus == "TRUSTED" && (media?.IsLinux == true
        ? d.Mode == "RESCUE" && d.Inventory?.Capabilities.Contains("DEPLOY_LINUX") == true
        : d.Mode == "WINPE" && d.Inventory?.Capabilities.Contains("DEPLOY_WINDOWS") == true);
    private static bool IsConversionDevice(Device d) => d.TrustStatus == "TRUSTED" && d.Mode == "WINPE" && d.Inventory?.Capabilities.Contains("CONVERT_GPT") == true;
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
    {
        if (_updating || Edition is null) return;
        FillEditions(null); Consent.IsChecked = false;
        var media = Media.SelectedItem as DeploymentMedia; var current = TargetDevice.SelectedItem as Device;
        _updating = true;
        try
        {
            var eligible = _devices.Where(d => IsDeploymentDevice(d, media) || IsConversionDevice(d)).ToArray();
            TargetDevice.ItemsSource = eligible; TargetDevice.SelectedItem = eligible.FirstOrDefault(d => d.Id == current?.Id); FillDisks((TargetDisk.SelectedItem as DiskInfo)?.Fingerprint);
        }
        finally { _updating = false; }
        UpdateButtons();
    }
    private void SelectionChangedClick(object sender, RoutedEventArgs e) => UpdateButtons();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updating && PlanButton is not null) { Consent.IsChecked = false; UpdateButtons(); } }
    private void UpdateButtons()
    {
        if (PlanButton is null) return;
        var disk = TargetDisk.SelectedItem as DiskInfo;
        DiskIdentity.Text = disk is null ? "" : $"{disk.Flags}\nSetor lógico: {disk.LogicalSectorBytes} B\nFingerprint: {disk.Fingerprint}";
        var media = Media.SelectedItem as DeploymentMedia;
        var ready = TargetDevice.SelectedItem is Device && disk is { IsSystem: false, IsReadOnly: false } && media is { State: "AVAILABLE" } && Edition.SelectedItem is WindowsImage i && IsAmd64(i.Architecture);
        PlanButton.IsEnabled = !_busy && ready && IsDeploymentDevice((Device)TargetDevice.SelectedItem!, media) && (media!.IsLinux == false || CreateAccount.IsChecked == true);
        ConvertButton.IsEnabled = !_busy && ready && IsConversionDevice((Device)TargetDevice.SelectedItem!) && media!.IsLinux == false;
        ImportButton.IsEnabled = !_busy;
    }
    private static bool IsAmd64(string value) => value.Equals("AMD64", StringComparison.OrdinalIgnoreCase) || value.Equals("x64", StringComparison.OrdinalIgnoreCase) || value == "9";
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao controlador.");
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Instalar sistema", MessageBoxButton.OK, MessageBoxImage.Warning); }
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
        if (Consent.IsChecked != true) throw new InvalidOperationException("Confirme a autorização para apagar e instalar o sistema no disco selecionado.");
        var device = TargetDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione o cliente.");
        var disk = TargetDisk.SelectedItem as DiskInfo ?? throw new InvalidOperationException("Selecione o disco.");
        var media = Media.SelectedItem as DeploymentMedia ?? throw new InvalidOperationException("Selecione a mídia.");
        var edition = Edition.SelectedItem as WindowsImage ?? throw new InvalidOperationException("Selecione a edição.");
        if (!IsDeploymentDevice(device, media) || disk.IsSystem || disk.IsReadOnly || media.State != "AVAILABLE" || !IsAmd64(edition.Architecture)) throw new InvalidOperationException("Seleção incompatível com a instalação x64.");
        var firmware = ((ComboBoxItem)Firmware.SelectedItem).Tag.ToString()!;
        var account = CreateAccount.IsChecked == true ? new DeploymentAccount(ComputerName.Text.Trim(), UserName.Text.Trim()) : null;
        var deployment = new DeploymentOptions(media.Id, edition.Index, firmware, Reboot.IsChecked == true, account);
        var job = await Client.PostAsync<Job>("/api/v1/jobs/plan", new { kind = media.IsLinux ? "DEPLOY_LINUX" : "DEPLOY_WINDOWS", deviceId = device.Id, diskFingerprint = disk.Fingerprint, consent = true, deployment });
        Consent.IsChecked = false;
        if (job.TemporaryPassword is { } password)
            MessageBox.Show(Window.GetWindow(this), $"Usuário: {account!.UserName}\nSenha temporária: {password}\n\nAnote agora: ela não será exibida de novo nem fica registrada no log.", "Senha temporária do cliente", MessageBoxButton.OK, MessageBoxImage.Information);
        if (_refresh is not null) await _refresh();
        if (_review is not null) await _review(job);
        Status.Text = $"Plano {job.Id} criado. Acompanhe Tarefas e logs. A confirmação local no cliente continua obrigatória.";
    });
    private async void Convert_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var device = TargetDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione o cliente WinPE iniciado em UEFI.");
        var disk = TargetDisk.SelectedItem as DiskInfo ?? throw new InvalidOperationException("Selecione o disco.");
        var media = Media.SelectedItem as DeploymentMedia ?? throw new InvalidOperationException("Selecione a mídia Windows que fornece o MBR2GPT.");
        var edition = Edition.SelectedItem as WindowsImage ?? throw new InvalidOperationException("Selecione a edição.");
        if (!IsConversionDevice(device) || media.IsLinux) throw new InvalidOperationException("Conversão exige WinPE em UEFI e mídia Windows.");
        var job = await Client.PostAsync<Job>("/api/v1/jobs/plan", new { kind = "CONVERT_GPT", deviceId = device.Id, diskFingerprint = disk.Fingerprint, consent = true, deployment = new DeploymentOptions(media.Id, edition.Index, "GPT_UEFI", false) });
        if (_refresh is not null) await _refresh();
        if (_review is not null) await _review(job);
        Status.Text = $"Conversão {job.Id} planejada. O WinPE valida com MBR2GPT antes de pedir a confirmação local.";
    });
}
