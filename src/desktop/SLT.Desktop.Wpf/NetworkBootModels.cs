namespace SLT.Desktop;

public sealed record NetworkBootConfig(int SchemaVersion, string InterfaceId, string BindIPv4,
    string AllowedCidr, string Mode, int HttpPort, int MaxConcurrentTransfers);
public sealed record NetworkBootInterface(string Id, string Name, string Description, string IPv4,
    string Cidr, long SpeedMbps, bool Eligible, string Reason)
{
    public string Label => $"{Name} • {IPv4} • {SpeedMbps} Mb/s{(Eligible ? "" : " • INDISPONÍVEL: " + Reason)}";
}
public sealed record NetworkBootArtifact(string Id, string RelativePath, long SizeBytes, string Sha256, string Source);
public sealed record NetworkBootProfile(string Id, string DisplayName, string Kind, string Architecture,
    string[] FirmwareSupport, string SecureBootStatus, string ValidationStatus, string Version,
    bool Enabled, NetworkBootArtifact[] Artifacts)
{
    public string FirmwareLabel => $"{Architecture} • {string.Join(" / ", FirmwareSupport)}";
    public string EnabledLabel => Enabled ? "Sim" : "Não";
}
public sealed record NetworkBootEvent(DateTimeOffset TimestampUtc, string Event, string Message)
{
    public string When => TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
}
public sealed record NetworkBootStatus(string State, NetworkBootConfig Config, DateTimeOffset? StartedAtUtc,
    string? LastError, NetworkBootProfile[] Profiles, NetworkBootEvent[] Events);
public sealed record NetworkBootValidation(bool Valid, string[] Errors, string[] Warnings);
