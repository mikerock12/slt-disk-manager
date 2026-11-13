using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Slt.Controller;
using Slt.Controller.LocalHost;

// Real PowerShell, real Storage cmdlets and real Hyper-V, on synthetic VHDX files only.
//   dotnet run --project tests/SLT.Controller.Tests -- --server-disks-vhd   (administrator)
//   dotnet run --project tests/SLT.Controller.Tests -- --hyperv-smoke        (administrator + Hyper-V)
// The manager runs with VirtualOnly=true: any physical disk is refused by the Controller itself.
internal static class ServerLocalE2E
{
    private static int passed;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }

    private static void DiskPart(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), "slt-e2e-diskpart-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(file, script);
        try
        {
            var p = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{file}\"") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("diskpart: " + output);
        }
        finally { File.Delete(file); }
    }

    private static (string Root, Controller Controller, Store Db, ServerDiskManager Disks, HostScripts Scripts) Setup(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"slt-{name}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "state"));
        var certs = new Certificates(Path.Combine(root, "state"), IPAddress.Loopback);
        var db = new Store(Path.Combine(root, "state"));
        var controller = new Controller(db, certs, Path.Combine(root, "repo"));
        var scripts = new HostScripts(new NativeHostCommandRunner());
        return (root, controller, db, new ServerDiskManager(controller, scripts, Path.Combine(root, "state"), virtualOnly: true), scripts);
    }

    private static ServerDiskOperation Run(ServerDiskManager disks, ServerDiskRequest request)
    {
        var plan = disks.PlanAsync(request).GetAwaiter().GetResult();
        disks.ConfirmAsync(plan.Id, plan.Challenge).GetAwaiter().GetResult();
        var until = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < until)
        {
            var op = disks.Get(plan.Id);
            if (op.State != "RUNNING") { if (op.State != "COMPLETED") Console.WriteLine(op.Log); return op; }
            Thread.Sleep(500);
        }
        throw new TimeoutException(request.Kind);
    }

    public static int ServerDisksVhd()
    {
        var (root, controller, db, disks, _) = Setup("disks-vhd");
        var a = Path.Combine(root, "origem.vhdx"); var b = Path.Combine(root, "destino.vhdx");
        try
        {
            DiskPart($"create vdisk file=\"{a}\" maximum=300 type=expandable\r\nattach vdisk\r\ncreate vdisk file=\"{b}\" maximum=600 type=expandable\r\nattach vdisk\r\nexit\r\n");
            ServerDisk Find(string file) => disks.InventoryAsync().GetAwaiter().GetResult().Disks.Single(d => d.Location.Equals(file, StringComparison.OrdinalIgnoreCase));
            var A = Find(a); var B = Find(b);
            Check(A.IsVirtual && B.IsVirtual && A.Protections!.Length == 0, "VHDX sintéticos aparecem como discos virtuais livres");
            var physical = disks.InventoryAsync().GetAwaiter().GetResult().Disks.Where(d => !d.IsVirtual).ToArray();
            Check(physical.All(d => d.Protections!.Contains("Modo de teste: só discos virtuais")), "discos físicos desta máquina ficam bloqueados no modo de teste");
            var op = Run(disks, new("FORMAT_DISK", A.Number, A.Fingerprint, TableType: "GPT", FileSystem: "NTFS", Label: "SLT-A"));
            A = Find(a);
            var data = A.Partitions.Single(p => p.FileSystem == "NTFS");
            Check(op.State == "COMPLETED" && A.PartitionStyle == "GPT" && data.Label == "SLT-A" && data.DriveLetter.Length == 1, "formatar disco: GPT, NTFS, rótulo e letra");
            var payload = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
            File.WriteAllBytes($"{data.DriveLetter}:\\dados.bin", payload);
            op = Run(disks, new("FORMAT_VOLUME", A.Number, A.Fingerprint, data.Number, FileSystem: "exFAT", Label: "TROCA"));
            A = Find(a);
            Check(op.State == "COMPLETED" && A.Partitions.Single(p => p.Number == data.Number).FileSystem == "exFAT" && !File.Exists($"{A.Partitions.Single(p => p.Number == data.Number).DriveLetter}:\\dados.bin"), "formatar partição troca o sistema de arquivos (NTFS → exFAT)");
            op = Run(disks, new("FORMAT_VOLUME", A.Number, A.Fingerprint, data.Number, FileSystem: "NTFS", Label: "SLT-A"));
            A = Find(a); data = A.Partitions.Single(p => p.Number == data.Number);
            File.WriteAllBytes($"{data.DriveLetter}:\\dados.bin", payload);
            op = Run(disks, new("CHECK_DISK", A.Number, A.Fingerprint));
            Check(op.State == "COMPLETED" && op.ResultJson!.Contains("NoErrorsFound"), "verificar disco: saúde e scan somente leitura sem erros");
            op = Run(disks, new("RESIZE_PARTITION", A.Number, A.Fingerprint, data.Number, SizeBytes: 160L * 1024 * 1024));
            A = Find(a);
            Check(op.State == "COMPLETED" && A.Partitions.Single(p => p.Number == data.Number).SizeBytes == 160L * 1024 * 1024, "redimensionar: reduz a partição para 160 MB");
            op = Run(disks, new("RESIZE_PARTITION", A.Number, A.Fingerprint, data.Number, SizeBytes: 0));
            A = Find(a);
            Check(op.State == "COMPLETED" && A.Partitions.Single(p => p.Number == data.Number).SizeBytes > 200L * 1024 * 1024, "redimensionar: estende ao máximo");
            op = Run(disks, new("REPAIR_VOLUME", A.Number, A.Fingerprint, data.Number));
            Check(op.State == "COMPLETED", "corrigir volume NTFS (Repair-Volume)");
            op = Run(disks, new("CONVERT_TABLE", B.Number, B.Fingerprint, TableType: "MBR"));
            B = Find(b);
            Check(op.State == "COMPLETED" && B.PartitionStyle == "MBR", "converter tabela: vazio → MBR");
            op = Run(disks, new("CONVERT_TABLE", B.Number, B.Fingerprint, TableType: "GPT"));
            B = Find(b);
            Check(op.State == "COMPLETED" && B.PartitionStyle == "GPT", "converter tabela: MBR → GPT");
            op = Run(disks, new("SET_OFFLINE", A.Number, A.Fingerprint));
            Check(op.State == "COMPLETED" && Find(a).IsOffline, "colocar offline");
            A = Find(a);
            op = Run(disks, new("SET_ONLINE", A.Number, A.Fingerprint));
            A = Find(a);
            Check(op.State == "COMPLETED" && !A.IsOffline, "colocar online");
            var refused = false;
            try { disks.PlanAsync(new("MBR2GPT", A.Number, A.Fingerprint, ValidateOnly: true)).GetAwaiter().GetResult(); } catch (ApiException) { refused = true; }
            Check(refused, "MBR2GPT recusado no planejamento quando o disco já é GPT");
            // Clone: 300 MB GPT with data → 600 MB.
            A = Find(a); B = Find(b);
            op = Run(disks, new("CLONE_DISK", A.Number, A.Fingerprint, TargetDiskNumber: B.Number, TargetFingerprint: B.Fingerprint));
            Check(op.State == "COMPLETED" && op.ResultJson!.Contains("\"gptRelocated\":true") && op.ResultJson.Contains("\"verified\":true"), "clonar disco: cópia conferida por SHA256 e GPT realocada");
            A = Find(a); B = Find(b);
            Check(!A.IsOffline && B.IsOffline, "origem volta online e destino fica offline");
            using (var target = RawDisk.Open($@"\\.\PhysicalDrive{B.Number}", false)) RawDisk.VerifyGpt(target, B.SizeBytes, B.LogicalSectorBytes);
            Check(true, "GPT primária e de reserva do destino conferem no dispositivo real");
            var part = A.Partitions.Single(p => p.FileSystem == "NTFS");
            using (var s = RawDisk.Open($@"\\.\PhysicalDrive{A.Number}", false))
            using (var t = RawDisk.Open($@"\\.\PhysicalDrive{B.Number}", false))
            {
                // Only the boot sector: once the source is online again Windows updates NTFS metadata on it.
                var x = new byte[512]; var y = new byte[512];
                s.Position = part.OffsetBytes; s.ReadExactly(x); t.Position = part.OffsetBytes; t.ReadExactly(y);
                Check(x.AsSpan().SequenceEqual(y), "setor de boot da partição NTFS idêntico no destino");
            }
            Console.WriteLine($"{passed}/{passed} verificações reais com VHDX sintéticos passaram.");
            return 0;
        }
        finally
        {
            try { DiskPart($"select vdisk file=\"{a}\"\r\ndetach vdisk\r\nselect vdisk file=\"{b}\"\r\ndetach vdisk\r\nexit\r\n"); } catch (Exception e) { Console.WriteLine("Aviso ao desanexar: " + e.Message); }
            db.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static int HyperVSmoke()
    {
        var (root, controller, db, disks, scripts) = Setup("hyperv");
        var vms = new HyperVManager(controller, scripts, disks, Path.Combine(root, "state"));
        var names = new List<string>();
        try
        {
            var status = vms.StatusAsync().GetAwaiter().GetResult();
            Check(status.GetProperty("available").GetBoolean(), "Hyper-V disponível");
            foreach (var generation in new[] { 2, 1 })
            {
                var name = $"SLT-Teste-G{generation}-" + Guid.NewGuid().ToString("N")[..6];
                names.Add(name);
                var plan = vms.PlanAsync(new(name, generation, 512, 1, 20, generation == 2, generation == 2 ? "MicrosoftUEFICertificateAuthority" : null, false,
                    new VmDiskRequest("NEW", Path.Combine(root, "vms"), 4), null, null)).GetAwaiter().GetResult();
                var record = vms.ConfirmAsync(plan.Id, plan.Challenge).GetAwaiter().GetResult();
                Check(record.CreatedFiles.Length == 1 && File.Exists(record.CreatedFiles[0]), $"geração {generation}: VM criada com VHDX novo na pasta escolhida");
                var listed = vms.StatusAsync().GetAwaiter().GetResult().GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);
                Check(listed.GetProperty("generation").GetInt32() == generation && listed.GetProperty("cpuMaximumPercent").GetInt32() == 20 && listed.GetProperty("memoryBytes").GetInt64() == 512L * 1024 * 1024, $"geração {generation}: memória e limite de CPU aplicados");
                var start = vms.ActionAsync(name, "start", null).GetAwaiter().GetResult();
                Check(start.GetProperty("state").GetString() == "Running", $"geração {generation}: VM liga");
                var off = vms.ActionAsync(name, "turn-off", null).GetAwaiter().GetResult();
                Check(off.GetProperty("state").GetString() == "Off", $"geração {generation}: VM desliga à força");
                var deleted = vms.ActionAsync(name, "delete", new VmNameRequest(DeleteFiles: true)).GetAwaiter().GetResult();
                Check(!File.Exists(record.CreatedFiles[0]) && !vms.StatusAsync().GetAwaiter().GetResult().GetProperty("machines").EnumerateArray().Any(m => m.GetProperty("name").GetString() == name), $"geração {generation}: VM e VHDX excluídos");
                names.Remove(name);
            }
            Console.WriteLine($"{passed}/{passed} verificações reais do Hyper-V passaram (VMs descartáveis).");
            return 0;
        }
        finally
        {
            foreach (var name in names) try { vms.ActionAsync(name, "delete", new VmNameRequest(DeleteFiles: true)).GetAwaiter().GetResult(); } catch (Exception) { }
            db.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
