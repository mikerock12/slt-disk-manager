using System.Collections.ObjectModel;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SLT.Desktop;

public partial class FilesPanel : UserControl
{
    private Func<ControllerClient>? _client;
    private Func<Job, Task>? _review;
    private Func<Task>? _refresh;
    private readonly ObservableCollection<BackupSelection> _basket = [];
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private PendingFileList? _pending;
    private string? _nextCursor;
    private string _listedPath = "";
    private bool _updating;
    private bool _busy;
    private bool _polling;
    public FilesPanel()
    {
        InitializeComponent();
        Basket.ItemsSource = _basket;
        RestoreFolder.Text = $"SLT-Restauracao-{DateTime.Now:yyyyMMdd-HHmmss}";
        _pollTimer.Tick += async (_, _) => await PollListAsync();
        Unloaded += (_, _) => _pollTimer.Stop();
        Loaded += (_, _) => { if (_pending is not null) _pollTimer.Start(); };
    }
    public void Configure(Func<ControllerClient> client, Func<Job, Task> review, Func<Task> refresh)
    { _client = client; _review = review; _refresh = refresh; }
    public void UpdateData(Device[] devices, FileBundle[] bundles)
    {
        _updating = true;
        try
        {
            var browseId = (BrowseDevice.SelectedItem as Device)?.Id;
            var browseRootId = (BrowseRoot.SelectedItem as FileRootInfo)?.Id;
            var restoreId = (RestoreDevice.SelectedItem as Device)?.Id;
            var restoreRootId = (RestoreRoot.SelectedItem as FileRootInfo)?.Id;
            var bundleId = (Bundles.SelectedItem as FileBundle)?.Id;
            BrowseDevice.ItemsSource = devices.Where(d => d.TrustStatus == "TRUSTED" && d.Inventory?.FileRoots?.Any(r => r.CanRead) == true).ToArray();
            RestoreDevice.ItemsSource = devices.Where(d => d.TrustStatus == "TRUSTED" && d.Inventory?.FileRoots?.Any(r => r.CanWrite) == true).ToArray();
            BrowseDevice.SelectedItem = devices.FirstOrDefault(d => d.Id == browseId);
            RestoreDevice.SelectedItem = devices.FirstOrDefault(d => d.Id == restoreId);
            FillRoots(BrowseDevice, BrowseRoot, browseRootId, false);
            FillRoots(RestoreDevice, RestoreRoot, restoreRootId, true);
            Bundles.ItemsSource = bundles;
            Bundles.SelectedItem = bundles.FirstOrDefault(b => b.Id == bundleId);
        }
        finally { _updating = false; }
        UpdateButtons();
    }
    private static void FillRoots(ComboBox device, ComboBox roots, string? selectedId, bool writable)
    {
        var all = ((device.SelectedItem as Device)?.Inventory?.FileRoots ?? []).Where(r => writable ? r.CanWrite : r.CanRead).ToArray();
        roots.ItemsSource = all;
        roots.SelectedItem = all.FirstOrDefault(r => r.Id == selectedId);
    }
    private void BrowseDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || BrowseRoot is null) return;
        FillRoots(BrowseDevice, BrowseRoot, null, false);
        ResetBrowser();
    }
    private void BrowseRoot_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && Entries is not null) ResetBrowser(); }
    private void RestoreDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_updating && RestoreRoot is not null) FillRoots(RestoreDevice, RestoreRoot, null, true); }
    private void ResetBrowser()
    {
        // An outstanding read may finish on the server, but cannot update a different scope.
        _pending = null; _pollTimer.Stop(); _nextCursor = null; _listedPath = "";
        RelativePath.Text = ""; Entries.ItemsSource = Array.Empty<FileEntry>(); _basket.Clear(); ReadConsent.IsChecked = false;
        BrowserStatus.Text = "Selecione uma raiz autorizada, confirme a autorização e solicite a listagem.";
        UpdateButtons();
    }
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao controlador.");
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Arquivos", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        if (ListButton is null) return;
        var pending = _pending is not null;
        ListButton.IsEnabled = !_busy && !pending;
        UpButton.IsEnabled = !_busy && !pending && _listedPath.Length > 0;
        NextButton.IsEnabled = !_busy && !pending && !string.IsNullOrEmpty(_nextCursor);
        CancelListButton.IsEnabled = !_busy && pending;
        BackupButton.IsEnabled = !_busy && !pending;
        RestoreButton.IsEnabled = !_busy;
        VerifyBundleButton.IsEnabled = !_busy;
        ImportFileButton.IsEnabled = !_busy;
        ImportFolderButton.IsEnabled = !_busy;
    }
    private async void List_Click(object sender, RoutedEventArgs e) => await RunAsync(() => RequestListAsync(RelativePath.Text.Trim(), null));
    private async void Next_Click(object sender, RoutedEventArgs e) => await RunAsync(() => RequestListAsync(_listedPath, _nextCursor));
    private async void Up_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var slash = _listedPath.LastIndexOf('/');
        return RequestListAsync(slash < 0 ? "" : _listedPath[..slash], null);
    });
    private async void Entries_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject clicked || FindParent<DataGridRow>(clicked) is null) return;
        if (Entries.SelectedItem is FileEntry { IsDirectory: true, IsLink: false } directory && _pending is null)
            await RunAsync(() => RequestListAsync(directory.RelativePath, null));
    }
    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? current = child; current is not null; current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
    private async Task RequestListAsync(string relativePath, string? cursor)
    {
        if (ReadConsent.IsChecked != true) throw new InvalidOperationException("Confirme a autorização para listar estes arquivos.");
        var device = BrowseDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione o computador de origem.");
        var root = BrowseRoot.SelectedItem as FileRootInfo ?? throw new InvalidOperationException("Selecione uma raiz autorizada para leitura.");
        if (device.Inventory?.Capabilities.Contains("FILE_LIST") != true) throw new InvalidOperationException("O agente não oferece listagem de arquivos.");
        ValidateRelative(relativePath, true);
        var job = await Client.PostAsync<Job>("/api/v1/files/list", new { deviceId = device.Id, rootId = root.Id, relativePath, cursor, pageSize = 200, consent = true });
        if ((BrowseDevice.SelectedItem as Device)?.Id != device.Id || (BrowseRoot.SelectedItem as FileRootInfo)?.Id != root.Id) return;
        _pending = new PendingFileList(job.Id, device.Id, root.Id, relativePath);
        BrowserStatus.Text = $"Listagem solicitada. Tarefa {job.Id}: {job.State}. Você pode navegar pelas outras abas enquanto o agente responde.";
        _pollTimer.Start();
        UpdateButtons();
    }
    private async Task PollListAsync()
    {
        if (_polling || _pending is not { } pending) return;
        _polling = true;
        try
        {
            var job = await Client.GetAsync<Job>($"/api/v1/jobs/{Uri.EscapeDataString(pending.JobId)}");
            if (_pending != pending) return;
            if (job.State == "COMPLETED")
            {
                if (job.Result is not { ValueKind: JsonValueKind.Object } result || !result.TryGetProperty("details", out var details))
                    throw new InvalidDataException("Resultado de listagem sem dados.");
                var page = JsonSerializer.Deserialize<FilePage>(details.GetString() ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Página de arquivos inválida.");
                if (page.RootId != pending.RootId || page.RelativePath != pending.RelativePath) throw new InvalidDataException("Resposta não corresponde à raiz e ao caminho solicitados.");
                Entries.ItemsSource = page.Entries;
                _listedPath = page.RelativePath; RelativePath.Text = page.RelativePath; _nextCursor = page.NextCursor;
                BrowserStatus.Text = $"{(BrowseRoot.SelectedItem as FileRootInfo)?.Label} / {page.RelativePath} • {page.Entries.Length} entradas nesta página{(string.IsNullOrEmpty(page.NextCursor) ? " • última página" : " • há mais entradas")}. Links são bloqueados.";
                _pending = null; _pollTimer.Stop();
            }
            else if (job.State is "FAILED" or "CANCELLED" or "INTERRUPTED" or "BLOCKED")
            {
                var message = job.Result is { ValueKind: JsonValueKind.Object } result && result.TryGetProperty("message", out var m) ? m.GetString() : job.State;
                BrowserStatus.Text = $"Listagem não concluída: {message}. Solicite nova listagem para reiniciar a paginação.";
                _pending = null; _pollTimer.Stop(); _nextCursor = null;
            }
            else BrowserStatus.Text = $"Listagem: {job.State}. Aguardando o agente; outras abas continuam disponíveis.";
        }
        catch (Exception ex)
        {
            BrowserStatus.Text = "Falha ao obter a listagem: " + ex.Message;
            _pending = null; _pollTimer.Stop(); _nextCursor = null;
        }
        finally { _polling = false; UpdateButtons(); }
    }
    private async void CancelList_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_pending is { } pending) await Client.PostAsync<Job>($"/api/v1/jobs/{Uri.EscapeDataString(pending.JobId)}/cancel", new { });
    });
    private void AddSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = Entries.SelectedItems.Cast<FileEntry>().ToArray();
        foreach (var entry in selected.Where(x => !x.IsLink))
            if (_basket.All(x => x.RelativePath != entry.RelativePath)) _basket.Add(new BackupSelection(entry.RelativePath, entry.IsDirectory));
        BrowserStatus.Text = $"{_basket.Count} caminhos na seleção de backup. Seleções de pastas incluem seu conteúdo e pastas vazias. Links não foram adicionados.";
    }
    private void RemoveSelection_Click(object sender, RoutedEventArgs e) { if (Basket.SelectedItem is BackupSelection entry) _basket.Remove(entry); }
    private async void Backup_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ReadConsent.IsChecked != true) throw new InvalidOperationException("Confirme a autorização de acesso e cópia dos arquivos.");
        var device = BrowseDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione a origem.");
        var root = BrowseRoot.SelectedItem as FileRootInfo ?? throw new InvalidOperationException("Selecione a raiz de origem.");
        if (_basket.Count == 0) throw new InvalidOperationException("Adicione os arquivos ou pastas desejados à seleção.");
        if (device.Inventory?.Capabilities.Contains("FILE_BACKUP") != true) throw new InvalidOperationException("O agente não oferece backup de arquivos.");
        if (string.IsNullOrWhiteSpace(BundleName.Text)) throw new InvalidOperationException("Dê um nome para o backup.");
        // Remove descendants when their directory is already selected; the agent enumerates that directory once.
        var paths = _basket.Where(x => !_basket.Any(parent => parent.IsDirectory && parent.RelativePath != x.RelativePath && x.RelativePath.StartsWith(parent.RelativePath + "/", StringComparison.Ordinal))).Select(x => x.RelativePath).ToArray();
        foreach (var path in paths) ValidateRelative(path, false);
        var file = new FileRequest(root.Id, "", paths, "", BundleName.Text.Trim(), null, 200);
        var job = await Client.PostAsync<Job>("/api/v1/jobs/plan", new { kind = "FILE_BACKUP", deviceId = device.Id, diskFingerprint = "", imageId = (string?)null, file, consent = true });
        if (_refresh is not null) await _refresh();
        if (_review is not null) await _review(job);
        BrowserStatus.Text = $"Plano de backup criado: {job.Id}. Acompanhe aprovação/progresso na aba Tarefas e logs e resultado no catálogo.";
    });
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (RestoreConsent.IsChecked != true) throw new InvalidOperationException("Confirme a autorização para gravar o backup no cliente selecionado.");
        var bundle = Bundles.SelectedItem as FileBundle ?? throw new InvalidOperationException("Selecione um backup no catálogo.");
        if (bundle.State != "AVAILABLE") throw new InvalidOperationException("O backup precisa estar AVAILABLE para restauração.");
        var device = RestoreDevice.SelectedItem as Device ?? throw new InvalidOperationException("Selecione o computador de destino.");
        var root = RestoreRoot.SelectedItem as FileRootInfo ?? throw new InvalidOperationException("Selecione uma raiz com permissão de gravação.");
        if (device.Inventory?.Capabilities.Contains("FILE_RESTORE") != true) throw new InvalidOperationException("O agente não oferece restauração de arquivos.");
        var directory = RestoreFolder.Text.Trim(); ValidateRelative(directory, false);
        var file = new FileRequest(root.Id, directory, [], bundle.Id, bundle.Name, null, 200);
        var job = await Client.PostAsync<Job>("/api/v1/jobs/plan", new { kind = "FILE_RESTORE", deviceId = device.Id, diskFingerprint = "", imageId = (string?)null, file, consent = true });
        if (_refresh is not null) await _refresh();
        if (_review is not null) await _review(job);
        RestoreConsent.IsChecked = false;
        BrowserStatus.Text = $"Plano de restauração criado: {job.Id}. A pasta nova será promovida somente após validar manifesto e hashes.";
    });
    private async void VerifyBundle_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var bundle = Bundles.SelectedItem as FileBundle ?? throw new InvalidOperationException("Selecione um backup.");
        var result = await Client.PostAsync<FileBundle>($"/api/v1/file-bundles/{Uri.EscapeDataString(bundle.Id)}/verify", new { });
        if (_refresh is not null) await _refresh();
        BrowserStatus.Text = result.State == "VERIFYING" ? "Verificação iniciada em segundo plano. Aguarde o catálogo apresentar AVAILABLE ou FAILED." : $"Estado de verificação: {result.State}.";
    });
    private void BundleDetails_Click(object sender, RoutedEventArgs e)
    {
        if (Bundles.SelectedItem is FileBundle bundle) new DetailsWindow("Manifesto do backup de arquivos", JsonSerializer.Serialize(bundle, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private async void ImportLocalFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Selecionar arquivo local do servidor para enviar aos clientes", Filter = "Todos os arquivos|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await ImportLocalAsync(picker.FileName);
    }
    private async void ImportLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Selecionar pasta local do servidor para enviar aos clientes" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await ImportLocalAsync(picker.FolderName);
    }
    private async Task ImportLocalAsync(string path) => await RunAsync(async () =>
    {
        if (MessageBox.Show(Window.GetWindow(this), $"Importar uma cópia destes arquivos para o repositório local do servidor?\n\n{path}\n\nConfirme que possui autorização. Depois da verificação, você poderá enviar o conteúdo para uma pasta nova no cliente.", "Importar arquivos locais", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var bundle = await Client.PostAsync<FileBundle>("/api/v1/file-bundles/import", new { path, name, consent = true });
        if (_refresh is not null) await _refresh();
        BrowserStatus.Text = $"Importação local {bundle.Name}: {bundle.State}. Aguarde AVAILABLE no catálogo para enviar ao cliente; hashes e manifesto são verificados em segundo plano.";
    });
    private static void ValidateRelative(string path, bool allowEmpty)
    {
        if (allowEmpty && path.Length == 0) return;
        if (path.Length == 0 || path.Length > 2048 || path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            path.StartsWith('/') || path.EndsWith('/') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidOperationException("Use um caminho relativo à raiz, com '/' entre pastas, sem '..', unidade ou caminho absoluto.");
    }
    private sealed record PendingFileList(string JobId, string DeviceId, string RootId, string RelativePath);
    private sealed record BackupSelection(string RelativePath, bool IsDirectory)
    { public string Label => $"{(IsDirectory ? "[Pasta]" : "[Arquivo]")} {RelativePath}"; }
}
