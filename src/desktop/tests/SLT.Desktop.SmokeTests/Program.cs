using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SLT.Desktop;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var app = new App(); app.InitializeComponent();
            var main = new MainWindow();
            Check(main.FindName("Cloning") is ClonePanel && main.FindName("Files") is FilesPanel, "XAML: abas de clonagem e arquivos constroem sem erro");
            Check(Children<TabItem>(main).Count() >= 8, "XAML: navegação principal preservada");
            var now = DateTimeOffset.UtcNow;
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var source = Device("source", "ORIGEM", ["CAPTURE", "RESTORE"], now);
            var target = Device("target", "DESTINO", ["RESTORE"], now);
            target = target with { Inventory = target.Inventory! with { Disks = [target.Inventory.Disks[0] with { SizeBytes = 8192 }] } };
            var plan = new JobPlan("job", "RESTORE", target.Id, "fingerprint", "/dev/test-only", 4096, 512, "image", "APAGAR DESTINO 1234", now.AddMinutes(15), true, new CloneOptions("FIT", 2));
            var job = new Job("job", plan, "AWAITING_APPROVAL", now, null, null, null);
            var approval = new PlanApprovalWindow(job, target, null, null);
            var challenge = Children<TextBox>(approval).Single();
            var consent = Children<CheckBox>(approval).Single();
            var approve = Children<Button>(approval).Single(b => Equals(b.Content, "Autorizar sobrescrita"));
            Check(challenge.Text == "" && consent.IsChecked == false && !approve.IsEnabled, "RESTORE inicia sem challenge preenchido nem consentimento");
            Check(!approve.IsDefault, "RESTORE não autoriza por Enter como botão padrão");
            consent.IsChecked = true; challenge.Text = "errado";
            Check(!approve.IsEnabled, "Challenge incorreto bloqueia autorização");
            challenge.Text = plan.Challenge;
            Check(approve.IsEnabled, "Challenge exato e consentimento permitem aprovação explícita");
            consent.IsChecked = false;
            Check(!approve.IsEnabled, "Remover consentimento bloqueia novamente");
            var expired = new PlanApprovalWindow(job with { Plan = plan with { ExpiresAtUtc = now.AddMinutes(-1) } }, target, null, null);
            Children<TextBox>(expired).Single().Text = plan.Challenge; Children<CheckBox>(expired).Single().IsChecked = true;
            Check(!Children<Button>(expired).Single(b => Equals(b.Content, "Autorizar sobrescrita")).IsEnabled, "Plano expirado permanece bloqueado");
            var serialized = JsonSerializer.Serialize(plan, json);
            Check(serialized.Contains("\"clone\":") && !serialized.Contains("\"cloneOptions\":"), "DTO preserva nome JSON clone");
            Check(JsonSerializer.Deserialize<JobPlan>(serialized, json)?.CloneOptions?.ResizePartitionNumber == 2, "DTO lê opções de adaptação assinadas");
            var summary=JsonSerializer.Deserialize<FileBundle>("{\"schemaVersion\":1,\"id\":\"catalog\",\"name\":\"Backup\",\"state\":\"AVAILABLE\",\"sourceDeviceId\":\"source\",\"rootId\":\"root\",\"createdAtUtc\":\"2026-10-27T00:00:00Z\",\"totalBytes\":512,\"entries\":[],\"entryCount\":100000}",json)!;
            Check(summary.EntryCount==100000 && summary.Entries.Length==0,"Catálogo leve mantém contagem sem carregar manifesto completo");
            var filesPlan = plan with { Kind = "FILE_RESTORE", Challenge = "GRAVAR DESTINO Pasta-Nova", File = new FileRequest("docs", "Pasta-Nova", [], "bundle", "Backup", null, 200) };
            var fileApproval = new PlanApprovalWindow(job with { Plan = filesPlan }, target, null, null, null, new FileRootInfo("docs", "Documentos autorizados", true, true));
            var fileText = string.Join("\n", Children<TextBlock>(fileApproval).Select(t => t.Text));
            Check(fileText.Contains("Pasta-Nova") && fileText.Contains("Documentos autorizados") && fileText.Contains("Não haverá sobrescrita"), "Aprovação de arquivos exibe raiz, pasta nova e ausência de sobrescrita");
            var clonePanel = (ClonePanel)main.FindName("Cloning");
            clonePanel.UpdateData([source, target], [], []);
            var sourceDevice = (ComboBox)clonePanel.FindName("SourceDevice"); sourceDevice.SelectedItem = source;
            var targetDevice = (ComboBox)clonePanel.FindName("TargetDevice"); targetDevice.SelectedItem = target;
            ((ComboBox)clonePanel.FindName("SourceDisk")).SelectedIndex = 0;
            ((ComboBox)clonePanel.FindName("TargetDisk")).SelectedIndex = 0;
            Check(!((Button)clonePanel.FindName("PlanButton")).IsEnabled, "PRESERVE_LAYOUT bloqueado sem capability RESTORE_LAYOUT");
            ((ComboBox)clonePanel.FindName("Strategy")).SelectedIndex = 0;
            Check(((Button)clonePanel.FindName("PlanButton")).IsEnabled, "EXACT_RAW disponível quando destino anuncia RESTORE");
            var filesPanel = (FilesPanel)main.FindName("Files");
            var fileDevice = source with { Inventory = source.Inventory! with { FileRoots = [new("docs", "Documentos", true, true)] } };
            filesPanel.UpdateData([fileDevice], []);
            ((ComboBox)filesPanel.FindName("BrowseDevice")).SelectedItem = fileDevice;
            ((ComboBox)filesPanel.FindName("BrowseRoot")).SelectedIndex = 0;
            var entries = (DataGrid)filesPanel.FindName("Entries");
            var normal = new FileEntry("normal.txt", "normal.txt", false, 12, now, false);
            var link = new FileEntry("link", "link", true, 0, now, true);
            entries.ItemsSource = new[] { normal, link }; entries.SelectedItems.Add(normal); entries.SelectedItems.Add(link);
            Children<Button>(filesPanel).Single(b => Equals(b.Content, "Adicionar seleção →")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((ListBox)filesPanel.FindName("Basket")).Items.Count == 1, "Seleção de backup exclui symlink/reparse anunciado");
            Check(((ComboBox)filesPanel.FindName("RestoreRoot")).Items.Count == 0, "Raiz de gravação exige seleção independente de cliente destino");
            var deploymentPanel = (DeploymentPanel)main.FindName("Deployment");
            var pe = target with { Mode = "WINPE", Inventory = target.Inventory! with { Mode = "WINPE", Capabilities = ["INVENTORY", "DEPLOY_WINDOWS"] } };
            var editionFixture = new WindowsImage(6, "Windows 10 Pro", "Fixture", "AMD64", "10.0", 4096);
            var mediaFixture = new DeploymentMedia("fixture-media", "Windows 10 • QA sintética", "install.esd", 4096, new string('a', 64), [], [editionFixture], "AVAILABLE", now);
            deploymentPanel.UpdateData([source, pe], [mediaFixture]);
            var peSelector = (ComboBox)deploymentPanel.FindName("TargetDevice");
            Check(peSelector.Items.Count == 1, "Instalação só oferece cliente WinPE confiável com capacidade explícita");
            peSelector.SelectedItem = pe;
            ((ComboBox)deploymentPanel.FindName("TargetDisk")).SelectedIndex = 0;
            ((ComboBox)deploymentPanel.FindName("Media")).SelectedIndex = 0;
            ((ComboBox)deploymentPanel.FindName("Edition")).SelectedIndex = 0;
            Check(((Button)deploymentPanel.FindName("PlanButton")).IsEnabled, "WinPE, mídia disponível e edição AMD64 permitem revisar plano");
            Check(((CheckBox)deploymentPanel.FindName("Consent")).IsChecked != true, "Instalação inicia sem consentimento preenchido");
            var deploymentPlan = plan with { Kind = "DEPLOY_WINDOWS", CloneOptions = null, Deployment = new DeploymentOptions(mediaFixture.Id, 6, "GPT_UEFI", false) };
            Check(JsonSerializer.Deserialize<JobPlan>(JsonSerializer.Serialize(deploymentPlan, json), json)?.Deployment?.Firmware == "GPT_UEFI", "DTO preserva opções de implantação assinadas");
            var deployApproval = new PlanApprovalWindow(job with { Plan = deploymentPlan }, pe, pe.Inventory!.Disks[0], null, media: mediaFixture);
            var deployText = string.Join("\n", Children<TextBlock>(deployApproval).Select(t => t.Text));
            Check(deployText.Contains("Windows 10 Pro") && deployText.Contains("GPT_UEFI") && deployText.Contains("TODOS OS DADOS E PARTIÇÕES"), "Aprovação de instalação identifica edição, firmware e apagamento integral");
            Check(Children<TextBox>(deployApproval).Single().Text == "" && !Children<Button>(deployApproval).Single(b => Equals(b.Content, "Autorizar sobrescrita")).IsEnabled, "Implantação não preenche challenge nem permite aprovação implícita");
            deploymentPanel.UpdateData([pe], [mediaFixture with { State = "IMPORTING" }]);
            Check(!((Button)deploymentPanel.FindName("PlanButton")).IsEnabled, "Importação em andamento bloqueia instalação");
            deploymentPanel.UpdateData([pe with { Inventory = pe.Inventory with { Disks = [pe.Inventory.Disks[0] with { IsSystem = true }] } }], [mediaFixture]);
            Check(!((Button)deploymentPanel.FindName("PlanButton")).IsEnabled, "Disco protegido do WinPE bloqueia planejamento");
            deploymentPanel.UpdateData([pe], [mediaFixture]);
            CheckThrows(() => new ControllerClient("http://127.0.0.1:7443", "token", new string('0', 64)), "API administrativa HTTP rejeitada");
            CheckThrows(() => new ControllerClient("https://192.168.1.10:7443", "token", new string('0', 64)), "API administrativa fora do loopback rejeitada");
            CheckThrows(() => new ControllerClient("https://127.0.0.1:7443", "token", "pin-invalido"), "Pin malformado rejeitado antes de conexão");
            var renderIndex = Array.IndexOf(args, "--render");
            if (renderIndex >= 0 && renderIndex + 1 < args.Length)
            {
                var output = Path.GetFullPath(args[renderIndex + 1]); Directory.CreateDirectory(output);
                var tabs = (TabControl)main.FindName("Workspace"); tabs.IsEnabled = true;
                ((TextBlock)main.FindName("StatusLine")).Text = "RENDERIZAÇÃO SINTÉTICA PARA QA — sem conexão de rede ou operações de disco.";
                var layout = new DiskLayout("GPT", 512, "fixture-gpt", 34, 976773134,
                    [new(1, 2048, 204800, "ESP", "fixture-efi", "EFI", "", false, "vfat", null),
                     new(2, 206848, 32768, "MSR", "fixture-msr", "MSR", "", false, "", null),
                     new(3, 239616, 974000000, "BASIC", "fixture-windows", "Windows", "", false, "ntfs", 120000000000),
                     new(4, 974239616, 2000000, "RECOVERY", "fixture-recovery", "Recovery", "", false, "ntfs", null)]);
                var after = layout with { LastUsableLba = 1953525134, Partitions = layout.Partitions.Select(p => p.Number == 3 ? p with { LengthLba = p.LengthLba + 976752000 } : p.Number == 4 ? p with { StartLba = p.StartLba + 976752000 } : p).ToArray() };
                var renderSource = source with { Inventory = source.Inventory! with { Disks = [source.Inventory.Disks[0] with { SizeBytes = 500107862016, Layout = layout }] } };
                var renderTarget = target with { Inventory = target.Inventory! with { Disks = [target.Inventory.Disks[0] with { SizeBytes = 1000204886016, Layout = layout }] } };
                var preview = new ClonePreview([], ["Fixture de QA: boot em outro hardware requer homologação."],
                    ["Preservar EFI, MSR, Windows e Recovery; adaptar NTFS 3 em cópia de trabalho.", "Deslocar Recovery e reposicionar GPT secundária antes de validar o destino."], layout, after, 3, 1500000000000);
                var session = new CloneSession("fixture-clone", source.Id, source.Inventory.Disks[0].Fingerprint, target.Id, target.Inventory.Disks[0].Fingerprint,
                    "FIT", "fixture-capture", null, "fixture-image", "IMAGE_READY", now, preview);
                clonePanel.UpdateData([renderSource, renderTarget], [session], []);
                ((DataGrid)clonePanel.FindName("Sessions")).SelectedItem = session;
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Clonagem"));
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-clonagem-fixture.png"));
                ((ScrollViewer)clonePanel.Content).ScrollToEnd();
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-clonagem-inferior-fixture.png"));
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Arquivos"));
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-arquivos-fixture.png"));
                ((ScrollViewer)filesPanel.Content).ScrollToEnd();
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-arquivos-inferior-fixture.png"));
                Render((FrameworkElement)fileApproval.Content, 740, 650, Path.Combine(output, "desktop-aprovacao-arquivos-fixture.png"));
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Instalar Windows"));
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-instalacao-fixture.png"));
                ((ScrollViewer)deploymentPanel.Content).ScrollToEnd();
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-instalacao-inferior-fixture.png"));
                Render((FrameworkElement)deployApproval.Content, 740, 650, Path.Combine(output, "desktop-aprovacao-instalacao-fixture.png"));
                Console.WriteLine("WPF render sintético para QA: " + output);
            }
            main.Close(); app.Shutdown();
            Console.WriteLine($"Desktop: {_checks}/{_checks} checks passaram. Nenhum acesso de rede ou disco físico.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static Device Device(string id, string name, string[] caps, DateTimeOffset now) => new(id, name, "RESCUE", "TRUSTED", now,
        new Inventory(id, name, "Linux fixture", "RESCUE", "0.1.0", caps,
            [new DiskInfo(id + "disk", "/dev/fixture", "Synthetic", id, 4096, 512, 512, id + "fingerprint", false, false, false, "TEST", [])], now, []));
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T item) yield return item;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("FAIL: " + label); Console.WriteLine("PASS: " + label); _checks++; }
    private static void CheckThrows(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { Check(true, label); return; }
        throw new InvalidOperationException("FAIL: " + label);
    }
    private static void Render(FrameworkElement element, int width, int height, string path)
    {
        if (element is Panel panel) panel.Background = new SolidColorBrush(Color.FromRgb(21, 25, 31));
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
