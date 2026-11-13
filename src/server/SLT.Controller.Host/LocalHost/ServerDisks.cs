using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slt.Controller.LocalHost;

public sealed record ServerPartition(int Number, long OffsetBytes, long SizeBytes, string Type, string GptType, bool IsActive, bool IsBoot, bool IsSystem,
    string DriveLetter, string FileSystem, string Label, long FreeBytes, bool HasWindows, string[] Protections);
public sealed record ServerDisk(int Number, string Model, string Serial, long SizeBytes, int LogicalSectorBytes, int PhysicalSectorBytes, string BusType,
    string PartitionStyle, bool IsSystem, bool IsBoot, bool IsOffline, bool IsReadOnly, string UniqueId, string Health, string MediaType, string Location,
    ServerPartition[] Partitions, string Fingerprint = "", string[]? Protections = null, bool IsVirtual = false);
public sealed record ServerDiskInventory(ServerDisk[] Disks, string SystemDrive, string[] PagefileDrives, bool VirtualOnly, DateTimeOffset CapturedAtUtc);
public sealed record ServerDiskRequest(string Kind, int DiskNumber, string Fingerprint, int? PartitionNumber = null, int? TargetDiskNumber = null,
    string? TargetFingerprint = null, string? TableType = null, string? FileSystem = null, string? Label = null, string? Mode = null, long? SizeBytes = null, bool ValidateOnly = false);
public sealed record ServerDiskOperation(string Id, string Kind, ServerDiskRequest Request, string Title, string[] Warnings, string Challenge, bool RequiresTyping,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, string State, string Signature, long BytesDone = 0, long BytesTotal = 0, string Log = "",
    string? Message = null, DateTimeOffset? FinishedAtUtc = null, string? ResultJson = null);
public sealed record ConfirmRequest(string Challenge);

// Disks of the server itself, managed from the panel. Same rules as the remote jobs: typed
// operations only, a plan signed by the server key with a short validity, a confirmation typed by
// the operator, and the disk identity re-read right before the write. Disks that hold the
// running Windows, the page file or SLT's own data are never written from here (kamikaze mode).
public sealed class ServerDiskManager
{
    public static readonly string[] Kinds = ["FORMAT_DISK", "CONVERT_TABLE", "FORMAT_VOLUME", "SET_OFFLINE", "SET_ONLINE", "RESIZE_PARTITION", "CHECK_DISK", "REPAIR_VOLUME", "MBR2GPT", "BOOT_REPAIR", "SYSTEM_REPAIR", "CLONE_DISK"];
    private const string EfiType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", MsrType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}", RecoveryType = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    private static readonly Regex LabelPattern = new("^[A-Za-z0-9 _-]{0,32}$");
    private readonly Controller controller;
    private readonly HostScripts scripts;
    private readonly string[] sltFolders;
    private readonly Dictionary<string, CancellationTokenSource> running = [];
    private readonly HashSet<int> busy = [];
    public bool VirtualOnly { get; }
    public string? SmartctlPath { get; init; }
    // Raw device path; tests point it at files that stand in for disks.
    public Func<int, string> DevicePath { get; init; } = n => $@"\\.\PhysicalDrive{n}";

    public ServerDiskManager(Controller controller, HostScripts scripts, string stateDirectory, bool virtualOnly)
    {
        this.controller = controller; this.scripts = scripts; VirtualOnly = virtualOnly;
        sltFolders = [stateDirectory, controller.Repository, AppContext.BaseDirectory];
        lock (controller.Gate)
            foreach (var op in controller.Db.All<ServerDiskOperation>("server-disk-op").Where(o => o.State == "RUNNING"))
            {
                controller.Db.Put("server-disk-op", op.Id, op with { State = "INTERRUPTED", Message = "Servidor reiniciado durante a operação; confira o disco antes de repetir.", FinishedAtUtc = DateTimeOffset.UtcNow });
                controller.Db.Log("system", "server-disk.interrupted", op.Id);
            }
    }

    public async Task<ServerDiskInventory> InventoryAsync(CancellationToken cancel = default)
    {
        var json = await scripts.RunAsync("SltLocalDisks.ps1", "inventory", new { }, TimeSpan.FromMinutes(2), cancellationToken: cancel);
        var raw = json.GetProperty("disks").Deserialize<ServerDisk[]>(Wire.Json) ?? [];
        var systemDrive = json.GetProperty("systemDrive").GetString() ?? "C";
        var pagefile = json.TryGetProperty("pagefileDrives", out var p) ? p.Deserialize<string[]>(Wire.Json) ?? [] : [];
        return Classify(raw, systemDrive, pagefile);
    }

