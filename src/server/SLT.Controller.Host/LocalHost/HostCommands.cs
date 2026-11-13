using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Slt.Controller.LocalHost;

public sealed record CommandOutput(int ExitCode, string Output);

// Every program the server runs on its own machine goes through here: a fixed executable with an
// argument list (never a shell string), bounded output and a timeout. Tests swap it for a fake.
public interface IHostCommandRunner
{
    Task<CommandOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout, Action<string>? onLine, CancellationToken cancellationToken);
}

public sealed class NativeHostCommandRunner : IHostCommandRunner
{
    private const int MaxOutput = 512 * 1024;
    public async Task<CommandOutput> RunAsync(string executable, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = standardInput != null, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line == null) return;
            // sfc.exe writes UTF-16 to redirected output; the NUL bytes are dropped here.
            line = line.Replace("\0", "");
            lock (output) { if (output.Length < MaxOutput) output.AppendLine(line); }
            onLine?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        if (!process.Start()) throw new IOException($"{Path.GetFileName(executable)} não iniciou.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        if (standardInput != null) { await process.StandardInput.WriteAsync(standardInput); process.StandardInput.Close(); }
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timer.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(executable)} excedeu o tempo limite ou foi cancelado.");
        }
        process.WaitForExit();
        lock (output) return new(process.ExitCode, output.ToString());
    }
}

public static class HostPaths
{
    public static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);
    public static string PowerShell => Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");
    public static string Tool(string name) => Path.Combine(System32, name);
    public static string Script(string name) => Path.Combine(AppContext.BaseDirectory, "LocalHost", "Scripts", name);
}

// Runs one of the scripts shipped with the server (LocalHost/Scripts) with a typed operation.
// Parameters travel as JSON on standard input; the script answers with one "SLT-RESULT:" line.
public sealed class HostScripts(IHostCommandRunner runner)
{
    public IHostCommandRunner Runner { get; } = runner;
    public async Task<JsonElement> RunAsync(string script, string operation, object parameters, TimeSpan timeout, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var path = HostPaths.Script(script);
        var result = await Runner.RunAsync(HostPaths.PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path, "-Operation", operation],
            Wire.Serialize(parameters), timeout, line => { if (!line.StartsWith("SLT-RESULT:", StringComparison.Ordinal)) onLine?.Invoke(line); }, cancellationToken);
        var marker = result.Output.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("SLT-RESULT:", StringComparison.Ordinal));
        if (marker == null) throw new ApiException(500, $"{operation}: o script não devolveu resultado (código {result.ExitCode}). {Tail(result.Output)}");
        using var document = JsonDocument.Parse(marker["SLT-RESULT:".Length..]);
        var root = document.RootElement.Clone();
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) throw new ApiException(409, error.GetString()!);
        return root;
    }
    public static string Tail(string text, int max = 1500) => text.Length <= max ? text.Trim() : "…" + text[^max..].Trim();
}
