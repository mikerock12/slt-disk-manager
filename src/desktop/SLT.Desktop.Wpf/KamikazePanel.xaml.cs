using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SLT.Desktop;

public partial class KamikazePanel : UserControl
{
    private const string Base = "/api/v1/server-local/kamikaze";
    private Func<ControllerClient>? _client;
    private KamikazeStatus? _status;
    private readonly ObservableCollection<KamikazeOperationRequest> _planned = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _busy;

    public KamikazePanel()
    {
        InitializeComponent();
        Planned.ItemsSource = _planned;
        _timer.Tick += async (_, _) => { if (!_busy && IsVisible && _status?.Sessions.Any(s => s.State is "STAGING" or "STAGED" or "REBOOTING") == true) await Safe(RefreshAsync, false); };
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue && _client != null) { _timer.Start(); await Safe(RefreshAsync); } else _timer.Stop(); };
    }
    public void Configure(Func<ControllerClient> client) => _client = client;
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao servidor.");

    private async Task Safe(Func<Task> action, bool show = true)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or InvalidDataException)
        { if (show) MessageBox.Show(Window.GetWindow(this), e.Message, "Modo kamikaze", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; }
    }

    private async Task RefreshAsync()
    {
        var statusTask = Client.GetAsync<KamikazeStatus>(Base);
        var mediaTask = Client.GetAsync<DeploymentMedia[]>("/api/v1/media");
        _status = await statusTask;
        var secure = _status.SecureBoot switch { true => "ativo", false => "desativado", null => "não informado" };
        EnvironmentStatus.Text = $"Firmware do servidor: {_status.Firmware} • Secure Boot: {secure}\n" +
            $"Sistema do modo kamikaze: {(_status.BootImage.Source == "NONE" ? "INDISPONÍVEL — " : "")}{_status.BootImage.Detail}\n" +
            $"Pacotes e mídias ficam em {_status.RepositoryRoot}SLT-Kamikaze (unidade do repositório). Instalar o Windows exige que o repositório esteja em outro disco que não o de destino." +
            (_status.Agent ? "" : "\nAgente WinPE ausente no pacote do servidor: atualize o servidor.") + (_status.DiskError is { } error ? "\n" + error : "");
        Disks.ItemsSource = _status.Disks;
        var usable = _status.Disks.Where(d => !d.Notes.Any(n => n.StartsWith("Identidade ambígua"))).ToArray();
        var source = (Source.SelectedItem as KamikazeDiskView)?.Identity; var target = (Target.SelectedItem as KamikazeDiskView)?.Identity;
        Source.ItemsSource = usable; Target.ItemsSource = usable;
        Source.SelectedItem = usable.FirstOrDefault(d => d.Identity == source); Target.SelectedItem = usable.FirstOrDefault(d => d.Identity == target);
        var selected = (Sessions.SelectedItem as KamikazeSession)?.Id;
        Sessions.ItemsSource = _status.Sessions;
        Sessions.SelectedItem = _status.Sessions.FirstOrDefault(s => s.Id == selected) ?? _status.Sessions.FirstOrDefault();
        try
        {
            var media = (await mediaTask).Where(m => m.State == "AVAILABLE" && m.Kind == "WINDOWS").ToArray();
            if (Media.Items.Count != media.Length) Media.ItemsSource = media;
        }
        catch (InvalidOperationException) { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Safe(RefreshAsync);
    private string KindTag => (Kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "CLONE_DISK";
    private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox == null) return;
        SourceBox.Visibility = KindTag == "CLONE_DISK" ? Visibility.Visible : Visibility.Collapsed;
        FormatBox.Visibility = KindTag == "FORMAT_DISK" ? Visibility.Visible : Visibility.Collapsed;
        DeployBox.Visibility = KindTag == "DEPLOY_WINDOWS" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Media_SelectionChanged(object sender, SelectionChangedEventArgs e) => Edition.ItemsSource = (Media.SelectedItem as DeploymentMedia)?.Images ?? [];

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = Target.SelectedItem as KamikazeDiskView ?? throw new InvalidOperationException("Escolha o disco de destino.");
            KamikazeOperationRequest op = KindTag switch
            {
                "CLONE_DISK" => Source.SelectedItem is KamikazeDiskView s && s.Identity != target.Identity
                    ? new("CLONE_DISK", target.Identity, s.Identity) { Description = $"Clonar disco {s.Number} ({s.Model}, {s.Size}) → disco {target.Number} ({target.Model}, {target.Size})" }
                    : throw new InvalidOperationException("Escolha uma origem diferente do destino."),
                "FORMAT_DISK" => new("FORMAT_DISK", target.Identity, TableType: (TableType.SelectedItem as ComboBoxItem)!.Content.ToString(), FileSystem: (FileSystem.SelectedItem as ComboBoxItem)!.Content.ToString(), Label: VolumeLabel.Text.Trim())
                    { Description = $"Formatar disco {target.Number} ({target.Model}, {target.Size}) em {(TableType.SelectedItem as ComboBoxItem)!.Content}/{(FileSystem.SelectedItem as ComboBoxItem)!.Content}" },
                _ => Media.SelectedItem is DeploymentMedia m && Edition.SelectedItem is WindowsImage w
                    ? new("DEPLOY_WINDOWS", target.Identity, MediaId: m.Id, ImageIndex: w.Index) { Description = $"Instalar {m.Name} — {w.Name} no disco {target.Number} ({target.Model}, {target.Size})" }
                    : throw new InvalidOperationException("Escolha a mídia Windows e a edição (importe a ISO na aba Instalar sistema).")
            };
            if (_planned.Count >= 8) throw new InvalidOperationException("No máximo 8 operações por reinício.");
            _planned.Add(op);
        }
        catch (InvalidOperationException x) { MessageBox.Show(Window.GetWindow(this), x.Message, "Modo kamikaze", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Planned.SelectedItem is KamikazeOperationRequest op) _planned.Remove(op); }

    private async void Prepare_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (_planned.Count == 0) throw new InvalidOperationException("Adicione ao menos uma operação.");
        var session = await Client.PostAsync<KamikazeSession>(Base + "/plan", new KamikazePlanRequest(_planned.ToArray(), RebootNow.IsChecked == true));
        var lines = new List<string>(session.Summary)
        {
            "Na tela do servidor, depois do reinício, digite para cada operação: " + string.Join("  |  ", session.ConsoleChallenges ?? []),
            "Digitar CANCELAR na tela do servidor volta ao Windows sem gravar nada.",
            RebootNow.IsChecked == true ? "O servidor reinicia 60 segundos depois de pronto: salve o que estiver aberto." : "Depois de pronto, use Reiniciar agora."
        };
        var dialog = new TypedConfirmWindow("Preparar o modo kamikaze neste servidor", lines, session.Warnings, session.Challenge, true,
            "Estou na frente do servidor, conferi os discos e autorizo as operações acima.", "Preparar") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) { await Client.PostAsync<KamikazeSession>($"{Base}/{session.Id}/cancel", new { }); await RefreshAsync(); return; }
        await Client.PostAsync<KamikazeSession>($"{Base}/{session.Id}/confirm", new ConfirmRequest(session.Challenge));
        _planned.Clear();
        await RefreshAsync();
    });

    private KamikazeSession Session => Sessions.SelectedItem as KamikazeSession ?? throw new InvalidOperationException("Selecione uma sessão.");
    private async void Reboot_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var s = Session;
        if (MessageBox.Show(Window.GetWindow(this), "Reiniciar o servidor no modo kamikaze em 60 segundos? Os computadores conectados perdem a conexão até ele voltar.", "Reiniciar agora", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Client.PostAsync<KamikazeSession>($"{Base}/{s.Id}/reboot", new { });
        await RefreshAsync();
    });
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        await Client.PostAsync<KamikazeSession>($"{Base}/{Session.Id}/cancel", new { });
        await RefreshAsync();
    });
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not KamikazeSession s) return;
        var text = $"Sessão {s.Id}\nEstado: {s.StateLabel}\n{s.Message}\n\n{string.Join("\n", s.Summary)}\n\nFrases no console: {string.Join(" | ", s.ConsoleChallenges ?? [])}\n\n{s.Log}\n{JsonPretty.Format(s.ResultJson)}";
        new DetailsWindow("Modo kamikaze", text) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
