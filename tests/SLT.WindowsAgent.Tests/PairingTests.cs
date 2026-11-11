using System.Net;
using System.Security.Cryptography;
using System.Text;
using SLT.WindowsAgent;

// Discovery, short-code pairing and SLT-mode staging. Nothing here touches the real EFI
// partition or boot configuration: commands are recorded by a fake and files go to a temp folder.
internal static class PairingTests
{
    private sealed class FakeCommands(string copyOutput = "A entrada foi copiada com êxito para {11111111-2222-3333-4444-555555555555}.", string? failOn = null) : ISystemCommands
    {
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> Run(string executable, string[] arguments, CancellationToken ct)
        {
            var line = executable + " " + string.Join(' ', arguments); Calls.Add(line);
            if (failOn is not null && line.Contains(failOn, StringComparison.Ordinal)) return Task.FromResult(new ProcessResult(1, ""));
            return Task.FromResult(new ProcessResult(0, arguments.Length > 0 && arguments[0] == "/copy" ? copyOutput : ""));
        }
    }

    public static void Run(Action<string, Action> check, Action<bool> assert, Action<Action> reject)
    {
        const string csr = "-----BEGIN CERTIFICATE REQUEST-----\nSLT\n-----END CERTIFICATE REQUEST-----\n";
        var pin = string.Concat(Enumerable.Repeat("ab", 32)); var client = string.Concat(Enumerable.Repeat("01", 32)); var server = string.Concat(Enumerable.Repeat("fe", 32));
        check("pairing code matches the vector shared with server and Rescue", () =>
        {
            assert(Pairing.Code(pin, csr, client, server) == "07111918");
            assert(Pairing.Commit(client) == "72cd6e8422c407fb6d098690f1130b7ded7ec2f7f5e1d30bd9d521f015363793");
            assert(Pairing.Code(new string('0', 64), csr, client, server) != "07111918");
            assert(Pairing.Spaced("07111918") == "0711 1918");
        });
        var source = IPAddress.Parse("192.168.1.23");
        string Reply(string url, string replyPin, string nonce = "0011223344556677") => $$"""{"slt":"server","v":1,"nonce":"{{nonce}}","name":"BANCADA","url":"{{url}}","pin":"{{replyPin}}","version":"0.1.0-beta.7"}""";
        check("discovery reply from the advertised LAN address accepted", () =>
        {
            var endpoint = Discovery.ParseReply(Encoding.UTF8.GetBytes(Reply("https://192.168.1.23:7443", pin)), "0011223344556677", source)!;
            assert(endpoint.Url == "https://192.168.1.23:7443" && endpoint.Pin == pin && endpoint.Name == "BANCADA");
        });
        check("discovery reply pointing to another address rejected", () => assert(Discovery.ParseReply(Encoding.UTF8.GetBytes(Reply("https://192.168.1.99:7443", pin)), "0011223344556677", source) is null));
        check("discovery reply with public address rejected", () => assert(Discovery.ParseReply(Encoding.UTF8.GetBytes(Reply("https://8.8.8.8:7443", pin)), "0011223344556677", IPAddress.Parse("8.8.8.8")) is null));
        check("discovery reply with foreign nonce rejected", () => assert(Discovery.ParseReply(Encoding.UTF8.GetBytes(Reply("https://192.168.1.23:7443", pin, "ffffffffffffffff")), "0011223344556677", source) is null));
        check("discovery reply with plain HTTP, path or bad pin rejected", () =>
        {
            foreach (var bad in new[] { Reply("http://192.168.1.23:7443", pin), Reply("https://192.168.1.23:7443/x", pin), Reply("https://192.168.1.23:7443", "zz" + pin[2..]), Reply("https://192.168.1.23:7443", pin[..62]), "not json at all, just forty characters long!!" })
                assert(Discovery.ParseReply(Encoding.UTF8.GetBytes(bad), "0011223344556677", source) is null);
        });
        check("directed broadcast derived from address and prefix", () =>
        {
            assert(Discovery.DirectedBroadcast(IPAddress.Parse("192.168.1.23"), 24).ToString() == "192.168.1.255");
            assert(Discovery.DirectedBroadcast(IPAddress.Parse("10.20.30.40"), 20).ToString() == "10.20.31.255");
        });
        check("server address and pin are optional on the command line", () =>
        {
            var options = AgentOptions.Parse(["--state-dir", Path.GetTempPath()]);
            assert(options.Server is null && options.ServerPin == "" && !options.PinVerified && options.DiscoveryPort == 7444);
            var typed = AgentOptions.Parse(["--server", "https://192.168.1.23:7443", "--server-pin", pin, "--state-dir", Path.GetTempPath()]);
            assert(typed.PinVerified && typed.ServerPin == pin);
        });
        check("pin without server and malformed pin rejected", () =>
        {
            reject(() => AgentOptions.Parse(["--server-pin", pin]));
            reject(() => AgentOptions.Parse(["--server", "https://192.168.1.23:7443", "--server-pin", "1234"]));
            reject(() => AgentOptions.Parse(["--discovery-address", "8.8.8.8"]));
        });

        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var loader = RandomNumberGenerator.GetBytes(128 * 1024);
        string Script(string kernel = "http://192.168.1.23:8088/boot/v1/assets/rescue-0-1-0-beta-7-kernel", string extra = "", string scriptTicket = "", string write = "1") => string.Join('\n',
            "#!ipxe",
            "echo SLT Disk Manager - iniciando o modo SLT pela rede local",
            "dhcp || goto failed",
            "imgfree",
            $"kernel --name vmlinuz {kernel} boot=live components nopersistence noeject fetch=http://192.168.1.23:8088/boot/v1/assets/rescue-0-1-0-beta-7-rootfs.squashfs hostname=slt-rescue username=technician initrd=initrd.img console=ttyS0,115200n8 console=tty0 slt.server=https://192.168.1.23:7443 slt.pin={pin} slt.ticket={(scriptTicket.Length == 0 ? ticket : scriptTicket)} slt.write={write}{extra} || goto failed",
            "initrd --name initrd.img http://192.168.1.23:8088/boot/v1/assets/rescue-0-1-0-beta-7-initrd || goto failed",
            "boot || goto failed",
            ":failed",
            "echo Falha ao iniciar o modo SLT. Nenhum disco foi modificado. Voltando ao sistema instalado.",
            "sleep 8",
            "exit") + "\n";
        RescueBootOptions Options(string? script = null) => new(Convert.ToHexStringLower(SHA256.HashData(loader)), loader.Length, script ?? Script(), ticket, true, 30);
        check("server-rendered SLT boot script accepted", () => RescueBoot.ValidateOptions(Options()));
        check("golden boot script rendered by the server is accepted", () =>
        {
            var directory = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(directory, "tests", "fixtures", "slt-mode-boot.ipxe"))) directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("tests/fixtures/slt-mode-boot.ipxe not found");
            var golden = File.ReadAllText(Path.Combine(directory, "tests", "fixtures", "slt-mode-boot.ipxe")).Replace("\r\n", "\n").Replace("{PIN}", pin).Replace("{TICKET}", ticket);
            RescueBoot.ValidateOptions(Options(golden));
        });
        check("SLT boot script cannot fetch from a public address", () => reject(() => RescueBoot.ValidateOptions(Options(Script(kernel: "http://8.8.8.8:8088/boot/v1/assets/rescue-kernel")))));
        check("SLT boot script cannot leave the boot asset path", () => reject(() => RescueBoot.ValidateOptions(Options(Script(kernel: "http://192.168.1.23:8088/other/file")))));
        check("SLT boot script cannot add kernel arguments", () => reject(() => RescueBoot.ValidateOptions(Options(Script(extra: " init=/bin/sh")))));
        check("SLT boot script cannot expand iPXE variables", () => reject(() => RescueBoot.ValidateOptions(Options(Script(extra: " hostname=${net0/ip}")))));
        check("SLT boot script cannot carry another ticket", () => reject(() => RescueBoot.ValidateOptions(Options(Script(scriptTicket: new string('a', 64))))));
        check("SLT boot script write mode must match the signed option", () => reject(() => RescueBoot.ValidateOptions(Options(Script(write: "0")))));
        check("SLT boot script rejects other iPXE commands", () =>
        {
            foreach (var command in new[] { "chain http://192.168.1.23:8088/boot/v1/menu.ipxe", "sanboot --no-describe --drive 0x80", "shell", "set net0/ip 1.2.3.4", "echo ok; reboot" })
                reject(() => RescueBoot.ValidateOptions(Options(Script().Replace("imgfree\n", "imgfree\n" + command + "\n", StringComparison.Ordinal))));
        });
        check("SLT plan with oversized loader, bad ticket or delay rejected", () =>
        {
            reject(() => RescueBoot.ValidateOptions(Options() with { LoaderSizeBytes = 64L * 1024 * 1024 }));
            reject(() => RescueBoot.ValidateOptions(Options() with { Ticket = "short" }));
            reject(() => RescueBoot.ValidateOptions(Options() with { RebootDelaySeconds = 0 }));
            reject(() => RescueBoot.ValidateOptions(Options(Script().Replace("\n", "\r\n", StringComparison.Ordinal))));
        });
        var now = DateTimeOffset.UtcNow; var deviceId = Guid.NewGuid().ToString();
        var plan = new JobPlan(Guid.NewGuid().ToString(), "REBOOT_RESCUE", deviceId, "", "", 0, 512, "", "REINICIAR PC MODO SLT", now.AddMinutes(10), true, RescueBoot: Options());
        check("reboot job accepted only with its signed options", () =>
        {
            assert(PinnedTransport.ValidatePlan(plan, deviceId, now).Kind == "REBOOT_RESCUE");
            reject(() => PinnedTransport.ValidatePlan(plan with { RescueBoot = null }, deviceId, now));
            reject(() => PinnedTransport.ValidatePlan(plan with { Kind = "INVENTORY" }, deviceId, now));
        });

        string Workspace() => Path.Combine(Environment.CurrentDirectory, ".build", "agent-tests", Guid.NewGuid().ToString("N"));
        check("staging writes loader and script, then selects the entry for the next boot only", () =>
        {
            var root = Workspace(); var esp = Path.Combine(root, "esp") + Path.DirectorySeparatorChar; Directory.CreateDirectory(esp);
            var state = new SecureState(Path.Combine(root, "state")); var commands = new FakeCommands();
            RescueBoot.Stage(plan, loader, esp, state, commands, CancellationToken.None).GetAwaiter().GetResult();
            assert(File.ReadAllBytes(Path.Combine(esp, "EFI", "SLT", "ipxe.efi")).SequenceEqual(loader));
            assert(File.ReadAllText(Path.Combine(esp, "EFI", "SLT", "autoexec.ipxe")) == Script());
            const string id = "{11111111-2222-3333-4444-555555555555}";
            assert(commands.Calls.SequenceEqual([
                "bcdedit.exe /copy {bootmgr} /d SLT Disk Manager",
                $@"bcdedit.exe /set {id} path \EFI\SLT\ipxe.efi",
                $"bcdedit.exe /set {{fwbootmgr}} displayorder {id} /addlast",
                $"bcdedit.exe /set {{fwbootmgr}} bootsequence {id}"]));
            assert(!commands.Calls.Any(c => c.Contains("/default", StringComparison.Ordinal) || c.Contains("/addfirst", StringComparison.Ordinal) || c.Contains("{bootmgr} path", StringComparison.Ordinal)));
            var marker = state.Read<RescueBootMarker>(RescueBoot.MarkerFile)!;
            assert(marker.EntryId == id && marker.JobId == plan.Id);
            // Windows has not restarted since the staging: the entry must survive an agent restart.
            assert(!RescueBoot.Cleanup(state, commands, force: false, CancellationToken.None).GetAwaiter().GetResult());
            assert(File.Exists(Path.Combine(esp, "EFI", "SLT", "ipxe.efi")));
            // After the next Windows start (marker older than the system start) everything is removed.
            state.Write(RescueBoot.MarkerFile, marker with { StagedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromMinutes(5) });
            assert(RescueBoot.Cleanup(state, commands, force: false, CancellationToken.None).GetAwaiter().GetResult());
            assert(commands.Calls[^1] == $"bcdedit.exe /delete {id} /f" && !Directory.Exists(Path.Combine(esp, "EFI", "SLT")) && state.Read<RescueBootMarker>(RescueBoot.MarkerFile) is null);
        });
        check("staging refuses a loader that differs from the signed hash", () =>
        {
            var root = Workspace(); var esp = Path.Combine(root, "esp") + Path.DirectorySeparatorChar; Directory.CreateDirectory(esp);
            var commands = new FakeCommands();
            reject(() => RescueBoot.Stage(plan, RandomNumberGenerator.GetBytes(loader.Length), esp, new SecureState(Path.Combine(root, "state")), commands, CancellationToken.None).GetAwaiter().GetResult());
            assert(commands.Calls.Count == 0 && !Directory.Exists(Path.Combine(esp, "EFI")));
        });
        check("failed boot configuration leaves no entry or file behind", () =>
        {
            var root = Workspace(); var esp = Path.Combine(root, "esp") + Path.DirectorySeparatorChar; Directory.CreateDirectory(Path.Combine(esp, "EFI", "Microsoft"));
            var state = new SecureState(Path.Combine(root, "state")); var commands = new FakeCommands(failOn: "bootsequence");
            reject(() => RescueBoot.Stage(plan, loader, esp, state, commands, CancellationToken.None).GetAwaiter().GetResult());
            assert(commands.Calls[^1] == "bcdedit.exe /delete {11111111-2222-3333-4444-555555555555} /f");
            assert(!Directory.Exists(Path.Combine(esp, "EFI", "SLT")) && Directory.Exists(Path.Combine(esp, "EFI", "Microsoft")) && state.Read<RescueBootMarker>(RescueBoot.MarkerFile) is null);
        });
        check("unrecognised bcdedit answer aborts before changing the boot sequence", () =>
        {
            var root = Workspace(); var esp = Path.Combine(root, "esp") + Path.DirectorySeparatorChar; Directory.CreateDirectory(esp);
            var commands = new FakeCommands(copyOutput: "operation completed");
            reject(() => RescueBoot.Stage(plan, loader, esp, new SecureState(Path.Combine(root, "state")), commands, CancellationToken.None).GetAwaiter().GetResult());
            assert(!commands.Calls.Any(c => c.Contains("bootsequence", StringComparison.Ordinal)) && !Directory.Exists(Path.Combine(esp, "EFI", "SLT")));
        });
    }
}
