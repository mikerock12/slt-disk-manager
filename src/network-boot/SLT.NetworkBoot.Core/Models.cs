using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slt.NetworkBoot;

public sealed record NetworkBootConfig(int SchemaVersion = 1, string InterfaceId = "", string BindIPv4 = "", string AllowedCidr = "", string Mode = "EXTERNAL_DHCP", int HttpPort = 8088, int MaxConcurrentTransfers = 8);
public sealed record BootArtifact(string Id, string RelativePath, long SizeBytes, string Sha256, string Source);
public sealed record BootProfile(int SchemaVersion, string Id, string DisplayName, string Kind, string Architecture, string[] FirmwareSupport, string SecureBootStatus, string ValidationStatus, string Version, BootArtifact[] Artifacts, bool Enabled = true);
public sealed record BootInterface(string Id, string Name, string Description, string Ipv4, string Cidr, long SpeedMbps, bool Eligible, string Reason);
public sealed record BootEvent(DateTimeOffset TimestampUtc, string Event, string Message);
public sealed record BootValidation(bool Valid, string[] Errors, string[] Warnings);
public sealed record BootStatus(string State, NetworkBootConfig Config, DateTimeOffset? StartedAtUtc, string? LastError, BootProfile[] Profiles, BootEvent[] Events);
public sealed record BootLaunch(NetworkBootConfig Config, string StorePath, BootProfile[] Profiles, bool Laboratory = false);
public static partial class BootJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("JSON vazio.");
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    public static bool ValidId(string? id) => id != null && IdPattern().IsMatch(id);
    public static bool Private(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168);
    }
}
