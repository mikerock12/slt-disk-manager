using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SLT.Desktop;

public partial class VirtualMachinesPanel : UserControl
{
    private const string Base = "/api/v1/server-local";
    private Func<ControllerClient>? _client;
    private VmStatus? _status;
    private bool _busy;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public VirtualMachinesPanel()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => { if (!_busy && IsVisible) await Safe(RefreshAsync, false); };
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue && _client != null) { _timer.Start(); await Safe(LoadAsync); } else _timer.Stop(); };
    }
    public void Configure(Func<ControllerClient> client) => _client = client;
    private ControllerClient Client => _client?.Invoke() ?? throw new InvalidOperationException("Conecte ao servidor.");

    private async Task Safe(Func<Task> action, bool show = true)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or InvalidDataException or System.ComponentModel.Win32Exception)
        { if (show) MessageBox.Show(Window.GetWindow(this), e.Message, "Máquinas virtuais", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; }
    }

    private async Task LoadAsync()
    {
        await RefreshAsync();
        var disks = await Client.GetAsync<ServerDiskInventory>(Base + "/disks");
        PhysicalDisk.ItemsSource = disks.Disks.Where(d => d.Protections.Length == 0).ToArray();
    }
    private async Task RefreshAsync()
    {
        var selected = (Machines.SelectedItem as VmMachine)?.Name;
        _status = await Client.GetAsync<VmStatus>(Base + "/vms");
        if (!_status.Available) { HostStatus.Text = "Hyper-V indisponível: " + _status.Reason; Machines.ItemsSource = null; return; }
        HostStatus.Text = $"Hyper-V pronto • {_status.LogicalProcessors} processadores lógicos • {Display.Bytes(_status.MemoryBytes)} de memória no servidor • redes: {string.Join(", ", _status.Switches ?? [])}";
        Memory.Maximum = Math.Max(1024, (_status.MemoryBytes / (1024 * 1024) - 2048) / 512 * 512);
        if (CpuCount.Items.Count != _status.LogicalProcessors)
        {
            CpuCount.ItemsSource = Enumerable.Range(1, Math.Max(1, _status.LogicalProcessors)).ToArray();
            CpuCount.SelectedItem = Math.Min(2, _status.LogicalProcessors);
        }
        var switches = new List<string> { "Sem rede" };
        switches.AddRange(_status.Switches ?? []);
        if (Switch.Items.Count != switches.Count) { Switch.ItemsSource = switches; Switch.SelectedIndex = switches.Count > 1 ? 1 : 0; }
        var machines = _status.Machines ?? [];
        Machines.ItemsSource = machines;
        Machines.SelectedItem = machines.FirstOrDefault(m => m.Name == selected);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Safe(LoadAsync);
    private void Memory_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (MemoryCaption != null) MemoryCaption.Text = $"Memória RAM: {Memory.Value:0} MB"; }
    private void Cpu_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (CpuCaption != null) CpuCaption.Text = $"Uso máximo do processador: {CpuPercent.Value:0}%"; }
    private void Firmware_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (UefiOptions != null) UefiOptions.IsEnabled = Generation == 2; }
    private int Generation => int.Parse(((Firmware.SelectedItem as ComboBoxItem)?.Tag as string) ?? "2");

    private void PickIso_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "ISO de instalação", Filter = "Imagem ISO|*.iso" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) IsoPath.Text = picker.FileName;
    }
    private void PickVhd_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Disco virtual", Filter = "Disco virtual|*.vhdx;*.vhd" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) ExistingVhd.Text = picker.FileName;
    }
    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Pasta para o disco virtual (escolha o disco com espaço livre)" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) VhdFolder.Text = picker.FolderName;
    }

    private async void Create_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        VmDiskRequest disk;
        if (DiskNew.IsChecked == true)
        {
            if (!int.TryParse(VhdSize.Text, out var size)) throw new InvalidOperationException("Tamanho do disco virtual em GB.");
            disk = new VmDiskRequest("NEW", Folder: VhdFolder.Text.Trim(), SizeGiB: size);
        }
        else if (DiskPhysical.IsChecked == true)
        {
            var d = PhysicalDisk.SelectedItem as ServerDisk ?? throw new InvalidOperationException("Escolha o HD/SSD do servidor (só aparecem discos livres para gravar).");
            disk = new VmDiskRequest("PHYSICAL", DiskNumber: d.Number, Fingerprint: d.Fingerprint);
        }
        else if (DiskExisting.IsChecked == true) disk = new VmDiskRequest("EXISTING", Path: ExistingVhd.Text.Trim());
        else disk = new VmDiskRequest("NONE");
        var uefi = Generation == 2;
        var request = new VmCreateRequest(VmName.Text.Trim(), Generation, (int)Memory.Value, (int)(CpuCount.SelectedItem ?? 1), (int)CpuPercent.Value,
            uefi && SecureBoot.IsChecked == true, uefi && SecureBoot.IsChecked == true ? (SecureTemplate.SelectedItem as ComboBoxItem)?.Tag as string : null,
            uefi && Tpm.IsChecked == true, disk, string.IsNullOrWhiteSpace(IsoPath.Text) ? null : IsoPath.Text.Trim(), Switch.SelectedIndex > 0 ? Switch.SelectedItem as string : null);
        var plan = await Client.PostAsync<VmPlan>(Base + "/vms/plan", request);
        var dialog = new TypedConfirmWindow(plan.Title, [$"Inicialização: {(uefi ? "UEFI (geração 2)" : "Legacy/BIOS (geração 1)")}", $"ISO: {request.IsoPath ?? "nenhuma"}", $"Rede: {request.SwitchName ?? "sem rede"}"],
            plan.Warnings, plan.Challenge, plan.RequiresTyping, plan.RequiresTyping ? "Conferi o HD e autorizo entregá-lo à máquina virtual." : "Criar esta máquina virtual.", "Criar") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        await Client.PostAsync<object>($"{Base}/vms/plan/{plan.Id}/confirm", new ConfirmRequest(plan.Challenge));
        await LoadAsync();
        MessageBox.Show(Window.GetWindow(this), "Máquina virtual criada. Selecione-a e use Ligar e Abrir a tela da VM para instalar o sistema.", "Máquinas virtuais", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    private VmMachine Machine => Machines.SelectedItem as VmMachine ?? throw new InvalidOperationException("Selecione uma máquina virtual.");
    private Task Action(string action, VmNameRequest? body = null) => Safe(async () =>
    {
        await Client.PostAsync<object>($"{Base}/vms/{Uri.EscapeDataString(Machine.Name)}/{action}", body ?? new VmNameRequest());
        await RefreshAsync();
    });
    private async void Start_Click(object sender, RoutedEventArgs e) => await Action("start");
    private async void Stop_Click(object sender, RoutedEventArgs e) => await Action("stop");
    private async void TurnOff_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Forçar o desligamento é como tirar da tomada: dados não salvos na VM se perdem. Continuar?", "Forçar desligamento", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) await Action("turn-off");
    }
    private async void SetIso_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "ISO para a máquina virtual", Filter = "Imagem ISO|*.iso" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await Action("set-iso", new VmNameRequest(IsoPath: picker.FileName));
    }
    private async void EjectIso_Click(object sender, RoutedEventArgs e) => await Action("set-iso", new VmNameRequest(IsoPath: ""));
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        VmMachine vm;
        try { vm = Machine; } catch (InvalidOperationException x) { MessageBox.Show(Window.GetWindow(this), x.Message); return; }
        var hasPhysical = vm.Drives.Any(d => d.DiskNumber >= 0);
        var files = MessageBox.Show(Window.GetWindow(this), $"Excluir a máquina virtual {vm.Name}?\n\nSim = excluir também o disco virtual (VHDX) criado pelo SLT\nNão = manter o VHDX\n\n" +
            (hasPhysical ? "O HD físico entregue à VM não é apagado; ele continua offline para você levá-lo ao cliente." : ""), "Excluir máquina virtual", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (files == MessageBoxResult.Cancel) return;
        await Action("delete", new VmNameRequest(DeleteFiles: files == MessageBoxResult.Yes));
    }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await Safe(() =>
    {
        var vmconnect = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe");
        if (!File.Exists(vmconnect)) throw new InvalidOperationException("Conexão de Máquina Virtual (vmconnect.exe) não está instalada: ative as ferramentas de gerenciamento do Hyper-V.");
        var start = new ProcessStartInfo(vmconnect) { UseShellExecute = false };
        start.ArgumentList.Add("localhost");
        start.ArgumentList.Add(Machine.Name);
        Process.Start(start);
        return Task.CompletedTask;
    });
}
