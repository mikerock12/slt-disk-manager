namespace Slt.Controller.LocalHost;

// Administration of the server machine itself (loopback + admin token, like every /api route).
public static class LocalEndpoints
{
    public static void Map(WebApplication app, ServerDiskManager disks, HyperVManager vms, KamikazeManager kamikaze)
    {
        const string p = "/api/v1/server-local";
        app.MapGet(p + "/disks", (CancellationToken ct) => disks.InventoryAsync(ct));
        app.MapGet(p + "/disk-operations", () => disks.List());
        app.MapGet(p + "/disk-operations/{id}", (string id) => disks.Get(id));
        app.MapPost(p + "/disk-operations", (ServerDiskRequest r, CancellationToken ct) => disks.PlanAsync(r, ct));
        app.MapPost(p + "/disk-operations/{id}/confirm", (string id, ConfirmRequest r, CancellationToken ct) => disks.ConfirmAsync(id, r.Challenge, ct));
        app.MapPost(p + "/disk-operations/{id}/cancel", (string id) => disks.Cancel(id));

        app.MapGet(p + "/vms", (CancellationToken ct) => vms.StatusAsync(ct));
        app.MapPost(p + "/vms/plan", (VmCreateRequest r, CancellationToken ct) => vms.PlanAsync(r, ct));
        app.MapPost(p + "/vms/plan/{id}/confirm", (string id, ConfirmRequest r, CancellationToken ct) => vms.ConfirmAsync(id, r.Challenge, ct));
        app.MapPost(p + "/vms/{name}/{action}", (string name, string action, VmNameRequest? r, CancellationToken ct) => vms.ActionAsync(name, action, r, ct));

        app.MapGet(p + "/kamikaze", (CancellationToken ct) => kamikaze.StatusAsync(disks, ct));
        app.MapPost(p + "/kamikaze/plan", (KamikazePlanRequest r, CancellationToken ct) => kamikaze.PlanAsync(r, disks, ct));
        app.MapPost(p + "/kamikaze/{id}/confirm", (string id, ConfirmRequest r) => kamikaze.Confirm(id, r.Challenge));
        app.MapPost(p + "/kamikaze/{id}/reboot", (string id) => kamikaze.RebootAsync(id));
        app.MapPost(p + "/kamikaze/{id}/cancel", (string id) => kamikaze.CancelAsync(id));
    }
}
