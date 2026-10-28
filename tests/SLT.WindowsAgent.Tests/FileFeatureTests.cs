using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SLT.WindowsAgent;

internal static class FileFeatureTests
{
    private static JobPlan Plan(string kind, FileRequest file) => new(Guid.NewGuid().ToString(), kind, "test-device", "", "", 0, 512,
        "", "AUTORIZAR ARQUIVOS", DateTimeOffset.UtcNow.AddMinutes(10), true, file);
    private static FileRequest Request(string root, string bundle, string relative = "", string[]? paths = null) =>
        new(root, relative, paths ?? [], bundle, "Test bundle", null, 2);
    public static void Run(Action<string, Action> check, Action<bool> assert, Action<Action> reject)
    {
        check("file paths reject traversal ADS UNC DOS names and ambiguous spelling", () =>
        {
            foreach (var path in new[] { "../secret", "a/../b", "/root", "C:/Windows", "a\\b", "a:b", "a//b", "a/./b", "NUL.txt", "com1", "folder/CON", "a.", "a ", "a/", "a\0b", "*.txt", "a|b", "CONIN$", "LPT².log", "CON .txt" })
                reject(() => FileSafety.ValidateRelative(path));
            FileSafety.ValidateRelative("Documentos/ação 2026.txt"); FileSafety.ValidateRelative("", true);
            reject(() => FileSafety.ValidateRelative(string.Join('/', Enumerable.Repeat("a", 65))));
        });
        check("file plan requires signed root and request limits", () =>
        {
            var request = Request("documents", Guid.NewGuid().ToString(), paths: ["reports"]);
            PinnedTransport.ValidatePlan(Plan("FILE_BACKUP", request), "test-device", DateTimeOffset.UtcNow);
            reject(() => PinnedTransport.ValidatePlan(Plan("FILE_RESTORE", request), "test-device", DateTimeOffset.UtcNow));
            reject(() => PinnedTransport.ValidatePlan(Plan("FILE_LIST", request with { PageSize = 201 }), "test-device", DateTimeOffset.UtcNow));
            reject(() => PinnedTransport.ValidatePlan(Plan("FILE_BACKUP", request with { Paths = [] }), "test-device", DateTimeOffset.UtcNow));
            PinnedTransport.ValidatePlan(Plan("FILE_LIST", request with { Paths = null! }), "test-device", DateTimeOffset.UtcNow);
            PinnedTransport.ValidatePlan(Plan("FILE_RESTORE", request with { RelativePath = "new-folder", Paths = null! }), "test-device", DateTimeOffset.UtcNow);
        });
        check("no configured scopes means no file capabilities", () =>
        {
            var options = new AgentOptions(new Uri("https://127.0.0.1:7443"), new string('0', 64), Environment.CurrentDirectory, true, 10, null, false);
            var catalog = new FileRootCatalog(options);
            assert(catalog.Inventory.Length == 0 && catalog.Capabilities.Length == 0);
            reject(() => catalog.Resolve("anything", false));
        });
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Windows handle/ACL fixture tests on non-Windows platform."); return; }
        var fixture = Path.Combine(Environment.CurrentDirectory, "runtime", "windows-file-fixtures-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(fixture, "source"); var destination = Path.Combine(fixture, "destination");
        var state = Path.Combine(fixture, "private-state");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination); Directory.CreateDirectory(state);
        Directory.CreateDirectory(Path.Combine(source, "docs", "empty"));
        var payload = new byte[FileJobExecutor.ChunkSize + 512]; Random.Shared.NextBytes(payload);
        System.IO.File.WriteAllBytes(Path.Combine(source, "alpha.bin"), payload);
        System.IO.File.WriteAllText(Path.Combine(source, "docs", "ação.txt"), "conteúdo sintético UTF8", new UTF8Encoding(false));
        System.IO.File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        var configs = new[] { new FileRootConfiguration("source", "Fonte", source, true, false), new FileRootConfiguration("destination", "Destino", destination, false, true) };
        var configPath = Path.Combine(fixture, "scopes.json");
        System.IO.File.WriteAllText(configPath, JsonSerializer.Serialize(configs, Wire.Json));
        var agentOptions = new AgentOptions(new Uri("https://127.0.0.1:7443"), new string('0', 64), state, true, 10, null, false, configPath);
        var roots = new FileRootCatalog(agentOptions);
        check("configured scopes advertise only explicit read write permissions", () =>
        {
            assert(roots.Inventory.Length == 2 && roots.Capabilities.Length == 3);
            reject(() => roots.Resolve("source", true)); reject(() => roots.Resolve("destination", false));
            assert(JsonSerializer.Serialize(roots.Inventory, Wire.Json).Contains("source") && !JsonSerializer.Serialize(roots.Inventory, Wire.Json).Contains(source));
        });
        check("scopes cannot expose private state or executable directory", () =>
        {
            foreach (var prohibited in new[] { state, AppContext.BaseDirectory, fixture })
            {
                var bad = Path.Combine(fixture, "bad.json");
                System.IO.File.WriteAllText(bad, JsonSerializer.Serialize(new[] { new FileRootConfiguration("bad", "bad", prohibited, true, false) }, Wire.Json));
                reject(() => _ = new FileRootCatalog(agentOptions with { FileRootsPath = bad }));
            }
        });
        check("file listing paginates and detects stale or foreign cursor", () =>
        {
            var request = Request("source", "") with { PageSize = 1 };
            var page = FileJobExecutor.List(configs[0], request);
            assert(page.Entries.Length == 1 && page.NextCursor is not null);
            var second = FileJobExecutor.List(configs[0], request with { Cursor = page.NextCursor });
            assert(second.Entries.Length == 1 && second.Entries[0].Name != page.Entries[0].Name);
            reject(() => FileJobExecutor.List(configs[0], request with { Cursor = page.NextCursor, RelativePath = "docs" }));
            System.IO.File.WriteAllText(Path.Combine(source, "new.txt"), "new");
            reject(() => FileJobExecutor.List(configs[0], request with { Cursor = page.NextCursor }));
            System.IO.File.Delete(Path.Combine(source, "new.txt"));
        });
        check("read handles prevent concurrent writer and ancestor rename", () =>
        {
            using var lease = SecuredPath.Open(configs[0], "alpha.bin", readFile: true);
            reject(() => { using var stream = new FileStream(Path.Combine(source, "alpha.bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            reject(() => Directory.Move(source, source + "-renamed"));
        });
        check("junctions are listed as links and never followed for backup or scopes", () =>
        {
            var outside = Path.Combine(fixture, "outside"); Directory.CreateDirectory(outside);
            System.IO.File.WriteAllText(Path.Combine(outside, "synthetic-secret.txt"), "out of scope");
            var junction = Path.Combine(source, "junction");
            var command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + junction.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" + outside.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var process = ProcessRunner.Run(powershell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))], TimeSpan.FromSeconds(20), 65536, CancellationToken.None).GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new IOException("Não foi possível criar junction sintética para o teste.");
            try
            {
                var page = FileJobExecutor.List(configs[0], Request("source", "") with { PageSize = 200 });
                assert(page.Entries.Single(e => e.Name == "junction").IsLink);
                reject(() => SecuredPath.Open(configs[0], "junction/synthetic-secret.txt"));
                reject(() => SecuredPath.OpenAbsolute(junction));
            }
            finally { Directory.Delete(junction); }
            assert(System.IO.File.Exists(Path.Combine(outside, "synthetic-secret.txt")));
        });
        check("hardlink aliases cannot expose an out of scope file", () =>
        {
            var original = Path.Combine(fixture, "outside", "synthetic-secret.txt");
            var alias = Path.Combine(source, "hardlink.txt");
            if (!WindowsFileLinks.CreateHardLink(alias, original, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { reject(() => SecuredPath.Open(configs[0], "hardlink.txt", readFile: true)); }
            finally { System.IO.File.Delete(alias); }
        });
        check("pinned native rename uses terminated UTF16 and never replaces another folder", () =>
        {
            var initial = Path.Combine(destination, "rename-initial"); Directory.CreateDirectory(initial);
            var blocked = Path.Combine(destination, "rename-blocked"); Directory.CreateDirectory(blocked);
            using var pinned = SecuredPath.OpenAbsolute(initial, deleteLeaf: true);
            reject(() => pinned.PromoteDirectory(blocked));
            assert(Directory.Exists(initial) && Directory.Exists(blocked));
            for (var index = 0; index < 24; index++)
            {
                var next = Path.Combine(destination, "rename-á-" + index);
                pinned.PromoteDirectory(next);
                assert(Directory.Exists(next));
            }
        });
        var bundleId = Guid.NewGuid().ToString();
        var mock = new MemoryFileTransport(bundleId);
        var executor = new FileJobExecutor(roots, mock);
        check("selected folder backup preserves empty directories zero files chunks and SHA256", () =>
        {
            var result = executor.Execute(Plan("FILE_BACKUP", Request("source", bundleId, paths: ["docs", "alpha.bin", "empty.bin"])), CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Success && mock.Bundle is { State: "AVAILABLE" });
            var bundle = mock.Bundle!; FileSafety.ValidateBundle(bundle, bundleId);
            assert(bundle.Entries.Any(e => e.RelativePath == "docs/empty" && e.IsDirectory));
            assert(bundle.Entries.Single(e => e.RelativePath == "empty.bin").Chunks.Length == 0);
            assert(bundle.Entries.Single(e => e.RelativePath == "alpha.bin").Chunks.Length == 2);
        });
        check("bundle restore verifies then atomically promotes a new folder", () =>
        {
            var result = executor.Execute(Plan("FILE_RESTORE", Request("destination", bundleId, "received")), CancellationToken.None).GetAwaiter().GetResult();
            assert(result.Success && System.IO.File.ReadAllBytes(Path.Combine(destination, "received", "alpha.bin")).SequenceEqual(payload));
            assert(Directory.Exists(Path.Combine(destination, "received", "docs", "empty")));
            assert(System.IO.File.ReadAllText(Path.Combine(destination, "received", "docs", "ação.txt")) == "conteúdo sintético UTF8");
            assert(!Directory.EnumerateDirectories(destination, "*.partial").Any());
        });
        check("existing destination is never merged or overwritten", () =>
        {
            Directory.CreateDirectory(Path.Combine(destination, "existing"));
            System.IO.File.WriteAllText(Path.Combine(destination, "existing", "marker.txt"), "preserve");
            reject(() => executor.Execute(Plan("FILE_RESTORE", Request("destination", bundleId, "existing")), CancellationToken.None).GetAwaiter().GetResult());
            assert(System.IO.File.ReadAllText(Path.Combine(destination, "existing", "marker.txt")) == "preserve");
        });
        check("corrupted download never promotes partial destination", () =>
        {
            mock.CorruptDownload = true;
            reject(() => executor.Execute(Plan("FILE_RESTORE", Request("destination", bundleId, "corrupt")), CancellationToken.None).GetAwaiter().GetResult());
            mock.CorruptDownload = false;
            assert(!Directory.Exists(Path.Combine(destination, "corrupt")) && !Directory.EnumerateDirectories(destination, "*.partial").Any());
        });
        check("cancelled file restore cleans staging and preserves existing data", () =>
        {
            mock.CancelOnRestore = true;
            reject(() => executor.Execute(Plan("FILE_RESTORE", Request("destination", bundleId, "cancelled")), CancellationToken.None).GetAwaiter().GetResult());
            mock.CancelOnRestore = false;
            assert(!Directory.Exists(Path.Combine(destination, "cancelled")) && !Directory.EnumerateDirectories(destination, "*.partial").Any());
        });
        check("case collisions and traversal manifests rejected before staging", () =>
        {
            var good = mock.Bundle!;
            var entry = good.Entries.First(e => !e.IsDirectory);
            foreach (var invalid in new[] { entry with { Id = Guid.NewGuid().ToString(), RelativePath = entry.RelativePath.ToUpperInvariant() }, entry with { Id = Guid.NewGuid().ToString(), RelativePath = "../outside" } })
            {
                mock.Bundle = good with { Entries = good.Entries.Append(invalid).ToArray() };
                reject(() => executor.Execute(Plan("FILE_RESTORE", Request("destination", bundleId, "invalid")), CancellationToken.None).GetAwaiter().GetResult());
                assert(!Directory.Exists(Path.Combine(destination, "invalid")) && !Directory.EnumerateDirectories(destination, "*.partial").Any());
            }
            mock.Bundle = good;
        });
        check("manifest requires explicit directory ancestors and contiguous chunks", () =>
        {
            var good = mock.Bundle!;
            var file = good.Entries.First(e => e.RelativePath.Contains('/') && !e.IsDirectory);
            reject(() => FileSafety.ValidateBundle(good with { Entries = [file], TotalBytes = file.SizeBytes }, bundleId));
            var raw = good.Entries.Single(e => e.RelativePath == "alpha.bin");
            reject(() => FileSafety.ValidateBundle(good with { Entries = [raw with { Chunks = [raw.Chunks[1]] }], TotalBytes = raw.SizeBytes }, bundleId));
        });
        check("source changed after manifest is not promoted as complete backup", () =>
        {
            var id = Guid.NewGuid().ToString(); var altered = new MemoryFileTransport(id);
            altered.OnCreated = () => System.IO.File.WriteAllText(Path.Combine(source, "alpha.bin"), "changed");
            reject(() => new FileJobExecutor(roots, altered).Execute(Plan("FILE_BACKUP", Request("source", id, paths: ["alpha.bin"])), CancellationToken.None).GetAwaiter().GetResult());
            assert(altered.Bundle?.State == "CREATING");
            System.IO.File.WriteAllBytes(Path.Combine(source, "alpha.bin"), payload);
        });
        // Deliberately keep this uniquely named synthetic fixture under ignored runtime, like E2E evidence.
        Console.WriteLine("File fixtures contain synthetic bytes only: " + fixture);
    }
}

internal static class WindowsFileLinks
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateHardLink(string path, string existing, IntPtr security);
}

internal sealed class MemoryFileTransport(string id) : IFileJobTransport
{
    private sealed record Initial(BundleEntry[] Entries, long TotalBytes);
    private readonly Dictionary<(string, int), byte[]> chunks = [];
    public FileBundle? Bundle { get; set; }
    public bool CorruptDownload { get; set; }
    public bool CancelOnRestore { get; set; }
    public Action? OnCreated { get; set; }
    public Task<FileBundle> Publish(string path, object body, CancellationToken ct)
    {
        if (path.EndsWith("/bundle-complete", StringComparison.Ordinal))
        {
            Bundle = Bundle! with { State = "AVAILABLE" }; return Task.FromResult(Bundle);
        }
        var initial = JsonSerializer.Deserialize<Initial>(JsonSerializer.Serialize(body, Wire.Json), Wire.Json)!;
        Bundle = new(1, id, "Synthetic bundle", "CREATING", "test-device", "source", DateTimeOffset.UtcNow, initial.TotalBytes, initial.Entries);
        OnCreated?.Invoke(); return Task.FromResult(Bundle);
    }
    public Task<FileBundle> Manifest(string path, CancellationToken ct) => Task.FromResult(Bundle!);
    public Task<FileChunk> UploadBytes(string path, byte[] bytes, string hash, CancellationToken ct)
    {
        var parts = path.Split('/'); var fileId = parts[^3]; var sequence = int.Parse(parts[^1]);
        var entryIndex = Array.FindIndex(Bundle!.Entries, e => e.Id == fileId); var entry = Bundle.Entries[entryIndex];
        var chunk = new FileChunk(sequence, (long)sequence * FileJobExecutor.ChunkSize, bytes.Length, hash);
        chunks.Add((fileId, sequence), bytes.ToArray()); Bundle.Entries[entryIndex] = entry with { Chunks = entry.Chunks.Append(chunk).ToArray() };
        return Task.FromResult(chunk);
    }
    public Task<byte[]> DownloadBytes(string path, CancellationToken ct)
    {
        var parts = path.Split('/'); var bytes = chunks[(parts[^3], int.Parse(parts[^1]))].ToArray();
        if (CorruptDownload && bytes.Length > 0) bytes[0] ^= 0xff;
        return Task.FromResult(bytes);
    }
    public Task<bool> Progress(string path, JobProgress progress, CancellationToken ct) => Task.FromResult(CancelOnRestore && progress.Phase == "RESTORING_FILES");
}
