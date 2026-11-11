using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Slt.NetworkBoot;

public static partial class BootArtifactStore
{
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
    public static string SafeFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 180 || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') || relative.Split('/').Any(s => s is "" or "." or "..")) throw new InvalidDataException("Caminho de artefato inválido.");
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Artefato fora do armazenamento.");
        var item = new FileInfo(path) as FileSystemInfo;
        while (item != null)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget != null) throw new InvalidDataException("Links/reparse points não permitidos no caminho de boot.");
            item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent;
        }
        return path;
    }
    public static BootProfile ReadManifest(string directory)
    {
        var file = SafeFile(directory, "netboot-manifest.json");
        if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Manifesto excede 64 KiB.");
        var p = BootJson.Parse<BootProfile>(File.ReadAllText(file)); Validate(p);
        return p with { ValidationStatus = "NETWORK_BOOT_DEV", SecureBootStatus = "UNSUPPORTED", Enabled = true };
    }
    public static void Validate(BootProfile p)
    {
        if (p.SchemaVersion != 1 || !BootJson.ValidId(p.Id) || p.DisplayName == null || p.DisplayName.Length is < 1 or > 100 || p.DisplayName.Any(char.IsControl) ||
            p.Version == null || p.Version.Length > 64 || p.Architecture != "X64" || p.FirmwareSupport == null || p.FirmwareSupport.Length != 2 || !p.FirmwareSupport.Contains("BIOS") || !p.FirmwareSupport.Contains("UEFI")) throw new InvalidDataException("Metadados de perfil inválidos.");
        string[] required = p.Kind switch { "RESCUE_LINUX" => ["kernel", "initrd", "rootfs"], "IPXE_BOOTSTRAP" => ["bios", "uefi"], _ => throw new InvalidDataException("Tipo de perfil ainda não suportado.") };
        // "uefi-local" is the optional loader a paired Windows client places on its own EFI partition (SLT mode).
        string[] optional = p.Kind == "IPXE_BOOTSTRAP" ? ["uefi-local"] : [];
        if (p.Artifacts == null || p.Artifacts.Any(a => a is null) || !required.All(id => p.Artifacts.Count(a => a.Id == id) == 1) || p.Artifacts.Any(a => !required.Contains(a.Id) && !optional.Contains(a.Id)) || p.Artifacts.Length > required.Length + optional.Length) throw new InvalidDataException("Artefatos obrigatórios ausentes/duplicados.");
        foreach (var a in p.Artifacts)
            if (!BootJson.ValidId(a.Id) || a.SizeBytes is < 1 or > 8L * 1024 * 1024 * 1024 || a.Sha256 == null || !HashPattern().IsMatch(a.Sha256) || a.Source == null || a.Source.Length > 512 || p.Kind == "IPXE_BOOTSTRAP" && a.SizeBytes > 2 * 1024 * 1024) throw new InvalidDataException("Tamanho/hash/origem de artefato inválido.");
        if (p.Artifacts.Select(a => a.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Artifacts.Length) throw new InvalidDataException("Paths duplicados.");
    }
    public static void Verify(string path, BootArtifact a)
    {
        using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length != a.SizeBytes || !Convert.ToHexStringLower(SHA256.HashData(f)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Tamanho/SHA256 de artefato não confere: " + a.Id);
    }
    public static BootProfile Import(string source, string store)
    {
        var p = ReadManifest(source);
        Directory.CreateDirectory(store);
        var target = Path.Combine(store, p.Id);
        if (Directory.Exists(target)) throw new InvalidDataException("Perfil já existe; use novo ID versionado.");
        var staging = Path.Combine(store, "incoming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var a in p.Artifacts)
            {
                var path = SafeFile(source, a.RelativePath); Verify(path, a);
                var output = Path.Combine(staging, a.Id);
                File.Copy(path, output, false); Verify(output, a);
            }
            p = p with { Artifacts = p.Artifacts.Select(a => a with { RelativePath = a.Id }).ToArray() };
            File.WriteAllText(Path.Combine(staging, "netboot-manifest.json"), BootJson.Serialize(p));
            Directory.Move(staging, target); return p;
        }
        catch { Directory.Delete(staging, true); throw; }
    }
    public static void VerifyAll(string store, IEnumerable<BootProfile> profiles)
    {
        foreach (var p in profiles)
        {
            Validate(p);
            foreach (var a in p.Artifacts) Verify(SafeFile(store, p.Id + "/" + a.RelativePath), a);
        }
    }
}
