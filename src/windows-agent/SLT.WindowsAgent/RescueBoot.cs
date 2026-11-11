using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SLT.WindowsAgent;

// "SLT mode": restart this PC once into the SLT Rescue system without a USB stick.
// The signed plan brings a small iPXE loader (hash checked) and its script. They go to
// \EFI\SLT on the EFI System Partition and a firmware entry is selected for the NEXT boot only.
// Windows' own boot entry, boot order and disks are left as they are; if the network or the
// server is unavailable the loader exits and the firmware starts Windows again.
public interface ISystemCommands
{
    Task<ProcessResult> Run(string executable, string[] arguments, CancellationToken ct);
}

public sealed class WindowsSystemCommands : ISystemCommands
{
    public Task<ProcessResult> Run(string executable, string[] arguments, CancellationToken ct) =>
        ProcessRunner.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable), arguments, TimeSpan.FromSeconds(60), 256 * 1024, ct);
}

public sealed record RescueBootMarker(string EntryId, string EspPath, DateTimeOffset StagedAtUtc, string JobId);
public sealed record RescueBootSupport(bool Available, string Reason);

public static partial class RescueBoot
{
    public const string MarkerFile = "slt-boot.json";
    private const string EspDirectory = @"EFI\SLT";
    private static readonly string[] SimpleCommands = ["dhcp", "imgfree", "boot", "exit"];

