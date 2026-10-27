using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SLT.Desktop;

public partial class MainWindow : Window
{
    private ControllerClient? _client;
    private Device[] _devices = [];
    private ImageManifest[] _images = [];
    private AuditEvent[] _audit = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _busy;
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => { if (!_busy && _client is not null) await RunAsync(RefreshAsync, false); };
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--connection");
            if (index >= 0 && index + 1 < args.Length)
                await RunAsync(async () => { LoadConnection(args[index + 1]); await ConnectAsync(); });
        };
        Closed += (_, _) => { _timer.Stop(); _client?.Dispose(); };
    }

    private async Task RunAsync(Func<Task> action, bool showError = true)
    {
        if (_busy) return;
        _busy = true;
        Workspace.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            var error = ex is System.Net.Http.HttpRequestException ? "Falha de conexão ou certificado. Confira se o servidor está iniciado e se o pin confere com o servidor. " + ex.Message : ex.Message;
            StatusLine.Text = error;
            ConnectionBadge.Text = "VERIFIQUE A CONEXÃO";
            if (showError) MessageBox.Show(this, error, "SLT Disk Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            Workspace.IsEnabled = _client is not null;
            RefreshButton.IsEnabled = _client is not null;
            ConnectButton.IsEnabled = true;
        }
    }

    private void LoadConnection(string path)
    {
        var connection = ControllerClient.ReadConnection(path);
        ServerUrl.Text = connection.Url;
        AdminToken.Password = connection.Token;
        ServerPin.Text = connection.ServerPin;
        StatusLine.Text = "Conexão carregada. Credenciais permanecem apenas na memória do painel.";
    }
    private async void OpenConnection_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Abrir conexão local do controlador", Filter = "Conexão JSON|*.json", FileName = "connection.json" };
        if (picker.ShowDialog(this) == true) await RunAsync(() => { LoadConnection(picker.FileName); return Task.CompletedTask; });
    }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunAsync(ConnectAsync);
    private async Task ConnectAsync()
    {
        var candidate = new ControllerClient(ServerUrl.Text, AdminToken.Password, ServerPin.Text);
        try { await candidate.GetAsync<Dashboard>("/api/v1/dashboard"); }
        catch { candidate.Dispose(); throw; }
        _client?.Dispose();
        _client = candidate;
        await RefreshAsync();
        _timer.Start();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async Task RefreshAsync()
    {
        var client = RequireClient();
        var dashboardTask = client.GetAsync<Dashboard>("/api/v1/dashboard");
        var devicesTask = client.GetAsync<Device[]>("/api/v1/devices");
        var enrollmentsTask = client.GetAsync<Enrollment[]>("/api/v1/enrollments");
        var jobsTask = client.GetAsync<Job[]>("/api/v1/jobs");
        var imagesTask = client.GetAsync<ImageManifest[]>("/api/v1/images");
        var auditTask = client.GetAsync<AuditEvent[]>("/api/v1/audit");
        await Task.WhenAll(dashboardTask, devicesTask, enrollmentsTask, jobsTask, imagesTask, auditTask);
        var dashboard = await dashboardTask;
        DeviceCount.Text = $"{dashboard.Devices} / {dashboard.Online}";
        JobCount.Text = dashboard.ActiveJobs.ToString();
        ImageCount.Text = dashboard.Images.ToString();
        FreeSpace.Text = Display.Bytes(dashboard.RepositoryFreeBytes);
        var deviceId = (DevicesGrid.SelectedItem as Device)?.Id;
        var diskFingerprint = (DisksGrid.SelectedItem as DiskInfo)?.Fingerprint;
        var enrollmentId = (EnrollmentsGrid.SelectedItem as Enrollment)?.Id;
        var jobId = (JobsGrid.SelectedItem as Job)?.Id;
        var imageId = (ImagesGrid.SelectedItem as ImageManifest)?.Id;
        var sourceId = (SourceImage.SelectedItem as ImageManifest)?.Id;
        _devices = await devicesTask;
        ApplyDeviceFilter();
        DevicesGrid.SelectedItem = _devices.FirstOrDefault(x => x.Id == deviceId);
        if (DisksGrid.ItemsSource is DiskInfo[] disks) DisksGrid.SelectedItem = disks.FirstOrDefault(x => x.Fingerprint == diskFingerprint);
        var enrollments = await enrollmentsTask;
        EnrollmentsGrid.ItemsSource = enrollments;
        EnrollmentsGrid.SelectedItem = enrollments.FirstOrDefault(x => x.Id == enrollmentId);
        var jobs = await jobsTask;
        JobsGrid.ItemsSource = jobs;
        JobsGrid.SelectedItem = jobs.FirstOrDefault(x => x.Id == jobId);
        _images = await imagesTask;
        ImagesGrid.ItemsSource = _images;
        ImagesGrid.SelectedItem = _images.FirstOrDefault(x => x.Id == imageId);
        SourceImage.ItemsSource = _images.Where(x => x.State == "AVAILABLE").ToArray();
        SourceImage.SelectedItem = _images.FirstOrDefault(x => x.Id == sourceId && x.State == "AVAILABLE");
        _audit = await auditTask;
        AuditGrid.ItemsSource = _audit;
        StatusLine.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}. Comunicação HTTPS com certificado fixado. Intervalo: 10 segundos.";
        ConnectionBadge.Text = "CONECTADO / LOCAL";
    }

    private ControllerClient RequireClient() => _client ?? throw new InvalidOperationException("Conecte ao controlador primeiro.");
    private Device RequireDevice() => DevicesGrid.SelectedItem as Device ?? throw new InvalidOperationException("Selecione um computador na aba Computadores.");
    private DiskInfo RequireDisk() => DisksGrid.SelectedItem as DiskInfo ?? throw new InvalidOperationException("Selecione um disco na aba Computadores.");
    private Job RequireJob() => JobsGrid.SelectedItem as Job ?? throw new InvalidOperationException("Selecione uma tarefa.");
    private ImageManifest RequireImage() => ImagesGrid.SelectedItem as ImageManifest ?? throw new InvalidOperationException("Selecione uma imagem.");
    private void ApplyDeviceFilter()
    {
        var query = DeviceSearch.Text.Trim();
        DevicesGrid.ItemsSource = _devices.Where(x => string.IsNullOrEmpty(query) || $"{x.Hostname} {x.Id} {x.OperatingSystem}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void DeviceSearch_TextChanged(object sender, TextChangedEventArgs e) { if (DevicesGrid is not null) ApplyDeviceFilter(); }
    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisksGrid is null) return;
        var device = DevicesGrid.SelectedItem as Device;
        DisksGrid.ItemsSource = device?.Inventory?.Disks ?? [];
        SelectedDeviceInfo.Text = device is null ? "" : $"{device.Hostname} • {device.Mode}";
        InventoryNotes.Text = device?.Inventory?.Notes is { Length: > 0 } notes ? string.Join(" • ", notes) : "";
        UpdateOperationTarget();
    }
    private void DisksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartitionsGrid is null) return;
        PartitionsGrid.ItemsSource = (DisksGrid.SelectedItem as DiskInfo)?.Partitions ?? [];
        UpdateOperationTarget();
    }
    private void UpdateOperationTarget()
    {
        if (OperationTarget is null) return;
        var device = DevicesGrid.SelectedItem as Device;
        var disk = DisksGrid.SelectedItem as DiskInfo;
        OperationTarget.Text = device is null || disk is null ? "Nenhum disco selecionado" : $"{device.Hostname} ({device.Mode})\n{disk.Label}\n{disk.Flags} • setor lógico {disk.LogicalSectorBytes} B";
        OperationFingerprint.Text = disk?.Fingerprint ?? "";
    }

    private async void ApproveEnrollment_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var enrollment = EnrollmentsGrid.SelectedItem as Enrollment ?? throw new InvalidOperationException("Selecione uma solicitação de pareamento.");
        var code = EnrollmentCode.Text.Trim();
        if (code.Length != 8 || !code.All(char.IsAsciiDigit)) throw new InvalidOperationException("Digite os 8 dígitos conferidos no console do cliente.");
        await RequireClient().PostAsync<JsonElement>($"/api/v1/enrollments/{Uri.EscapeDataString(enrollment.Id)}/approve", new { code });
        EnrollmentCode.Clear();
        await RefreshAsync();
    });
    private async void Revoke_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var device = RequireDevice();
        if (MessageBox.Show(this, $"Revogar o certificado de {device.Hostname}? O agente perderá acesso ao servidor.", "Revogar agente", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RequireClient().PostAsync<JsonElement>($"/api/v1/devices/{Uri.EscapeDataString(device.Id)}/revoke", new { });
        await RefreshAsync();
    });
    private async void QuickJob_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var kind = ((Button)sender).Tag?.ToString() ?? "INVENTORY";
        var device = RequireDevice();
        var disk = kind == "INVENTORY" ? DisksGrid.SelectedItem as DiskInfo : RequireDisk();
        if (MessageBox.Show(this, $"Você tem autorização para consultar {kind} em {device.Hostname}?", "Autorização de diagnóstico", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await CreatePlanAsync(kind, device, disk, null);
    });
    private async void CreatePlan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (OperationConsent.IsChecked != true) throw new InvalidOperationException("Confirme que possui autorização do responsável para esta tarefa.");
        var kind = ((ComboBoxItem)OperationKind.SelectedItem).Tag.ToString()!;
        var device = RequireDevice();
        var disk = kind == "INVENTORY" ? DisksGrid.SelectedItem as DiskInfo : RequireDisk();
        var image = SourceImage.SelectedItem as ImageManifest;
        if (kind == "RESTORE" && image is null) throw new InvalidOperationException("Selecione a imagem de origem validada.");
        if (kind is "CAPTURE" or "RESTORE" && device.Mode != "RESCUE") throw new InvalidOperationException("Captura e restauração exigem um agente em modo RESCUE.");
        await CreatePlanAsync(kind, device, disk, image);
        OperationConsent.IsChecked = false;
    });
    private async Task CreatePlanAsync(string kind, Device device, DiskInfo? disk, ImageManifest? image)
    {
        var job = await RequireClient().PostAsync<Job>("/api/v1/jobs/plan", new { kind, deviceId = device.Id, diskFingerprint = disk?.Fingerprint ?? "", imageId = image?.Id, consent = true });
        await RefreshAsync();
        await ReviewPlanAsync(job);
    }
    private async void ApproveJob_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await ReviewPlanAsync(RequireJob()); });
    private async Task ReviewPlanAsync(Job job)
    {
        if (job.State is not ("AWAITING_APPROVAL" or "PREFLIGHT" or "DRAFT")) throw new InvalidOperationException($"A tarefa está em {job.State}; não aceita aprovação nesta etapa.");
        var device = _devices.FirstOrDefault(x => x.Id == job.Plan.DeviceId);
        var disk = device?.Inventory?.Disks.FirstOrDefault(x => x.Fingerprint == job.Plan.DiskFingerprint);
        var image = _images.FirstOrDefault(x => x.Id == job.Plan.ImageId);
        var dialog = new PlanApprovalWindow(job, device, disk, image) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RequireClient().PostAsync<JsonElement>($"/api/v1/jobs/{Uri.EscapeDataString(job.Id)}/approve", new { challenge = dialog.TypedChallenge });
        await RefreshAsync();
    }
    private async void CancelJob_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var job = RequireJob();
        var message = job.Plan.Kind == "RESTORE" ? "Cancelar durante restauração pode deixar o disco de destino incompleto e sem inicialização. A interrupção é best effort. Solicitar cancelamento?" : "Solicitar cancelamento da tarefa selecionada?";
        if (MessageBox.Show(this, message, "Cancelar tarefa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RequireClient().PostAsync<JsonElement>($"/api/v1/jobs/{Uri.EscapeDataString(job.Id)}/cancel", new { });
        await RefreshAsync();
    });
    private async void VerifyImage_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var image = RequireImage();
        StatusLine.Text = "Solicitando verificação dos chunks da imagem.";
        var verified = await RequireClient().PostAsync<ImageManifest>($"/api/v1/images/{Uri.EscapeDataString(image.Id)}/verify", new { });
        await RefreshAsync();
        var message = verified.State == "VERIFYING"
            ? "Verificação iniciada em segundo plano. Acompanhe o estado no catálogo; a imagem só estará disponível após a validação dos hashes."
            : $"Estado retornado pelo servidor: {verified.State}. Consulte o catálogo e os logs para o resultado.";
        MessageBox.Show(this, message, "Verificação da imagem", MessageBoxButton.OK, MessageBoxImage.Information);
    });
    private void ImageDetails_Click(object sender, RoutedEventArgs e) => ShowDetails(() => RequireImage(), "Manifesto da imagem");
    private void JobDetails_Click(object sender, RoutedEventArgs e) => ShowDetails(() => RequireJob(), "Plano e resultado da tarefa");
    private void ShowDetails(Func<object> getValue, string title)
    {
        try { new DetailsWindow(title, JsonSerializer.Serialize(getValue(), PrettyJson)) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void ExportJob_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { var job = RequireJob(); await ExportAsync(job, $"slt-job-{job.Id}.json"); });
    private async void ExportAudit_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ExportAsync(_audit, $"slt-auditoria-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
    private async Task ExportAsync(object value, string name)
    {
        var picker = new SaveFileDialog { Title = "Exportar relatório", Filter = "Relatório JSON|*.json", FileName = name, AddExtension = true, DefaultExt = "json" };
        if (picker.ShowDialog(this) == true)
        {
            await File.WriteAllTextAsync(picker.FileName, JsonSerializer.Serialize(value, PrettyJson));
            StatusLine.Text = "Relatório exportado. Armazene-o conforme a política de privacidade da assistência.";
        }
    }
}
