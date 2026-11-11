using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SLT.Desktop;

// Small choice dialogs opened from the Computers page. They only collect options;
// the typed confirmation still happens in PlanApprovalWindow.
public sealed class SltModeDialog : Window
{
    private readonly CheckBox _write;
    public bool AllowWrite => _write.IsChecked == true;

    public SltModeDialog(string hostname)
    {
        Title = "Reiniciar em modo SLT";
        Style = (Style)Application.Current.FindResource("AppWindow");
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = $"Reiniciar {hostname} em modo SLT", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 0, 0, 14),
            Text = "O computador reinicia no sistema SLT, carregado deste servidor pela rede, sem pendrive. Nesse modo o Windows dele não está em uso, então você pode clonar, formatar, copiar o disco ou instalar Windows e Linux daqui do painel.\n\nPara voltar ao Windows basta reiniciar o computador." });
        _write = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Permitir gravação controlada pelo servidor (clonar, formatar, instalar) sem confirmação no teclado do cliente" } };
        content.Children.Add(_write);
        content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 18),
            Text = "Desmarcado, o modo SLT só permite inventário, diagnóstico e captura de imagem. Cada gravação continua exigindo, neste painel, a confirmação digitada com o nome do computador e do disco." });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Style = (Style)Application.Current.FindResource("SecondaryButton"), IsCancel = true };
        var next = new Button { Content = "Continuar…", Margin = new Thickness(0) };
        next.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(next);
        content.Children.Add(buttons);
        Content = content;
    }
}

public sealed class FormatDialog : Window
{
    private readonly ComboBox _table, _fileSystem;
    private readonly TextBox _label;
    private readonly TextBlock _problem;
    public FormatOptions Options => new(Selected(_table), Selected(_fileSystem), _label.Text.Trim());
    private static string Selected(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    // Same rules the server enforces, so the operator learns before the plan is refused.
    public static string? Validate(FormatOptions options, long diskBytes, int sectorBytes)
    {
        var limit = options.FileSystem is "FAT32" or "EXFAT" ? 11 : 16;
        if (options.Label.Length > limit || !options.Label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return $"Rótulo: até {limit} caracteres, só letras sem acento, números, - e _.";
        if (options.TableType == "MBR" && diskBytes / Math.Max(sectorBytes, 1) > uint.MaxValue) return "Discos acima de 2 TiB exigem tabela GPT.";
        if (options.FileSystem == "FAT32" && diskBytes > 2L * 1024 * 1024 * 1024 * 1024) return "FAT32 não cobre discos acima de 2 TiB; use exFAT ou NTFS.";
        return null;
    }

    public FormatDialog(Device device, DiskInfo disk)
    {
        Title = "Formatar disco";
        Style = (Style)Application.Current.FindResource("AppWindow");
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "Formatar disco", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), LineHeight = 22,
            Text = $"{device.Hostname}\n{disk.Model} • serial {disk.Serial} • {disk.Size}\nTudo o que está neste disco será apagado e ele ficará com uma única partição." });
        ComboBox Choice(string caption, params (string Text, string Tag)[] items)
        {
            content.Children.Add(new TextBlock { Text = caption, Foreground = muted, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 6) });
            var box = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var (text, tag) in items) box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            box.SelectedIndex = 0; content.Children.Add(box); return box;
        }
        _fileSystem = Choice("Sistema de arquivos",
            ("NTFS — Windows (disco interno ou externo)", "NTFS"), ("exFAT — pendrives e discos externos grandes, Windows/macOS/Linux", "EXFAT"),
            ("FAT32 — máxima compatibilidade, arquivos de até 4 GB", "FAT32"), ("ext4 — Linux", "EXT4"));
        _table = Choice("Tabela de partições", ("GPT — computadores UEFI e discos acima de 2 TB (recomendado)", "GPT"), ("MBR — computadores antigos (BIOS) e alguns aparelhos", "MBR"));
        content.Children.Add(new TextBlock { Text = "Rótulo do volume (opcional)", Foreground = muted, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 6) });
        _label = new TextBox { MaxLength = 16, Margin = new Thickness(0, 0, 0, 10) };
        content.Children.Add(_label);
        _problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("AccentTextBrush"), Margin = new Thickness(0, 0, 0, 14) };
        content.Children.Add(_problem);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Style = (Style)Application.Current.FindResource("SecondaryButton"), IsCancel = true };
        var next = new Button { Content = "Conferir e confirmar…", Margin = new Thickness(0) };
        void Update() { _problem.Text = Validate(Options, disk.SizeBytes, disk.LogicalSectorBytes) ?? ""; next.IsEnabled = _problem.Text.Length == 0; }
        _label.TextChanged += (_, _) => Update(); _table.SelectionChanged += (_, _) => Update(); _fileSystem.SelectionChanged += (_, _) => Update();
        next.Click += (_, _) => { Update(); if (next.IsEnabled) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(next);
        content.Children.Add(buttons);
        Content = content;
        Update();
    }
}
