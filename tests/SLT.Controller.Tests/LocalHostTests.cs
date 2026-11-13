using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Slt.Controller;
using Slt.Controller.LocalHost;

// Server-machine features (disks of the server, Hyper-V, kamikaze) with a fake command runner and
// files that stand in for disks. No real disk, VM or boot entry is touched here.
internal static class LocalHostTests
{
    private sealed class FakeRunner : IHostCommandRunner
    {
        public readonly List<(string Exe, string[] Args, string? Stdin)> Calls = [];
        public Func<string, string[], string?, string> Handler = (_, _, _) => "";
        public Task<CommandOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout, Action<string>? onLine, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add((executable, arguments.ToArray(), standardInput));
            var output = Handler(executable, arguments.ToArray(), standardInput);
            foreach (var line in output.Split('\n')) onLine?.Invoke(line.TrimEnd('\r'));
            return Task.FromResult(new CommandOutput(0, output));
        }
        public string Operation(string[] args) => args.SkipWhile(a => a != "-Operation").Skip(1).FirstOrDefault() ?? "";
        public string[] Operations() { lock (Calls) return Calls.Select(c => Operation(c.Args)).Where(o => o.Length > 0).ToArray(); }
    }

    private static string Result(object value) => "log\nSLT-RESULT:" + JsonSerializer.Serialize(value, Wire.Json);

    // Same layout the kamikaze Go test uses: protective MBR, both GPT copies, one partition.
    public static void WriteGpt(string path, long total, ulong start, ulong end)
    {
        const int sector = 512;
        using var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        f.SetLength(total);
        var last = (ulong)(total / sector - 1);
        var mbr = new byte[512];
        mbr[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12), (uint)last);
        mbr[510] = 0x55; mbr[511] = 0xAA;
        var entries = new byte[128 * 128];
        entries.AsSpan(0, 16).Fill(0xAB); entries.AsSpan(16, 16).Fill(0xCD);
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(32), start);
        BinaryPrimitives.WriteUInt64LittleEndian(entries.AsSpan(40), end);
        var crc = Crc(entries);
        byte[] Header(ulong current, ulong alternate, ulong array)
        {
            var h = new byte[sector];
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), current);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), alternate);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(48), last - 33);
            h.AsSpan(56, 16).Fill(0x11);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(72), array);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc(h.AsSpan(0, 92)));
            return h;
        }
        void At(long offset, byte[] data) { f.Position = offset; f.Write(data); }
        At(0, mbr); At(2 * sector, entries); At(sector, Header(1, last, 2));
        At((long)(last - 32) * sector, entries); At((long)last * sector, Header(last, 1, last - 32));
        var data = RandomNumberGenerator.GetBytes((int)((end - start + 1) * sector));
        At((long)start * sector, data);
    }
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
        return ~crc;
    }

    private static object Disk(int number, string model, long size, string style, bool system, string busType, params object[] partitions) => new
    {
        number, model, serial = "SN" + number, sizeBytes = size, logicalSectorBytes = 512, physicalSectorBytes = 4096, busType, partitionStyle = style,
        isSystem = system, isBoot = system, isOffline = false, isReadOnly = false, uniqueId = "UID" + number, health = "Healthy", mediaType = "SSD", location = "", partitions
    };
    private static object Part(int number, string letter, string fs, string type = "Basic", string gptType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}", bool windows = false, long size = 1_000_000_000) => new
    {
        number, offsetBytes = 1048576L * number, sizeBytes = size, type, gptType, isActive = false, isBoot = false, isSystem = false, driveLetter = letter, fileSystem = fs, label = "", freeBytes = 0L, hasWindows = windows
    };

    public static void Run(Action<bool, string> check)
    {
        void Reject(Func<Task> action, string name)
        {
            try { action().GetAwaiter().GetResult(); } catch (ApiException) { check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "slt-localhost-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            RawDiskTests(root, check, Reject);
            Directory.CreateDirectory(Path.Combine(root, "state"));
            var certs = new Certificates(Path.Combine(root, "state"), IPAddress.Loopback);
            using var db = new Store(Path.Combine(root, "state"));
            var repository = Path.Combine(root, "repo");
            var controller = new Controller(db, certs, repository);
            var runner = new FakeRunner();
            var repoLetter = Path.GetPathRoot(repository)!.TrimEnd('\\', ':');
            var diskFiles = new Dictionary<int, string> { [2] = Path.Combine(root, "disk2.img"), [3] = Path.Combine(root, "disk3.img"), [4] = Path.Combine(root, "disk4.img") };
            const long mib = 1024 * 1024;
            WriteGpt(diskFiles[2], 8 * mib, 2048, 12000);
            File.WriteAllBytes(diskFiles[3], Enumerable.Repeat((byte)0x5A, (int)(16 * mib)).ToArray());
            File.WriteAllBytes(diskFiles[4], new byte[4 * mib]);
            var inventoryDisks = new List<object>
            {
                Disk(0, "SERVER SSD", 240_000_000_000, "GPT", true, "SATA", Part(1, "", "FAT32", "System", "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"), Part(2, "", "", "Reserved", "{e3c9e316-0b5c-4db8-817d-f92df00215ae}"), Part(3, repoLetter, "NTFS", windows: true), Part(4, "", "NTFS", "Recovery", "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}")),
                Disk(1, "DATA HDD", 3_000_000_000_000, "GPT", false, "SATA", Part(1, "P", "NTFS")),
                Disk(2, "SYNTHETIC SOURCE", 8 * mib, "GPT", false, "File Backed Virtual", Part(1, "S", "NTFS")),
                Disk(3, "SYNTHETIC TARGET", 16 * mib, "GPT", false, "File Backed Virtual", Part(1, "T", "NTFS")),
                Disk(4, "SYNTHETIC SMALL", 4 * mib, "MBR", false, "File Backed Virtual"),
                Disk(5, "PENDRIVE", 64_000_000_000, "MBR", false, "USB", Part(1, "U", "FAT32", windows: false))
            };
            runner.Handler = (exe, args, stdin) => runner.Operation(args) switch
            {
                "inventory" => Result(new { disks = inventoryDisks, pagefileDrives = new[] { "P" }, systemDrive = repoLetter }),
                "set-offline" => Result(new { isOffline = true }),
                "status" => Result(new { available = true, logicalProcessors = 8, memoryBytes = 16L * 1024 * 1024 * 1024, switches = new[] { "Default Switch" }, machines = Array.Empty<object>(), vmConnect = true }),
                "create" => Result(new { name = "x", created = new[] { "file.vhdx" } }),
                "" => "",
                var other => Result(new { ok = other })
            };
            var scripts = new HostScripts(runner);
            var disks = new ServerDiskManager(controller, scripts, Path.Combine(root, "state"), virtualOnly: false) { DevicePath = n => diskFiles[n] };
            var inventory = disks.InventoryAsync().GetAwaiter().GetResult();
            ServerDisk D(int n) => inventory.Disks.Single(d => d.Number == n);
            check(D(0).Protections!.Contains("Disco do sistema do servidor") && D(0).Protections!.Contains("Windows do servidor") && D(0).Protections!.Contains("Dados do SLT Disk Manager"), "disco do servidor protegido (sistema, Windows e dados do SLT)");
            check(D(1).Protections!.Contains("Arquivo de paginação"), "disco com arquivo de paginação protegido");
            check(D(5).Protections!.Length == 0 && D(2).IsVirtual && !D(5).IsVirtual, "pendrive e discos virtuais livres para gravar");
            check(D(0).Partitions.Single(p => p.Number == 1).Protections.Contains("Partição EFI") && D(0).Partitions.Single(p => p.Number == 4).Protections.Contains("Recuperação do Windows"), "partições EFI e de recuperação marcadas");
            Reject(() => disks.PlanAsync(new("FORMAT_DISK", 0, D(0).Fingerprint, TableType: "GPT", FileSystem: "NTFS")), "formatar o disco do servidor recusado (use kamikaze)");
            Reject(() => disks.PlanAsync(new("FORMAT_DISK", 1, D(1).Fingerprint, TableType: "GPT", FileSystem: "NTFS")), "formatar disco com paginação recusado");
            Reject(() => disks.PlanAsync(new("FORMAT_DISK", 5, "outro", TableType: "GPT", FileSystem: "NTFS")), "fingerprint diferente recusado");
            Reject(() => disks.PlanAsync(new("FORMAT_DISK", 5, D(5).Fingerprint, TableType: "MBR", FileSystem: "FAT32")), "FAT32 acima de 32 GB recusado");
            Reject(() => disks.PlanAsync(new("FORMAT_DISK", 5, D(5).Fingerprint, TableType: "GPT", FileSystem: "NTFS", Label: "a\"b")), "rótulo com aspas recusado");
            Reject(() => disks.PlanAsync(new("FORMAT_VOLUME", 0, D(0).Fingerprint, 1, FileSystem: "NTFS")), "formatar partição EFI recusado");
            Reject(() => disks.PlanAsync(new("REPAIR_VOLUME", 0, D(0).Fingerprint, 3)), "chkdsk no volume do Windows do servidor recusado");
            Reject(() => disks.PlanAsync(new("CLONE_DISK", 0, D(0).Fingerprint, TargetDiskNumber: 3, TargetFingerprint: D(3).Fingerprint)), "clonar o disco do servidor recusado (use kamikaze)");
            Reject(() => disks.PlanAsync(new("CONVERT_TABLE", 5, D(5).Fingerprint, TableType: "MBR")), "conversão para a mesma tabela recusada");
            Reject(() => disks.PlanAsync(new("SHELL", 5, D(5).Fingerprint)), "operação desconhecida recusada");
            var format = disks.PlanAsync(new("FORMAT_DISK", 5, D(5).Fingerprint, TableType: "GPT", FileSystem: "exFAT", Label: "PENDRIVE")).GetAwaiter().GetResult();
            check(format.Challenge == "APAGAR DISCO 5 64GB" && format.RequiresTyping, "formatação exige digitar disco e capacidade");
            Reject(() => disks.ConfirmAsync(format.Id, "APAGAR DISCO 4 64GB"), "confirmação de outro disco recusada");
            var tampered = disks.PlanAsync(new("FORMAT_DISK", 5, D(5).Fingerprint, TableType: "GPT", FileSystem: "NTFS")).GetAwaiter().GetResult();
            db.Put("server-disk-op", tampered.Id, tampered with { Request = tampered.Request with { DiskNumber = 1 } });
            Reject(() => disks.ConfirmAsync(tampered.Id, tampered.Challenge), "plano alterado depois de assinado recusado");
            var expired = disks.PlanAsync(new("FORMAT_DISK", 5, D(5).Fingerprint, TableType: "GPT", FileSystem: "NTFS")).GetAwaiter().GetResult();
            db.Put("server-disk-op", expired.Id, expired with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
            Reject(() => disks.ConfirmAsync(expired.Id, expired.Challenge), "plano expirado recusado");
            disks.ConfirmAsync(format.Id, "apagar disco 5 64gb").GetAwaiter().GetResult();
            var done = Wait(() => disks.Get(format.Id), o => o.State != "RUNNING");
            var formatCall = runner.Calls.Last(c => runner.Operation(c.Args) == "format-disk");
            check(done.State == "COMPLETED" && formatCall.Stdin!.Contains("\"number\":5") && formatCall.Stdin.Contains("\"fileSystem\":\"exFAT\"") && formatCall.Args.Contains("-File"), "formatação confirmada executa o script tipado com o disco planejado");
            Reject(() => disks.ConfirmAsync(format.Id, format.Challenge), "plano não é reutilizado");
            // Disk changed between plan and confirmation (another pendrive in the same port).
            var replug = disks.PlanAsync(new("FORMAT_DISK", 5, D(5).Fingerprint, TableType: "GPT", FileSystem: "NTFS")).GetAwaiter().GetResult();
            inventoryDisks[5] = Disk(5, "OUTRO PENDRIVE", 32_000_000_000, "MBR", false, "USB");
            Reject(() => disks.ConfirmAsync(replug.Id, replug.Challenge), "disco trocado antes da confirmação recusado");
            // Raw clone between files that stand in for disks 2 (8 MiB GPT) and 3 (16 MiB).
            var clone = disks.PlanAsync(new("CLONE_DISK", 2, D(2).Fingerprint, TargetDiskNumber: 3, TargetFingerprint: D(3).Fingerprint)).GetAwaiter().GetResult();
            disks.ConfirmAsync(clone.Id, clone.Challenge).GetAwaiter().GetResult();
            var cloned = Wait(() => disks.Get(clone.Id), o => o.State != "RUNNING");
            using (var target = File.OpenRead(diskFiles[3])) RawDisk.VerifyGpt(target, 16 * mib, 512);
            var src = File.ReadAllBytes(diskFiles[2]); var dst = File.ReadAllBytes(diskFiles[3]);
            check(cloned.State == "COMPLETED" && src.AsSpan(2048 * 512, 9953 * 512).SequenceEqual(dst.AsSpan(2048 * 512, 9953 * 512)) && cloned.ResultJson!.Contains("\"gptRelocated\":true"), "clone local copia, confere e move a GPT de reserva");
            var offlineCalls = runner.Calls.Where(c => runner.Operation(c.Args) == "set-offline").Select(c => c.Stdin!).ToArray();
            check(offlineCalls.Count(s => s.Contains("\"offline\":true")) >= 2 && offlineCalls.Any(s => s.Contains("\"number\":2") && s.Contains("\"offline\":false")), "clone deixa os dois discos offline e devolve só a origem");
            var small = disks.PlanAsync(new("CLONE_DISK", 2, D(2).Fingerprint, TargetDiskNumber: 4, TargetFingerprint: D(4).Fingerprint)).GetAwaiter().GetResult();
            disks.ConfirmAsync(small.Id, small.Challenge).GetAwaiter().GetResult();
            var refused = Wait(() => disks.Get(small.Id), o => o.State != "RUNNING");
            check(refused.State == "FAILED" && refused.Message!.Contains("Reduza") && File.ReadAllBytes(diskFiles[4]).All(b => b == 0), "clone para disco pequeno demais falha antes de gravar");
            var testOnly = new ServerDiskManager(controller, scripts, Path.Combine(root, "state"), virtualOnly: true);
            var guarded = testOnly.InventoryAsync().GetAwaiter().GetResult();
            Reject(() => testOnly.PlanAsync(new("FORMAT_DISK", 1, guarded.Disks.Single(d => d.Number == 1).Fingerprint, TableType: "GPT", FileSystem: "NTFS")), "modo de teste recusa disco físico");
            check(guarded.Disks.Single(d => d.Number == 2).Protections!.Length == 0, "modo de teste aceita disco virtual");

            HyperVTests(controller, scripts, runner, disks, Path.Combine(root, "state"), root, check, Reject);
            KamikazeTests(controller, certs, db, runner, disks, root, repository, check, Reject);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static T Wait<T>(Func<T> get, Func<T, bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until) { var value = get(); if (done(value)) return value; Thread.Sleep(50); }
        throw new TimeoutException("Operação não terminou.");
    }

    private static void RawDiskTests(string root, Action<bool, string> check, Action<Func<Task>, string> reject)
    {
        const long mib = 1024 * 1024;
        var source = Path.Combine(root, "raw-source.img"); var target = Path.Combine(root, "raw-target.img");
        WriteGpt(source, 16 * mib, 2048, 12000);
        File.WriteAllBytes(target, new byte[8 * mib]);
        using (var s = File.OpenRead(source))
        using (var t = new FileStream(target, FileMode.Open, FileAccess.ReadWrite))
        {
            var length = RawDisk.CloneLength(s, 16 * mib, 8 * mib, 512);
            check(length <= 8 * mib - 33 * 512 && length >= 12001 * 512, "destino menor aceito quando as partições cabem");
            RawDisk.CopyAndVerify(s, t, length, null, CancellationToken.None);
            check(RawDisk.RelocateGpt(t, 8 * mib, 512), "GPT realocada no destino menor");
            RawDisk.VerifyGpt(t, 8 * mib, 512);
            check(true, "GPT primária e de reserva conferem no destino menor");
        }
        var wide = Path.Combine(root, "raw-wide.img");
        WriteGpt(wide, 16 * mib, 2048, 30000);
        using (var w = File.OpenRead(wide)) reject(() => Task.Run(() => RawDisk.CloneLength(w, 16 * mib, 8 * mib, 512)), "partição maior que o destino recusada");
        var mbr = new byte[8 * mib];
        mbr[446 + 4] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12), 4096);
        mbr[510] = 0x55; mbr[511] = 0xAA;
        using (var m = new MemoryStream(mbr)) check(RawDisk.CloneLength(m, 8 * mib, 4 * mib, 512) == 3 * mib, "clone MBR copia até o fim da última partição");
        using (var m = new MemoryStream(mbr)) check(!RawDisk.RelocateGpt(m, 8 * mib, 512), "disco MBR não recebe GPT");
        var corrupt = File.ReadAllBytes(source); corrupt[512 + 20] ^= 0xFF; File.WriteAllBytes(target, corrupt);
        using (var c = new FileStream(target, FileMode.Open, FileAccess.ReadWrite)) reject(() => Task.Run(() => RawDisk.RelocateGpt(c, 16 * mib, 512)), "GPT com CRC inválido não é reescrita");
        // A target that does not keep what was written fails the verification.
        using (var s = File.OpenRead(source)) reject(() => Task.Run(() => RawDisk.CopyAndVerify(s, new LossyStream(), 4 * mib, null, CancellationToken.None)), "conferência SHA256 detecta destino que não gravou");
    }

    private sealed class LossyStream : MemoryStream
    {
        public LossyStream() : base(new byte[16 * 1024 * 1024]) { }
        public override void Write(byte[] buffer, int offset, int count) { var copy = (byte[])buffer.Clone(); copy[offset] ^= 1; base.Write(copy, offset, count); }
    }

    private static void HyperVTests(Controller controller, HostScripts scripts, FakeRunner runner, ServerDiskManager disks, string state, string root, Action<bool, string> check, Action<Func<Task>, string> reject)
    {
        var vms = new HyperVManager(controller, scripts, disks, state);
        var inventory = disks.InventoryAsync().GetAwaiter().GetResult();
        var folder = Path.Combine(root, "vms");
        VmCreateRequest Req(string name = "Bancada-1", int gen = 2, int mem = 4096, int cpu = 2, int pct = 50, bool sb = true, VmDiskRequest? disk = null) =>
            new(name, gen, mem, cpu, pct, sb, sb ? "MicrosoftWindows" : null, false, disk ?? new VmDiskRequest("NEW", folder, 64), null, "Default Switch");
        reject(() => vms.PlanAsync(Req(name: "a;rm")), "nome de VM com caracteres de comando recusado");
        reject(() => vms.PlanAsync(Req(mem: 15 * 1024)), "memória que não deixa 2 GB ao servidor recusada");
        reject(() => vms.PlanAsync(Req(cpu: 9)), "mais processadores que o servidor recusado");
        reject(() => vms.PlanAsync(Req(pct: 0)), "limite de CPU fora de 1-100% recusado");
        reject(() => vms.PlanAsync(Req(gen: 1, sb: true)), "inicialização segura em Legacy recusada");
        reject(() => vms.PlanAsync(Req(disk: new VmDiskRequest("NEW", state, 64))), "VHDX na pasta de estado do servidor recusado");
        reject(() => vms.PlanAsync(Req(disk: new VmDiskRequest("PHYSICAL", DiskNumber: 0, Fingerprint: inventory.Disks.Single(d => d.Number == 0).Fingerprint))), "disco do servidor não vai para a VM");
        var plain = vms.PlanAsync(Req()).GetAwaiter().GetResult();
        check(!plain.RequiresTyping && plain.Request.Disk.Path == Path.Combine(folder, "Bancada-1.vhdx"), "VM com VHDX novo não exige digitação e grava na pasta escolhida");
        var physical = vms.PlanAsync(Req(name: "Instalar-HD", gen: 1, sb: false, disk: new VmDiskRequest("PHYSICAL", DiskNumber: 5, Fingerprint: inventory.Disks.Single(d => d.Number == 5).Fingerprint))).GetAwaiter().GetResult();
        check(physical.RequiresTyping && physical.Challenge == "ENTREGAR DISCO 5 A VM", "entregar HD físico à VM exige confirmação digitada");
        reject(() => vms.ConfirmAsync(physical.Id, "ENTREGAR DISCO 4 A VM"), "confirmação errada não cria a VM");
        vms.ConfirmAsync(physical.Id, physical.Challenge).GetAwaiter().GetResult();
        var create = runner.Calls.Last(c => runner.Operation(c.Args) == "create").Stdin!;
        check(create.Contains("\"generation\":1") && create.Contains("\"cpuPercent\":50") && create.Contains("\"kind\":\"PHYSICAL\"") && create.Contains("\"number\":5") && create.Contains("\"memoryMiB\":4096"), "criação envia geração, memória, limite de CPU e disco físico ao script");
        reject(() => vms.ActionAsync("x\"y", "start", null), "ação com nome inválido recusada");
        reject(() => vms.ActionAsync("Bancada-1", "format", null), "ação desconhecida recusada");
    }

    private static void KamikazeTests(Controller controller, Certificates certs, Store db, FakeRunner runner, ServerDiskManager disks, string root, string repository, Action<bool, string> check, Action<Func<Task>, string> reject)
    {
        var windows = Path.Combine(root, "windows"); var system = Path.Combine(root, "system-volume") + "\\"; var bundleRoot = Path.Combine(root, "repo-volume") + "\\";
        Directory.CreateDirectory(Path.Combine(windows, "System32", "Recovery"));
        Directory.CreateDirectory(Path.Combine(windows, "Boot", "DVD", "EFI"));
        File.WriteAllText(Path.Combine(windows, "System32", "Recovery", "Winre.wim"), "synthetic winre");
        File.WriteAllText(Path.Combine(windows, "Boot", "DVD", "EFI", "boot.sdi"), "sdi");
        var agent = Path.Combine(root, "slt-winpe-agent.exe"); File.WriteAllText(agent, "agent");
        string Identity(string model, long size) => Wire.Hash($"{model}|SN|{size}|512");
        var agentDisks = new object[]
        {
            new { number = 0, identity = Identity("SERVER SSD", 240_000_000_000), model = "SERVER SSD", serial = "SN", sizeBytes = 240_000_000_000L, logicalSectorBytes = 512, offline = false, readOnly = false },
            new { number = 1, identity = Identity("DATA HDD", 3_000_000_000_000), model = "DATA HDD", serial = "SN", sizeBytes = 3_000_000_000_000L, logicalSectorBytes = 512, offline = false, readOnly = false },
            new { number = 5, identity = Identity("PENDRIVE", 64_000_000_000), model = "PENDRIVE", serial = "SN", sizeBytes = 64_000_000_000L, logicalSectorBytes = 512, offline = false, readOnly = false }
        };
        var guids = 0;
        var previous = runner.Handler;
        runner.Handler = (exe, args, stdin) =>
        {
            var name = Path.GetFileName(exe).ToLowerInvariant();
            if (name == "slt-winpe-agent.exe") return JsonSerializer.Serialize(agentDisks);
            if (name == "dism.exe" && args[0] == "/Mount-Image")
            {
                var mount = args.Single(a => a.StartsWith("/MountDir:"))["/MountDir:".Length..];
                Directory.CreateDirectory(Path.Combine(mount, "Windows", "System32", "Boot"));
                File.WriteAllText(Path.Combine(mount, "Windows", "System32", "Boot", "winload.efi"), "");
                return "ok";
            }
            if (name == "bcdedit.exe" && args[0] == "/create") return $"A entrada {{{Guid.Parse($"00000000-0000-0000-0000-{++guids:D12}")}}} foi criada com êxito.";
            return name is "dism.exe" or "bcdedit.exe" or "shutdown.exe" or "reagentc.exe" ? "ok" : previous(exe, args, stdin);
        };
        var boot = DateTimeOffset.UtcNow.AddDays(-1);
        var k = new KamikazeManager(controller, runner) { AgentPath = agent, SystemRoot = system, WindowsDirectory = windows, FirmwareOverride = "UEFI", BundleRootOverride = bundleRoot, ServerName = "SRV-TESTE", BootTime = () => boot };
        var mediaId = Guid.NewGuid().ToString();
        var mediaFile = Path.Combine(repository, "media", mediaId, "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(mediaFile)!); File.WriteAllText(mediaFile, "wim");
        db.Put("media", mediaId, new DeploymentMedia(mediaId, "Windows 10", "install.wim", 3, Wire.Hash("wim"), [], [new WindowsImage(1, "Pro", "", "AMD64", "10", 1)], "AVAILABLE", DateTimeOffset.UtcNow));
        var ssd = Identity("SERVER SSD", 240_000_000_000); var hdd = Identity("DATA HDD", 3_000_000_000_000); var usb = Identity("PENDRIVE", 64_000_000_000);
        reject(() => k.PlanAsync(new([new("FORMAT_DISK", "0000", TableType: "GPT", FileSystem: "NTFS")]), disks, default), "disco desconhecido recusado no kamikaze");
        reject(() => k.PlanAsync(new([new("DEPLOY_WINDOWS", ssd, MediaId: mediaId, ImageIndex: 1)]), disks, default), "instalar Windows no disco do repositório recusado (mídia estaria no destino)");
        reject(() => k.PlanAsync(new([new("FORMAT_DISK", usb, TableType: "GPT", FileSystem: "NTFS"), new("CLONE_DISK", hdd, usb)]), disks, default), "disco gravado e depois usado como origem recusado");
        reject(() => k.PlanAsync(new([new("FORMAT_DISK", ssd, TableType: "GPT", FileSystem: "NTFS"), new("DEPLOY_WINDOWS", hdd, MediaId: mediaId, ImageIndex: 1)]), disks, default), "apagar o disco da mídia antes de instalar recusado");
        var session = k.PlanAsync(new([new("DEPLOY_WINDOWS", hdd, MediaId: mediaId, ImageIndex: 1), new("FORMAT_DISK", ssd, TableType: "GPT", FileSystem: "NTFS", Label: "SERVIDOR")]), disks, default).GetAwaiter().GetResult();
        check(session.Challenge == "KAMIKAZE SRV-TESTE" && session.Warnings.Any(w => w.Contains("disco do Windows do servidor")) && session.ConsoleChallenges!.Length == 2, "plano kamikaze avisa que apaga o disco do servidor e tem frase por operação");
        reject(() => Task.Run(() => k.Confirm(session.Id, "KAMIKAZE OUTRO")), "confirmação kamikaze errada recusada");
        k.Confirm(session.Id, "kamikaze srv-teste");
        var staged = Wait(() => db.Get<KamikazeSession>("kamikaze", session.Id)!, s => s.State != "STAGING");
        check(staged.State == "STAGED", staged.State == "STAGED" ? "modo kamikaze preparado" : "modo kamikaze preparado: " + staged.Message + staged.Log);
        var bundlePath = Path.Combine(bundleRoot, "SLT-Kamikaze", session.Id, "bundle.json");
        var signed = Wire.Read<SignedAssignment>(File.ReadAllText(bundlePath));
        var payload = Convert.FromBase64String(signed.Payload);
        using (var key = certs.Server.GetRSAPublicKey()!)
            check(key.VerifyData(payload, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "pacote kamikaze assinado pela chave do servidor");
        var bundle = Encoding.UTF8.GetString(payload);
        check(bundle.Contains($"\"sessionId\":\"{session.Id}\"") && bundle.Contains("install.wim") && bundle.Contains("\"firmware\":\"GPT_UEFI\"") && bundle.Contains("\"identity\":\"" + hdd + "\""), "pacote leva identidade do disco, mídia e firmware");
        var bcd = runner.Calls.Where(c => Path.GetFileName(c.Exe) == "bcdedit.exe").Select(c => string.Join(' ', c.Args)).ToArray();
        check(bcd.Any(a => a.StartsWith("/bootsequence {")) && bcd.Any(a => a.Contains("winpe yes")) && bcd.Any(a => a.Contains(@"\Windows\System32\Boot\winload.efi")) && !bcd.Any(a => a.Contains("/default")), "entrada de boot única (bootsequence), WinPE em ramdisk, sem mudar o boot padrão");
        var winpeshl = runner.Calls.Any(c => Path.GetFileName(c.Exe) == "dism.exe" && c.Args.Contains("/Commit"));
        check(winpeshl && File.Exists(Path.Combine(system, "SLT-Kamikaze", "boot", "boot.wim")) && File.Exists(Path.Combine(system, "SLT-Kamikaze", "boot", "boot.sdi")), "imagem de boot preparada com DISM e copiada com boot.sdi");
        reject(() => k.PlanAsync(new([new("FORMAT_DISK", usb, TableType: "GPT", FileSystem: "NTFS")]), disks, default), "segundo modo kamikaze recusado enquanto há um preparado");
        // The console wrote its result; the server came back to Windows.
        File.WriteAllText(Path.Combine(bundleRoot, "SLT-Kamikaze", session.Id, "result.json"), "{\"sessionId\":\"x\",\"success\":true,\"message\":\"Todas as operações foram concluídas.\"}");
        k.Reconcile();
        var finished = db.Get<KamikazeSession>("kamikaze", session.Id)!;
        Thread.Sleep(200);
        var deletes = runner.Calls.Count(c => Path.GetFileName(c.Exe) == "bcdedit.exe" && c.Args[0] == "/delete");
        check(finished.State == "COMPLETED" && finished.ResultJson!.Contains("success") && deletes == 2 && !Directory.Exists(Path.Combine(bundleRoot, "SLT-Kamikaze", session.Id)), "resultado do console coletado e entradas de boot removidas");
        // Second session: the server rebooted but the console never ran (e.g. Secure Boot refused it).
        var second = k.PlanAsync(new([new("FORMAT_DISK", usb, TableType: "MBR", FileSystem: "FAT32")]), disks, default);
        reject(() => second, "FAT32 em disco de 64 GB recusado no kamikaze");
        var third = k.PlanAsync(new([new("FORMAT_DISK", usb, TableType: "MBR", FileSystem: "EXFAT", Label: "USB")]), disks, default).GetAwaiter().GetResult();
        k.Confirm(third.Id, third.Challenge);
        Wait(() => db.Get<KamikazeSession>("kamikaze", third.Id)!, s => s.State != "STAGING");
        check(runner.Calls.Count(c => Path.GetFileName(c.Exe) == "dism.exe" && c.Args[0] == "/Mount-Image") == 1, "imagem de boot reaproveitada do cache na segunda sessão");
        var reboot = new KamikazeManager(controller, runner) { AgentPath = agent, SystemRoot = system, WindowsDirectory = windows, FirmwareOverride = "UEFI", BundleRootOverride = bundleRoot, ServerName = "SRV-TESTE", BootTime = () => DateTimeOffset.UtcNow.AddMinutes(5) };
        reboot.Reconcile();
        check(db.Get<KamikazeSession>("kamikaze", third.Id)!.State == "NOT_RUN", "reinício sem execução do console vira NÃO EXECUTOU e limpa a entrada");
        var fourth = k.PlanAsync(new([new("FORMAT_DISK", usb, TableType: "GPT", FileSystem: "NTFS")]), disks, default).GetAwaiter().GetResult();
        k.Confirm(fourth.Id, fourth.Challenge);
        Wait(() => db.Get<KamikazeSession>("kamikaze", fourth.Id)!, s => s.State != "STAGING");
        var cancelled = k.CancelAsync(fourth.Id).GetAwaiter().GetResult();
        check(cancelled.State == "CANCELLED" && !Directory.Exists(Path.Combine(bundleRoot, "SLT-Kamikaze", fourth.Id)), "cancelar remove a entrada de boot e o pacote");
        runner.Handler = previous;
    }
}
