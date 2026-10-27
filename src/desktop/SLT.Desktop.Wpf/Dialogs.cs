using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SLT.Desktop;

public sealed class PlanApprovalWindow : Window
{
    private readonly TextBox _challenge;
    public string TypedChallenge => _challenge.Text;

    public PlanApprovalWindow(Job job, Device? device, DiskInfo? disk, ImageManifest? image)
    {
        Title = job.Plan.Kind == "RESTORE" ? "Confirmar sobrescrita do disco de destino" : "Examinar e aprovar tarefa";
        Width = 790;
        Height = 740;
        MinWidth = 650;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        void Text(string text, double size = 14, bool bold = false, Brush? color = null)
        {
            content.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 13), Foreground = color ?? Brushes.WhiteSmoke });
        }
        Text($"{job.Plan.Kind} • revisão do plano", 23, true);
        Text($"Tarefa: {job.Id}\nComputador: {device?.Hostname ?? job.Plan.DeviceId}\nModo: {device?.Mode ?? "não disponível"}");
        if (job.Plan.Kind == "RESTORE")
        {
            Text("ORIGEM — IMAGEM VALIDADA", 14, true, Brushes.SandyBrown);
            Text($"{image?.Name ?? job.Plan.ImageId}\nID: {job.Plan.ImageId}\nCapacidade lógica: {Display.Bytes(image?.LogicalSizeBytes ?? 0)}");
            Text("DESTINO — TODOS OS DADOS SERÃO SOBRESCRITOS", 16, true, Brushes.SandyBrown);
        }
        else Text(job.Plan.Kind == "CAPTURE" ? "ORIGEM — DISCO A CAPTURAR" : "ALVO DO DIAGNÓSTICO", 14, true, Brushes.SandyBrown);
        Text(disk is null ? $"{job.Plan.DiskPath}\nFingerprint: {job.Plan.DiskFingerprint}" :
            $"Modelo: {disk.Model}\nSerial: {disk.Serial}\nCaminho: {disk.Path}\nCapacidade: {disk.Size} • Setor: {disk.LogicalSectorBytes} bytes\nEstado: {disk.Flags}\nFingerprint: {disk.Fingerprint}");
        if (job.Plan.Kind == "RESTORE" && disk is not null)
        {
            Text("Partições atuais que serão removidas: " + (disk.Partitions.Length == 0 ? "nenhuma registrada" : string.Join("; ", disk.Partitions.Select(p => $"{p.Name} {p.FileSystem} {p.Size}"))));
            Text("Faça backup dos dados necessários antes de continuar. Cancelar durante escrita pode deixar o disco incompleto. A inicialização do sistema não é garantida pela verificação dos chunks.", 14, false, Brushes.SandyBrown);
        }
        Text($"Aprovação válida até: {job.Plan.ExpiresAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}");
        Text("Digite exatamente este identificador para aprovar:", 14, true);
        Text(job.Plan.Challenge, 19, true, Brushes.SandyBrown);
        _challenge = new TextBox { MinHeight = 36, Margin = new Thickness(0, 0, 0, 14), MaxLength = Math.Max(job.Plan.Challenge.Length + 10, 64) };
        content.Children.Add(_challenge);
        var consent = new CheckBox { Content = job.Plan.Kind == "RESTORE" ? "Conferi o destino e autorizo a perda de todos os seus dados." : "Conferi a identidade e autorizo a execução deste plano.", IsChecked = false, Margin = new Thickness(0, 0, 0, 16) };
        content.Children.Add(consent);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        layout.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Voltar sem executar", Style = (Style)Application.Current.FindResource("SecondaryButton"), IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var approve = new Button { Content = job.Plan.Kind == "RESTORE" ? "Autorizar sobrescrita" : "Aprovar e enfileirar", IsEnabled = false, IsDefault = false };
        void Update() => approve.IsEnabled = string.Equals(_challenge.Text, job.Plan.Challenge, StringComparison.Ordinal) && consent.IsChecked == true && DateTimeOffset.UtcNow < job.Plan.ExpiresAtUtc;
        _challenge.TextChanged += (_, _) => Update();
        consent.Checked += (_, _) => Update();
        consent.Unchecked += (_, _) => Update();
        approve.Click += (_, _) =>
        {
            Update();
            if (approve.IsEnabled) DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(approve);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);
        Content = layout;
    }
}

public sealed class DetailsWindow : Window
{
    public DetailsWindow(string title, string details)
    {
        Title = title;
        Width = 940;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TextBox { Text = details, IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
            FontSize = 13, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(18) };
    }
}
