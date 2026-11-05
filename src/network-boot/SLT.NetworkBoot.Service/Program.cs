using System.Net;
using System.Text;
using Slt.NetworkBoot;

// Only inherited stdin/stdout control the child. There is no network admin endpoint.
// --lab allows virtual Ethernet in a dedicated test namespace, never changes RFC1918/CIDR checks.
try
{
    var line = await Console.In.ReadLineAsync();
    if (line == null || line.Length > 65536) throw new InvalidDataException("Configuração privada ausente/excessiva.");
    var launch = BootJson.Parse<BootLaunch>(line);
    bool laboratory = args.SequenceEqual(new[] { "--lab" }) && launch.Laboratory;
    var validation = NetworkTopologyValidator.Validate(launch.Config, laboratory);
    if (!validation.Valid) throw new InvalidDataException(string.Join(" ", validation.Errors));
    BootArtifactStore.VerifyAll(launch.StorePath, launch.Profiles);
    var bootstrap = launch.Profiles.SingleOrDefault(p => p.Enabled && p.Kind == "IPXE_BOOTSTRAP") ?? throw new InvalidDataException("Importe exatamente um perfil iPXE BIOS/UEFI.");
    byte[] Read(string id) => File.ReadAllBytes(BootArtifactStore.SafeFile(launch.StorePath, bootstrap.Id + "/" + bootstrap.Artifacts.Single(a => a.Id == id).RelativePath));
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["undionly.kpxe"] = Read("bios"), ["ipxe.efi"] = Read("uefi"), ["autoexec.ipxe"] = Encoding.ASCII.GetBytes(BootScriptRenderer.Autoexec(launch.Config)) };
    bool ready = false;
    void Report(BootEvent e) { if (ready) Console.WriteLine(BootJson.Serialize(e)); }
    await using var tftp = new TftpBootstrapServer(IPAddress.Parse(launch.Config.BindIPv4), IPNetwork.Parse(launch.Config.AllowedCidr), files, launch.Config.MaxConcurrentTransfers, report: Report);
    await using var http = await BootHttpServer.Start(launch, report: Report);
    Console.WriteLine(BootJson.Serialize(new { state = "RUNNING" })); ready = true; tftp.Start();
    // Any command/EOF means stop. No arbitrary remote commands or process launch inputs.
    var input = Console.In.ReadLineAsync();
    if (await Task.WhenAny(input, tftp.Completion) == tftp.Completion)
    {
        await tftp.Completion;
        throw new IOException("Listener TFTP encerrou inesperadamente; HTTP será encerrado também.");
    }
    await input;
}
catch (Exception e)
{
    Console.WriteLine(BootJson.Serialize(new { state = "FAILED", error = e.Message })); Environment.ExitCode = 1;
}
