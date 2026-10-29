using System.Net;

namespace SLT.WindowsAgent;

public sealed record AgentOptions(Uri Server, string ServerPin, string StateDirectory, bool Once, int PollSeconds,
    string? SmartctlPath, bool CollectOnly, string? FileRootsPath = null)
{
    public static AgentOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var once = false;
        var collect = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--once") { once = true; continue; }
            if (args[i] == "--collect-only") { collect = true; continue; }
            if (args[i] is not ("--server" or "--server-pin" or "--state-dir" or "--poll-seconds" or "--smartctl" or "--file-roots"))
                throw new ArgumentException($"Opção desconhecida: {args[i]}");
            if (++i == args.Length) throw new ArgumentException("Uma opção está sem valor.");
            if (!values.TryAdd(args[i - 1], args[i])) throw new ArgumentException("Opção repetida.");
        }
        var server = new Uri(values.GetValueOrDefault("--server", "https://127.0.0.1:7443"), UriKind.Absolute);
        EnsureLocalServer(server);
        var pin = values.GetValueOrDefault("--server-pin", "").Replace(":", "", StringComparison.Ordinal);
        if (!collect && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
            throw new ArgumentException("Informe --server-pin com os 64 caracteres SHA256 exibidos pelo servidor.");
        var poll = int.Parse(values.GetValueOrDefault("--poll-seconds", "10"));
        if (poll is < 2 or > 300) throw new ArgumentException("--poll-seconds deve estar entre 2 e 300.");
        var state = Path.GetFullPath(values.GetValueOrDefault("--state-dir", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SLT", "WindowsAgent")));
        var smartctl = values.GetValueOrDefault("--smartctl");
        if (smartctl is not null && (!Path.IsPathFullyQualified(smartctl) || !File.Exists(smartctl)))
            throw new ArgumentException("--smartctl exige caminho absoluto para smartctl.exe existente.");
        // The client package ships smartmontools in tools\smartmontools; --smartctl overrides it.
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "smartmontools", "smartctl.exe");
        if (smartctl is null && File.Exists(bundled) && !File.GetAttributes(bundled).HasFlag(FileAttributes.ReparsePoint)) smartctl = bundled;
        var fileRoots = values.GetValueOrDefault("--file-roots");
        if (fileRoots is not null && (!Path.IsPathFullyQualified(fileRoots) || !System.IO.File.Exists(fileRoots)))
            throw new ArgumentException("--file-roots exige caminho absoluto para JSON local existente.");
        return new(server, pin.ToLowerInvariant(), state, once, poll, smartctl, collect, fileRoots);
    }

    public static void EnsureLocalServer(Uri server)
    {
        if (server.Scheme != "https" || server.UserInfo.Length != 0 || server.AbsolutePath != "/" ||
            server.Query.Length != 0 || server.Fragment.Length != 0 || !IPAddress.TryParse(server.Host, out var address) || !IsLocal(address))
            throw new ArgumentException("Servidor deve ser HTTPS com IP literal da LAN (sem caminho, credenciais ou endereço público).");
    }

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 169 && bytes[1] == 254;
    }
}
