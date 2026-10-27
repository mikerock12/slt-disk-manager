using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SLT.WindowsAgent;

internal static class WindowsFilesE2E
{
    private sealed record Connection(string Url, string Token, string ServerPin);
    private sealed record LiveJob(string Id, JobPlan Plan, string State, JobResult? Result);
    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> stdout;
        private readonly Task<string> stderr;
        private readonly string logs;
        private readonly string name;
        public Child(string dll, string[] args, string directory, string output, string label)
        {
            logs = output; name = label;
            process = new Process { StartInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = directory,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add(dll);
            foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
            process.Start(); stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
        }
        public void EnsureRunning() { if (process.HasExited) throw new IOException($"Processo {name} encerrou com código {process.ExitCode}; consulte logs privados do E2E."); }
        public void Dispose()
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
            System.IO.File.WriteAllText(Path.Combine(logs, name + ".stdout.log"), stdout.GetAwaiter().GetResult());
            System.IO.File.WriteAllText(Path.Combine(logs, name + ".stderr.log"), stderr.GetAwaiter().GetResult());
            process.Dispose();
        }
    }

    public static async Task<int> Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("E2E de arquivos exige Windows."); return 1; }
        var repository = Environment.CurrentDirectory;
        var runtime = Path.Combine(repository, "runtime", "windows-files-e2e-" + Guid.NewGuid().ToString("N"));
        var serverState = Path.Combine(runtime, "server"); var agentState = Path.Combine(runtime, "agent");
        var source = Path.Combine(runtime, "source"); var destination = Path.Combine(runtime, "destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination); Directory.CreateDirectory(Path.Combine(source, "docs", "empty"));
        var sourceBytes = new byte[FileJobExecutor.ChunkSize + 512]; RandomNumberGenerator.Fill(sourceBytes);
        System.IO.File.WriteAllBytes(Path.Combine(source, "whole.bin"), sourceBytes);
        System.IO.File.WriteAllBytes(Path.Combine(source, "zero.bin"), []);
        System.IO.File.WriteAllText(Path.Combine(source, "docs", "ação.txt"), "Dados sintéticos para validação E2E.");
        var configPath = Path.Combine(runtime, "scopes.json");
        System.IO.File.WriteAllText(configPath, JsonSerializer.Serialize(new[] { new FileRootConfiguration("source", "Fonte de teste", source, true, false),
            new FileRootConfiguration("destination", "Destino de teste", destination, false, true) }, Wire.Json));
        var serverDll = Path.Combine(repository, "src", "server", "SLT.Controller.Host", "bin", "Debug", "net10.0", "SLT.Controller.Host.dll");
        var agentDll = Path.Combine(repository, "src", "windows-agent", "SLT.WindowsAgent", "bin", "Debug", "net10.0", "SLT.WindowsAgent.dll");
        Child? agent = null;
        Child? server = null;
        try
        {
            server = new(serverDll, ["--bind", "127.0.0.1", "--port", "17445", "--state-dir", serverState], repository, runtime, "controller");
            var connectionFile = Path.Combine(serverState, "connection.json");
            await Wait(() => { server.EnsureRunning(); return Task.FromResult(System.IO.File.Exists(connectionFile)); });
            var connection = JsonSerializer.Deserialize<Connection>(await System.IO.File.ReadAllTextAsync(connectionFile), Wire.Json)!;
            var expectedPin = Convert.FromHexString(connection.ServerPin);
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                PinnedTransport.VerifyPin(certificate, connection.ServerPin, DateTimeOffset.UtcNow);
            using var admin = new HttpClient(handler) { BaseAddress = new Uri(connection.Url), Timeout = TimeSpan.FromSeconds(10) };
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
            await Wait(async () => { try { using var response = await admin.GetAsync("/health"); return response.IsSuccessStatusCode; } catch (HttpRequestException) { return false; } });
            agent = new(agentDll, ["--server", connection.Url, "--server-pin", connection.ServerPin, "--state-dir", agentState,
                "--file-roots", configPath, "--poll-seconds", "2"], repository, runtime, "agent");
            var pendingFile = Path.Combine(agentState, "enrollment.json");
            await Wait(() => { agent.EnsureRunning(); return Task.FromResult(System.IO.File.Exists(pendingFile)); });
            var pending = JsonSerializer.Deserialize<EnrollmentPending>(await System.IO.File.ReadAllTextAsync(pendingFile), Wire.Json)!;
            await Post<JsonElement>(admin, "/api/v1/enrollments/" + pending.Id + "/approve", new { code = pending.Code });
            var identityFile = Path.Combine(agentState, "identity.json");
            await Wait(() => { agent.EnsureRunning(); return Task.FromResult(System.IO.File.Exists(identityFile)); });
            var identity = JsonSerializer.Deserialize<Identity>(await System.IO.File.ReadAllTextAsync(identityFile), Wire.Json)!;
            await Wait(async () =>
            {
                agent.EnsureRunning(); var device = await Get<JsonElement>(admin, "/api/v1/devices/" + identity.DeviceId);
                return device.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Object;
            });
            Console.WriteLine("PASS File scopes advertised through real pinned mTLS Windows agent");
            var first = await Post<LiveJob>(admin, "/api/v1/files/list", new { deviceId = identity.DeviceId, rootId = "source", relativePath = "", cursor = (string?)null, pageSize = 1, consent = true });
            var list = await Finished(admin, first.Id, agent);
            Require(list.State == "COMPLETED", "FILE_LIST não concluiu: " + list.Result?.Details);
            var page = JsonSerializer.Deserialize<FilePage>(list.Result!.Details, Wire.Json)!;
            Require(page.Entries.Length == 1 && page.NextCursor is not null, "Primeira página não contém cursor.");
            var next = await Post<LiveJob>(admin, "/api/v1/files/list", new { deviceId = identity.DeviceId, rootId = "source", relativePath = "", cursor = page.NextCursor, pageSize = 1, consent = true });
            var nextResult = await Finished(admin, next.Id, agent);
            Require(nextResult.State == "COMPLETED", "Segunda página não concluiu: " + nextResult.Result?.Details);
            var page2 = JsonSerializer.Deserialize<FilePage>(nextResult.Result!.Details, Wire.Json)!;
            Require(page2.Entries[0].Name != page.Entries[0].Name, "Cursor repetiu entrada.");
            Console.WriteLine("PASS Real FILE_LIST pagination via signed jobs and asynchronous polling");
            var backup = await Post<LiveJob>(admin, "/api/v1/jobs/plan", new { kind = "FILE_BACKUP", deviceId = identity.DeviceId, diskFingerprint = "", consent = true,
                file = new FileRequest("source", "", ["docs", "whole.bin", "zero.bin"], "", "E2E selected files", null, 100) });
            await Post<JsonElement>(admin, "/api/v1/jobs/" + backup.Id + "/approve", new { challenge = backup.Plan.Challenge });
            var backupResult = await Finished(admin, backup.Id, agent);
            Require(backupResult.State == "COMPLETED", "FILE_BACKUP não concluiu: " + backupResult.Result?.Details);
            var bundles = await Get<FileBundle[]>(admin, "/api/v1/file-bundles");
            var bundle = bundles.Single(b => b.Id == backup.Plan.File!.BundleId);
            FileSafety.ValidateBundle(bundle, backup.Plan.File!.BundleId);
            Require(bundle.Entries.Any(e => e.IsDirectory && e.RelativePath == "docs/empty"), "Pasta vazia ausente.");
            Console.WriteLine("PASS Real FILE_BACKUP selected files, empty folder, zero file, multi-chunk SHA256 and AVAILABLE catalog");
            var restore = await Post<LiveJob>(admin, "/api/v1/jobs/plan", new { kind = "FILE_RESTORE", deviceId = identity.DeviceId, diskFingerprint = "", consent = true,
                file = new FileRequest("destination", "received", [], bundle.Id, "Receive E2E", null, 100) });
            await Post<JsonElement>(admin, "/api/v1/jobs/" + restore.Id + "/approve", new { challenge = restore.Plan.Challenge });
            var restored = await Finished(admin, restore.Id, agent);
            Require(restored.State == "COMPLETED", "FILE_RESTORE não concluiu: " + restored.Result?.Details);
            Require(System.IO.File.ReadAllBytes(Path.Combine(destination, "received", "whole.bin")).SequenceEqual(sourceBytes), "Conteúdo final diferente da origem.");
            Require(Directory.Exists(Path.Combine(destination, "received", "docs", "empty")), "Diretório vazio perdido no restore.");
            Require(System.IO.File.ReadAllText(Path.Combine(destination, "received", "docs", "ação.txt")) == System.IO.File.ReadAllText(Path.Combine(source, "docs", "ação.txt")), "UTF8 divergente.");
            Console.WriteLine("PASS Real server-to-client restore into new folder with byte-for-byte verification");
            var duplicate = await Post<LiveJob>(admin, "/api/v1/jobs/plan", new { kind = "FILE_RESTORE", deviceId = identity.DeviceId, diskFingerprint = "", consent = true,
                file = new FileRequest("destination", "received", [], bundle.Id, "Must not overwrite", null, 100) });
            await Post<JsonElement>(admin, "/api/v1/jobs/" + duplicate.Id + "/approve", new { challenge = duplicate.Plan.Challenge });
            var rejected = await Finished(admin, duplicate.Id, agent);
            Require(rejected.State == "FAILED", "Destino existente foi aceito indevidamente.");
            Require(System.IO.File.ReadAllBytes(Path.Combine(destination, "received", "whole.bin")).SequenceEqual(sourceBytes), "Destino existente foi alterado.");
            Require(!Directory.EnumerateDirectories(destination, "*.partial").Any(), "Staging incompleto sobrou.");
            Console.WriteLine("PASS Existing destination rejected without overwriting any file");
            Console.WriteLine("Windows file E2E: 5/5 scenarios passed. Synthetic files only; logs/state remain in ignored runtime.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("Windows file E2E failed: " + exception.Message); Console.Error.WriteLine("Private evidence directory: " + runtime); return 1; }
        finally { agent?.Dispose(); server?.Dispose(); }
    }
    private static async Task Wait(Func<Task<bool>> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(180);
        while (DateTimeOffset.UtcNow < deadline) { if (await predicate()) return; await Task.Delay(300); }
        throw new TimeoutException("Condição E2E não concluída em 180 segundos.");
    }
    private static async Task<T> Get<T>(HttpClient admin, string path)
    {
        using var response = await admin.GetAsync(path); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Wire.Json) ?? throw new InvalidDataException("Resposta JSON vazia.");
    }
    private static async Task<T> Post<T>(HttpClient admin, string path, object body)
    {
        using var response = await admin.PostAsJsonAsync(path, body, Wire.Json);
        if (!response.IsSuccessStatusCode) throw new IOException($"E2E POST {path} HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<T>(Wire.Json) ?? throw new InvalidDataException("Resposta JSON vazia.");
    }
    private static async Task<LiveJob> Finished(HttpClient admin, string id, Child agent)
    {
        LiveJob? job = null;
        await Wait(async () => { agent.EnsureRunning(); job = await Get<LiveJob>(admin, "/api/v1/jobs/" + id); return job.State is "COMPLETED" or "FAILED" or "CANCELLED" or "INTERRUPTED"; });
        return job!;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