    public ServerDiskInventory Classify(ServerDisk[] raw, string systemDrive, string[] pagefile)
    {
        var sltLetters = sltFolders.Select(f => Path.GetPathRoot(Path.GetFullPath(f))?.TrimEnd('\\', ':').ToUpperInvariant() ?? "").Where(l => l.Length == 1).ToHashSet();
        var disks = raw.Select(d =>
        {
            var isVirtual = d.BusType.Equals("File Backed Virtual", StringComparison.OrdinalIgnoreCase);
            var parts = d.Partitions.Select(part =>
            {
                var reasons = new List<string>();
                var letter = part.DriveLetter.ToUpperInvariant();
                if (letter.Length == 1 && letter == systemDrive.ToUpperInvariant()) reasons.Add("Windows do servidor");
                if (part.IsBoot || part.IsSystem) reasons.Add("Inicialização do servidor");
                if (letter.Length == 1 && pagefile.Contains(letter, StringComparer.OrdinalIgnoreCase)) reasons.Add("Arquivo de paginação");
                if (letter.Length == 1 && sltLetters.Contains(letter)) reasons.Add("Dados do SLT Disk Manager");
                var type = part.GptType.ToLowerInvariant();
                if (type == EfiType || part.Type.Equals("System", StringComparison.OrdinalIgnoreCase)) reasons.Add("Partição EFI");
                if (type == MsrType || part.Type.Equals("Reserved", StringComparison.OrdinalIgnoreCase)) reasons.Add("Partição reservada (MSR)");
                if (type == RecoveryType || part.Type.Equals("Recovery", StringComparison.OrdinalIgnoreCase)) reasons.Add("Recuperação do Windows");
                return part with { Protections = reasons.Distinct().ToArray() };
            }).ToArray();
            var protections = new List<string>();
            if (d.IsSystem || d.IsBoot) protections.Add("Disco do sistema do servidor");
            foreach (var reason in parts.SelectMany(x => x.Protections).Where(r => r is "Windows do servidor" or "Arquivo de paginação" or "Dados do SLT Disk Manager")) protections.Add(reason);
            if (VirtualOnly && !isVirtual) protections.Add("Modo de teste: só discos virtuais");
            var disk = d with { Partitions = parts, Protections = protections.Distinct().ToArray(), IsVirtual = isVirtual };
            return disk with { Fingerprint = Fingerprint(disk) };
        }).ToArray();
        return new(disks, systemDrive, pagefile, VirtualOnly, DateTimeOffset.UtcNow);
    }

    // The number is part of the identity: a disk that was renumbered must be planned again.
    public static string Fingerprint(ServerDisk d) => Wire.Hash($"{d.Number}|{d.Model}|{d.Serial}|{d.SizeBytes}|{d.LogicalSectorBytes}|{d.UniqueId}");

    public ServerDiskOperation[] List() { lock (controller.Gate) return controller.Db.All<ServerDiskOperation>("server-disk-op").OrderByDescending(o => o.CreatedAtUtc).Take(50).ToArray(); }
    public ServerDiskOperation Get(string id) { lock (controller.Gate) return controller.Db.Get<ServerDiskOperation>("server-disk-op", id) ?? throw new ApiException(404, "Operação não encontrada."); }
    private void Save(ServerDiskOperation op) { lock (controller.Gate) controller.Db.Put("server-disk-op", op.Id, op); }

