using System.Security.Cryptography;
using System.Text;

namespace Slt.NetworkBoot;

// Installed with the verified Windows package, outside operational/imported state.
// Changing this registry requires the same write access as replacing the application.
public sealed record TrustedBootProfile(string Id, string Fingerprint);
public sealed record TrustedBootRegistry(int SchemaVersion, string SourceCommit, TrustedBootProfile[] Profiles);
public sealed class TrustedBootProfiles
{
    private readonly Dictionary<string, string> fingerprints = new(StringComparer.Ordinal);
    public TrustedBootProfiles() { }
    public TrustedBootProfiles(string file)
    {
        if (!File.Exists(file)) return;
        var safe = BootArtifactStore.SafeFile(Path.GetDirectoryName(Path.GetFullPath(file))!, Path.GetFileName(file));
        if (new FileInfo(safe).Length > 65536) throw new InvalidDataException("Catálogo de perfis verificados excede 64 KiB.");
        var registry = BootJson.Parse<TrustedBootRegistry>(File.ReadAllText(safe));
        if (registry.SchemaVersion != 1 || registry.SourceCommit?.Length != 40 || !registry.SourceCommit.All(Uri.IsHexDigit) || registry.Profiles is not { Length: >= 2 and <= 32 }) throw new InvalidDataException("Catálogo de laboratório inválido.");
        foreach (var profile in registry.Profiles)
        {
            if (profile == null || !BootJson.ValidId(profile.Id) || profile.Fingerprint?.Length != 64 || !profile.Fingerprint.All(Uri.IsHexDigit) || !fingerprints.TryAdd(profile.Id, profile.Fingerprint.ToLowerInvariant())) throw new InvalidDataException("Perfil verificado inválido/duplicado.");
        }
    }
    public static string Fingerprint(BootProfile p)
    {
        BootArtifactStore.Validate(p);
        var fields = new List<string> { "SLT_BOOT_PROFILE_V1", p.Id, p.Kind, p.Architecture, p.Version };
        foreach (var a in p.Artifacts.OrderBy(a => a.Id, StringComparer.Ordinal)) fields.Add($"{a.Id}:{a.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{a.Sha256.ToLowerInvariant()}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', fields) + "\n")));
    }
    public BootProfile Classify(BootProfile p) => p with
    {
        ValidationStatus = fingerprints.TryGetValue(p.Id, out var hash) && hash == Fingerprint(p) ? "NETWORK_BOOT_LAB" : "NETWORK_BOOT_DEV",
        SecureBootStatus = "UNSUPPORTED"
    };
}
