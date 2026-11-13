using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slt.Controller.LocalHost;

public sealed record VmDiskRequest(string Kind, string? Folder = null, int SizeGiB = 0, string? Path = null, int? DiskNumber = null, string? Fingerprint = null);
public sealed record VmCreateRequest(string Name, int Generation, int MemoryMiB, int CpuCount, int CpuPercent, bool SecureBoot, string? SecureBootTemplate,
    bool Tpm, VmDiskRequest Disk, string? IsoPath, string? SwitchName);
public sealed record VmPlan(string Id, VmCreateRequest Request, string Title, string[] Warnings, string Challenge, bool RequiresTyping, DateTimeOffset ExpiresAtUtc, string State);
public sealed record VmRecord(string Id, string Name, VmCreateRequest Request, string[] CreatedFiles, DateTimeOffset CreatedAtUtc);
public sealed record VmNameRequest(string? IsoPath = null, bool DeleteFiles = false, bool OnlineDisks = false);

// Virtual machines on the server's Hyper-V: Generation 1 boots Legacy/BIOS, Generation 2 boots
// UEFI. A physical disk of the server can be handed to the VM (Hyper-V pass-through) to install
// and prepare a system that later goes into the client machine.
public sealed class HyperVManager(Controller controller, HostScripts scripts, ServerDiskManager disks, string stateDirectory)
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9 _-]{0,39}$");

    public Task<JsonElement> StatusAsync(CancellationToken cancel = default) => scripts.RunAsync("SltHyperV.ps1", "status", new { }, TimeSpan.FromMinutes(2), cancellationToken: cancel);

    private static string SafeFolder(string? folder, string stateDirectory)
    {
        Controller.Ensure(!string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && !folder.StartsWith(@"\\"), 400, "Pasta do disco virtual: caminho completo em um disco local (ex.: D:\\SLT VMs).");
        var full = Path.GetFullPath(folder!).TrimEnd('\\');
        Controller.Ensure(full.Length > 3 && !full.Contains('"') && new DriveInfo(full[..1]).DriveType == DriveType.Fixed, 400, "Use uma pasta (não a raiz) em um disco local fixo.");
        Controller.Ensure(!SLT.WindowsAgent.FileSafety.Under(full, stateDirectory), 400, "Não use a pasta de estado do servidor para máquinas virtuais.");
        return full;
    }

    public async Task<VmPlan> PlanAsync(VmCreateRequest r, CancellationToken cancel = default)
    {
        Controller.Ensure(NamePattern.IsMatch(r.Name ?? ""), 400, "Nome da máquina virtual: até 40 letras, números, espaço, _ ou -.");
        Controller.Ensure(r.Generation is 1 or 2, 400, "Escolha Legacy/BIOS (geração 1) ou UEFI (geração 2).");
        var status = await StatusAsync(cancel);
        Controller.Ensure(status.GetProperty("available").GetBoolean(), 409, status.TryGetProperty("reason", out var reason) ? reason.GetString()! : "Hyper-V indisponível.");
        var hostMemoryMiB = status.GetProperty("memoryBytes").GetInt64() / (1024 * 1024);
        var logical = status.GetProperty("logicalProcessors").GetInt32();
        Controller.Ensure(r.MemoryMiB >= 512 && r.MemoryMiB <= hostMemoryMiB - 2048 && r.MemoryMiB % 2 == 0, 400, $"Memória entre 512 MB e {hostMemoryMiB - 2048} MB (deixe 2 GB para o servidor), em número par.");
        Controller.Ensure(r.CpuCount >= 1 && r.CpuCount <= logical, 400, $"Processadores virtuais: de 1 a {logical}.");
        Controller.Ensure(r.CpuPercent is >= 1 and <= 100, 400, "Limite de processador: de 1% a 100%.");
        Controller.Ensure(r.Generation == 2 || (!r.SecureBoot && !r.Tpm), 400, "Inicialização segura e TPM só existem em UEFI (geração 2).");
        Controller.Ensure(!r.SecureBoot || r.SecureBootTemplate is "MicrosoftWindows" or "MicrosoftUEFICertificateAuthority", 400, "Modelo de inicialização segura: MicrosoftWindows (Windows) ou MicrosoftUEFICertificateAuthority (Linux).");
        if (!string.IsNullOrEmpty(r.IsoPath))
            Controller.Ensure(Path.IsPathFullyQualified(r.IsoPath) && r.IsoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(r.IsoPath), 400, "Arquivo ISO não encontrado.");
        if (!string.IsNullOrEmpty(r.SwitchName))
            Controller.Ensure(status.GetProperty("switches").EnumerateArray().Any(s => s.GetString() == r.SwitchName), 400, "Rede virtual (switch) não encontrada.");
        Controller.Ensure(!status.GetProperty("machines").EnumerateArray().Any(m => m.GetProperty("name").GetString()!.Equals(r.Name, StringComparison.OrdinalIgnoreCase)), 409, "Já existe uma máquina virtual com esse nome.");
        var warnings = new List<string>();
        string title; string challenge = $"CRIAR VM {r.Name!.ToUpperInvariant()}"; var typing = false;
        var disk = r.Disk ?? new VmDiskRequest("NONE");
        switch (disk.Kind)
        {
            case "NEW":
                var folder = SafeFolder(disk.Folder, stateDirectory);
                Controller.Ensure(disk.SizeGiB is >= 4 and <= 16384, 400, "Disco virtual entre 4 GB e 16 TB.");
                var path = Path.Combine(folder, r.Name + ".vhdx");
                Controller.Ensure(!File.Exists(path), 409, $"Já existe {path}.");
                var free = new DriveInfo(folder[..1]).AvailableFreeSpace;
                if (free < (long)disk.SizeGiB * 1024 * 1024 * 1024) warnings.Add($"O disco virtual cresce conforme o uso; a unidade tem {free / 1e9:N0} GB livres para até {disk.SizeGiB} GB.");
                r = r with { Disk = disk with { Folder = folder, Path = path } };
                title = $"Criar a VM {r.Name} com disco virtual de {disk.SizeGiB} GB em {folder}";
                break;
            case "EXISTING":
                Controller.Ensure(Path.IsPathFullyQualified(disk.Path ?? "") && (disk.Path!.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || disk.Path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)) && File.Exists(disk.Path), 400, "Disco virtual existente (.vhdx ou .vhd) não encontrado.");
                title = $"Criar a VM {r.Name} usando {disk.Path}";
                break;
            case "PHYSICAL":
            {
                var inventory = await disks.InventoryAsync(cancel);
                var d = inventory.Disks.SingleOrDefault(x => x.Number == disk.DiskNumber) ?? throw new ApiException(404, "Disco não encontrado; atualize a lista.");
                Controller.Ensure(Controller.SafeEquals(d.Fingerprint, disk.Fingerprint ?? ""), 409, "O disco mudou desde que a lista foi lida.");
                Controller.Ensure(d.Protections!.Length == 0, 409, $"Disco protegido ({string.Join(", ", d.Protections)}): não pode ir para a máquina virtual.");
                warnings.Add($"O disco {d.Number} ({d.Model}) fica offline no servidor e passa a pertencer à máquina virtual: o que for instalado nela fica nele.");
                warnings.Add(r.Generation == 2 ? "UEFI: o Windows/Linux será instalado em GPT e o disco iniciará em computadores UEFI." : "Legacy/BIOS: o sistema será instalado em MBR e o disco iniciará em computadores com BIOS/CSM.");
                title = $"Criar a VM {r.Name} usando o disco físico {d.Number} ({d.Model}, {d.SizeBytes / 1e9:N0} GB)";
                challenge = $"ENTREGAR DISCO {d.Number} A VM";
                typing = true;
                break;
            }
            case "NONE":
                title = $"Criar a VM {r.Name} sem disco";
                break;
            default:
                throw new ApiException(400, "Disco da VM: NEW, EXISTING, PHYSICAL ou NONE.");
        }
        warnings.Add($"Memória fixa de {r.MemoryMiB} MB e {r.CpuCount} processador(es) limitado(s) a {r.CpuPercent}% enquanto a VM estiver ligada.");
        var plan = new VmPlan(Wire.Id(), r, title, warnings.ToArray(), challenge, typing, DateTimeOffset.UtcNow.AddMinutes(15), "AWAITING_CONFIRMATION");
        lock (controller.Gate) { controller.Db.Put("vm-plan", plan.Id, plan); controller.Db.Log("local-admin", "vm.planned", $"{plan.Id} {title}"); }
        return plan;
    }

    public async Task<VmRecord> ConfirmAsync(string id, string challenge, CancellationToken cancel = default)
    {
        VmPlan plan;
        lock (controller.Gate) plan = controller.Db.Get<VmPlan>("vm-plan", id) ?? throw new ApiException(404, "Plano não encontrado.");
        Controller.Ensure(plan.State == "AWAITING_CONFIRMATION" && plan.ExpiresAtUtc > DateTimeOffset.UtcNow, 409, "Plano expirado ou já usado.");
        Controller.Ensure(Controller.SafeEquals(plan.Challenge, (challenge ?? "").Trim().ToUpperInvariant()), 400, "Confirmação digitada incorreta.");
        var r = plan.Request;
        if (r.Disk.Kind == "PHYSICAL")
        {
            var d = (await disks.InventoryAsync(cancel)).Disks.SingleOrDefault(x => x.Number == r.Disk.DiskNumber);
            Controller.Ensure(d != null && Controller.SafeEquals(d.Fingerprint, r.Disk.Fingerprint ?? "") && d.Protections!.Length == 0, 409, "O disco mudou ou ficou protegido; planeje de novo.");
        }
        lock (controller.Gate) controller.Db.Put("vm-plan", id, plan with { State = "USED" });
        var vmId = Wire.Id();
        var result = await scripts.RunAsync("SltHyperV.ps1", "create", new
        {
            id = vmId, name = r.Name, generation = r.Generation, memoryMiB = r.MemoryMiB, cpuCount = r.CpuCount, cpuPercent = r.CpuPercent,
            secureBoot = r.SecureBoot, secureBootTemplate = r.SecureBootTemplate ?? "", tpm = r.Tpm, isoPath = r.IsoPath ?? "", switchName = r.SwitchName ?? "",
            disk = new { kind = r.Disk.Kind, path = r.Disk.Path ?? "", sizeGiB = r.Disk.SizeGiB, number = r.Disk.DiskNumber ?? -1 }
        }, TimeSpan.FromMinutes(10), cancellationToken: cancel);
        var created = result.GetProperty("created").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var record = new VmRecord(vmId, r.Name, r, created, DateTimeOffset.UtcNow);
        lock (controller.Gate) { controller.Db.Put("vm", vmId, record); controller.Db.Log("local-admin", "vm.created", $"{vmId} {r.Name}"); }
        return record;
    }

    private static string CheckName(string name) { Controller.Ensure(NamePattern.IsMatch(name ?? ""), 400, "Nome inválido."); return name!; }

    public async Task<JsonElement> ActionAsync(string name, string action, VmNameRequest? body, CancellationToken cancel = default)
    {
        CheckName(name);
        switch (action)
        {
            case "start" or "stop" or "turn-off":
                lock (controller.Gate) controller.Db.Log("local-admin", "vm." + action, name);
                return await scripts.RunAsync("SltHyperV.ps1", action, new { name }, TimeSpan.FromMinutes(5), cancellationToken: cancel);
            case "set-iso":
                var iso = body?.IsoPath ?? "";
                Controller.Ensure(iso == "" || (Path.IsPathFullyQualified(iso) && iso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(iso)), 400, "Arquivo ISO não encontrado.");
                return await scripts.RunAsync("SltHyperV.ps1", "set-iso", new { name, isoPath = iso }, TimeSpan.FromMinutes(2), cancellationToken: cancel);
            case "delete":
                string[] files;
                lock (controller.Gate) files = body?.DeleteFiles == true ? controller.Db.All<VmRecord>("vm").Where(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(v => v.CreatedFiles).ToArray() : [];
                var result = await scripts.RunAsync("SltHyperV.ps1", "delete", new { name, deletePaths = files, onlineDisks = body?.OnlineDisks == true }, TimeSpan.FromMinutes(5), cancellationToken: cancel);
                lock (controller.Gate)
                {
                    foreach (var v in controller.Db.All<VmRecord>("vm").Where(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) controller.Db.Put("vm", v.Id, v with { Name = v.Name + " (excluída)" });
                    controller.Db.Log("local-admin", "vm.deleted", $"{name} arquivos={files.Length} discosOnline={body?.OnlineDisks == true}");
                }
                return result;
        }
        throw new ApiException(400, "Ação inválida.");
    }
}
