using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SLT.WindowsAgent;

public interface IFileJobTransport
{
    Task<FileBundle> Publish(string path, object body, CancellationToken ct);
    Task<FileBundle> Manifest(string path, CancellationToken ct);
    Task<FileChunk> UploadBytes(string path, byte[] bytes, string sha256, CancellationToken ct);
    Task<byte[]> DownloadBytes(string path, CancellationToken ct);
    Task<bool> Progress(string path, JobProgress progress, CancellationToken ct);
}

public sealed class FileJobExecutor(FileRootCatalog catalog, IFileJobTransport transport)
{
    public const int ChunkSize = 4 * 1024 * 1024;
    private sealed record Cursor(string RootId, string RelativePath, string Stamp, int Offset);
    private sealed record Snapshot(BundleEntry[] Entries, Dictionary<string, DateTimeOffset> Directories, long Bytes);

    public async Task<JobResult> Execute(JobPlan plan, CancellationToken ct)
    {
        if (plan.File is null) throw new InvalidDataException("Plano de arquivos ausente.");
        FileSafety.ValidateRequest(plan.Kind, plan.File);
        var root = catalog.Resolve(plan.File.RootId, plan.Kind == "FILE_RESTORE");
        await Report(plan, "PREFLIGHT", 0, 0, ct);
        return plan.Kind switch
        {
            "FILE_LIST" => new(true, "Página de arquivos consultada.", JsonSerializer.Serialize(List(root, plan.File), Wire.Json)),
            "FILE_BACKUP" => await Backup(plan, root, ct),
            "FILE_RESTORE" => await Restore(plan, root, ct),
            _ => throw new InvalidDataException("Job de arquivos não permitido.")
        };
    }

