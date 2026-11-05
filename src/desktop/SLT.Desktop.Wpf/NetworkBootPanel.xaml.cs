using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SLT.Desktop;

public partial class NetworkBootPanel : UserControl
{
    private Func<ControllerClient>? _client;
    private NetworkBootStatus? _status;
    private NetworkBootInterface[] _interfaces = [];
    private bool _busy;
    private bool _updating;
    private bool _dirty;
    private bool _validated;
    public NetworkBootPanel()
    {
        InitializeComponent();
        UpdateButtons();
        Loaded += async (_, _) => { if (_client is not null) await RefreshStatusAsync(); };
    }
    public void Configure(Func<ControllerClient> client) => _client = client;
    public void ResetConnection()
    {
        _status = null;
        _dirty = false;
        ResetValidation();
        ServiceStatus.Text = "ESTADO NÃO CONSULTADO";
        ServiceError.Text = "";
        UpdateButtons();
    }
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao Controller local.");
    private bool Running => _status?.State is "RUNNING" or "STARTING";
    public Task RefreshStatusAsync() => RunAsync(LoadAsync);
    private async Task LoadAsync()
    {
        var client = Client;
        var stateTask = client.GetAsync<NetworkBootStatus>("/api/v1/network-boot/status");
        var interfacesTask = client.GetAsync<NetworkBootInterface[]>("/api/v1/network-boot/interfaces");
        await Task.WhenAll(stateTask, interfacesTask);
        UpdateData(await stateTask, await interfacesTask);
    }
    public void UpdateData(NetworkBootStatus status, NetworkBootInterface[] interfaces)
    {
        var configChanged = _status?.Config != status.Config;
        _status = status;
        _interfaces = interfaces;
        _updating = true;
        try
        {
            var selected = (Interface.SelectedItem as NetworkBootInterface)?.Id;
            Interface.ItemsSource = interfaces;
            Interface.SelectedItem = interfaces.FirstOrDefault(i => i.Id == (_dirty ? selected : status.Config.InterfaceId) && i.IPv4 == (_dirty ? BindIPv4.Text : status.Config.BindIPv4));
            if (!_dirty)
            {
                BindIPv4.Text = status.Config.BindIPv4;
                AllowedCidr.Text = status.Config.AllowedCidr;
                HttpPort.Text = status.Config.HttpPort.ToString();
                Mode.SelectedIndex = 0;
            }
            if (configChanged) ResetValidation();
            ServiceStatus.Text = status.State switch
            {
                "STOPPED" => "DESLIGADO", "STARTING" => "INICIANDO", "RUNNING" => "DISTRIBUINDO BOOT", "FAILED" => "FALHA — VERIFIQUE O DIAGNÓSTICO", _ => "ESTADO DESCONHECIDO"
            };
            ServiceDetails.Text = $"{status.Config.Mode} • {status.Config.BindIPv4}:{status.Config.HttpPort} • CIDR {status.Config.AllowedCidr}" +
                (status.StartedAtUtc is { } started ? $" • iniciado {started.ToLocalTime():dd/MM HH:mm:ss}" : " • início somente por ação explícita");
            ServiceError.Text = status.LastError ?? "";
            BootStore.Text = "Catálogo separado de boot, gerenciado pelo Controller. Importação local validada por tamanho e SHA-256.";
            var profileId = (Profiles.SelectedItem as NetworkBootProfile)?.Id;
            Profiles.ItemsSource = status.Profiles;
            Profiles.SelectedItem = status.Profiles.FirstOrDefault(p => p.Id == profileId);
            Events.ItemsSource = status.Events.OrderByDescending(e => e.TimestampUtc).ToArray();
        }
        finally { _updating = false; }
        UpdateButtons();
    }
    private void ResetValidation()
    {
        _validated = false;
        NetworkConsent.IsChecked = false;
        ValidationResult.Text = "Salve e valide a configuração antes de iniciar.";
    }
    private void ConfigurationChanged(object sender, TextChangedEventArgs e) => MarkDirty();
    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();
    private void MarkDirty()
    {
        if (_updating || StartButton is null) return;
        _dirty = true;
        ResetValidation();
        UpdateButtons();
    }
    private void Interface_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || BindIPv4 is null) return;
        if (Interface.SelectedItem is NetworkBootInterface nic)
        {
            _updating = true;
            try { BindIPv4.Text = nic.IPv4; AllowedCidr.Text = nic.Cidr; }
            finally { _updating = false; }
        }
        MarkDirty();
    }
    private void Consent_Click(object sender, RoutedEventArgs e) => UpdateButtons();
    private void UpdateButtons()
    {
        if (StartButton is null) return;
        var known = _status?.State is "STOPPED" or "RUNNING" or "STARTING" or "FAILED";
        ConfigFields.IsEnabled = !_busy && known && !Running;
        SaveButton.IsEnabled = !_busy && known && !Running;
        ValidateButton.IsEnabled = !_busy && known && !Running && !_dirty;
        ImportButton.IsEnabled = !_busy && known && !Running;
        RefreshButton.IsEnabled = !_busy;
        NetworkConsent.IsEnabled = !_busy && !Running && _validated && !_dirty;
        StartButton.IsEnabled = !_busy && known && !Running && !_dirty && _validated && NetworkConsent.IsChecked == true;
        StopButton.IsEnabled = !_busy && known && Running;
        ToggleProfileButton.IsEnabled = !_busy && known && !Running && Profiles.SelectedItem is NetworkBootProfile;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        try { await action(); }
        catch (Exception ex)
        {
            ResetValidation();
            ServiceError.Text = ex.Message;
            Status.Text = ex.Message;
        }
        finally { _busy = false; UpdateButtons(); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var nic = Interface.SelectedItem as NetworkBootInterface ?? throw new InvalidOperationException("Selecione uma interface Ethernet elegível.");
        if (!nic.Eligible) throw new InvalidOperationException("Interface indisponível: " + nic.Reason);
        if (!int.TryParse(HttpPort.Text.Trim(), out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("Informe uma porta HTTP de 1024 a 65535.");
        var config = new NetworkBootConfig(1, nic.Id, BindIPv4.Text.Trim(), AllowedCidr.Text.Trim(), "EXTERNAL_DHCP", port, _status?.Config.MaxConcurrentTransfers ?? 8);
        await Client.PutAsync<JsonElement>("/api/v1/network-boot/config", config);
        _dirty = false;
        ResetValidation();
        await LoadAsync();
        Status.Text = "Configuração salva. Valide a rede e os artefatos antes de autorizar o início.";
    });
    private async void Validate_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        ResetValidation();
        var result = await Client.PostAsync<NetworkBootValidation>("/api/v1/network-boot/validate", new { });
        _validated = result.Valid;
        ValidationResult.Text = string.Join("\n", new[] { result.Valid ? "Pré-verificação aprovada." : "Pré-verificação bloqueada." }
            .Concat(result.Errors.Select(error => "Erro: " + error)).Concat(result.Warnings.Select(warning => "Aviso: " + warning)));
        Status.Text = result.Valid ? "Confira a interface e marque a autorização da rede de bancada para iniciar." : "Corrija os erros informados antes de iniciar.";
    });
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!_validated || _dirty || NetworkConsent.IsChecked != true) throw new InvalidOperationException("Valide a configuração e confirme a autorização desta rede de bancada.");
        NetworkConsent.IsChecked = false;
        var status = await Client.PostAsync<NetworkBootStatus>("/api/v1/network-boot/start", new { confirmIsolatedNetwork = true });
        UpdateData(status, _interfaces);
        Status.Text = "Solicitação de início enviada. Confira o estado e os eventos; o pareamento dos agentes continua independente.";
    });
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var status = await Client.PostAsync<NetworkBootStatus>("/api/v1/network-boot/stop", new { });
        ResetValidation();
        UpdateData(status, _interfaces);
        Status.Text = "Solicitação de parada concluída. Confira o estado; jobs do Controller seguem seu próprio ciclo.";
    });
    private void PickManifest_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Selecionar manifesto do pacote SLT Netboot no servidor", Filter = "Manifesto SLT Netboot|netboot-manifest.json" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) ManifestPath.Text = picker.FileName;
    }
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var path = ManifestPath.Text.Trim();
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("netboot-manifest.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selecione netboot-manifest.json do pacote local de boot.");
        await Client.PostAsync<JsonElement>("/api/v1/network-boot/profiles/import", new { directoryPath = Path.GetDirectoryName(path) });
        ResetValidation();
        await LoadAsync();
        Status.Text = "Perfil importado. Confira a compatibilidade e valide novamente antes de iniciar.";
    });
    private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private async void ToggleProfile_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var profile = Profiles.SelectedItem as NetworkBootProfile ?? throw new InvalidOperationException("Selecione um perfil.");
        await Client.PutAsync<JsonElement>("/api/v1/network-boot/profiles/" + Uri.EscapeDataString(profile.Id), new { enabled = !profile.Enabled });
        ResetValidation();
        await LoadAsync();
        Status.Text = "Estado do perfil atualizado. Valide os artefatos antes de iniciar.";
    });
}
