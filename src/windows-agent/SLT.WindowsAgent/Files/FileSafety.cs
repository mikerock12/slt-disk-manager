using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SLT.WindowsAgent;

public static class FileSafety
{
    public const int MaximumEntries = 100_000;
    public const int MaximumDepth = 64;
    public const long MaximumBundleBytes = 1L << 40; // Local upper bound: 1 TiB, never a memory allocation.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };

    public static void ValidateRelative(string path, bool allowEmpty = false)
    {
        if (path is null || path.Length > 2048 || !allowEmpty && path.Length == 0)
            throw new InvalidDataException("Caminho relativo vazio ou excessivo.");
        if (allowEmpty && path.Length == 0) return;
        if (path.Any(c => c < 32 || "\\:<>\"|?*".Contains(c)) || Path.IsPathRooted(path))
            throw new InvalidDataException("Caminho relativo contém raiz, ADS ou caracteres proibidos.");
        var components = path.Split('/');
        if (components.Length > MaximumDepth) throw new InvalidDataException("Profundidade de arquivo excede 64.");
        foreach (var component in components)
            if (component.Length is 0 or > 255 || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ') ||
                Reserved.Contains(component.Split('.')[0].TrimEnd(' ')))
                throw new InvalidDataException("Componente de caminho Windows inválido ou reservado.");
    }

    public static void ValidateRequest(string kind, FileRequest request)
    {
        if (request.RootId is null || !Regex.IsMatch(request.RootId, "^[a-zA-Z0-9_-]{1,48}$")) throw new InvalidDataException("Escopo inválido.");
        ValidateRelative(request.RelativePath, kind != "FILE_RESTORE");
        if (request.Paths?.Length > 1000 || request.Cursor?.Length > 8192 || request.Name?.Length > 256)
            throw new InvalidDataException("Seleção ou cursor excede limite local.");
        if (kind == "FILE_LIST" && request.PageSize is < 1 or > 200) throw new InvalidDataException("Página deve conter 1 a 200 itens.");
        if (kind == "FILE_BACKUP" && request.Paths is not { Length: > 0 }) throw new InvalidDataException("Selecione arquivos ou pastas para backup.");
        foreach (var relative in request.Paths ?? []) ValidateRelative(relative);
        if (kind is "FILE_BACKUP" or "FILE_RESTORE" && !Guid.TryParse(request.BundleId, out _)) throw new InvalidDataException("BundleId deve ser UUID.");
    }

