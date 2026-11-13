using System.Diagnostics;
using System.Net;
using Slt.Controller;
using Slt.Controller.LocalHost;

// Builds the real kamikaze boot image on this machine (WinRE from the recovery partition, real
// DISM mount/commit, real agent inventory) while bcdedit and shutdown are simulated: the boot
// configuration of the machine running the test is never changed and nothing is written to disks.
//   dotnet run --project tests/SLT.Controller.Tests -- --kamikaze-image <work folder> <agent.exe>
internal static class KamikazeImageE2E
{
    private sealed class SafeRunner(IHostCommandRunner real) : IHostCommandRunner
    {
        public readonly List<string> Simulated = [];
        private int guid;
        public Task<CommandOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout, Action<string>? onLine, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(executable).ToLowerInvariant();
            if (name is "bcdedit.exe" or "shutdown.exe")
            {
                lock (Simulated) Simulated.Add(name + " " + string.Join(' ', arguments));
                var output = name == "bcdedit.exe" && arguments[0] == "/create" ? $"{{{new Guid(Interlocked.Increment(ref guid), 0, 0, new byte[8])}}}" : "simulado";
                return Task.FromResult(new CommandOutput(0, output));
            }
            if (name is not ("dism.exe" or "reagentc.exe" or "slt-winpe-agent.exe" or "powershell.exe")) throw new InvalidOperationException("Comando inesperado no teste: " + name);
            return real.RunAsync(executable, arguments, standardInput, timeout, onLine, cancellationToken);
        }
    }

    public static int Run(string work, string agent)
    {
        var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
        var root = Path.Combine(Path.GetFullPath(work), "kamikaze-image-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "state"));
        var certs = new Certificates(Path.Combine(root, "state"), IPAddress.Loopback);
        using var db = new Store(Path.Combine(root, "state"));
        var controller = new Controller(db, certs, Path.Combine(root, "repo"));
        var runner = new SafeRunner(new NativeHostCommandRunner());
        var disks = new ServerDiskManager(controller, new HostScripts(runner), Path.Combine(root, "state"), virtualOnly: true);
        var kamikaze = new KamikazeManager(controller, runner) { AgentPath = Path.GetFullPath(agent), SystemRoot = Path.Combine(root, "system") + "\\", BundleRootOverride = Path.Combine(root, "volume") + "\\" };
        var source = kamikaze.BootSourceAsync(default).GetAwaiter().GetResult();
        Check(source.Source != "NONE", "fonte da imagem kamikaze encontrada: " + source.Detail);
        var agentDisks = kamikaze.AgentDisksAsync(default).GetAwaiter().GetResult();
        Check(agentDisks.Length > 0 && agentDisks.All(d => d.Identity.Length == 64), $"agente lista {agentDisks.Length} disco(s) com identidade sem número");
        var target = agentDisks.OrderBy(d => d.SizeBytes).First();
        var session = kamikaze.PlanAsync(new([new("FORMAT_DISK", target.Identity, TableType: "GPT", FileSystem: "NTFS", Label: "NAO-EXECUTA")]), disks, default).GetAwaiter().GetResult();
        kamikaze.Confirm(session.Id, session.Challenge);
        var until = DateTime.UtcNow.AddMinutes(20);
        KamikazeSession state;
        do { Thread.Sleep(2000); state = db.Get<KamikazeSession>("kamikaze", session.Id)!; } while (state.State == "STAGING" && DateTime.UtcNow < until);
        Console.WriteLine(state.Log);
        Check(state.State == "STAGED", "imagem preparada com o DISM real: " + state.Message);
        var wim = Path.Combine(root, "system", "SLT-Kamikaze", "boot", "boot.wim");
        Check(File.Exists(wim) && new FileInfo(wim).Length > 100L * 1024 * 1024 && File.Exists(Path.Combine(root, "system", "SLT-Kamikaze", "boot", "boot.sdi")), $"boot.wim ({new FileInfo(wim).Length / 1e6:N0} MB) e boot.sdi no lugar");
        var mount = Path.Combine(root, "check");
        Directory.CreateDirectory(mount);
        Dism($"/Mount-Image /ImageFile:\"{wim}\" /Index:1 /MountDir:\"{mount}\" /ReadOnly");
        try
        {
            var ini = File.ReadAllText(Path.Combine(mount, "Windows", "System32", "winpeshl.ini"));
            Check(ini.Contains(@"%SYSTEMDRIVE%\SLT\slt-winpe-agent.exe, kamikaze --session auto"), "winpeshl.ini inicia o console kamikaze");
            Check(File.ReadAllBytes(Path.Combine(mount, "SLT", "slt-winpe-agent.exe")).AsSpan().SequenceEqual(File.ReadAllBytes(agent)), "agente gravado na imagem é o mesmo do pacote");
            Check(File.ReadAllText(Path.Combine(mount, "SLT", "trust.pem")) == certs.Server.ExportCertificatePem(), "certificado do servidor gravado na imagem");
            Check(File.Exists(Path.Combine(mount, "Windows", "System32", "winload.efi")) || File.Exists(Path.Combine(mount, "Windows", "System32", "Boot", "winload.efi")), "imagem tem o carregador UEFI");
            Console.WriteLine("bootsect.exe na imagem: " + File.Exists(Path.Combine(mount, "Windows", "System32", "bootsect.exe")) + " | wpeutil.exe: " + File.Exists(Path.Combine(mount, "Windows", "System32", "wpeutil.exe")) + " | diskpart.exe: " + File.Exists(Path.Combine(mount, "Windows", "System32", "diskpart.exe")));
        }
        finally { Dism($"/Unmount-Image /MountDir:\"{mount}\" /Discard"); }
        Check(runner.Simulated.Any(s => s.StartsWith("bcdedit.exe /bootsequence")) && runner.Simulated.Any(s => s.Contains("winpe yes")), "comandos de boot gerados (simulados, nada gravado no BCD desta máquina)");
        var copy = Path.Combine(Path.GetFullPath(work), "kamikaze-boot.wim");
        File.Copy(wim, copy, true);
        kamikaze.CancelAsync(session.Id).GetAwaiter().GetResult();
        Check(!Directory.Exists(Path.Combine(root, "volume", "SLT-Kamikaze", session.Id)), "cancelamento apaga o pacote assinado");
        Console.WriteLine($"Imagem guardada para o laboratório em {copy}");
        Console.WriteLine($"{passed}/{passed} verificações da imagem kamikaze real passaram (boot desta máquina inalterado).");
        db.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return 0;
    }

    private static void Dism(string arguments)
    {
        var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"), arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("dism " + arguments + ": " + output);
    }
}
