using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slt.Controller.LocalHost;

public sealed record KamikazeOperationRequest(string Kind, string TargetIdentity, string? SourceIdentity = null, string? TableType = null, string? FileSystem = null,
    string? Label = null, string? MediaId = null, int ImageIndex = 0, DeploymentAccount? Account = null);
public sealed record KamikazePlanRequest(KamikazeOperationRequest[] Operations, bool RebootNow = false);
public sealed record KamikazeDiskInfo(int Number, string Identity, string Model, string Serial, long SizeBytes, int LogicalSectorBytes, bool Offline, bool ReadOnly);
public sealed record KamikazeDiskView(int Number, string Identity, string Model, string Serial, long SizeBytes, int LogicalSectorBytes, string PartitionStyle, string[] Notes, string Volumes);
public sealed record KamikazeSession(string Id, string State, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, string Challenge, string[] Summary, string[] Warnings,
    string Payload, string BundleRoot, bool RebootNow, string Log = "", string? BootEntry = null, string? DeviceEntry = null, DateTimeOffset? StagedAtUtc = null,
    DateTimeOffset? FinishedAtUtc = null, string? ResultJson = null, string? Message = null, string[]? ConsoleChallenges = null);

// Kamikaze mode: the server reboots once into a WinRE/WinPE loaded in RAM (Windows Boot Manager
// ramdisk, so BIOS, UEFI and Secure Boot work as for any Windows recovery boot) and runs, at its
// own console, the operations signed here: clone, format or reinstall even the disk the server
// runs from. The boot entry is one-shot (bcdedit /bootsequence): the next boot returns to the
// normal Windows, and results are collected when the Controller starts again.
public sealed class KamikazeManager
{
    private static readonly Regex Guid = new(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");
    private static readonly Regex Label = new("^[A-Za-z0-9_-]{0,16}$");
    private readonly Controller controller;
    private readonly IHostCommandRunner runner;
    private readonly object staging = new();
    public string AgentPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "tools", "slt-winpe-agent.exe");
    public string SystemRoot { get; init; } = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
    public string WindowsDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public string? FirmwareOverride { get; init; }
    public Func<DateTimeOffset> BootTime { get; init; } = () => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
    public string ServerName { get; init; } = Environment.MachineName;
    // Root of the volume that holds the repository; tests point it at a temporary folder.
    public string? BundleRootOverride { get; init; }
    private string RepositoryRoot => BundleRootOverride ?? Path.GetPathRoot(Path.GetFullPath(controller.Repository))!;
    private string Work => Path.Combine(controller.Repository, "kamikaze");
    private string BootDirectory => Path.Combine(SystemRoot, "SLT-Kamikaze", "boot");

    public KamikazeManager(Controller controller, IHostCommandRunner runner)
    {
        this.controller = controller; this.runner = runner;
        lock (controller.Gate)
            foreach (var s in controller.Db.All<KamikazeSession>("kamikaze").Where(s => s.State == "STAGING"))
                controller.Db.Put("kamikaze", s.Id, s with { State = "FAILED", Message = "Servidor reiniciado enquanto o modo kamikaze era preparado; nada foi gravado nos discos.", FinishedAtUtc = DateTimeOffset.UtcNow });
    }

    private KamikazeSession Get(string id) { lock (controller.Gate) return controller.Db.Get<KamikazeSession>("kamikaze", id) ?? throw new ApiException(404, "Sessão kamikaze não encontrada."); }
    private void Save(KamikazeSession s) { lock (controller.Gate) controller.Db.Put("kamikaze", s.Id, s); }
    private void Audit(string action, string details) { lock (controller.Gate) controller.Db.Log("local-admin", "kamikaze." + action, details); }