    public async Task<ServerDiskOperation> PlanAsync(ServerDiskRequest request, CancellationToken cancel = default)
    {
        Controller.Ensure(Kinds.Contains(request.Kind), 400, "Operação de disco não suportada.");
        var inventory = await InventoryAsync(cancel);
        var (title, warnings, challenge, typing) = Validate(request, inventory);
        var id = Wire.Id();
        var op = new ServerDiskOperation(id, request.Kind, request, title, warnings, challenge, typing, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15), "AWAITING_CONFIRMATION", "");
        op = op with { Signature = Sign(op) };
        Save(op);
        lock (controller.Gate) controller.Db.Log("local-admin", "server-disk.planned", $"{id} {request.Kind} disco={request.DiskNumber} {title}");
        return op;
    }

    private string Canonical(ServerDiskOperation op) => Wire.Serialize(new { op.Id, op.Kind, op.Request, op.Challenge, op.ExpiresAtUtc });
    private string Sign(ServerDiskOperation op)
    {
        using var key = controller.Certificates.Server.GetRSAPrivateKey()!;
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(Canonical(op)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private bool Verify(ServerDiskOperation op)
    {
        using var key = controller.Certificates.Server.GetRSAPublicKey()!;
        try { return key.VerifyData(Encoding.UTF8.GetBytes(Canonical(op)), Convert.FromBase64String(op.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); }
        catch (FormatException) { return false; }
    }

    private static string Gb(long bytes) => $"{Math.Round(bytes / 1e9):0}GB";

    // Shared by planning and by the re-check right before running: same request, fresh inventory.
    public (string Title, string[] Warnings, string Challenge, bool Typing) Validate(ServerDiskRequest r, ServerDiskInventory inventory)
    {
        var warnings = new List<string>();
        ServerDisk Disk(int number, string fingerprint)
        {
            var d = inventory.Disks.SingleOrDefault(x => x.Number == number) ?? throw new ApiException(404, $"Disco {number} não encontrado; atualize a lista.");
            Controller.Ensure(Controller.SafeEquals(d.Fingerprint, fingerprint ?? ""), 409, $"O disco {number} mudou desde que a lista foi lida; atualize e escolha de novo.");
            return d;
        }
        void Writable(ServerDisk d)
        {
            Controller.Ensure(d.Protections!.Length == 0, 409, $"Disco {d.Number} protegido ({string.Join(", ", d.Protections)}). Para gravar no disco do próprio servidor use o modo kamikaze.");
            Controller.Ensure(!VirtualOnly || d.IsVirtual, 409, "Modo de teste: só discos virtuais podem ser gravados.");
        }
        ServerPartition Part(ServerDisk d)
        {
            Controller.Ensure(r.PartitionNumber is > 0, 400, "Escolha uma partição.");
            return d.Partitions.SingleOrDefault(p => p.Number == r.PartitionNumber) ?? throw new ApiException(404, "Partição não encontrada; atualize a lista.");
        }
        string Volume(ServerPartition p) => p.DriveLetter.Length == 1 ? $"{p.DriveLetter}:" : $"partição {p.Number}";
        void FileSystem(long size)
        {
            Controller.Ensure(r.FileSystem is "NTFS" or "exFAT" or "FAT32", 400, "Sistema de arquivos: NTFS, exFAT ou FAT32.");
            Controller.Ensure(LabelPattern.IsMatch(r.Label ?? "") && (r.FileSystem == "NTFS" || (r.Label ?? "").Length <= 11), 400, "Rótulo inválido (letras, números, espaço, _ e -; até 11 caracteres em FAT/exFAT).");
            Controller.Ensure(r.FileSystem != "FAT32" || size <= 32L * 1024 * 1024 * 1024, 400, "O Windows só formata FAT32 até 32 GB: use exFAT ou NTFS.");
        }
        var disk = r.Kind == "SYSTEM_REPAIR" && r.Mode == "ONLINE" ? null : Disk(r.DiskNumber, r.Fingerprint);
        switch (r.Kind)
        {
            case "FORMAT_DISK":
                Writable(disk!); FileSystem(disk!.SizeBytes);
                Controller.Ensure(r.TableType is "GPT" or "MBR", 400, "Tabela: GPT ou MBR.");
                Controller.Ensure(r.TableType == "GPT" || disk.SizeBytes <= 2L * 1024 * 1024 * 1024 * 1024, 400, "MBR só endereça 2 TiB; use GPT.");
                return ($"Formatar o disco {disk.Number} ({disk.Model}, {Gb(disk.SizeBytes)}) em {r.TableType}/{r.FileSystem}", ["Todas as partições e dados do disco serão apagados."], $"APAGAR DISCO {disk.Number} {Gb(disk.SizeBytes)}", true);
            case "CONVERT_TABLE":
                Writable(disk!);
                Controller.Ensure(r.TableType is "GPT" or "MBR" && !disk!.PartitionStyle.Equals(r.TableType, StringComparison.OrdinalIgnoreCase), 400, "Escolha a outra tabela (GPT ou MBR).");
                Controller.Ensure(r.TableType == "GPT" || disk!.SizeBytes <= 2L * 1024 * 1024 * 1024 * 1024, 400, "MBR só endereça 2 TiB.");
                warnings.Add("O Windows só converte discos vazios: todas as partições serão apagadas. Para converter um disco com Windows sem perder dados use MBR → GPT (MBR2GPT).");
                return ($"Converter o disco {disk!.Number} de {disk.PartitionStyle} para {r.TableType}", warnings.ToArray(), $"APAGAR DISCO {disk.Number} {Gb(disk.SizeBytes)}", true);
            case "FORMAT_VOLUME":
            {
                Controller.Ensure(!VirtualOnly || disk!.IsVirtual, 409, "Modo de teste: só discos virtuais.");
                var part = Part(disk!);
                Controller.Ensure(part.Protections.Length == 0, 409, $"Partição protegida ({string.Join(", ", part.Protections)}).");
                FileSystem(part.SizeBytes);
                return ($"Formatar {Volume(part)} do disco {disk!.Number} em {r.FileSystem}", ["Os arquivos desta partição serão apagados."], $"APAGAR VOLUME {(part.DriveLetter.Length == 1 ? part.DriveLetter : $"{disk.Number}-{part.Number}")}", true);
            }
            case "SET_OFFLINE" or "SET_ONLINE":
                Writable(disk!);
                return (r.Kind == "SET_OFFLINE" ? $"Deixar o disco {disk!.Number} offline (o Windows deixa de usá-lo)" : $"Colocar o disco {disk!.Number} online", [], $"{(r.Kind == "SET_OFFLINE" ? "DESLIGAR" : "LIGAR")} DISCO {disk.Number}", false);
            case "RESIZE_PARTITION":
            {
                Controller.Ensure(!VirtualOnly || disk!.IsVirtual, 409, "Modo de teste: só discos virtuais.");
                var part = Part(disk!);
                Controller.Ensure(!part.Protections.Any(p => p.StartsWith("Partição") || p.StartsWith("Recuperação")), 409, "Partições EFI, MSR e de recuperação não são redimensionadas aqui.");
                Controller.Ensure(r.SizeBytes is >= 0, 400, "Tamanho inválido (0 = máximo).");
                if (part.Protections.Length > 0) warnings.Add("Partição em uso pelo servidor: o Windows redimensiona sem desmontar, mas tenha backup.");
                return ($"Redimensionar {Volume(part)} do disco {disk!.Number} para {(r.SizeBytes == 0 ? "o máximo possível" : Gb(r.SizeBytes!.Value))}", warnings.ToArray(), $"REDIMENSIONAR {disk.Number}-{part.Number}", true);
            }
            case "CHECK_DISK":
                return ($"Verificar o disco {disk!.Number} ({disk.Model}) sem alterar nada", [], $"VERIFICAR DISCO {disk.Number}", false);
            case "REPAIR_VOLUME":
            {
                Controller.Ensure(!VirtualOnly || disk!.IsVirtual, 409, "Modo de teste: só discos virtuais.");
                var part = Part(disk!);
                Controller.Ensure(!part.Protections.Contains("Windows do servidor") && !part.Protections.Contains("Arquivo de paginação") && !part.Protections.Contains("Dados do SLT Disk Manager"), 409, "Volume em uso pelo servidor: use Reparar Windows (SFC/DISM) ou o modo kamikaze.");
                Controller.Ensure(part.FileSystem is "NTFS" or "ReFS" or "FAT32" or "FAT" or "exFAT", 409, "Sistema de arquivos sem correção disponível.");
                return ($"Corrigir o sistema de arquivos de {Volume(part)} (disco {disk!.Number})", ["O volume fica indisponível durante a correção."], $"CORRIGIR VOLUME {(part.DriveLetter.Length == 1 ? part.DriveLetter : $"{disk.Number}-{part.Number}")}", true);
            }
            case "MBR2GPT":
                Controller.Ensure(!VirtualOnly || disk!.IsVirtual, 409, "Modo de teste: só discos virtuais.");
                Controller.Ensure(disk!.PartitionStyle.Equals("MBR", StringComparison.OrdinalIgnoreCase), 409, "O disco já não é MBR.");
                if (disk.IsSystem || disk.IsBoot) warnings.Add("Este é o disco do servidor: depois de converter, mude o firmware do servidor para UEFI ou ele não iniciará.");
                warnings.Add("Conversão sem apagar dados (MBR2GPT da Microsoft); exige Windows instalado no disco. Tenha backup.");
                return (r.ValidateOnly ? $"Validar se o disco {disk.Number} pode ir para GPT" : $"Converter o disco {disk.Number} de MBR para GPT sem apagar dados", warnings.ToArray(), r.ValidateOnly ? $"VALIDAR DISCO {disk.Number}" : $"CONVERTER DISCO {disk.Number} PARA GPT", !r.ValidateOnly);
            case "BOOT_REPAIR":
            {
                Controller.Ensure(!VirtualOnly || disk!.IsVirtual, 409, "Modo de teste: só discos virtuais.");
                var part = Part(disk!);
                Controller.Ensure(part.HasWindows || part.DriveLetter.Length == 0, 409, "Escolha a partição onde o Windows está instalado.");
                if (disk!.IsSystem || disk.IsBoot) warnings.Add("Disco do próprio servidor: os arquivos de inicialização dele serão recriados.");
                warnings.Add("Recria os arquivos de inicialização com BCDBoot (UEFI em GPT, BIOS em MBR).");
                return ($"Reparar a inicialização do Windows da partição {part.Number} do disco {disk.Number}", warnings.ToArray(), $"REPARAR BOOT DISCO {disk.Number}", true);
            }
            case "SYSTEM_REPAIR":
                if (r.Mode == "ONLINE") return ("Reparar o Windows do servidor (DISM RestoreHealth e SFC)", ["Pode levar de 15 minutos a mais de uma hora. O DISM usa o Windows Update."], "REPARAR WINDOWS", false);
                Controller.Ensure(r.Mode == "OFFLINE", 400, "Modo: ONLINE (este Windows) ou OFFLINE (Windows em outro disco).");
                {
                    Controller.Ensure(!VirtualOnly || disk!.IsVirtual, 409, "Modo de teste: só discos virtuais.");
                    var part = Part(disk!);
                    Controller.Ensure(!part.Protections.Contains("Windows do servidor"), 409, "Para o Windows do servidor use o modo ONLINE.");
                    return ($"Reparar o Windows da partição {part.Number} do disco {disk!.Number} (DISM e SFC offline)", ["Sem fonte de reparo o DISM offline pode não corrigir tudo; o SFC usa os arquivos do próprio disco."], $"REPARAR WINDOWS DISCO {disk.Number}", true);
                }
            case "CLONE_DISK":
            {
                Controller.Ensure(r.TargetDiskNumber is >= 0 && r.TargetDiskNumber != disk!.Number, 400, "Escolha um disco de destino diferente da origem.");
                var target = Disk(r.TargetDiskNumber!.Value, r.TargetFingerprint ?? "");
                Writable(target);
                Controller.Ensure(disk!.Protections!.Length == 0, 409, $"A origem está em uso pelo servidor ({string.Join(", ", disk.Protections)}). Para clonar o disco do servidor use o modo kamikaze.");
                Controller.Ensure(!VirtualOnly || disk.IsVirtual, 409, "Modo de teste: só discos virtuais.");
                Controller.Ensure(disk.LogicalSectorBytes == target.LogicalSectorBytes, 409, "Origem e destino têm tamanhos de setor diferentes (512 e 4096): cópia bruta não serve.");
                if (target.SizeBytes < disk.SizeBytes) warnings.Add("Destino menor: só é possível se todas as partições da origem couberem nele (confirmado antes de gravar).");
                warnings.Add("Os dois discos ficam offline durante a cópia. O destino continua offline depois (é uma cópia idêntica e o Windows não usa duas iguais ao mesmo tempo): leve-o para o cliente.");
                return ($"Clonar o disco {disk.Number} ({disk.Model}, {Gb(disk.SizeBytes)}) para o disco {target.Number} ({target.Model}, {Gb(target.SizeBytes)})", warnings.ToArray(), $"APAGAR DISCO {target.Number} {Gb(target.SizeBytes)}", true);
            }
        }
        throw new ApiException(400, "Operação não suportada.");
    }

    public async Task<ServerDiskOperation> ConfirmAsync(string id, string challenge, CancellationToken cancel = default)
    {
        var op = Get(id);
        Controller.Ensure(op.State == "AWAITING_CONFIRMATION", 409, "Operação já confirmada ou encerrada.");
        Controller.Ensure(Verify(op), 409, "Assinatura do plano inválida; planeje de novo.");
        if (op.ExpiresAtUtc <= DateTimeOffset.UtcNow) { Save(op with { State = "EXPIRED" }); throw new ApiException(409, "Plano expirado; planeje de novo."); }
        Controller.Ensure(Controller.SafeEquals(op.Challenge, (challenge ?? "").Trim().ToUpperInvariant()), 400, "Confirmação digitada incorreta.");
        // Fresh inventory: the disk must still be the same and still writable.
        Validate(op.Request, await InventoryAsync(cancel));
        var disks = new[] { op.Request.DiskNumber, op.Request.TargetDiskNumber ?? -1 }.Where(n => n >= 0 && !(op.Kind == "SYSTEM_REPAIR" && op.Request.Mode == "ONLINE")).ToArray();
        var source = new CancellationTokenSource();
        lock (running)
        {
            Controller.Ensure(!disks.Any(busy.Contains), 409, "Já há uma operação em andamento neste disco.");
            foreach (var n in disks) busy.Add(n);
            running[id] = source;
        }
        op = op with { State = "RUNNING" };
        Save(op);
        lock (controller.Gate) controller.Db.Log("local-admin", "server-disk.confirmed", $"{id} {op.Kind} {op.Title}");
        _ = Task.Run(() => RunAsync(op, disks, source.Token));
        return op;
    }

    public ServerDiskOperation Cancel(string id)
    {
        var op = Get(id);
        if (op.State == "AWAITING_CONFIRMATION") { op = op with { State = "CANCELLED", FinishedAtUtc = DateTimeOffset.UtcNow }; Save(op); return op; }
        Controller.Ensure(op.State == "RUNNING" && op.Kind == "CLONE_DISK", 409, "Só o clone em andamento pode ser interrompido (formatar/converter não são interrompidos no meio).");
        lock (running) if (running.TryGetValue(id, out var source)) source.Cancel();
        return op;
    }

    private async Task RunAsync(ServerDiskOperation op, int[] disks, CancellationToken cancel)
    {
        var log = new StringBuilder();
        var lastSave = DateTime.MinValue;
        void Line(string text)
        {
            lock (log) { log.AppendLine(text); if (log.Length > 64 * 1024) log.Remove(0, log.Length - 48 * 1024); }
            if ((DateTime.UtcNow - lastSave).TotalSeconds >= 1) { lastSave = DateTime.UtcNow; lock (log) Save(Get(op.Id) with { Log = log.ToString() }); }
        }
        try
        {
            var r = op.Request;
            var timeout = TimeSpan.FromMinutes(30);
            JsonElement? result = op.Kind switch
            {
                "FORMAT_DISK" => await scripts.RunAsync("SltLocalDisks.ps1", "format-disk", new { number = r.DiskNumber, tableType = r.TableType, fileSystem = r.FileSystem, label = r.Label ?? "" }, timeout, Line, cancel),
                "CONVERT_TABLE" => await scripts.RunAsync("SltLocalDisks.ps1", "convert-table", new { number = r.DiskNumber, tableType = r.TableType }, timeout, Line, cancel),
                "FORMAT_VOLUME" => await scripts.RunAsync("SltLocalDisks.ps1", "format-volume", new { number = r.DiskNumber, partition = r.PartitionNumber, fileSystem = r.FileSystem, label = r.Label ?? "" }, timeout, Line, cancel),
                "SET_OFFLINE" or "SET_ONLINE" => await scripts.RunAsync("SltLocalDisks.ps1", "set-offline", new { number = r.DiskNumber, offline = op.Kind == "SET_OFFLINE" }, timeout, Line, cancel),
                "RESIZE_PARTITION" => await scripts.RunAsync("SltLocalDisks.ps1", "resize-partition", new { number = r.DiskNumber, partition = r.PartitionNumber, sizeBytes = r.SizeBytes ?? 0 }, timeout, Line, cancel),
                "CHECK_DISK" => await scripts.RunAsync("SltLocalDisks.ps1", "check-disk", new { number = r.DiskNumber, smartctl = SmartctlPath ?? "" }, TimeSpan.FromHours(4), Line, cancel),
                "REPAIR_VOLUME" => await scripts.RunAsync("SltLocalDisks.ps1", "repair-volume", new { number = r.DiskNumber, partition = r.PartitionNumber }, TimeSpan.FromHours(8), Line, cancel),
                "MBR2GPT" => await scripts.RunAsync("SltLocalDisks.ps1", "mbr2gpt", new { number = r.DiskNumber, validateOnly = r.ValidateOnly }, timeout, Line, cancel),
                "BOOT_REPAIR" => await scripts.RunAsync("SltLocalDisks.ps1", "boot-repair", new { number = r.DiskNumber, partition = r.PartitionNumber }, timeout, Line, cancel),
                "SYSTEM_REPAIR" => await scripts.RunAsync("SltLocalDisks.ps1", "system-repair", new { mode = r.Mode, number = r.DiskNumber, partition = r.PartitionNumber ?? 0 }, TimeSpan.FromHours(4), Line, cancel),
                "CLONE_DISK" => await CloneAsync(op, Line, cancel),
                _ => throw new ApiException(400, "Operação não suportada.")
            };
            Finish(op.Id, "COMPLETED", "Concluída.", log, result?.GetRawText());
        }
        catch (Exception e)
        {
            Finish(op.Id, cancel.IsCancellationRequested ? "CANCELLED" : "FAILED", e is OperationCanceledException ? "Interrompida pelo operador; o destino ficou incompleto." : e.Message, log, null);
        }
        finally
        {
            lock (running) { foreach (var n in disks) busy.Remove(n); running.Remove(op.Id); }
        }
    }

    private void Finish(string id, string state, string message, StringBuilder log, string? result)
    {
        lock (log) Save(Get(id) with { State = state, Message = message, Log = log.ToString(), FinishedAtUtc = DateTimeOffset.UtcNow, ResultJson = result });
        lock (controller.Gate) controller.Db.Log("system", $"server-disk.{state.ToLowerInvariant()}", $"{id} {message}");
    }

    private async Task<JsonElement?> CloneAsync(ServerDiskOperation op, Action<string> line, CancellationToken cancel)
    {
        var inventory = await InventoryAsync(cancel);
        var source = inventory.Disks.Single(d => d.Number == op.Request.DiskNumber);
        var target = inventory.Disks.Single(d => d.Number == op.Request.TargetDiskNumber);
        long length;
        using (var probe = RawDisk.Open(DevicePath(source.Number), false)) length = RawDisk.CloneLength(probe, source.SizeBytes, target.SizeBytes, source.LogicalSectorBytes);
        line($"Copiando {length / 1e9:N2} GB do disco {source.Number} para o disco {target.Number}.");
        var sourceWasOffline = source.IsOffline;
        foreach (var n in new[] { source.Number, target.Number })
            await scripts.RunAsync("SltLocalDisks.ps1", "set-offline", new { number = n, offline = true }, TimeSpan.FromMinutes(2), line, cancel);
        var relocated = false;
        try
        {
            using var src = RawDisk.Open(DevicePath(source.Number), false);
            using var dst = RawDisk.Open(DevicePath(target.Number), true);
            var started = DateTime.UtcNow; var reported = DateTime.MinValue;
            Save(Get(op.Id) with { BytesTotal = length });
            RawDisk.CopyAndVerify(src, dst, length, done =>
            {
                if ((DateTime.UtcNow - reported).TotalSeconds < 2) return;
                reported = DateTime.UtcNow;
                Save(Get(op.Id) with { BytesDone = done, BytesTotal = length });
            }, cancel);
            Save(Get(op.Id) with { BytesDone = length, BytesTotal = length });
            line($"Cópia conferida por SHA256 em {(DateTime.UtcNow - started).TotalMinutes:N1} min.");
            if (length != source.SizeBytes || target.SizeBytes != source.SizeBytes)
            {
                relocated = RawDisk.RelocateGpt(dst, target.SizeBytes, target.LogicalSectorBytes);
                if (relocated) line("GPT de reserva movida para o fim do destino e conferida.");
            }
        }
        finally
        {
            if (!sourceWasOffline)
                try { await scripts.RunAsync("SltLocalDisks.ps1", "set-offline", new { number = source.Number, offline = false }, TimeSpan.FromMinutes(2), line, CancellationToken.None); }
                catch (Exception e) { line($"Aviso: a origem não voltou online sozinha ({e.Message}); use Colocar online."); }
        }
        line("O destino continua offline. Leve-o para o cliente ou coloque-o online depois de desconectar a origem.");
        using var document = JsonDocument.Parse(Wire.Serialize(new { bytesCopied = length, verified = true, gptRelocated = relocated }));
        return document.RootElement.Clone();
    }
}
