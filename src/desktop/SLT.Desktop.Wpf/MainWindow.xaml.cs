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
    private FileBundle[] _fileBundles = [];
    private DeploymentMedia[] _media = [];
    private AuditEvent[] _audit = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _busy;
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public MainWindow()
    {
        InitializeComponent();
        Cloning.Configure(RequireClient, ReviewPlanAsync, RefreshAsync);
        Files.Configure(RequireClient, ReviewPlanAsync, RefreshAsync);
        Deployment.Configure(RequireClient, ReviewPlanAsync, RefreshAsync);
        NetworkBoot.Configure(RequireClient);
        ServerDisks.Configure(RequireClient);
        VirtualMachines.Configure(RequireClient);
        Kamikaze.Configure(RequireClient);
        _timer.Tick += async (_, _) => { if (!_busy && _client is not null) await RunAsync(RefreshAsync, false); };
        Loaded += async (_, _) =>
        {
            UpdateDeviceActions();
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--connection");
            if (index < 0 || index + 1 >= args.Length) return;
            // The service keeps connection.json readable only by Administrators and SYSTEM. Opening the
            // panel without elevation used to end in a silent "check the connection": say what to do instead.
            if (ExplainConnectionProblem(args[index + 1]) is { } problem) { ShowConnectionProblem(problem.Title, problem.Text, problem.CanElevate); return; }
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
            if (_client is null) SetBadge("VERIFIQUE A CONEXÃO", false);
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

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    // Null when the file can be read. Otherwise what the operator should do about it.
    public static (string Title, string Text, bool CanElevate)? ExplainConnectionProblem(string path)
    {
        try { using var probe = File.OpenRead(path); return null; }
        catch (UnauthorizedAccessException)
        {
            return IsElevated()
                ? ("Sem permissão para ler a conexão do servidor", $"Mesmo como administrador, este usuário não pode ler:\n{path}\nConfira as permissões da pasta do servidor (Administradores e SISTEMA).", false)
                : ("Abra o painel como administrador", $"O servidor está instalado como serviço do Windows e guarda a chave de acesso em um arquivo que só administradores podem ler:\n{path}\nClique em “Reabrir como administrador” ou use o atalho “SLT Disk Manager - Painel”.", true);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return ("O servidor ainda não está em execução", $"O arquivo de conexão não existe:\n{path}\nInicie o serviço “SLT Disk Manager Controller” (ou o atalho “Servidor e painel”, no modo portátil) e abra o painel de novo.", false);
        }
        catch (IOException e) { return ("Não foi possível ler a conexão do servidor", e.Message, false); }
    }
    private void ShowConnectionProblem(string title, string text, bool canElevate)
    {
        PermissionTitle.Text = title; PermissionText.Text = text;
        ElevateButton.Visibility = canElevate ? Visibility.Visible : Visibility.Collapsed;
        PermissionPanel.Visibility = Visibility.Visible;
        SetBadge(canElevate ? "PRECISA DE ADMINISTRADOR" : "SERVIDOR INDISPONÍVEL", false);
        StatusLine.Text = title + ".";
    }
    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var arguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains('"') ? throw new InvalidOperationException("Argumento inválido.") : '"' + a + '"'));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = arguments });
            Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusLine.Text = "A permissão de administrador não foi concedida. O painel continua sem acesso ao servidor.";
        }
    }
    private void SetBadge(string text, bool connected)
    {
        ConnectionBadge.Text = text;
        ConnectionBadge.Foreground = (System.Windows.Media.Brush)FindResource(connected ? "OkBrush" : "AccentTextBrush");
        ConnectionPill.Background = connected ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x3A, 0x2C)) : (System.Windows.Media.Brush)FindResource("WarningPanelBrush");
    }
    private void ConnectionToggle_Click(object sender, RoutedEventArgs e) =>
        ConnectionPanel.Visibility = ConnectionPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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
        NetworkBoot.ResetConnection();
        PermissionPanel.Visibility = Visibility.Collapsed;
        ConnectionPanel.Visibility = Visibility.Collapsed;
        try
        {
            var server = await candidate.GetAsync<ServerInfo>("/api/v1/server");
            ServerSummary.Text = $"Servidor {server.Name} • {server.Url.Replace("https://", "")} • versão {server.Version}";
            ServerIdentity.Text = $"Este servidor: {server.Name} — {server.Url}\n" +
                (server.DiscoveryPort is { } port ? $"Busca automática ligada (UDP {port}): os clientes desta versão encontram o servidor sozinhos." : "Busca automática desligada: informe o endereço acima no cliente.") +
                $"\nRepositório de imagens e mídias: {server.Repository}";
            LegacyPin.Text = $"{server.Url}   pin: {server.Pin}";
            _serverPin = server.Pin;
        }
        catch (InvalidOperationException) { ServerSummary.Text = "Servidor de versão anterior • atualize o servidor para usar a busca automática"; }
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
        var clonesTask = client.GetAsync<CloneSession[]>("/api/v1/clones");
        var bundlesTask = client.GetAsync<FileBundle[]>("/api/v1/file-bundles");
        var mediaTask = client.GetAsync<DeploymentMedia[]>("/api/v1/media");
        await Task.WhenAll(dashboardTask, devicesTask, enrollmentsTask, jobsTask, imagesTask, auditTask, clonesTask, bundlesTask, mediaTask);
        var dashboard = await dashboardTask;
        DeviceCount.Text = $"{dashboard.Online} de {dashboard.Devices}";
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
        UpdatePairingBanner(enrollments);
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
        _fileBundles = await bundlesTask;
        Cloning.UpdateData(_devices, await clonesTask, jobs);
        Files.UpdateData(_devices, _fileBundles);
        _media = await mediaTask;
        Deployment.UpdateData(_devices, _media);
        if (NetworkBoot.IsVisible) await NetworkBoot.RefreshStatusAsync();
        StatusLine.Text = $"Atualizado às {DateTime.Now:HH:mm:ss}. Atualização automática a cada 10 segundos.";
        SetBadge("CONECTADO", true);
        UpdateDeviceActions();
    }

    // Synthetic data for QA renders and smoke tests: fills the first two pages without a server.
    public void ShowFixture(Device[] devices, Enrollment[] enrollments)
    {
        _devices = devices;
        ApplyDeviceFilter();
        EnrollmentsGrid.ItemsSource = enrollments;
        UpdatePairingBanner(enrollments);
        UpdateDeviceActions();
    }

    private string _serverPin = "";
    private string? _bannerEnrollmentId;
    public static string NormalizeCode(string text) => new(text.Where(char.IsAsciiDigit).ToArray());
    private void UpdatePairingBanner(Enrollment[] enrollments)
    {
        var pending = enrollments.Where(x => x.Status == "PENDING").OrderBy(x => x.ExpiresAtUtc).ToArray();
        if (pending.Length == 0) { PairingBanner.Visibility = Visibility.Collapsed; _bannerEnrollmentId = null; BannerCode.Clear(); return; }
        var first = pending[0];
        if (_bannerEnrollmentId != first.Id) BannerCode.Clear();
        _bannerEnrollmentId = first.Id;
        PairingBannerTitle.Text = $"{first.Hostname} quer conectar ({first.ModeLabel})";
        PairingBannerText.Text = "Digite o código de 8 dígitos mostrado na tela dele." + (pending.Length > 1 ? $" Há mais {pending.Length - 1} aguardando em “Conectar computador”." : "");
        PairingBanner.Visibility = Visibility.Visible;
    }
    private async Task ApproveEnrollmentAsync(string id, string typed, Action clear)
    {
        var code = NormalizeCode(typed);
        if (code.Length != 8) throw new InvalidOperationException("Digite os 8 dígitos mostrados na tela do computador cliente.");
        try { await RequireClient().PostAsync<JsonElement>($"/api/v1/enrollments/{Uri.EscapeDataString(id)}/approve", new { code }); }
        finally { clear(); }
        await RefreshAsync();
        StatusLine.Text = "Computador conectado. Ele aparece em Computadores em alguns segundos.";
    }
    private async void BannerApprove_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_bannerEnrollmentId is not { } id) return;
        await ApproveEnrollmentAsync(id, BannerCode.Text, BannerCode.Clear);
    });
    private async void BannerCode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || _bannerEnrollmentId is not { } id) return;
        e.Handled = true;
        await RunAsync(() => ApproveEnrollmentAsync(id, BannerCode.Text, BannerCode.Clear));
    }
    private void BannerOpen_Click(object sender, RoutedEventArgs e) => Workspace.SelectedIndex = 1;
    private async void RejectEnrollment_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var enrollment = EnrollmentsGrid.SelectedItem as Enrollment ?? throw new InvalidOperationException("Selecione uma solicitação.");
        if (enrollment.Status != "PENDING") throw new InvalidOperationException("Esta solicitação já foi concluída.");
        if (MessageBox.Show(this, $"Recusar a conexão de {enrollment.Hostname}?", "Recusar solicitação", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RequireClient().PostAsync<JsonElement>($"/api/v1/enrollments/{Uri.EscapeDataString(enrollment.Id)}/reject", new { });
        await RefreshAsync();
    });
    private void CopyPin_Click(object sender, RoutedEventArgs e)
    {
        if (_serverPin.Length == 0) return;
        Clipboard.SetText(_serverPin);
        StatusLine.Text = "Pin do servidor copiado.";
    }

    // Says, next to the buttons, why an action is or is not available for the selected computer.
    private void UpdateDeviceActions()
    {
        if (SltModeButton is null) return;
        var device = DevicesGrid.SelectedItem as Device;
        var disk = DisksGrid.SelectedItem as DiskInfo;
        var online = device is { TrustStatus: "TRUSTED" } && DateTimeOffset.UtcNow - device.LastSeenAtUtc < TimeSpan.FromSeconds(45);
        var capabilities = device?.Inventory?.Capabilities ?? [];
        SltModeButton.IsEnabled = online && device!.Mode == "WINDOWS" && capabilities.Contains("REBOOT_RESCUE");
        FormatButton.IsEnabled = online && device!.Mode == "RESCUE" && capabilities.Contains("FORMAT_DISK") && disk is { IsSystem: false, Mounted: false, IsReadOnly: false };
        DeviceActionHint.Text = device switch
        {
            null => "Selecione um computador. Para clonar, formatar ou instalar sistema, o computador precisa estar em modo SLT (ou iniciado pelo SLT Rescue).",
            _ when !online => "Este computador está offline ou foi desconectado.",
            { Mode: "WINDOWS" } when SltModeButton.IsEnabled => "Cliente Windows: “Reiniciar em modo SLT” leva este computador ao sistema SLT sem pendrive. Nesse modo você pode clonar, formatar e instalar sistema por aqui.",
            { Mode: "WINDOWS" } => "Modo SLT indisponível neste cliente: " + (device.Inventory?.Notes.FirstOrDefault(n => n.StartsWith("MODO_SLT:", StringComparison.Ordinal))?[9..].Trim() ?? "atualize o cliente para a versão atual, instalado como serviço.") + " Alternativas: pendrive SLT Rescue ou boot pela rede.",
            { Mode: "RESCUE" } when !capabilities.Contains("FORMAT_DISK") => "Este computador está no SLT Rescue sem gravação habilitada: só inventário, diagnóstico e captura.",
            { Mode: "RESCUE" } when disk is null => "Modo SLT ativo. Selecione um disco para formatar, ou use as páginas Clonagem e Instalar sistema.",
            { Mode: "RESCUE" } when !FormatButton.IsEnabled => "O disco selecionado está em uso pelo sistema, montado ou protegido contra gravação.",
            { Mode: "RESCUE" } => device.UnattendedWrite ? "Modo SLT: as gravações que você aprovar aqui são executadas sem nova confirmação no teclado do cliente." : "SLT Rescue: gravações pedem também a confirmação no teclado do cliente.",
            _ => "Cliente WinPE: use a página Instalar sistema."
        };
    }
    private async void SltMode_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var device = RequireDevice();
        var dialog = new SltModeDialog(device.Hostname) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var job = await RequireClient().PostAsync<Job>("/api/v1/jobs/plan", new { kind = "REBOOT_RESCUE", deviceId = device.Id, diskFingerprint = "", consent = true, rescueBoot = new { unattendedWrite = dialog.AllowWrite } });
        await RefreshAsync();
        await ReviewPlanAsync(job);
    });
    private async void Format_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var device = RequireDevice(); var disk = RequireDisk();
        var dialog = new FormatDialog(device, disk) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var job = await RequireClient().PostAsync<Job>("/api/v1/jobs/plan", new { kind = "FORMAT_DISK", deviceId = device.Id, diskFingerprint = disk.Fingerprint, consent = true, format = dialog.Options });
        await RefreshAsync();
        await ReviewPlanAsync(job);
    });

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
        SelectedDeviceInfo.Text = device is null ? "" : $"{device.Hostname} • {device.ModeLabel} • ID {device.Id[..Math.Min(8, device.Id.Length)]}";
        InventoryNotes.Text = device?.Inventory?.Notes.Where(n => !n.StartsWith("MODO_SLT:", StringComparison.Ordinal)).ToArray() is { Length: > 0 } notes ? string.Join(" • ", notes) : "";
        UpdateOperationTarget();
        UpdateDeviceActions();
    }
    private void DisksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartitionsGrid is null) return;
        PartitionsGrid.ItemsSource = (DisksGrid.SelectedItem as DiskInfo)?.Partitions ?? [];
        UpdateOperationTarget();
        UpdateDeviceActions();
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
        var enrollment = EnrollmentsGrid.SelectedItem as Enrollment ?? (EnrollmentsGrid.Items.OfType<Enrollment>().Where(x => x.Status == "PENDING").ToArray() is [var only] ? only : throw new InvalidOperationException("Selecione na lista o computador que está pedindo para conectar."));
        await ApproveEnrollmentAsync(enrollment.Id, EnrollmentCode.Text, EnrollmentCode.Clear);
    });
    private async void Revoke_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var device = RequireDevice();
        if (MessageBox.Show(this, $"Desconectar {device.Hostname}? O certificado dele é revogado e, para voltar, será preciso conectar de novo com um código.", "Desconectar computador", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
        if (job.State is not ("AWAITING_APPROVAL" or "PREFLIGHT" or "DRAFT")) throw new InvalidOperationException($"A tarefa está em “{job.StateLabel}”; não aceita aprovação nesta etapa.");
        var device = _devices.FirstOrDefault(x => x.Id == job.Plan.DeviceId);
        var disk = device?.Inventory?.Disks.FirstOrDefault(x => x.Fingerprint == job.Plan.DiskFingerprint);
        var image = _images.FirstOrDefault(x => x.Id == job.Plan.ImageId);
        var bundle = _fileBundles.FirstOrDefault(x => x.Id == job.Plan.File?.BundleId);
        var root = device?.Inventory?.FileRoots?.FirstOrDefault(x => x.Id == job.Plan.File?.RootId);
        var media = _media.FirstOrDefault(x => x.Id == job.Plan.Deployment?.MediaId);
        var dialog = new PlanApprovalWindow(job, device, disk, image, bundle, root, media) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RequireClient().PostAsync<JsonElement>($"/api/v1/jobs/{Uri.EscapeDataString(job.Id)}/approve", new { challenge = dialog.TypedChallenge });
        await RefreshAsync();
    }
    private async void CancelJob_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var job = RequireJob();
        var message = job.Plan.Kind is "RESTORE" or "DEPLOY_WINDOWS" or "DEPLOY_LINUX" or "FORMAT_DISK" ? "Cancelar durante gravação pode deixar o disco de destino incompleto e sem inicialização. A interrupção é best effort. Solicitar cancelamento?" : "Solicitar cancelamento da tarefa selecionada?";
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
