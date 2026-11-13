using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SLT.Desktop;

public partial class ServerDisksPanel : UserControl
{
    private Func<ControllerClient>? _client;
    private ServerDiskInventory? _inventory;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _busy;

    public ServerDisksPanel()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => { if (!_busy && IsVisible) await Safe(RefreshOperationsAsync, false); };
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue && _client != null) { _timer.Start(); if (_inventory == null) await Safe(RefreshAsync); } else _timer.Stop(); };
    }
    public void Configure(Func<ControllerClient> client) => _client = client;
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao servidor.");
    private const string Base = "/api/v1/server-local";

    private async Task Safe(Func<Task> action, bool show = true)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.InvalidDataException)
        { if (show) MessageBox.Show(Window.GetWindow(this), e.Message, "Discos do servidor", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; }
    }

    private async Task RefreshAsync()
    {
        var number = (Disks.SelectedItem as ServerDisk)?.Number;
        _inventory = await Client.GetAsync<ServerDiskInventory>(Base + "/disks");
        Disks.ItemsSource = _inventory.Disks;
        Disks.SelectedItem = _inventory.Disks.FirstOrDefault(d => d.Number == number) ?? _inventory.Disks.FirstOrDefault();
        CloneTarget.ItemsSource = _inventory.Disks.Where(d => d.Protections.Length == 0).ToArray();
        if (_inventory.VirtualOnly) Hint.Text = "MODO DE TESTE: só discos virtuais (VHD/VHDX) podem ser gravados. " + Hint.Text;
        await RefreshOperationsAsync();
    }
    private async Task RefreshOperationsAsync()
    {
        var selected = (Operations.SelectedItem as ServerDiskOperation)?.Id;
        var ops = await Client.GetAsync<ServerDiskOperation[]>(Base + "/disk-operations");
        Operations.ItemsSource = ops;
        Operations.SelectedItem = ops.FirstOrDefault(o => o.Id == selected);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Safe(RefreshAsync);
    private void Disks_SelectionChanged(object sender, SelectionChangedEventArgs e) => Partitions.ItemsSource = (Disks.SelectedItem as ServerDisk)?.Partitions ?? [];

    private ServerDisk Disk => Disks.SelectedItem as ServerDisk ?? throw new InvalidOperationException("Selecione um disco.");
    private ServerPartition Partition => Partitions.SelectedItem as ServerPartition ?? throw new InvalidOperationException("Selecione uma partição do disco.");
    private string Combo(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    // Plan on the server, show what will happen, confirm by typing, then the server runs it.
    private async Task PlanAndRunAsync(ServerDiskRequest request, string consent, string approve)
    {
        var plan = await Client.PostAsync<ServerDiskOperation>(Base + "/disk-operations", request);
        var lines = new List<string> { plan.Title };
        var disk = _inventory?.Disks.FirstOrDefault(d => d.Number == request.DiskNumber);
        if (disk != null) lines.Add($"Disco {disk.Number}: {disk.Model} • serial {(disk.Serial.Length > 0 ? disk.Serial : "—")} • {disk.Size} • {disk.Bus} • {disk.Table}");
        if (request.TargetDiskNumber is { } t && _inventory?.Disks.FirstOrDefault(d => d.Number == t) is { } target)
            lines.Add($"DESTINO {target.Number}: {target.Model} • serial {(target.Serial.Length > 0 ? target.Serial : "—")} • {target.Size}. Partições que serão apagadas: {(target.Partitions.Length == 0 ? "nenhuma" : string.Join("; ", target.Partitions.Select(p => p.ToString())))}");
        var dialog = new TypedConfirmWindow(plan.Title, lines, plan.Warnings, plan.Challenge, plan.RequiresTyping, consent, approve) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) { await Client.PostAsync<ServerDiskOperation>($"{Base}/disk-operations/{plan.Id}/cancel", new { }); return; }
        await Client.PostAsync<ServerDiskOperation>($"{Base}/disk-operations/{plan.Id}/confirm", new ConfirmRequest(plan.Challenge));
        await RefreshOperationsAsync();
    }
    private Task Plan(string kind, string consent, string approve, Func<ServerDisk, ServerDiskRequest>? build = null) => Safe(async () =>
    {
        var d = Disk;
        await PlanAndRunAsync(build?.Invoke(d) ?? new ServerDiskRequest(kind, d.Number, d.Fingerprint), consent, approve);
    });

    private async void Check_Click(object sender, RoutedEventArgs e) => await Plan("CHECK_DISK", "Executar a verificação (não altera o disco).", "Verificar");
    private async void FormatDisk_Click(object sender, RoutedEventArgs e) => await Plan("FORMAT_DISK", "Conferi o disco e autorizo apagar tudo o que há nele.", "Formatar",
        d => new ServerDiskRequest("FORMAT_DISK", d.Number, d.Fingerprint, TableType: Combo(TableType), FileSystem: Combo(FileSystem), Label: VolumeLabel.Text.Trim()));
    private async void ConvertTable_Click(object sender, RoutedEventArgs e) => await Plan("CONVERT_TABLE", "Conferi o disco e autorizo apagar suas partições.", "Converter",
        d => new ServerDiskRequest("CONVERT_TABLE", d.Number, d.Fingerprint, TableType: d.PartitionStyle == "GPT" ? "MBR" : "GPT"));
    private async void Mbr2Gpt_Click(object sender, RoutedEventArgs e)
    {
        var validateOnly = MessageBox.Show(Window.GetWindow(this), "Primeiro só validar se o disco pode ser convertido?\n\nSim = só validar (não altera)\nNão = converter agora", "MBR → GPT", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (validateOnly == MessageBoxResult.Cancel) return;
        await Plan("MBR2GPT", "Tenho backup e autorizo a conversão da tabela deste disco.", validateOnly == MessageBoxResult.Yes ? "Validar" : "Converter",
            d => new ServerDiskRequest("MBR2GPT", d.Number, d.Fingerprint, ValidateOnly: validateOnly == MessageBoxResult.Yes));
    }
    private async void Clone_Click(object sender, RoutedEventArgs e) => await Plan("CLONE_DISK", "Conferi origem e destino e autorizo apagar o destino.", "Clonar", d =>
    {
        var target = CloneTarget.SelectedItem as ServerDisk ?? throw new InvalidOperationException("Escolha o disco de destino do clone.");
        return new ServerDiskRequest("CLONE_DISK", d.Number, d.Fingerprint, TargetDiskNumber: target.Number, TargetFingerprint: target.Fingerprint);
    });
    private async void Offline_Click(object sender, RoutedEventArgs e) => await Plan("SET_OFFLINE", "Deixar este disco offline.", "Colocar offline");
    private async void Online_Click(object sender, RoutedEventArgs e) => await Plan("SET_ONLINE", "Colocar este disco online.", "Colocar online");

    private async void FormatVolume_Click(object sender, RoutedEventArgs e) => await Plan("FORMAT_VOLUME", "Conferi a partição e autorizo apagar seus arquivos.", "Formatar partição",
        d => new ServerDiskRequest("FORMAT_VOLUME", d.Number, d.Fingerprint, Partition.Number, FileSystem: Combo(FileSystem), Label: VolumeLabel.Text.Trim()));
    private async void Resize_Click(object sender, RoutedEventArgs e) => await Plan("RESIZE_PARTITION", "Tenho backup e autorizo redimensionar a partição.", "Redimensionar", d =>
    {
        if (!double.TryParse(NewSize.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) || gb < 0)
            throw new InvalidOperationException("Novo tamanho em GB (0 = máximo possível).");
        return new ServerDiskRequest("RESIZE_PARTITION", d.Number, d.Fingerprint, Partition.Number, SizeBytes: (long)(gb * 1e9) / (1024 * 1024) * (1024 * 1024));
    });
    private async void RepairVolume_Click(object sender, RoutedEventArgs e) => await Plan("REPAIR_VOLUME", "Autorizo corrigir o sistema de arquivos (o volume fica indisponível).", "Corrigir",
        d => new ServerDiskRequest("REPAIR_VOLUME", d.Number, d.Fingerprint, Partition.Number));
    private async void BootRepair_Click(object sender, RoutedEventArgs e) => await Plan("BOOT_REPAIR", "Autorizo recriar os arquivos de inicialização deste Windows.", "Reparar inicialização",
        d => new ServerDiskRequest("BOOT_REPAIR", d.Number, d.Fingerprint, Partition.Number));
    private async void OfflineRepair_Click(object sender, RoutedEventArgs e) => await Plan("SYSTEM_REPAIR", "Autorizo reparar os arquivos de sistema deste Windows.", "Reparar",
        d => new ServerDiskRequest("SYSTEM_REPAIR", d.Number, d.Fingerprint, Partition.Number, Mode: "OFFLINE"));
    private async void OnlineRepair_Click(object sender, RoutedEventArgs e) => await Safe(() => PlanAndRunAsync(new ServerDiskRequest("SYSTEM_REPAIR", -1, "", Mode: "ONLINE"), "Autorizo o reparo (DISM e SFC) do Windows deste servidor.", "Reparar"));

    private void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        if (Operations.SelectedItem is not ServerDiskOperation op) return;
        var text = $"{op.Title}\nEstado: {op.StateLabel}\n{op.Message}\n\n{op.Log}\n{JsonPretty.Format(op.ResultJson)}";
        new DetailsWindow("Registro da operação", text) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private async void CancelOperation_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var op = Operations.SelectedItem as ServerDiskOperation ?? throw new InvalidOperationException("Selecione o clone em andamento.");
        if (MessageBox.Show(Window.GetWindow(this), "Interromper o clone deixa o destino incompleto. Continuar?", "Interromper clone", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Client.PostAsync<ServerDiskOperation>($"{Base}/disk-operations/{op.Id}/cancel", new { });
        await RefreshOperationsAsync();
    });
}
