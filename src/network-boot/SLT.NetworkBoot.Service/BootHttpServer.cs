using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Win32.SafeHandles;

namespace Slt.NetworkBoot;

public sealed class BootHttpServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<FileStream> handles = [];
    private BootHttpServer(WebApplication app) => this.app = app;
    public static async Task<BootHttpServer> Start(BootLaunch launch, CancellationToken ct = default, Action<BootEvent>? report = null)
    {
        var c = launch.Config; var address = IPAddress.Parse(c.BindIPv4); var network = IPNetwork.Parse(c.AllowedCidr);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(address, c.HttpPort); k.Limits.MaxRequestBodySize = 0; k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10); k.Limits.MaxConcurrentConnections = c.MaxConcurrentTransfers + 8; });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            // Global limit bounds attacker-controlled cardinality and protects files plus menu.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter("boot", _ => new() { PermitLimit = 120, Window = TimeSpan.FromSeconds(1), QueueLimit = 0 }));
            o.AddConcurrencyLimiter("transfer", l => { l.PermitLimit = c.MaxConcurrentTransfers; l.QueueLimit = 0; });
        });
        var app = builder.Build(); var server = new BootHttpServer(app);
        try
        {
            var assets = new Dictionary<string, (FileStream Stream, BootArtifact Artifact)>();
            foreach (var p in launch.Profiles.Where(p => p.Enabled)) foreach (var a in p.Artifacts)
            {
                var stream = File.Open(BootArtifactStore.SafeFile(launch.StorePath, p.Id + "/" + a.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
                server.handles.Add(stream);
                if (stream.Length != a.SizeBytes || !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Artefato alterado ao iniciar.");
                assets.Add(p.Id + "-" + a.Id, (stream, a));
                // live-boot selects its fetch decoder using the URL extension.
                if (p.Kind == "RESCUE_LINUX" && a.Id == "rootfs") assets.Add(p.Id + "-rootfs.squashfs", (stream, a));
            }
            app.Use(async (context, next) =>
            {
                if (context.Connection.RemoteIpAddress is not { } remote || !network.Contains(remote)) { context.Response.StatusCode = 403; return; }
                context.Response.Headers.XContentTypeOptions = "nosniff"; context.Response.Headers.CacheControl = "no-store";
                await next(context);
                if (context.Response.StatusCode is 200 or 206 && context.Request.Method == "GET" && context.Request.Path.StartsWithSegments("/boot/v1"))
                    report?.Invoke(new(DateTimeOffset.UtcNow, "HTTP", $"{remote} {context.Request.Path}: HTTP {context.Response.StatusCode}, {context.Response.ContentLength?.ToString() ?? "stream"} bytes."));
            });
            app.UseRateLimiter();
            app.MapGet("/boot/v1/health", () => new { status = "running" });
            app.MapMethods("/boot/v1/menu.ipxe", ["GET", "HEAD"], () => Results.Text(BootScriptRenderer.Menu(c, launch.Profiles), "text/plain"));
            app.MapMethods("/boot/v1/assets/{id}", ["GET", "HEAD"], (string id, HttpContext context) =>
            {
                if (id.Length > 140 || !assets.TryGetValue(id, out var asset)) return Results.NotFound();
                if (context.Request.Method == "GET") report?.Invoke(new(DateTimeOffset.UtcNow, "HTTP_BEGIN", $"{context.Connection.RemoteIpAddress} {id}: transferência iniciada."));
                return Results.Stream(new PositionalStream(asset.Stream.SafeFileHandle, asset.Artifact.SizeBytes), "application/octet-stream", enableRangeProcessing: true, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue('"' + asset.Artifact.Sha256.ToLowerInvariant() + '"'));
            }).RequireRateLimiting("transfer");
            await app.StartAsync(ct); return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(limit.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync(); foreach (var handle in handles) handle.Dispose();
    }
    private sealed class PositionalStream(SafeFileHandle handle, long length) : Stream
    {
        private long position;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
        public override int Read(byte[] buffer, int offset, int count) { var n = RandomAccess.Read(handle, buffer.AsSpan(offset, count), position); position += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { int n = await RandomAccess.ReadAsync(handle, buffer, position, cancellationToken); position += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, SeekOrigin.End => length + offset, _ => throw new ArgumentException("Seek inválido.") };
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
