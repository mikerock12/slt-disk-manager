using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Slt.Controller;

try
{
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "SLTDiskManagerController");
var directory = Path.GetFullPath(builder.Configuration["state-dir"] ?? Path.Combine(AppContext.BaseDirectory, "state"));
Directory.CreateDirectory(directory);
// Claim state before opening keys/database or reconciling interrupted jobs.
using var instanceLock = new FileStream(Path.Combine(directory, "controller.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
// The directory is private before any key/token is written.
if (OperatingSystem.IsWindows())
{
    var info = new DirectoryInfo(directory); var acl = new System.Security.AccessControl.DirectorySecurity();
    acl.SetAccessRuleProtection(true, false);
    var rights = System.Security.AccessControl.FileSystemRights.FullControl;
    var inheritance = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
    foreach (var sid in new[] { System.Security.Principal.WindowsIdentity.GetCurrent().User!, new System.Security.Principal.SecurityIdentifier("S-1-5-18"), new System.Security.Principal.SecurityIdentifier("S-1-5-32-544") })
        acl.AddAccessRule(new(sid, rights, inheritance, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
    System.IO.FileSystemAclExtensions.SetAccessControl(info, acl);
}
else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var bind = IPAddress.Parse(builder.Configuration["bind"] ?? "127.0.0.1");
var port = int.Parse(builder.Configuration["port"] ?? "7443");
bool IsPrivate(IPAddress address)
{
    if (IPAddress.IsLoopback(address)) return true;
    var b = address.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168);
}
if (!IsPrivate(bind)) throw new InvalidOperationException("--bind deve ser IPv4 privado RFC1918 ou loopback explícito.");
var subnet = builder.Configuration["allowed-subnet"];
System.Net.IPNetwork? allowed = subnet != null ? System.Net.IPNetwork.Parse(subnet) : null;
if (!IPAddress.IsLoopback(bind) && (allowed == null || !allowed.Value.Contains(bind) || !IsPrivate(allowed.Value.BaseAddress)))
    throw new InvalidOperationException("Configure --allowed-subnet para a sub-rede privada da bancada (ex.: 192.168.1.0/24).");
var certs = new Certificates(directory, bind);
var tokenPath = Path.Combine(directory, "admin.token");
var token = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
File.WriteAllText(tokenPath, token);
File.WriteAllText(Path.Combine(directory, "connection.json"), Wire.Serialize(new { url = $"https://127.0.0.1:{port}", token, serverPin = certs.Pin, processId = Environment.ProcessId }));
File.WriteAllText(Path.Combine(directory, "server-info.txt"), $"Server: https://{bind}:{port}\nSHA256 pin: {certs.Pin}\nAdmin connection: {Path.Combine(directory, "connection.json")}\n");
if (!OperatingSystem.IsWindows()) foreach (var file in Directory.GetFiles(directory)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
var store = new Store(directory);
var controller = new Controller(store, certs, builder.Configuration["repository"] ?? Path.Combine(directory, "repository"));
builder.Services.AddSingleton(controller);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("enroll", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = Controller.ChunkSize + 65536;
    void Listen(IPAddress ip) => options.Listen(ip, port, listen => listen.UseHttps(https =>
    {
        https.ServerCertificate = certs.Server;
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.ClientCertificateValidation = (cert, _, _) => certs.ValidateClient(cert);
    }));
    Listen(IPAddress.Loopback); if (!IPAddress.IsLoopback(bind)) Listen(bind);
});
var app = builder.Build();
app.Use(async (ctx, next) =>
{
    try
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote == null || !(IPAddress.IsLoopback(remote) || IsPrivate(remote) && allowed?.Contains(remote) == true)) throw new ApiException(403, "Origem fora da rede autorizada.");
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            if (!IPAddress.IsLoopback(remote)) throw new ApiException(403, "Administração disponível somente no próprio servidor.");
            if (!Controller.SafeEquals(ctx.Request.Headers.Authorization.ToString(), $"Bearer {token}")) throw new ApiException(401, "Token administrativo obrigatório.");
        }
        else if (ctx.Request.Path.StartsWithSegments("/agent") && !ctx.Request.Path.StartsWithSegments("/agent/v1/enroll"))
        {
            lock (controller.Gate) ctx.Items["deviceId"] = controller.Authenticate(ctx.Connection.ClientCertificate);
        }
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await next(ctx);
    }
    catch (ApiException e) { ctx.Response.StatusCode = e.Status; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) when (e is JsonException or CryptographicException or ArgumentException or InvalidDataException or BadHttpRequestException)
    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Entrada inválida: " + e.Message }); }
    catch (Exception e)
    {
        app.Logger.LogError(e, "Falha de operação {Path}", ctx.Request.Path); ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "Falha interna; consulte o log local. Nenhuma etapa será repetida automaticamente." });
    }
});
app.UseRateLimiter();
T Locked<T>(Func<T> operation) { lock (controller.Gate) return operation(); }
string DeviceId(HttpContext ctx) => (string)ctx.Items["deviceId"]!;
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.1.0", protocol = 1 }));
app.MapPost("/agent/v1/enroll", (EnrollmentRequest r) => Locked(() => { var e = controller.Enroll(r); return Results.Ok(new { e.Id, e.Code, e.ExpiresAtUtc }); })).RequireRateLimiting("enroll");
app.MapGet("/agent/v1/enroll/{id}", (string id, HttpContext ctx) => Locked(() =>
{
    var e = controller.GetEnrollment(id, ctx.Request.Headers["X-Enrollment-Secret"].ToString());
    return Results.Ok(new { e.Status, e.DeviceId, e.CertificatePem, caPem = e.Status == "APPROVED" ? certs.CaPem : null });
})).RequireRateLimiting("enroll");
app.MapGet("/api/v1/enrollments", () => Locked(() => store.All<Enrollment>("enrollment").Select(e => new { e.Id, e.Hostname, e.Mode, e.Code, e.ExpiresAtUtc, status = e.ExpiresAtUtc < DateTimeOffset.UtcNow && e.Status == "PENDING" ? "EXPIRED" : e.Status }).ToArray()));
app.MapPost("/api/v1/enrollments/{id}/approve", (string id, CodeRequest r) => Locked(() => { var e = controller.ApproveEnrollment(id, r.Code); return Results.Ok(new { e.Id, e.Status, e.DeviceId }); }));
app.MapGet("/api/v1/devices", () => Locked(() => store.All<Device>("device")));
app.MapGet("/api/v1/devices/{id}", (string id) => Locked(() => controller.Device(id)));
app.MapPost("/api/v1/devices/{id}/revoke", (string id) => Locked(() => { controller.Revoke(id); return Results.Ok(new { ok = true }); }));
app.MapPost("/agent/v1/inventory", (Inventory r, HttpContext ctx) => Locked(() => { controller.Inventory(DeviceId(ctx), r); return Results.Ok(new { ok = true }); }));
app.MapGet("/agent/v1/jobs/next", (HttpContext ctx) => Locked(() => controller.Next(DeviceId(ctx)) is {} a ? Results.Ok(a) : (IResult)Results.NoContent()));
app.MapGet("/api/v1/jobs", () => Locked(() => store.All<Job>("job")));
app.MapPost("/api/v1/jobs/plan", (PlanRequest r) => Locked(() => controller.Plan(r)));
app.MapPost("/api/v1/jobs/{id}/approve", (string id, ApprovalRequest r) => Locked(() => controller.Approve(id, r.Challenge)));
app.MapPost("/api/v1/jobs/{id}/cancel", (string id) => Locked(() => controller.Cancel(id)));
app.MapPost("/agent/v1/jobs/{id}/progress", (string id, Progress r, HttpContext ctx) => Locked(() =>
{ controller.Progress(DeviceId(ctx), id, r); return Results.Ok(new { cancelRequested = controller.GetJob(id).CancelRequested }); }));
app.MapPost("/agent/v1/jobs/{id}/result", (string id, JobResult r, HttpContext ctx) => Locked(() =>
{ controller.Result(DeviceId(ctx), id, r); return Results.Ok(new { ok = true }); }));
app.MapGet("/api/v1/images", () => Locked(() => store.All<ImageManifest>("image")));
app.MapPost("/api/v1/images/{id}/verify", (string id) => Locked(() => controller.StartVerification(id)));
app.MapPut("/agent/v1/jobs/{id}/chunks/{seq:int}", async (string id, int seq, HttpContext ctx) =>
{
    using var memory = new MemoryStream(); var buffer = new byte[65536]; int count;
    while ((count = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
    { if (memory.Length + count > Controller.ChunkSize) throw new ApiException(413, "Chunk excede 4 MiB."); memory.Write(buffer, 0, count); }
    return Locked(() => controller.Upload(DeviceId(ctx), id, seq, memory.ToArray(), ctx.Request.Headers["X-Content-SHA256"].ToString()));
});
app.MapPost("/agent/v1/jobs/{id}/capture-complete", (string id, HttpContext ctx) => Locked(() => controller.Complete(DeviceId(ctx), id)));
app.MapGet("/agent/v1/jobs/{id}/manifest", (string id, HttpContext ctx) => Locked(() => controller.DownloadManifest(DeviceId(ctx), id)));
app.MapGet("/agent/v1/jobs/{id}/chunks/{seq:int}", (string id, int seq, HttpContext ctx) => Locked(() => Results.Bytes(controller.Download(DeviceId(ctx), id, seq), "application/octet-stream")));
app.MapGet("/api/v1/audit", () => Locked(() => store.Audit()));
app.MapGet("/api/v1/dashboard", () => Locked(() => new
{
    devices = store.All<Device>("device").Count,
    online = store.All<Device>("device").Count(d => d.TrustStatus == "TRUSTED" && d.LastSeenAtUtc > DateTimeOffset.UtcNow.AddSeconds(-45)),
    activeJobs = store.All<Job>("job").Count(j => j.State is "RUNNING" or "QUEUED" or "CANCELLING"),
    images = store.All<ImageManifest>("image").Count(i => i.State == "AVAILABLE"), repositoryFreeBytes = controller.FreeBytes()
}));
app.Lifetime.ApplicationStopped.Register(store.Dispose);
Console.WriteLine($"SLT Disk Manager 0.1.0 | TLS SHA256 {certs.Pin}\nConexão do painel: {Path.Combine(directory, "connection.json")}");
app.Run();
}
catch (Exception startupError)
{
    Console.Error.WriteLine("SLT Controller não iniciou: " + startupError.Message);
    Environment.ExitCode = 1;
}