    public static bool Under(string candidate, string parent)
    {
        candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateBundle(FileBundle bundle, string expectedId)
    {
        if (bundle.SchemaVersion != 1 || !Guid.TryParse(bundle.Id, out _) || bundle.Id != expectedId || bundle.State != "AVAILABLE" ||
            bundle.Entries is null || bundle.Entries.Length > MaximumEntries || bundle.TotalBytes is < 0 or > MaximumBundleBytes)
            throw new InvalidDataException("Manifesto de bundle inválido ou não disponível.");
        var paths = new Dictionary<string, BundleEntry>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in bundle.Entries)
        {
            ValidateRelative(entry.RelativePath);
            if (!Guid.TryParse(entry.Id, out _) || !ids.Add(entry.Id) || !paths.TryAdd(entry.RelativePath, entry) || entry.Chunks is null || entry.SizeBytes < 0)
                throw new InvalidDataException("Entradas duplicadas, colisão de nomes ou ID inválido.");
            if (entry.IsDirectory && (entry.SizeBytes != 0 || entry.Chunks.Length != 0)) throw new InvalidDataException("Diretório com dados de arquivo.");
            if (entry.LastWriteTimeUtc.UtcDateTime < DateTime.FromFileTimeUtc(0)) throw new InvalidDataException("Data de arquivo inválida.");
            long offset = 0;
            for (var sequence = 0; sequence < entry.Chunks.Length; sequence++)
            {
                var chunk = entry.Chunks[sequence];
                if (chunk.Sequence != sequence || chunk.OffsetBytes != offset || chunk.SizeBytes <= 0 || chunk.SizeBytes > FileJobExecutor.ChunkSize ||
                    chunk.Sha256 is null || chunk.Sha256.Length != 64 || !chunk.Sha256.All(Uri.IsHexDigit) ||
                    chunk.SizeBytes != Math.Min(FileJobExecutor.ChunkSize, entry.SizeBytes - offset))
                    throw new InvalidDataException("Chunks fora de ordem, tamanho ou hash inválido.");
                offset = checked(offset + chunk.SizeBytes);
            }
            if (offset != entry.SizeBytes) throw new InvalidDataException("Tamanho do arquivo não corresponde aos chunks.");
            total = checked(total + entry.SizeBytes);
            if (total > MaximumBundleBytes) throw new InvalidDataException("Bundle excede limite de 1 TiB.");
        }
        foreach (var item in paths.Values)
        {
            var parent = item.RelativePath;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (!paths.TryGetValue(parent, out var ancestor) || !ancestor.IsDirectory)
                    throw new InvalidDataException("Manifesto sem diretório ancestral explícito ou ancestral é arquivo.");
            }
        }
        if (total != bundle.TotalBytes) throw new InvalidDataException("Total do bundle incorreto.");
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed class FileRootCatalog
{
    private readonly FileRootConfiguration[] roots;
    public FileRootCatalog(AgentOptions options)
    {
        if (options.FileRootsPath is null) { roots = []; return; }
        var info = new FileInfo(options.FileRootsPath);
        if (info.Length > 65536) throw new InvalidDataException("Configuração de escopos excede 64 KiB.");
        roots = JsonSerializer.Deserialize<FileRootConfiguration[]>(System.IO.File.ReadAllText(info.FullName), Wire.Json)
            ?? throw new InvalidDataException("Configuração de escopos vazia.");
        if (roots.Length > 32) throw new InvalidDataException("Máximo de 32 escopos locais.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root.Id is null || !Regex.IsMatch(root.Id, "^[a-zA-Z0-9_-]{1,48}$") || !ids.Add(root.Id) ||
                root.Label is null || root.Label.Length > 128 || !root.AllowRead && !root.AllowWrite ||
                !Path.IsPathFullyQualified(root.Path) || root.Path.StartsWith("\\", StringComparison.Ordinal))
                throw new InvalidDataException("Escopo local inválido/duplicado ou caminho de rede.");
            var absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
            if (!Directory.Exists(absolute) || absolute == Path.GetPathRoot(absolute)) throw new InvalidDataException("Escopo exige pasta local existente, não a raiz de um volume.");
            foreach (var protectedPath in new[] { options.StateDirectory, AppContext.BaseDirectory, info.FullName })
                if (FileSafety.Under(absolute, protectedPath) || FileSafety.Under(protectedPath, absolute))
                    throw new InvalidDataException("Escopo não pode incluir estado, credenciais, binários ou configuração do agente.");
            using var lease = SecuredPath.OpenAbsolute(absolute);
            if (!lease.Info.IsDirectory) throw new InvalidDataException("Escopo não é diretório.");
            roots[i] = root with { Path = absolute };
        }
    }
    public FileRootInfo[] Inventory => roots.Select(r => new FileRootInfo(r.Id, r.Label, r.AllowRead, r.AllowWrite)).ToArray();
    public string[] Capabilities => (roots.Any(r => r.AllowRead) ? new[] { "FILE_LIST", "FILE_BACKUP" } : [])
        .Concat(roots.Any(r => r.AllowWrite) ? new[] { "FILE_RESTORE" } : []).ToArray();
    public FileRootConfiguration Resolve(string id, bool write)
    {
        var root = roots.SingleOrDefault(r => r.Id == id) ?? throw new UnauthorizedAccessException("Escopo não configurado localmente.");
        if (write ? !root.AllowWrite : !root.AllowRead) throw new UnauthorizedAccessException("Operação não permitida neste escopo.");
        return root;
    }
}
