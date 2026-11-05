using Slt.NetworkBoot;

namespace Slt.Controller;

public static class NetworkBootEndpoints
{
    public sealed record StartRequest(bool ConfirmIsolatedNetwork);
    public sealed record ImportRequest(string DirectoryPath);
    public sealed record EnableRequest(bool Enabled);
    public static void Map(WebApplication app, NetworkBootManagement management)
    {
        const string prefix = "/api/v1/network-boot";
        app.MapGet(prefix + "/status", management.Status);
        app.MapGet(prefix + "/interfaces", NetworkTopologyValidator.Interfaces);
        app.MapGet(prefix + "/profiles", async () => (await management.Status()).Profiles);
        app.MapGet(prefix + "/events", async () => (await management.Status()).Events);
        app.MapPut(prefix + "/config", management.Configure);
        app.MapPost(prefix + "/validate", management.Validate);
        app.MapPost(prefix + "/start", (StartRequest r) => management.Start(r.ConfirmIsolatedNetwork));
        app.MapPost(prefix + "/stop", management.Stop);
        app.MapPost(prefix + "/profiles/import", (ImportRequest r) => management.Import(r.DirectoryPath));
        app.MapPut(prefix + "/profiles/{id}", (string id, EnableRequest r) => management.Enable(id, r.Enabled));
    }
}