    public static FilePage List(FileRootConfiguration root, FileRequest request)
    {
        FileSafety.ValidateRequest("FILE_LIST", request);
        if (!root.AllowRead || root.Id != request.RootId) throw new UnauthorizedAccessException("Listagem fora do escopo.");
        using var directory = SecuredPath.Open(root, request.RelativePath);
        if (!directory.Info.IsDirectory) throw new IOException("Caminho consultado não é diretório.");
        var entries = new List<FilePageEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory.AbsolutePath))
        {
            if (entries.Count == FileSafety.MaximumEntries) throw new IOException("Diretório excede 100.000 entradas; selecione pasta menor.");
            var name = Path.GetFileName(path);
            var relative = Join(request.RelativePath, name);
            FileSafety.ValidateRelative(relative);
            using var child = SecuredPath.Open(root, relative, allowLinkLeaf: true);
            var info = child.Info;
            entries.Add(new(name, relative, info.IsDirectory, info.IsDirectory || info.IsLink ? 0 : info.Length,
                info.LastWriteTimeUtc, info.IsLink || info.Links > 1));
        }
        entries.Sort((a, b) => { var comparison = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Name, b.Name); });
        var stamp = FileSafety.Hash(JsonSerializer.SerializeToUtf8Bytes(entries, Wire.Json));
        var offset = 0;
        if (request.Cursor is not null)
        {
            Cursor cursor;
            try { cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(request.Cursor), Wire.Json) ?? throw new InvalidDataException(); }
            catch (Exception error) when (error is JsonException or FormatException or InvalidDataException) { throw new InvalidDataException("Cursor inválido; atualize a pasta."); }
            if (cursor.RootId != root.Id || cursor.RelativePath != request.RelativePath || cursor.Stamp != stamp || cursor.Offset < 0 || cursor.Offset > entries.Count)
                throw new InvalidDataException("Diretório alterado ou cursor de outra pasta. Recomece a listagem.");
            offset = cursor.Offset;
        }
        var page = entries.Skip(offset).Take(request.PageSize).ToArray();
        var next = offset + page.Length;
        var token = next < entries.Count ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(root.Id, request.RelativePath, stamp, next), Wire.Json)) : null;
        return new(root.Id, request.RelativePath, page, token, stamp);
    }

    private static Snapshot CaptureSelection(FileRootConfiguration root, FileRequest request)
    {
        var entries = new Dictionary<string, BundleEntry>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        void Add(string relative, bool recurse)
        {
            FileSafety.ValidateRelative(relative);
            if (entries.TryGetValue(relative, out var duplicate))
            {
                if (!duplicate.RelativePath.Equals(relative, StringComparison.Ordinal)) throw new InvalidDataException("Colisão de nomes por maiúsculas/minúsculas na origem.");
                return;
            }
            using var lease = SecuredPath.Open(root, relative);
            var info = lease.Info;
            if (info.IsLink) throw new UnauthorizedAccessException("Links não podem ser incluídos no backup.");
            if (entries.Count >= FileSafety.MaximumEntries) throw new IOException("Seleção excede 100.000 entradas.");
            bytes = checked(bytes + (info.IsDirectory ? 0 : info.Length));
            if (bytes > FileSafety.MaximumBundleBytes) throw new IOException("Seleção excede limite local de 1 TiB.");
            entries.Add(relative, new(Guid.NewGuid().ToString(), relative, info.IsDirectory, info.IsDirectory ? 0 : info.Length,
                info.LastWriteTimeUtc, []));
            if (!info.IsDirectory) return;
            directories.Add(relative, info.LastWriteTimeUtc);
            if (!recurse) return;
            foreach (var child in Directory.EnumerateFileSystemEntries(lease.AbsolutePath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                Add(Join(relative, Path.GetFileName(child)), true);
        }
        foreach (var path in request.Paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Count(c => c == '/')))
        {
            if (entries.TryGetValue(path, out var existing) && existing.IsDirectory) continue;
            var parent = "";
            foreach (var component in path.Split('/').SkipLast(1)) { parent = Join(parent, component); Add(parent, false); }
            Add(path, true);
        }
        return new(entries.Values.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), directories, bytes);
    }

    private async Task<JobResult> Backup(JobPlan plan, FileRootConfiguration root, CancellationToken ct)
    {
        var request = plan.File!;
        var selection = CaptureSelection(root, request);
        var path = JobPath(plan);
        var created = await transport.Publish(path + "/bundle", new { entries = selection.Entries, totalBytes = selection.Bytes }, ct);
        if (created.Id != request.BundleId || created.State != "CREATING" || created.TotalBytes != selection.Bytes)
            throw new InvalidDataException("Servidor alterou identidade/tamanho do bundle planejado.");
        var capturedChunks = selection.Entries.ToDictionary(e => e.Id, _ => new List<FileChunk>(), StringComparer.Ordinal);
        long processed = 0;
        foreach (var entry in selection.Entries.Where(e => !e.IsDirectory))
        {
            using var lease = SecuredPath.Open(root, entry.RelativePath, readFile: true);
            var before = lease.Info;
            if (before.IsDirectory || before.Length != entry.SizeBytes || before.LastWriteTimeUtc != entry.LastWriteTimeUtc)
                throw new IOException("Arquivo mudou após seleção. Nenhum backup parcial será promovido.");
            using var stream = lease.ReadStream();
            long offset = 0;
            var sequence = 0;
            while (offset < entry.SizeBytes)
            {
                await Report(plan, "BACKING_UP", processed, selection.Bytes, ct);
                var length = (int)Math.Min(ChunkSize, entry.SizeBytes - offset);
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, ct);
                var hash = FileSafety.Hash(bytes);
                var receipt = await transport.UploadBytes($"{path}/bundle/{entry.Id}/chunks/{sequence}", bytes, hash, ct);
                if (receipt.Sequence != sequence || receipt.SizeBytes != length || !receipt.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Recibo persistido de chunk inválido.");
                capturedChunks[entry.Id].Add(new(sequence, offset, length, hash));
                offset += length; processed += length; sequence++;
            }
            if (stream.Length != before.Length || lease.Info.LastWriteTimeUtc != before.LastWriteTimeUtc)
                throw new IOException("Arquivo instável durante captura; backup abortado.");
        }
        foreach (var entry in selection.Directories)
        {
            using var directory = SecuredPath.Open(root, entry.Key);
            if (directory.Info.LastWriteTimeUtc != entry.Value) throw new IOException("Pasta alterada durante captura; refaça o backup.");
        }
        await Report(plan, "VERIFYING", processed, selection.Bytes, ct);
        var bundle = await transport.Publish(path + "/bundle-complete", new { }, ct);
        FileSafety.ValidateBundle(bundle, request.BundleId);
        var finalEntries = bundle.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        if (bundle.TotalBytes != selection.Bytes || bundle.Entries.Length != selection.Entries.Length ||
            selection.Entries.Any(entry => !finalEntries.TryGetValue(entry.Id, out var e) || e.RelativePath != entry.RelativePath || e.SizeBytes != entry.SizeBytes || e.IsDirectory != entry.IsDirectory ||
                !e.Chunks.SequenceEqual(capturedChunks[entry.Id])))
            throw new InvalidDataException("Manifesto final diverge da seleção capturada.");
        return new(true, "Backup de arquivos concluído e verificado.", JsonSerializer.Serialize(new { bundleId = bundle.Id, entries = bundle.Entries.Length, totalBytes = bundle.TotalBytes }, Wire.Json));
    }

    private async Task<JobResult> Restore(JobPlan plan, FileRootConfiguration root, CancellationToken ct)
    {
        var request = plan.File!;
        var bundle = await transport.Manifest(JobPath(plan) + "/bundle", ct);
        FileSafety.ValidateBundle(bundle, request.BundleId); // Entire manifest checked before creating anything.
        var parentRelative = request.RelativePath.Contains('/') ? request.RelativePath[..request.RelativePath.LastIndexOf('/')] : "";
        var targetName = request.RelativePath.Split('/')[^1];
        using var parent = SecuredPath.Open(root, parentRelative);
        if (!parent.Info.IsDirectory) throw new IOException("Pasta de destino não existe.");
        var target = Path.Combine(parent.AbsolutePath, targetName);
        if (Exists(target)) throw new IOException("Destino já existe. Escolha uma pasta nova; nada será sobrescrito.");
        var free = new DriveInfo(Path.GetPathRoot(parent.AbsolutePath)!).AvailableFreeSpace;
        if (bundle.TotalBytes > free - 16L * 1024 * 1024) throw new IOException("Espaço insuficiente no volume de destino.");
        var stage = Path.Combine(parent.AbsolutePath, ".slt-" + Guid.NewGuid().ToString("N") + ".partial");
        NativeFiles.CreatePrivateStaging(stage);
        var promoted = false;
        try
        {
            using var stageLease = SecuredPath.OpenAbsolute(stage, deleteLeaf: true);
            NativeFiles.AssertPrivateDirectory(stage);
            long processed = 0;
            foreach (var entry in bundle.Entries.OrderBy(e => e.RelativePath.Count(c => c == '/')).ThenBy(e => e.IsDirectory ? 0 : 1))
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(stage, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!FileSafety.Under(destination, stage)) throw new UnauthorizedAccessException("Entrada fora do staging.");
                if (entry.IsDirectory) { Directory.CreateDirectory(destination); continue; }
                await using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                {
                    foreach (var chunk in entry.Chunks)
                    {
                        await Report(plan, "RESTORING_FILES", processed, bundle.TotalBytes, ct);
                        var bytes = await transport.DownloadBytes($"{JobPath(plan)}/bundle/{entry.Id}/chunks/{chunk.Sequence}", ct);
                        if (bytes.Length != chunk.SizeBytes || !FileSafety.Hash(bytes).Equals(chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Chunk corrompido, bloqueado antes de gravar o arquivo.");
                        await stream.WriteAsync(bytes, ct); processed += bytes.Length;
                    }
                    await stream.FlushAsync(ct); stream.Flush(true);
                }
                System.IO.File.SetLastWriteTimeUtc(destination, entry.LastWriteTimeUtc.UtcDateTime);
            }
            // Re-read every persisted byte before the single atomic rename of the complete tree.
            foreach (var entry in bundle.Entries.Where(e => !e.IsDirectory))
            {
                var destination = Path.Combine(stage, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != entry.SizeBytes) throw new IOException("Tamanho persistido diverge do manifesto.");
                foreach (var chunk in entry.Chunks)
                {
                    await Report(plan, "VERIFYING_FILES", processed, bundle.TotalBytes, ct);
                    var bytes = new byte[chunk.SizeBytes];
                    await stream.ReadExactlyAsync(bytes, ct);
                    if (!FileSafety.Hash(bytes).Equals(chunk.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Falha na releitura do arquivo restaurado.");
                }
            }
            foreach (var directory in bundle.Entries.Where(e => e.IsDirectory).OrderByDescending(e => e.RelativePath.Count(c => c == '/')))
                Directory.SetLastWriteTimeUtc(Path.Combine(stage, directory.RelativePath.Replace('/', Path.DirectorySeparatorChar)), directory.LastWriteTimeUtc.UtcDateTime);
            await Report(plan, "PROMOTING_FILES", processed, bundle.TotalBytes, ct);
            if (Exists(target)) throw new IOException("Destino foi criado durante transferência; promoção bloqueada sem sobrescrever.");
            stageLease.PromoteDirectory(target); // Rename the pinned handle, keeping anti-reparse/delete protection through ACL promotion.
            promoted = true;
            NativeFiles.InheritDestinationPermissions(target);
            return new(true, "Arquivos recebidos em pasta nova, verificados e promovidos atomicamente.", JsonSerializer.Serialize(new { bundleId = bundle.Id, rootId = root.Id, relativePath = request.RelativePath, totalBytes = bundle.TotalBytes }, Wire.Json));
        }
        finally
        {
            if (!promoted && FileSafety.Under(stage, parent.AbsolutePath) && Path.GetFileName(stage).StartsWith(".slt-", StringComparison.Ordinal) && Directory.Exists(stage))
            {
                try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    private static bool Exists(string path)
    {
        try { _ = System.IO.File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    private async Task Report(JobPlan plan, string phase, long processed, long total, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (await transport.Progress(JobPath(plan) + "/progress", new(phase, processed, total), ct))
            throw new OperationCanceledException("Transferência de arquivos cancelada; destino existente foi preservado.", ct);
    }
    private static string JobPath(JobPlan plan) => "/agent/v1/jobs/" + plan.Id;
    private static string Join(string left, string right) => left.Length == 0 ? right : left + "/" + right;
}
