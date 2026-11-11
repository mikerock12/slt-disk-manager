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
            var captureIndex = Array.IndexOf(args, "--capture");
            if (captureIndex >= 0 && captureIndex + 1 < args.Length) return Capture(args[captureIndex + 1]);
            var main = new MainWindow();
            Check(main.FindName("Cloning") is ClonePanel && main.FindName("Files") is FilesPanel, "XAML: abas de clonagem e arquivos constroem sem erro");
            Check(Children<TabItem>(main).Count() >= 8, "XAML: navegação principal preservada");
            Check(main.Background is SolidColorBrush { Color: { R: < 40, G: < 40, B: < 40 } } && main.FontSize >= 15 && ((TabControl)main.FindName("Workspace")).TabStripPlacement == Dock.Left, "Janela aplica o tema escuro, fonte de 15 px e menu lateral");
            var approvalStyle = new PlanApprovalWindow(new Job("j", new JobPlan("j", "INVENTORY", "d", "", "", 0, 512, "", "AUTORIZAR", DateTimeOffset.UtcNow.AddMinutes(5), true), "AWAITING_APPROVAL", DateTimeOffset.UtcNow, null, null, null), null, null, null);
            Check(approvalStyle.Background is SolidColorBrush { Color: { R: < 40 } }, "Janelas de confirmação usam o mesmo tema");
            var missing = MainWindow.ExplainConnectionProblem(Path.Combine(Path.GetTempPath(), "slt-inexistente-" + Guid.NewGuid().ToString("N"), "connection.json"));
            Check(missing is { CanElevate: false } && missing.Value.Title.Contains("não está em execução"), "Conexão inexistente explica que o servidor não está em execução");
            var denied = Path.Combine(Path.GetTempPath(), "slt-desktop-acl-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(denied, "{}");
            try
            {
                var info = new FileInfo(denied); var acl = info.GetAccessControl();
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny));
                info.SetAccessControl(acl);
                var problem = MainWindow.ExplainConnectionProblem(denied);
                Check(problem is not null && (problem.Value.Title.Contains("administrador") || problem.Value.Title.Contains("permissão")) && problem.Value.Text.Contains(denied), "Conexão sem permissão de leitura explica o motivo e aponta o arquivo");
                Check(problem!.Value.CanElevate == !MainWindow.IsElevated(), "Botão de reabrir como administrador só aparece quando o painel não está elevado");
                acl.RemoveAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny));
                info.SetAccessControl(acl);
                Check(MainWindow.ExplainConnectionProblem(denied) is null, "Conexão legível não gera aviso");
            }
            finally { try { File.Delete(denied); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            Check(MainWindow.NormalizeCode(" 0711 1918 ") == "07111918" && MainWindow.NormalizeCode("0711-19") == "071119", "Código de pareamento aceita espaços e é reduzido aos dígitos");
            var nowUi = DateTimeOffset.UtcNow;
            DiskInfo UiDisk(string serial, bool system) => new("d-" + serial, "/dev/" + serial, "Synthetic SSD", serial, 256L * 1024 * 1024 * 1024, 512, 512, "fp-" + serial, system, false, system, "TEST", [new("p1", "/dev/p1", 100L * 1024 * 1024, "vfat", ""), new("p2", "/dev/p2", 200L * 1024 * 1024 * 1024, "ntfs", "")]);
            Device Ui(string id, string name, string mode, string[] caps, string[] notes, string? origin = null, bool write = false) => new(id, name, mode, "TRUSTED", nowUi,
                new Inventory(id, name, mode == "WINDOWS" ? "Windows 10 Pro 22H2" : "SLT Rescue (Linux)", mode, "0.1.0", caps, [UiDisk("A" + id, mode == "WINDOWS"), UiDisk("B" + id, false)], nowUi, notes), origin, write);
            var uiWindows = Ui("11111111-aaaa", "PC-BALCAO", "WINDOWS", ["INVENTORY", "SMART", "REBOOT_RESCUE"], []);
            var uiLegacy = Ui("22222222-bbbb", "PC-ANTIGO", "WINDOWS", ["INVENTORY"], ["MODO_SLT: este computador inicia em BIOS/Legacy; use o pendrive SLT Rescue ou o boot pela rede (PXE)."]);
            var uiSlt = Ui("33333333-cccc", "NOTE-CLIENTE (modo SLT)", "RESCUE", ["INVENTORY", "SMART", "CAPTURE", "RESTORE", "CLONE_STREAM", "FORMAT_DISK"], [], "11111111-aaaa", true);
            var uiPending = new Enrollment("e1", "NOTE-MARIA", "WINDOWS", "", nowUi.AddMinutes(9), "PENDING", true);
            var uiOld = new Enrollment("e2", "PC-RECEPCAO", "RESCUE", "48213370", nowUi.AddMinutes(4), "PENDING", false);
            main.ShowFixture([uiWindows, uiLegacy, uiSlt], [uiPending, uiOld, new Enrollment("e3", "PC-BALCAO", "WINDOWS", "", nowUi.AddMinutes(-30), "APPROVED", true)]);
            var banner = (Border)main.FindName("PairingBanner");
            Check(banner.Visibility == Visibility.Visible && ((TextBlock)main.FindName("PairingBannerTitle")).Text.Contains("PC-RECEPCAO") && ((TextBlock)main.FindName("PairingBannerText")).Text.Contains("mais 1"), "Pedido de conexão aparece em destaque em qualquer página, o mais próximo de expirar primeiro");
            Check(uiPending.CodeLabel == "mostrado na tela do cliente" && uiOld.CodeLabel == "48213370" && uiPending.StatusLabel == "Aguardando código", "Código curto nunca é exibido no painel; o operador lê na tela do cliente");
            var devicesGrid = (DataGrid)main.FindName("DevicesGrid"); var disksGrid = (DataGrid)main.FindName("DisksGrid");
            var sltButton = (Button)main.FindName("SltModeButton"); var formatButton = (Button)main.FindName("FormatButton"); var hint = (TextBlock)main.FindName("DeviceActionHint");
            Check(!sltButton.IsEnabled && !formatButton.IsEnabled && hint.Text.Contains("Selecione um computador"), "Sem seleção, modo SLT e formatação ficam bloqueados com orientação");
            devicesGrid.SelectedItem = uiWindows;
            Check(sltButton.IsEnabled && !formatButton.IsEnabled && uiWindows.ModeLabel == "Windows (cliente)", "Cliente Windows com REBOOT_RESCUE oferece reinício em modo SLT e não oferece formatação");
            devicesGrid.SelectedItem = uiLegacy;
            Check(!sltButton.IsEnabled && hint.Text.Contains("BIOS/Legacy") && !((TextBlock)main.FindName("InventoryNotes")).Text.Contains("MODO_SLT"), "Cliente sem modo SLT mostra o motivo informado pelo próprio cliente");
            devicesGrid.SelectedItem = uiSlt;
            Check(!sltButton.IsEnabled && !formatButton.IsEnabled && uiSlt.ModeLabel == "Modo SLT" && hint.Text.Contains("Selecione um disco"), "Sessão em modo SLT pede a escolha do disco antes de formatar");
            disksGrid.SelectedItem = uiSlt.Inventory!.Disks[1];
            Check(formatButton.IsEnabled && hint.Text.Contains("sem nova confirmação no teclado"), "Modo SLT com gravação avisa que o painel é a confirmação");
            disksGrid.SelectedItem = uiSlt.Inventory.Disks[1] with { IsReadOnly = true };
            Check(formatButton.IsEnabled, "Seleção fora da lista não altera o estado do botão");
            Check(FormatDialog.Validate(new("GPT", "NTFS", "DADOS"), 256L << 30, 512) is null && FormatDialog.Validate(new("MBR", "NTFS", ""), 3L << 40, 512) is not null && FormatDialog.Validate(new("GPT", "FAT32", ""), 3L << 40, 512) is not null && FormatDialog.Validate(new("GPT", "FAT32", "ABCDEFGHIJKL"), 1L << 30, 512) is not null && FormatDialog.Validate(new("GPT", "NTFS", "meu disco"), 1L << 30, 512) is not null, "Formatação valida rótulo, MBR acima de 2 TiB e FAT32 grande antes de pedir o plano");
            var formatDialog = new FormatDialog(uiSlt, uiSlt.Inventory.Disks[1]);
            Check(formatDialog.Options == new FormatOptions("GPT", "NTFS", "") && string.Join("\n", Children<TextBlock>(formatDialog).Select(t => t.Text)).Contains("será apagado"), "Diálogo de formatação parte de GPT/NTFS e avisa que apaga o disco");
            var sltDialog = new SltModeDialog("PC-BALCAO");
            Check(sltDialog.AllowWrite && string.Join("\n", Children<TextBlock>(sltDialog).Select(t => t.Text)).Contains("sem pendrive"), "Diálogo do modo SLT explica o reinício e a gravação controlada pelo servidor");
            var rebootPlan = new JobPlan("r", "REBOOT_RESCUE", uiWindows.Id, "", "", 0, 512, "", "REINICIAR PC-BALCAO MODO SLT", nowUi.AddMinutes(15), true, RescueBoot: new("a", 1, "", "", true));
            var rebootApproval = new PlanApprovalWindow(new Job("r", rebootPlan, "AWAITING_APPROVAL", nowUi, null, null, null), uiWindows, null, null);
            var rebootText = string.Join("\n", Children<TextBlock>(rebootApproval).Select(t => t.Text));
            Check(rebootText.Contains("SERÁ REINICIADO") && rebootText.Contains("Secure Boot") && !rebootText.Contains("Fingerprint") && !Children<Button>(rebootApproval).Single(b => Equals(b.Content, "Reiniciar em modo SLT")).IsEnabled, "Aprovação do modo SLT explica o reinício, os requisitos e exige a confirmação digitada");
            var formatPlan = new JobPlan("f", "FORMAT_DISK", uiSlt.Id, "fp", "/dev/sdb", 256L << 30, 512, "", "FORMATAR NOTE-CLIENTE fp", nowUi.AddMinutes(15), true, Format: new("GPT", "EXFAT", "EXTERNO"));
            var formatApproval = new PlanApprovalWindow(new Job("f", formatPlan, "AWAITING_APPROVAL", nowUi, null, null, null), uiSlt, uiSlt.Inventory.Disks[1], null);
            var formatText = string.Join("\n", Children<TextBlock>(formatApproval).Select(t => t.Text));
            Check(formatText.Contains("TODOS OS DADOS E PARTIÇÕES SERÃO APAGADOS") && formatText.Contains("EXFAT") && formatText.Contains("EXTERNO") && formatText.Contains("Partições atuais que serão removidas") && !Children<Button>(formatApproval).Single(b => Equals(b.Content, "Autorizar formatação")).IsEnabled, "Aprovação de formatação mostra sistema de arquivos, partições que somem e exige a confirmação digitada");
            var planJson = JsonSerializer.Serialize(formatPlan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(planJson.Contains("\"format\":{\"tableType\":\"GPT\",\"fileSystem\":\"EXFAT\",\"label\":\"EXTERNO\"}"), "DTO de formatação preserva o contrato camelCase");
            Check(Display.JobKind("REBOOT_RESCUE") == "Reiniciar em modo SLT" && Display.JobState("AWAITING_APPROVAL") == "Aguardando aprovação" && Display.JobKind("FUTURO") == "FUTURO", "Tarefas e estados aparecem em português, sem esconder tipos desconhecidos");
            var now = DateTimeOffset.UtcNow;
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var networkBoot = (NetworkBootPanel)main.FindName("NetworkBoot");
            var startBoot = (Button)networkBoot.FindName("StartButton");
            var stopBoot = (Button)networkBoot.FindName("StopButton");
            var bootConsent = (CheckBox)networkBoot.FindName("NetworkConsent");
            Check(!startBoot.IsEnabled && !stopBoot.IsEnabled && bootConsent.IsChecked != true, "PXE inicia sem estado conhecido, autorização ou ação automática");
            var bootConfig = new NetworkBootConfig(1, "fixture-nic", "192.168.90.2", "192.168.90.0/24", "EXTERNAL_DHCP", 8088, 8);
            var bootNic = new NetworkBootInterface("fixture-nic", "Bancada sintética", "Ethernet fixture", bootConfig.BindIPv4, bootConfig.AllowedCidr, 1000, true, "");
            var bootProfile = new NetworkBootProfile("fixture-rescue", "SLT Rescue • QA sintética", "RESCUE_LINUX", "X64", ["BIOS", "UEFI"], "UNSUPPORTED", "NETWORK_BOOT_DEV", "fixture", true, []);
            var bootState = new NetworkBootStatus("STOPPED", bootConfig, null, null, [bootProfile], []);
            networkBoot.UpdateData(bootState, [bootNic]);
            Check(!startBoot.IsEnabled && !stopBoot.IsEnabled && !bootConsent.IsEnabled, "PXE parado exige pré-verificação antes da autorização");
            Check(!startBoot.IsDefault, "PXE não inicia por Enter como botão padrão");
            var bootGrid = (DataGrid)networkBoot.FindName("Profiles");
            Check(bootGrid.Items.Cast<NetworkBootProfile>().Single().SecureBootStatus == "UNSUPPORTED" && bootProfile.FirmwareLabel.Contains("BIOS / UEFI"), "Perfil PXE mantém firmware e Secure Boot explícitos");
            networkBoot.UpdateData(bootState with { State = "RUNNING", StartedAtUtc = now }, [bootNic]);
            Check(stopBoot.IsEnabled && !startBoot.IsEnabled && !((Grid)networkBoot.FindName("ConfigFields")).IsEnabled, "PXE em execução permite parar e bloqueia reconfiguração");
            networkBoot.UpdateData(bootState, [bootNic]);
            bootConsent.IsChecked = true;
            ((TextBox)networkBoot.FindName("HttpPort")).Text = "8089";
            Check(bootConsent.IsChecked != true && !startBoot.IsEnabled && !((Button)networkBoot.FindName("ValidateButton")).IsEnabled, "Editar rede invalida autorização e exige salvar antes de validar");
            networkBoot.ResetConnection();
            Check(!startBoot.IsEnabled && !stopBoot.IsEnabled && bootConsent.IsChecked != true, "Trocar conexão descarta estado e autorização PXE anteriores");
            var bootConfigJson = JsonSerializer.Serialize(bootConfig, json);
            Check(bootConfigJson.Contains("\"bindIPv4\"") && !bootConfigJson.Contains("\"enabled\"") && JsonSerializer.Deserialize<NetworkBootConfig>(bootConfigJson, json) == bootConfig, "DTO PXE preserva contrato camelCase sem auto-start persistido");
            networkBoot.UpdateData(bootState, [bootNic]);
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
            var planButton = (Button)clonePanel.FindName("PlanButton");
            var strategy = (ComboBox)clonePanel.FindName("Strategy"); var transfer = (ComboBox)clonePanel.FindName("Transfer");
            Check(transfer.SelectedIndex == 0 && !planButton.IsEnabled, "Clone em lotes é o padrão e exige CLONE_STREAM/STREAM_LAYOUT");
            transfer.SelectedIndex = 1;
            Check(!planButton.IsEnabled, "PRESERVE_LAYOUT com imagem bloqueado sem capability RESTORE_LAYOUT");
            strategy.SelectedIndex = 0;
            Check(planButton.IsEnabled, "EXACT_RAW com imagem completa disponível quando destino anuncia RESTORE");
            transfer.SelectedIndex = 0;
            Check(!planButton.IsEnabled, "Em lotes bloqueado quando origem/destino não anunciam CLONE_STREAM");
            var streamSource = source with { Inventory = source.Inventory! with { Capabilities = ["CAPTURE", "RESTORE", "CLONE_STREAM"], Disks = [source.Inventory.Disks[0] with { SizeBytes = 8192 }] } };
            var streamTarget = target with { Inventory = target.Inventory! with { Capabilities = ["RESTORE", "CLONE_STREAM", "STREAM_LAYOUT", "STREAM_FIT"], Disks = [target.Inventory.Disks[0] with { SizeBytes = 4096 }] } };
            clonePanel.UpdateData([streamSource, streamTarget], [], []);
            sourceDevice.SelectedItem = streamSource; targetDevice.SelectedItem = streamTarget;
            ((ComboBox)clonePanel.FindName("SourceDisk")).SelectedIndex = 0; ((ComboBox)clonePanel.FindName("TargetDisk")).SelectedIndex = 0;
            strategy.SelectedIndex = 0;
            Check(planButton.IsEnabled, "Em lotes disponível com CLONE_STREAM nos dois Rescue");
            strategy.SelectedIndex = 2;
            Check(!planButton.IsEnabled && ((TextBlock)clonePanel.FindName("PreviewSummary")).Text.Contains("STREAM_SHRINK"), "Redução em lotes exige STREAM_SHRINK no destino");
            var shrinkTarget = streamTarget with { Inventory = streamTarget.Inventory! with { Capabilities = [.. streamTarget.Inventory.Capabilities, "STREAM_SHRINK"] } };
            clonePanel.UpdateData([streamSource, shrinkTarget], [], []);
            sourceDevice.SelectedItem = streamSource; targetDevice.SelectedItem = shrinkTarget;
            ((ComboBox)clonePanel.FindName("SourceDisk")).SelectedIndex = 0; ((ComboBox)clonePanel.FindName("TargetDisk")).SelectedIndex = 0;
            strategy.SelectedIndex = 2;
            Check(planButton.IsEnabled, "Redução em lotes disponível com STREAM_SHRINK");
            strategy.SelectedIndex = 0;
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
            var linuxMediaFixture = mediaFixture with { Id = "linux-media", Name = "Xubuntu QA", Kind = "LINUX", Images = [editionFixture with { Index = 1, Name = "Xubuntu 26.04" }] };
            var rescueLinux = source with { Id = "rescue-linux", Inventory = source.Inventory! with { Capabilities = ["INVENTORY", "DEPLOY_LINUX"] } };
            var peUefi = pe with { Id = "pe-uefi", Inventory = pe.Inventory! with { Capabilities = ["INVENTORY", "DEPLOY_WINDOWS", "CONVERT_GPT"] } };
            deploymentPanel.UpdateData([peUefi, rescueLinux], [mediaFixture, linuxMediaFixture]);
            var mediaBox = (ComboBox)deploymentPanel.FindName("Media"); var targetBox = (ComboBox)deploymentPanel.FindName("TargetDevice");
            var planInstall = (Button)deploymentPanel.FindName("PlanButton"); var convert = (Button)deploymentPanel.FindName("ConvertButton");
            mediaBox.SelectedItem = linuxMediaFixture;
            Check(targetBox.Items.OfType<Device>().Any(d => d.Id == "rescue-linux"), "Mídia Linux oferece cliente SLT Rescue com DEPLOY_LINUX");
            targetBox.SelectedItem = rescueLinux; ((ComboBox)deploymentPanel.FindName("TargetDisk")).SelectedIndex = 0; ((ComboBox)deploymentPanel.FindName("Edition")).SelectedIndex = 0;
            Check(planInstall.IsEnabled && !convert.IsEnabled, "Instalação Linux disponível pelo Rescue; conversão GPT não se aplica");
            var createAccount = (CheckBox)deploymentPanel.FindName("CreateAccount"); createAccount.IsChecked = false; createAccount.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Check(!planInstall.IsEnabled, "Instalação Linux exige usuário inicial");
            createAccount.IsChecked = true; createAccount.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            mediaBox.SelectedItem = mediaFixture; targetBox.SelectedItem = peUefi; ((ComboBox)deploymentPanel.FindName("TargetDisk")).SelectedIndex = 0; ((ComboBox)deploymentPanel.FindName("Edition")).SelectedIndex = 0;
            Check(convert.IsEnabled && planInstall.IsEnabled, "WinPE em UEFI com mídia Windows oferece instalação e conversão MBR→GPT");
            var conversionPlan = plan with { Kind = "CONVERT_GPT", Challenge = "CONVERTER DESTINO 1234", CloneOptions = null, Deployment = new DeploymentOptions(mediaFixture.Id, 6, "GPT_UEFI", false, null, new string('c', 64)) };
            var conversionApproval = new PlanApprovalWindow(job with { Plan = conversionPlan }, peUefi, peUefi.Inventory!.Disks[0], null, media: mediaFixture);
            var conversionText = string.Join("\n", Children<TextBlock>(conversionApproval).Select(t => t.Text));
            Check(conversionText.Contains("MBR PARA GPT") && conversionText.Contains(new string('c', 64)) && !Children<Button>(conversionApproval).Single(b => Equals(b.Content, "Autorizar conversão")).IsEnabled, "Aprovação de conversão mostra ferramenta assinada e exige challenge");
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
                    ["Preservar EFI, MSR, Windows e Recovery; adaptar NTFS 3 em cópia de trabalho.", "Deslocar Recovery e reposicionar GPT secundária antes de validar o destino."], layout, after, 3, 1500000000000, 500107862016);
                var session = new CloneSession("fixture-clone", source.Id, source.Inventory.Disks[0].Fingerprint, target.Id, target.Inventory.Disks[0].Fingerprint,
                    "FIT", "fixture-capture", null, "fixture-image", "IMAGE_READY", now, preview);
                clonePanel.UpdateData([renderSource, renderTarget], [session], []);
                ((DataGrid)clonePanel.FindName("Sessions")).SelectedItem = session;
                devicesGrid.SelectedItem = uiSlt; disksGrid.SelectedItem = uiSlt.Inventory.Disks[1];
                ((TextBlock)main.FindName("ServerSummary")).Text = "Servidor BANCADA • 192.168.1.23:7443 • versão QA";
                ((TextBlock)main.FindName("ServerIdentity")).Text = "Este servidor: BANCADA — https://192.168.1.23:7443\nBusca automática ligada (UDP 7444): os clientes desta versão encontram o servidor sozinhos.";
                ((Border)main.FindName("ConnectionPanel")).Visibility = Visibility.Collapsed;
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Computadores"));
                Render((FrameworkElement)main.Content, 1380, 860, Path.Combine(output, "desktop-computadores-fixture.png"));
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Conectar computador"));
                Render((FrameworkElement)main.Content, 1380, 860, Path.Combine(output, "desktop-conectar-fixture.png"));
                Render((FrameworkElement)formatDialog.Content, 640, 520, Path.Combine(output, "desktop-formatar-fixture.png"));
                Render((FrameworkElement)rebootApproval.Content, 760, 700, Path.Combine(output, "desktop-aprovacao-modo-slt-fixture.png"));
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Clonagem"));
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-clonagem-fixture.png"));
                ((ScrollViewer)clonePanel.Content).ScrollToEnd();
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-clonagem-inferior-fixture.png"));
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Arquivos"));
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-arquivos-fixture.png"));
                ((ScrollViewer)filesPanel.Content).ScrollToEnd();
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-arquivos-inferior-fixture.png"));
                Render((FrameworkElement)fileApproval.Content, 740, 650, Path.Combine(output, "desktop-aprovacao-arquivos-fixture.png"));
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Instalar sistema"));
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-instalacao-fixture.png"));
                ((ScrollViewer)deploymentPanel.Content).ScrollToEnd();
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-instalacao-inferior-fixture.png"));
                Render((FrameworkElement)deployApproval.Content, 740, 650, Path.Combine(output, "desktop-aprovacao-instalacao-fixture.png"));
                tabs.SelectedItem = Children<TabItem>(main).Single(t => Equals(t.Header, "Boot pela rede"));
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-network-boot-fixture.png"));
                ((ScrollViewer)networkBoot.Content).ScrollToVerticalOffset(420);
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-network-boot-perfis-fixture.png"));
                ((ScrollViewer)networkBoot.Content).ScrollToEnd();
                Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-network-boot-inferior-fixture.png"));
                Console.WriteLine("WPF render sintético para QA: " + output);
            }
            main.Close(); app.Shutdown();
            Console.WriteLine($"Desktop: {_checks}/{_checks} checks passaram. Nenhum acesso de rede ou disco físico.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    // Documentation screenshots of a lab server: the panel connects through its own
    // --connection argument, off screen, and each tab is rendered by WPF itself.
    private static int Capture(string outputDir)
    {
        var output = Path.GetFullPath(outputDir); Directory.CreateDirectory(output);
        var main = new MainWindow { Left = -20000, Top = -20000, Width = 1316, Height = 840, ShowActivated = false, ShowInTaskbar = false };
        main.Show();
        Pump(TimeSpan.FromSeconds(12));
        var tabs = (TabControl)main.FindName("Workspace");
        foreach (var tab in Children<TabItem>(main).ToList())
        {
            tabs.SelectedItem = tab;
            Pump(TimeSpan.FromSeconds(2));
            var name = new string(tab.Header.ToString()!.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD).Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ').ToArray()).Replace(' ', '-');
            Render((FrameworkElement)main.Content, 1316, 840, Path.Combine(output, "desktop-" + name + ".png"));
        }
        Console.WriteLine("Capturas do painel conectado: " + output + " — " + ((TextBlock)main.FindName("StatusLine")).Text);
        main.Close();
        return 0;
    }
    private static void Pump(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
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
        if (element is Panel panel) panel.Background = new SolidColorBrush(Color.FromRgb(16, 19, 23));
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