    [DllImport("kernel32.dll")] private static extern bool GetFirmwareType(out uint type);
    public string Firmware()
    {
        if (FirmwareOverride != null) return FirmwareOverride;
        if (!OperatingSystem.IsWindows()) return "UNKNOWN";
        return GetFirmwareType(out var type) ? type switch { 1 => "BIOS", 2 => "UEFI", _ => "UNKNOWN" } : "UNKNOWN";
    }
    public static bool? SecureBoot()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int value ? value == 1 : null;
    }

    public async Task<KamikazeDiskInfo[]> AgentDisksAsync(CancellationToken cancel)
    {
        Controller.Ensure(File.Exists(AgentPath), 409, "Agente WinPE (tools\\slt-winpe-agent.exe) ausente no pacote do servidor; reinstale/atualize o servidor.");
        var output = await runner.RunAsync(AgentPath, ["disks"], null, TimeSpan.FromMinutes(1), null, cancel);
        Controller.Ensure(output.ExitCode == 0, 500, "Inventário de discos do agente falhou: " + HostScripts.Tail(output.Output));
        return JsonSerializer.Deserialize<KamikazeDiskInfo[]>(output.Output.Trim(), Wire.Json) ?? [];
    }

    public async Task<object> StatusAsync(ServerDiskManager disks, CancellationToken cancel)
    {
        Reconcile();
        var firmware = Firmware();
        var image = await BootSourceAsync(cancel);
        KamikazeDiskView[] views = [];
        string? diskError = null;
        try { views = await DiskViewsAsync(disks, cancel); } catch (ApiException e) { diskError = e.Message; }
        KamikazeSession[] sessions;
        lock (controller.Gate) sessions = controller.Db.All<KamikazeSession>("kamikaze").OrderByDescending(s => s.CreatedAtUtc).Take(10).Select(s => s with { Payload = "" }).ToArray();
        return new { firmware, secureBoot = SecureBoot(), bootImage = new { source = image.Source, path = image.Path, detail = image.Detail }, agent = File.Exists(AgentPath), disks = views, diskError, sessions, repositoryRoot = RepositoryRoot };
    }

    private async Task<KamikazeDiskView[]> DiskViewsAsync(ServerDiskManager disks, CancellationToken cancel)
    {
        var agent = await AgentDisksAsync(cancel);
        var inventory = await disks.InventoryAsync(cancel);
        var repoLetter = Path.GetPathRoot(Path.GetFullPath(controller.Repository))!.TrimEnd('\\', ':').ToUpperInvariant();
        return agent.Select(a =>
        {
            var d = inventory.Disks.FirstOrDefault(x => x.Number == a.Number);
            var notes = new List<string>();
            if (d != null && (d.IsSystem || d.IsBoot)) notes.Add("Disco do Windows do servidor");
            if (d != null && d.Partitions.Any(p => p.DriveLetter.Equals(repoLetter, StringComparison.OrdinalIgnoreCase))) notes.Add("Repositório e pacote kamikaze");
            if (agent.Count(x => x.Identity == a.Identity) > 1) notes.Add("Identidade ambígua: não pode ser usado");
            var volumes = d == null ? "" : string.Join(" ", d.Partitions.Where(p => p.DriveLetter.Length == 1).Select(p => $"{p.DriveLetter}: {p.FileSystem}"));
            return new KamikazeDiskView(a.Number, a.Identity, a.Model, a.Serial, a.SizeBytes, a.LogicalSectorBytes, d?.PartitionStyle ?? "", notes.ToArray(), volumes);
        }).ToArray();
    }

    public record BootSource(string Source, string Path, string Detail);
    public async Task<BootSource> BootSourceAsync(CancellationToken cancel)
    {
        var custom = Path.Combine(Work, "winpe.wim");
        if (File.Exists(custom)) return new("WINPE", custom, "WinPE fornecido pelo operador (kamikaze\\winpe.wim no repositório).");
        var local = Path.Combine(WindowsDirectory, "System32", "Recovery", "Winre.wim");
        if (File.Exists(local)) return new("WINRE", local, "Ambiente de Recuperação do Windows deste servidor.");
        try
        {
            var info = await runner.RunAsync(Path.Combine(WindowsDirectory, "System32", "reagentc.exe"), ["/info"], null, TimeSpan.FromMinutes(1), null, cancel);
            var line = info.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase));
            if (line != null)
            {
                var path = line[line.IndexOf(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase)..].Trim() + @"\Winre.wim";
                return new("WINRE", path, "Ambiente de Recuperação do Windows deste servidor (partição de recuperação).");
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException or System.ComponentModel.Win32Exception) { }
        return new("NONE", "", "Sem WinRE ativo. Ative com 'reagentc /enable' ou coloque um WinPE (New-WinPEKit) em kamikaze\\winpe.wim no repositório.");
    }

    public async Task<KamikazeSession> PlanAsync(KamikazePlanRequest request, ServerDiskManager disks, CancellationToken cancel)
    {
        Reconcile();
        lock (controller.Gate)
            Controller.Ensure(!controller.Db.All<KamikazeSession>("kamikaze").Any(s => s.State is "STAGING" or "STAGED" or "REBOOTING"), 409, "Já existe um modo kamikaze preparado; cancele-o antes de planejar outro.");
        Controller.Ensure(request.Operations is { Length: > 0 and <= 8 }, 400, "Inclua de 1 a 8 operações.");
        var firmware = Firmware();
        Controller.Ensure(firmware is "UEFI" or "BIOS", 409, "Firmware do servidor não identificado.");
        var source = await BootSourceAsync(cancel);
        Controller.Ensure(source.Source != "NONE", 409, source.Detail);
        var agent = await AgentDisksAsync(cancel);
        var views = await DiskViewsAsync(disks, cancel);
        var repositoryRoot = RepositoryRoot;
        var repoDisks = views.Where(v => v.Notes.Contains("Repositório e pacote kamikaze")).Select(v => v.Identity).ToHashSet();
        KamikazeDiskInfo Disk(string identity, string role)
        {
            var found = agent.Where(d => d.Identity == identity).ToArray();
            Controller.Ensure(found.Length == 1, 409, $"Disco de {role} não encontrado ou ambíguo; atualize a lista.");
            Controller.Ensure(!found[0].ReadOnly, 409, $"Disco de {role} protegido contra gravação.");
            return found[0];
        }
        object KDisk(KamikazeDiskInfo d) => new { identity = d.Identity, model = d.Model, serial = d.Serial, sizeBytes = d.SizeBytes, logicalSectorBytes = d.LogicalSectorBytes };
        var written = new HashSet<string>(); var read = new HashSet<string>();
        var ops = new List<object>(); var summary = new List<string>(); var warnings = new List<string>(); var consoleChallenges = new List<string>();
        foreach (var (op, index) in request.Operations.Select((o, i) => (o, i + 1)))
        {
            var target = Disk(op.TargetIdentity, "destino");
            Controller.Ensure(!written.Contains(target.Identity) && !read.Contains(target.Identity), 409, $"Operação {index}: o mesmo disco não pode ser gravado duas vezes nem depois de servir de origem.");
            var challenge = $"APAGAR {Math.Round(target.SizeBytes / 1e9):0}GB {target.Identity[..4].ToUpperInvariant()}";
            var view = views.Single(v => v.Number == target.Number);
            if (view.Notes.Contains("Disco do Windows do servidor")) warnings.Add($"Operação {index} apaga o disco do Windows do servidor: depois dela o servidor só volta a iniciar se você instalar um sistema nele (ou clonar um para ele).");
            switch (op.Kind)
            {
                case "CLONE_DISK":
                {
                    var src = Disk(op.SourceIdentity ?? "", "origem");
                    Controller.Ensure(src.Identity != target.Identity && !written.Contains(src.Identity), 409, $"Operação {index}: origem inválida.");
                    Controller.Ensure(src.LogicalSectorBytes == target.LogicalSectorBytes, 409, $"Operação {index}: setores de tamanhos diferentes.");
                    if (target.SizeBytes < src.SizeBytes)
                    {
                        using var probe = RawDisk.Open($@"\\.\PhysicalDrive{src.Number}", false);
                        RawDisk.CloneLength(probe, src.SizeBytes, target.SizeBytes, src.LogicalSectorBytes);
                    }
                    read.Add(src.Identity);
                    ops.Add(new { kind = "CLONE_DISK", target = KDisk(target), source = KDisk(src), challenge });
                    summary.Add($"{index}. Clonar {src.Model} ({src.SizeBytes / 1e9:N0} GB) → {target.Model} ({target.SizeBytes / 1e9:N0} GB)");
                    break;
                }
                case "FORMAT_DISK":
                {
                    var fs = (op.FileSystem ?? "").ToUpperInvariant();
                    Controller.Ensure(op.TableType is "GPT" or "MBR" && fs is "NTFS" or "EXFAT" or "FAT32", 400, $"Operação {index}: tabela GPT/MBR e sistema NTFS, exFAT ou FAT32.");
                    Controller.Ensure(Label.IsMatch(op.Label ?? "") && (fs == "NTFS" || (op.Label ?? "").Length <= 11), 400, $"Operação {index}: rótulo com até 16 letras, números, _ ou - (11 em FAT).");
                    Controller.Ensure(fs != "FAT32" || target.SizeBytes <= 32L * 1024 * 1024 * 1024, 400, $"Operação {index}: FAT32 só até 32 GB.");
                    Controller.Ensure(op.TableType == "GPT" || target.SizeBytes <= 2L * 1024 * 1024 * 1024 * 1024, 400, $"Operação {index}: MBR só até 2 TiB.");
                    ops.Add(new { kind = "FORMAT_DISK", target = KDisk(target), format = new { tableType = op.TableType, fileSystem = fs, label = op.Label ?? "" }, challenge });
                    summary.Add($"{index}. Formatar {target.Model} ({target.SizeBytes / 1e9:N0} GB) em {op.TableType}/{fs}");
                    break;
                }
                case "DEPLOY_WINDOWS":
                {
                    var media = controller.Media.Get(op.MediaId ?? "");
                    Controller.Ensure(media.State == "AVAILABLE" && media.Kind == "WINDOWS" && media.Images.Any(i => i.Index == op.ImageIndex), 409, $"Operação {index}: mídia Windows ou edição indisponível.");
                    Controller.Ensure(!written.Any(repoDisks.Contains), 409, $"Operação {index}: uma operação anterior apaga o disco do repositório, onde está a mídia do Windows. Coloque a instalação antes dela na lista.");
                    Controller.Ensure(!repoDisks.Contains(target.Identity), 409, $"Operação {index}: a mídia do Windows está no repositório, que fica no disco de destino. Mova o repositório para outro disco (Set-ServerRepository) ou use um disco de destino diferente.");
                    var file = Path.Combine(controller.Repository, "media", media.Id, media.FileName);
                    Controller.Ensure(File.Exists(file), 409, $"Operação {index}: arquivo da mídia não encontrado no repositório.");
                    Controller.Ensure(op.Account == null, 400, $"Operação {index}: no modo kamikaze o Windows instalado abre a configuração inicial normal; a conta é criada no primeiro boot.");
                    var deployFirmware = firmware == "UEFI" ? "GPT_UEFI" : "MBR_BIOS";
                    ops.Add(new { kind = "DEPLOY_WINDOWS", target = KDisk(target), deployment = new { mediaPath = Path.GetRelativePath(repositoryRoot, file), mediaSha256 = media.Sha256, mediaSizeBytes = media.SizeBytes, imageIndex = op.ImageIndex, firmware = deployFirmware }, challenge });
                    summary.Add($"{index}. Instalar {media.Name} (edição {op.ImageIndex}, {(firmware == "UEFI" ? "UEFI/GPT" : "BIOS/MBR")}) em {target.Model} ({target.SizeBytes / 1e9:N0} GB)");
                    if (firmware == "BIOS") warnings.Add("Instalação em BIOS usa o bootsect.exe da imagem de boot; se ele não existir no WinRE a operação para antes de gravar.");
                    break;
                }
                default:
                    throw new ApiException(400, $"Operação {index}: tipo não suportado no modo kamikaze (clonar, formatar ou instalar Windows).");
            }
            written.Add(target.Identity);
            consoleChallenges.Add(challenge);
        }
        if (SecureBoot() == true && source.Source == "WINPE") warnings.Add("Secure Boot ativo: o WinPE do operador precisa ser assinado pela Microsoft (o do ADK é).");
        var id = Wire.Id();
        var created = DateTimeOffset.UtcNow;
        var payload = Wire.Serialize(new { schemaVersion = 1, sessionId = id, serverName = ServerName, createdAtUtc = created, expiresAtUtc = created.AddHours(24), operations = ops });
        var session = new KamikazeSession(id, "AWAITING_CONFIRMATION", created, created.AddMinutes(15), $"KAMIKAZE {ServerName.ToUpperInvariant()}", summary.ToArray(),
            warnings.Distinct().ToArray(), payload, repositoryRoot, request.RebootNow, ConsoleChallenges: consoleChallenges.ToArray());
        Save(session);
        Audit("planned", $"{id} {string.Join(" | ", summary)}");
        return session with { Payload = "" };
    }

    public KamikazeSession Confirm(string id, string challenge)
    {
        var s = Get(id);
        Controller.Ensure(s.State == "AWAITING_CONFIRMATION" && s.ExpiresAtUtc > DateTimeOffset.UtcNow, 409, "Plano expirado ou já usado; planeje de novo.");
        Controller.Ensure(Controller.SafeEquals(s.Challenge, (challenge ?? "").Trim().ToUpperInvariant()), 400, "Confirmação digitada incorreta.");
        lock (staging)
        {
            lock (controller.Gate)
                Controller.Ensure(!controller.Db.All<KamikazeSession>("kamikaze").Any(x => x.Id != id && x.State is "STAGING" or "STAGED" or "REBOOTING"), 409, "Já existe um modo kamikaze preparado.");
            s = s with { State = "STAGING" };
            Save(s);
        }
        Audit("confirmed", id);
        _ = Task.Run(() => StageAsync(id));
        return s with { Payload = "" };
    }

    private void Line(string id, string text)
    {
        var s = Get(id);
        var log = s.Log + DateTime.Now.ToString("HH:mm:ss ") + text + Environment.NewLine;
        Save(s with { Log = log.Length > 32768 ? log[^32768..] : log });
    }

    private async Task<CommandOutput> Run(string id, string exe, params string[] args)
    {
        Line(id, "> " + Path.GetFileName(exe) + " " + string.Join(' ', args));
        var output = await runner.RunAsync(exe, args, null, TimeSpan.FromMinutes(20), null, CancellationToken.None);
        if (output.ExitCode != 0) throw new ApiException(500, $"{Path.GetFileName(exe)} falhou (código {output.ExitCode}): {HostScripts.Tail(output.Output, 600)}");
        return output;
    }
    private string Tool(string name) => Path.Combine(WindowsDirectory, "System32", name);

    private async Task StageAsync(string id)
    {
        var s = Get(id);
        string? loader = null, device = null;
        var bundleDirectory = Path.Combine(s.BundleRoot, "SLT-Kamikaze", id);
        try
        {
            var firmware = Firmware();
            var (wim, winload) = await PrepareImageAsync(id);
            ProtectDirectory(Path.Combine(SystemRoot, "SLT-Kamikaze"));
            Directory.CreateDirectory(BootDirectory);
            Line(id, "Copiando a imagem de boot para " + BootDirectory);
            File.Copy(wim, Path.Combine(BootDirectory, "boot.wim"), true);
            var sdi = Path.Combine(WindowsDirectory, "Boot", "DVD", firmware == "UEFI" ? "EFI" : "PCAT", "boot.sdi");
            Controller.Ensure(File.Exists(sdi), 409, "boot.sdi do Windows não encontrado.");
            File.Copy(sdi, Path.Combine(BootDirectory, "boot.sdi"), true);
            ProtectDirectory(Path.Combine(s.BundleRoot, "SLT-Kamikaze"));
            Directory.CreateDirectory(bundleDirectory);
            var payload = Encoding.UTF8.GetBytes(s.Payload);
            using (var key = controller.Certificates.Server.GetRSAPrivateKey()!)
            {
                var signature = key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                File.WriteAllText(Path.Combine(bundleDirectory, "bundle.json"), Wire.Serialize(new SignedAssignment(Convert.ToBase64String(payload), Convert.ToBase64String(signature))));
            }
            Line(id, "Pacote assinado gravado em " + bundleDirectory);
            var drive = SystemRoot.TrimEnd('\\');
            var bcd = Tool("bcdedit.exe");
            device = Guid.Match((await Run(id, bcd, "/create", "/d", "SLT Kamikaze ramdisk", "/device")).Output).Value;
            Controller.Ensure(device.Length > 0, 500, "bcdedit não devolveu o identificador do ramdisk.");
            Save(Get(id) with { DeviceEntry = device });
            await Run(id, bcd, "/set", device, "ramdisksdidevice", $"partition={drive}");
            await Run(id, bcd, "/set", device, "ramdisksdipath", @"\SLT-Kamikaze\boot\boot.sdi");
            loader = Guid.Match((await Run(id, bcd, "/create", "/d", "SLT Disk Manager - Kamikaze", "/application", "osloader")).Output).Value;
            Controller.Ensure(loader.Length > 0, 500, "bcdedit não devolveu o identificador da entrada de boot.");
            Save(Get(id) with { BootEntry = loader });
            var ramdisk = $@"ramdisk=[{drive}]\SLT-Kamikaze\boot\boot.wim,{device}";
            await Run(id, bcd, "/set", loader, "device", ramdisk);
            await Run(id, bcd, "/set", loader, "osdevice", ramdisk);
            await Run(id, bcd, "/set", loader, "path", firmware == "UEFI" ? winload + ".efi" : winload + ".exe");
            await Run(id, bcd, "/set", loader, "systemroot", @"\Windows");
            await Run(id, bcd, "/set", loader, "detecthal", "yes");
            await Run(id, bcd, "/set", loader, "winpe", "yes");
            // One-shot: only the next boot uses this entry; the default entry is untouched.
            await Run(id, bcd, "/bootsequence", loader);
            Save(Get(id) with { State = "STAGED", StagedAtUtc = DateTimeOffset.UtcNow, Message = "Pronto: o próximo reinício entra no modo kamikaze." });
            Audit("staged", $"{id} entrada={loader}");
            if (s.RebootNow) await RebootAsync(id);
        }
        catch (Exception e)
        {
            Line(id, "ERRO: " + e.Message);
            await RemoveBootEntriesAsync(id, loader, device);
            TryDelete(bundleDirectory);
            Save(Get(id) with { State = "FAILED", Message = "Preparação falhou; nada foi gravado nos discos. " + e.Message, FinishedAtUtc = DateTimeOffset.UtcNow });
            Audit("failed", $"{id} {e.Message}");
        }
    }

    // Boot image = WinRE (or the operator's WinPE) + agent + server certificate + winpeshl.ini.
    // Cached per (source image, agent, certificate): the next sessions reuse it.
    private async Task<(string Wim, string Winload)> PrepareImageAsync(string id)
    {
        var source = await BootSourceAsync(CancellationToken.None);
        Controller.Ensure(source.Source != "NONE", 409, source.Detail);
        Directory.CreateDirectory(Path.Combine(Work, "cache"));
        var temp = Path.Combine(Work, "work-" + id);
        Directory.CreateDirectory(temp);
        try
        {
            var copy = Path.Combine(temp, "source.wim");
            Line(id, $"Copiando {source.Detail}");
            File.Copy(source.Path, copy, true);
            string Hash(string file) { using var f = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(f)); }
            var cacheKey = Wire.Hash($"v1|{Hash(copy)}|{Hash(AgentPath)}|{controller.Certificates.Server.Thumbprint}");
            var cached = Path.Combine(Work, "cache", cacheKey + ".wim");
            var meta = cached + ".winload";
            if (File.Exists(cached) && File.Exists(meta)) { Line(id, "Imagem de boot já preparada antes (cache)."); return (cached, File.ReadAllText(meta).Trim()); }
            var mount = Path.Combine(temp, "mount");
            Directory.CreateDirectory(mount);
            var dism = Tool("dism.exe");
            await Run(id, dism, "/Mount-Image", "/ImageFile:" + copy, "/Index:1", "/MountDir:" + mount);
            string winload;
            try
            {
                Directory.CreateDirectory(Path.Combine(mount, "SLT"));
                File.Copy(AgentPath, Path.Combine(mount, "SLT", "slt-winpe-agent.exe"), true);
                File.WriteAllText(Path.Combine(mount, "SLT", "trust.pem"), controller.Certificates.Server.ExportCertificatePem());
                File.WriteAllText(Path.Combine(mount, "Windows", "System32", "winpeshl.ini"), "[LaunchApps]\r\n%SYSTEMDRIVE%\\SLT\\slt-winpe-agent.exe, kamikaze --session auto\r\n", Encoding.ASCII);
                winload = File.Exists(Path.Combine(mount, "Windows", "System32", "Boot", "winload.efi")) || File.Exists(Path.Combine(mount, "Windows", "System32", "Boot", "winload.exe"))
                    ? @"\Windows\System32\Boot\winload" : @"\Windows\System32\winload";
                await Run(id, dism, "/Unmount-Image", "/MountDir:" + mount, "/Commit");
            }
            catch
            {
                try { await runner.RunAsync(dism, ["/Unmount-Image", "/MountDir:" + mount, "/Discard"], null, TimeSpan.FromMinutes(10), null, CancellationToken.None); } catch (Exception) { }
                throw;
            }
            File.Move(copy, cached, true);
            File.WriteAllText(meta, winload);
            foreach (var old in Directory.GetFiles(Path.Combine(Work, "cache"), "*.wim").Where(f => f != cached)) { TryDelete(old); TryDelete(old + ".winload"); }
            Line(id, "Imagem de boot preparada com o agente e o certificado deste servidor.");
            return (cached, winload);
        }
        finally { TryDelete(temp); }
    }

    public async Task<KamikazeSession> RebootAsync(string id)
    {
        var s = Get(id);
        Controller.Ensure(s.State is "STAGED", 409, "O modo kamikaze não está preparado.");
        await Run(id, Tool("shutdown.exe"), "/r", "/t", "60", "/c", "SLT Disk Manager: reiniciando no modo kamikaze em 60 segundos.");
        s = Get(id) with { State = "REBOOTING", Message = "Reiniciando em 60 segundos. Acompanhe na tela do servidor." };
        Save(s); Audit("reboot", id);
        return s with { Payload = "" };
    }

    public async Task<KamikazeSession> CancelAsync(string id)
    {
        var s = Get(id);
        Controller.Ensure(s.State is "AWAITING_CONFIRMATION" or "STAGED" or "REBOOTING", 409, "Nada a cancelar nesta sessão.");
        if (s.State == "REBOOTING") try { await runner.RunAsync(Tool("shutdown.exe"), ["/a"], null, TimeSpan.FromSeconds(30), null, CancellationToken.None); } catch (Exception) { }
        await RemoveBootEntriesAsync(id, s.BootEntry, s.DeviceEntry);
        TryDelete(Path.Combine(s.BundleRoot, "SLT-Kamikaze", id));
        s = Get(id) with { State = "CANCELLED", Message = "Cancelado; a entrada de boot foi removida e nenhum disco foi gravado.", FinishedAtUtc = DateTimeOffset.UtcNow };
        Save(s); Audit("cancelled", id);
        return s with { Payload = "" };
    }

    private async Task RemoveBootEntriesAsync(string id, string? loader, string? device)
    {
        foreach (var entry in new[] { loader, device }.Where(e => !string.IsNullOrEmpty(e)))
            try { await runner.RunAsync(Tool("bcdedit.exe"), ["/delete", entry!, "/f"], null, TimeSpan.FromMinutes(1), null, CancellationToken.None); }
            catch (Exception e) { Line(id, $"Aviso: entrada {entry} não removida ({e.Message})."); }
    }

    // The root of C: lets ordinary users create and change subfolders: the boot image and the
    // plan get a protected ACL (SYSTEM and Administrators only), like the server state folder.
    public static void ProtectDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) return;
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { new System.Security.Principal.SecurityIdentifier("S-1-5-18"), new System.Security.Principal.SecurityIdentifier("S-1-5-32-544") })
            acl.AddAccessRule(new(sid, System.Security.AccessControl.FileSystemRights.FullControl, inheritance, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), acl);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Called at startup and on every status request: collects what the console wrote.
    public void Reconcile()
    {
        KamikazeSession[] pending;
        lock (controller.Gate) pending = controller.Db.All<KamikazeSession>("kamikaze").Where(s => s.State is "STAGED" or "REBOOTING").ToArray();
        foreach (var s in pending)
        {
            var directory = Path.Combine(s.BundleRoot, "SLT-Kamikaze", s.Id);
            var resultPath = Path.Combine(directory, "result.json");
            string state, message; string? result = null;
            if (File.Exists(resultPath))
            {
                result = File.ReadAllText(resultPath);
                using var json = JsonDocument.Parse(result);
                var success = json.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean();
                var cancelled = json.RootElement.TryGetProperty("cancelled", out var c) && c.GetBoolean();
                state = success ? "COMPLETED" : cancelled ? "CANCELLED" : "FAILED";
                message = json.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            }
            else if (File.Exists(Path.Combine(directory, "started.json"))) { state = "INTERRUPTED"; message = "O modo kamikaze começou mas não registrou o fim (queda de energia ou reinício). Confira os discos."; }
            else if (s.StagedAtUtc != null && BootTime() > s.StagedAtUtc.Value.AddMinutes(1)) { state = "NOT_RUN"; message = "O servidor reiniciou mas o modo kamikaze não chegou a executar (firmware, Secure Boot ou imagem de boot). Nenhum disco foi gravado por ele."; }
            else continue;
            _ = RemoveBootEntriesAsync(s.Id, s.BootEntry, s.DeviceEntry);
            TryDelete(directory); TryDelete(BootDirectory);
            Save(s with { State = state, Message = message, ResultJson = result, FinishedAtUtc = DateTimeOffset.UtcNow });
            Audit("finished", $"{s.Id} {state} {message}");
        }
    }
}
