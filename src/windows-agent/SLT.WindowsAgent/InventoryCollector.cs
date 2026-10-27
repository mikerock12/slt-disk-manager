using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SLT.WindowsAgent;

public sealed class InventoryCollector(AgentOptions options, FileRootCatalog? fileRoots = null)
{
    private readonly FileRootCatalog roots = fileRoots ?? new FileRootCatalog(options);
    private sealed record Collected(string Os, DiskInfo[] Disks, string[] Notes);

    public async Task<Inventory> Collect(string deviceId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Inventário Windows exige Windows 10/11 x64.");
        var script = Path.Combine(AppContext.BaseDirectory, "Hardware", "Collect-Inventory.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Coletor PowerShell ausente no pacote.", script);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await ProcessRunner.Run(powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script], TimeSpan.FromSeconds(90), 4 * 1024 * 1024, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Coletor PowerShell encerrou com código {result.ExitCode}.");
        var collected = JsonSerializer.Deserialize<Collected>(result.Output.Trim('\uFEFF', '\r', '\n', ' '), Wire.Json)
            ?? throw new InvalidOperationException("Coletor retornou inventário vazio.");
        var disks = (collected.Disks ?? []).Select(d => d with { Fingerprint = Wire.Fingerprint(d), Partitions = d.Partitions ?? [] }).ToArray();
        var caps = new List<string> { "INVENTORY" };
        if (options.SmartctlPath is not null) caps.Add("SMART");
        caps.AddRange(roots.Capabilities);
        return new Inventory(deviceId, Environment.MachineName, collected.Os, "WINDOWS", "0.1.0", caps.ToArray(), disks,
            DateTimeOffset.UtcNow, collected.Notes ?? [], roots.Inventory);
    }

    public async Task<JobResult> Smart(JobPlan plan, Inventory inventory, CancellationToken ct)
    {
        if (options.SmartctlPath is null) return new(false, "smartctl não configurado neste agente.", "SMARTCTL_UNAVAILABLE");
        var disk = inventory.Disks.SingleOrDefault(d => d.Fingerprint == plan.DiskFingerprint && d.Path == plan.DiskPath);
        if (disk is null || disk.SizeBytes != plan.DiskSizeBytes || disk.LogicalSectorBytes != plan.LogicalSectorBytes)
            return new(false, "Identidade do disco mudou após o plano.", "DISK_IDENTITY_MISMATCH");
        // The path is freshly collected locally, never forwarded from an unvalidated remote command.
        var result = await ProcessRunner.Run(options.SmartctlPath, ["--json", "--all", SmartDevicePath(disk.Path)], TimeSpan.FromSeconds(60), 4 * 1024 * 1024, ct);
        // smartctl exit status is a bitmask; bits >= 3 describe health evidence, not command failure.
        if ((result.ExitCode & 7) != 0) return new(false, "smartctl não conseguiu consultar este dispositivo.", result.Output);
        using var json = JsonDocument.Parse(result.Output);
        return new(true, "Relatório SMART coletado em modo somente leitura. Consulte os alertas do firmware no relatório.", json.RootElement.GetRawText());
    }

    public static string SmartDevicePath(string physicalPath)
    {
        const string prefix = @"\\.\PhysicalDrive";
        if (!physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(physicalPath.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number is < 0 or > 255)
            throw new InvalidOperationException("Caminho físico não suportado pelo adapter SMART Windows.");
        return "/dev/pd" + number.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed record ProcessResult(int ExitCode, string Output);

public static class ProcessRunner
{
    public static async Task<ProcessResult> Run(string executable, string[] arguments, TimeSpan timeout, int maxOutput,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = ReadBounded(process.StandardOutput, maxOutput, linked.Token);
        var stderr = ReadBounded(process.StandardError, 64 * 1024, linked.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr);
            return new(process.ExitCode, await stdout);
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, int max, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (result.Length + count > max) throw new InvalidOperationException("Saída da ferramenta excedeu limite permitido.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