    [GeneratedRegex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern();
    [GeneratedRegex(@"^\\\\\?\\Volume\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}\\$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumePattern();
    [GeneratedRegex(@"^http://(\d{1,3}(?:\.\d{1,3}){3}):(\d{2,5})/boot/v1/assets/[a-z0-9][a-z0-9.-]{0,90}$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPattern();
    [GeneratedRegex(@"^https://(\d{1,3}(?:\.\d{1,3}){3}):(\d{2,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerPattern();
    [GeneratedRegex(@"^[A-Za-z0-9=.,_:/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KernelArgumentPattern();

    // The plan is signed by the pinned server, yet the script is still checked line by line:
    // only the fixed vocabulary needed to start the SLT Rescue from a LAN address is accepted.
    public static void ValidateOptions(RescueBootOptions o)
    {
        static InvalidOperationException Bad(string what) => new("Plano de modo SLT inválido: " + what + ".");
        if (o.LoaderSha256 is not { Length: 64 } || !o.LoaderSha256.All(Uri.IsHexDigit) || o.LoaderSizeBytes is < 64 * 1024 or > 4 * 1024 * 1024) throw Bad("carregador");
        if (o.Ticket is not { Length: 64 } || !o.Ticket.All(Uri.IsHexDigit)) throw Bad("ticket");
        if (o.RebootDelaySeconds is < 5 or > 600) throw Bad("prazo de reinício");
        if (o.Script is not { Length: > 40 and <= 4096 } || !o.Script.StartsWith("#!ipxe\n", StringComparison.Ordinal) || !o.Script.EndsWith('\n') ||
            o.Script.Any(ch => ch != '\n' && (ch < ' ' || ch > '~'))) throw Bad("script");
        var lines = o.Script.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        int kernels = 0, initrds = 0, tickets = 0;
        static bool PrivateHost(string ip) => IPAddress.TryParse(ip, out var address) && AgentOptions.IsLocal(address) && !IPAddress.IsLoopback(address);
        static bool Asset(string url) => AssetPattern().Match(url) is { Success: true } m && PrivateHost(m.Groups[1].Value) && int.Parse(m.Groups[2].Value) is >= 1024 and <= 65535;
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith(':')) { if (line.Length < 2 || !line[1..].All(char.IsAsciiLetterLower)) throw Bad("rótulo"); continue; }
            if (line.StartsWith("echo ", StringComparison.Ordinal)) { if (line.Any(ch => ch is '$' or '|' or '&' or ';' or '`')) throw Bad("mensagem"); continue; }
            if (line == "sleep 8") continue;
            if (line.EndsWith(" || goto failed", StringComparison.Ordinal)) line = line[..^" || goto failed".Length];
            if (line.Any(ch => ch is '$' or '|' or '&' or ';' or '`' or '"' or '\'')) throw Bad("caractere de script");
            var words = line.Split(' ');
            if (words.Length == 1 && SimpleCommands.Contains(words[0])) continue;
            if (words is ["initrd", "--name", "initrd.img", var initrd] && Asset(initrd)) { initrds++; continue; }
            if (words is ["kernel", "--name", "vmlinuz", var kernel, ..] && Asset(kernel))
            {
                kernels++;
                foreach (var argument in words.Skip(4))
                {
                    if (!KernelArgumentPattern().IsMatch(argument)) throw Bad("argumento do kernel");
                    var separator = argument.IndexOf('=');
                    var (name, value) = separator < 0 ? (argument, "") : (argument[..separator], argument[(separator + 1)..]);
                    switch (name)
                    {
                        case "fetch": if (!Asset(value)) throw Bad("origem do sistema"); break;
                        case "slt.server": if (ServerPattern().Match(value) is not { Success: true } s || !PrivateHost(s.Groups[1].Value)) throw Bad("servidor"); break;
                        case "slt.pin": if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw Bad("pin"); break;
                        case "slt.ticket": if (value != o.Ticket) throw Bad("ticket do script"); tickets++; break;
                        case "slt.write": if (value != (o.UnattendedWrite ? "1" : "0")) throw Bad("modo de gravação"); break;
                        case "boot" or "components" or "nopersistence" or "noeject" or "hostname" or "username" or "initrd" or "console": break;
                        default: throw Bad("argumento do kernel não reconhecido");
                    }
                }
                continue;
            }
            throw Bad("comando não permitido");
        }
        if (kernels != 1 || initrds != 1 || tickets != 1) throw Bad("estrutura do script");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    // Reported in the inventory: either the REBOOT_RESCUE capability or a note saying why not.
    public static RescueBootSupport Probe()
    {
        if (!OperatingSystem.IsWindows()) return new(false, "exige Windows 10/11.");
        if (!GetFirmwareType(out var firmware) || firmware != 2) return new(false, "este computador inicia em BIOS/Legacy; use o pendrive SLT Rescue ou o boot pela rede (PXE).");
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            if (key?.GetValue("UEFISecureBootEnabled") is int enabled && enabled != 0)
                return new(false, "o Secure Boot está ativado; desative-o na configuração do firmware (UEFI) ou use o pendrive SLT Rescue.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(false, "o cliente precisa estar instalado como serviço ou aberto como administrador.");
        return new(true, "");
    }

    public static async Task<string> FindEsp(ISystemCommands commands, CancellationToken ct)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Hardware", "Find-EfiSystemPartition.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Localizador da partição EFI ausente no pacote.", script);
        var result = await commands.Run(@"WindowsPowerShell\v1.0\powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script], ct);
        var paths = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (result.ExitCode != 0 || paths.Length != 1 || !VolumePattern().IsMatch(paths[0]))
            throw new InvalidOperationException("Não foi possível identificar uma única partição EFI no disco de inicialização.");
        return paths[0];
    }

    private static async Task<string> Bcd(ISystemCommands commands, CancellationToken ct, params string[] arguments)
    {
        var result = await commands.Run("bcdedit.exe", arguments, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("O Windows recusou a alteração da inicialização (bcdedit " + arguments[0] + ").");
        return result.Output;
    }

    // Writes the loader and selects it for the next boot. Any failure removes what was created.
    public static async Task Stage(JobPlan plan, byte[] loader, string espPath, SecureState state, ISystemCommands commands, CancellationToken ct)
    {
        var options = plan.RescueBoot ?? throw new InvalidOperationException("Plano sem opções de modo SLT.");
        ValidateOptions(options);
        if (loader.Length != options.LoaderSizeBytes || !Convert.ToHexStringLower(SHA256.HashData(loader)).Equals(options.LoaderSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Carregador de boot não confere com o plano assinado.");
        await Cleanup(state, commands, force: true, ct);
        var directory = Path.Combine(espPath, EspDirectory);
        string? entry = null;
        try
        {
            Directory.CreateDirectory(directory);
            var loaderPath = Path.Combine(directory, "ipxe.efi");
            File.WriteAllBytes(loaderPath, loader);
            File.WriteAllText(Path.Combine(directory, "autoexec.ipxe"), options.Script, new ASCIIEncoding());
            if (!Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(loaderPath))).Equals(options.LoaderSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Carregador gravado na partição EFI não confere.");
            var copied = EntryPattern().Matches(await Bcd(commands, ct, "/copy", "{bootmgr}", "/d", "SLT Disk Manager"));
            if (copied.Count != 1) throw new InvalidOperationException("O Windows não devolveu o identificador da nova entrada de inicialização.");
            entry = copied[0].Value.ToLowerInvariant();
            // The marker goes first: whatever happens next, a later start can remove the entry.
            state.Write(MarkerFile, new RescueBootMarker(entry, espPath, DateTimeOffset.UtcNow, plan.Id));
            await Bcd(commands, ct, "/set", entry, "path", @"\EFI\SLT\ipxe.efi");
            // Keep Windows first in the permanent order; only the next boot is redirected.
            await Bcd(commands, ct, "/set", "{fwbootmgr}", "displayorder", entry, "/addlast");
            await Bcd(commands, ct, "/set", "{fwbootmgr}", "bootsequence", entry);
        }
        catch
        {
            try { await Cleanup(state, commands, force: true, CancellationToken.None, entry, espPath); } catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // Removes the one-time entry and the files. Without force it waits until Windows has started
    // again after the staging (system start time later than the marker).
    public static async Task<bool> Cleanup(SecureState state, ISystemCommands commands, bool force, CancellationToken ct, string? entry = null, string? espPath = null)
    {
        var marker = state.Read<RescueBootMarker>(MarkerFile);
        entry ??= marker?.EntryId; espPath ??= marker?.EspPath;
        if (entry is null && espPath is null) return false;
        if (!force && marker is not null && DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) < marker.StagedAtUtc) return false;
        if (entry is not null && EntryPattern().IsMatch(entry)) await commands.Run("bcdedit.exe", ["/delete", entry, "/f"], ct);
        if (espPath is not null && Path.IsPathFullyQualified(espPath))
        {
            var directory = Path.Combine(espPath, EspDirectory);
            foreach (var name in new[] { "autoexec.ipxe", "ipxe.efi" })
            {
                var file = Path.Combine(directory, name);
                if (File.Exists(file)) File.Delete(file);
            }
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        var path = state.FilePath(MarkerFile);
        if (File.Exists(path)) File.Delete(path);
        return true;
    }

    public static async Task Restart(ISystemCommands commands, int delaySeconds, CancellationToken ct)
    {
        var result = await commands.Run("shutdown.exe", ["/r", "/t", delaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "/d", "p:4:1", "/c",
            "SLT Disk Manager: este computador vai reiniciar no modo SLT, controlado pelo servidor da assistência. Salve seu trabalho."], ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("O Windows recusou o reinício agendado.");
    }

    // Full job: fetch the loader named in the signed plan, stage it, answer the server, restart.
    public static async Task<JobResult> Execute(JobPlan plan, PinnedTransport transport, SecureState state, ISystemCommands commands, CancellationToken ct)
    {
        var support = Probe();
        if (!support.Available) return new(false, "Este computador não pode reiniciar em modo SLT: " + support.Reason, "REBOOT_UNSUPPORTED");
        var loader = await transport.DownloadBytes("/agent/v1/jobs/" + Uri.EscapeDataString(plan.Id) + "/rescue-loader", ct);
        var esp = await FindEsp(commands, ct);
        await Stage(plan, loader, esp, state, commands, ct);
        return new(true, $"Reinício em modo SLT agendado para {plan.RescueBoot!.RebootDelaySeconds} segundos.", "REBOOT_SCHEDULED");
    }
}
