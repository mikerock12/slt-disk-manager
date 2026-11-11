using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Slt.Controller;

// Short-code pairing (docs/ADR/003-descoberta-e-pareamento-curto.md).
// Discovery only tells a client where a server claims to be. Trust comes from the code:
// it binds the TLS certificate the client actually saw to the CSR the server actually
// received, with nonces committed before either side can see the other's.
public static class Pairing
{
    public static bool IsHex(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    public static string Commit(string nonceHex) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(nonceHex)));
    public static string Code(string serverPin, string csrPem, string clientNonce, string serverNonce)
    {
        var text = string.Join('\n', "SLT-PAIR-2", serverPin.ToLowerInvariant(), Wire.Hash(csrPem), clientNonce.ToLowerInvariant(), serverNonce.ToLowerInvariant()) + "\n";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return (BinaryPrimitives.ReadUInt64BigEndian(digest) % 100_000_000UL).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    }
}

// Answers "is there an SLT server here?" on the bench LAN. The answer carries no secret and
// grants nothing: the address and pin it advertises are confirmed later by the pairing code.
public sealed class DiscoveryResponder : IDisposable
{
    public const int DefaultPort = 7444;
    private readonly UdpClient socket;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<IPAddress, bool> allowed;
    private byte[] Reply(string nonce) => JsonSerializer.SerializeToUtf8Bytes(new { slt = "server", v = 1, nonce, name, url, pin, version }, Wire.Json);
    private readonly string name, url, pin, version;
    private int answered; private long window = Environment.TickCount64;
    public int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    public DiscoveryResponder(IPAddress bind, int serverPort, int port, string pin, string version, Func<IPAddress, bool> allowed)
    {
        this.allowed = allowed; this.pin = pin; this.version = version; name = Environment.MachineName; url = $"https://{bind}:{serverPort}";
        // Broadcasts only reach a wildcard socket; the source filter below keeps the answer inside the bench subnet.
        socket = new UdpClient(new IPEndPoint(IPAddress.IsLoopback(bind) ? IPAddress.Loopback : IPAddress.Any, port));
        _ = Task.Run(Run);
    }
    public static string? ParseRequest(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 20 or > 256) return null;
        try
        {
            var reader = new Utf8JsonReader(packet, new JsonReaderOptions { MaxDepth = 2 });
            using var document = JsonDocument.ParseValue(ref reader); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("slt", out var kind) || kind.GetString() != "discover" ||
                !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1 || !root.TryGetProperty("nonce", out var nonce)) return null;
            var text = nonce.GetString();
            return text is { Length: >= 16 and <= 64 } && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
    private async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var received = await socket.ReceiveAsync(stop.Token);
                var remote = received.RemoteEndPoint.Address; if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
                if (!allowed(remote) || ParseRequest(received.Buffer) is not { } nonce) continue;
                if (Environment.TickCount64 - window > 1000) { answered = 0; window = Environment.TickCount64; }
                if (++answered > 20) continue;
                await socket.SendAsync(Reply(nonce), received.RemoteEndPoint, stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { /* ICMP unreachable from a client that already left */ }
        }
    }
    public void Dispose() { stop.Cancel(); socket.Dispose(); stop.Dispose(); }
}

public sealed partial class Controller
{
    // Set by the host once Network Boot is wired; null result means "boot service not serving Rescue now".
    public Func<(RescueBootSource? Source, string Reason)> RescueBootProvider { get; set; } = () => (null, "Boot pela rede indisponível neste servidor.");
    public string ServerUrl { get; set; } = "";

    private Enrollment EnrollWithTicket(EnrollmentRequest request)
    {
        Ensure(request.Mode == "RESCUE" && Pairing.IsHex(request.Ticket, 64), 400, "Ticket de modo SLT inválido.");
        var hash = Wire.Hash(request.Ticket!.ToLowerInvariant());
        var ticket = Db.Get<RescueTicket>("rescueticket", hash) ?? throw new ApiException(403, "Ticket de modo SLT desconhecido.");
        Ensure(!ticket.Used && ticket.ExpiresAtUtc > DateTimeOffset.UtcNow, 403, "Ticket de modo SLT expirado ou já utilizado.");
        var origin = Db.Get<Device>("device", ticket.OriginDeviceId);
        Ensure(origin?.TrustStatus == "TRUSTED", 403, "Cliente de origem não está mais autorizado.");
        Db.Put("rescueticket", hash, ticket with { Used = true });
        // A live session keeps no identity: earlier SLT-mode sessions of the same PC are retired.
        foreach (var old in Db.All<Device>("device").Where(d => d.OriginDeviceId == origin!.Id && d.TrustStatus == "TRUSTED")) Revoke(old.Id);
        var deviceId = Wire.Id(); var pem = Certificates.Issue(request.CsrPem, deviceId);
        using var cert = X509Certificate2.CreateFromPem(pem); Db.SetCredential(deviceId, cert.Thumbprint);
        Db.Put("device", deviceId, new Device(deviceId, ticket.Hostname + " (modo SLT)", "RESCUE", "TRUSTED", DateTimeOffset.MinValue, null, origin!.Id, ticket.UnattendedWrite));
        var e = new Enrollment(Wire.Id(), ticket.Hostname, "RESCUE", request.CsrPem, Wire.Hash(request.Secret), "", DateTimeOffset.UtcNow.AddMinutes(10), "APPROVED", deviceId, pem);
        Db.Put("enrollment", e.Id, e); Db.Log("system", "enrollment.ticket", $"{e.Id} device={deviceId} origin={origin.Id} job={ticket.JobId}"); return e;
    }
    public Enrollment Reveal(string id, string secret, string nonce)
    {
        var e = GetEnrollment(id, secret);
        Ensure(e.Status == "COMMITTED" && e.Commitment != null && e.ServerNonce != null, 409, "Pareamento não aguarda confirmação do cliente.");
        Ensure(Pairing.IsHex(nonce, 64) && SafeEquals(Pairing.Commit(nonce), e.Commitment!), 400, "Valor revelado não corresponde ao compromisso.");
        e = e with { Status = "PENDING", Code = Pairing.Code(Certificates.Pin, e.CsrPem, nonce, e.ServerNonce!) };
        Db.Put("enrollment", id, e); Db.Log("bootstrap", "enrollment.request", $"{e.Id} {e.Hostname} {e.Mode} short-code"); return e;
    }
    // Certificates last 30 days. A paired client asks for the next one over its current mTLS
    // session; no new pairing code is needed and the device identity does not change.
    public string Renew(string deviceId, string csrPem)
    {
        Ensure(csrPem is { Length: > 0 and < 12000 }, 400, "CSR inválido.");
        var device = Device(deviceId); Ensure(device.TrustStatus == "TRUSTED", 403, "Agente revogado.");
        var pem = Certificates.Issue(csrPem, deviceId);
        using var cert = X509Certificate2.CreateFromPem(pem); Db.SetPendingCredential(deviceId, cert.Thumbprint);
        Db.Log(deviceId, "credential.renewed", $"validade {cert.NotAfter.ToUniversalTime():yyyy-MM-dd}"); return pem;
    }
    public void RejectEnrollment(string id)
    {
        var e = Db.Get<Enrollment>("enrollment", id) ?? throw new ApiException(404, "Pareamento não encontrado.");
        Ensure(e.Status is "PENDING" or "COMMITTED", 409, "Pareamento já processado.");
        Db.Put("enrollment", id, e with { Status = "REJECTED" }); Db.Log("admin", "enrollment.rejected", id);
    }

    public static string RescueScript(RescueBootSource source, string serverUrl, string pin, string ticket, bool write)
    {
        var assets = $"{source.HttpBase}/boot/v1/assets/{source.ProfileId}";
        return string.Join('\n',
            "#!ipxe",
            "echo SLT Disk Manager - iniciando o modo SLT pela rede local",
            "dhcp || goto failed",
            "imgfree",
            $"kernel --name vmlinuz {assets}-kernel boot=live components nopersistence noeject fetch={assets}-rootfs.squashfs hostname=slt-rescue username=technician initrd=initrd.img console=ttyS0,115200n8 console=tty0 slt.server={serverUrl} slt.pin={pin} slt.ticket={ticket} slt.write={(write ? 1 : 0)} || goto failed",
            $"initrd --name initrd.img {assets}-initrd || goto failed",
            "boot || goto failed",
            ":failed",
            "echo Falha ao iniciar o modo SLT. Nenhum disco foi modificado. Voltando ao sistema instalado.",
            "sleep 8",
            "exit") + "\n";
    }
    private Job PlanRescueBoot(PlanRequest request, bool local)
    {
        Ensure(request.Consent, 400, "Registre a autorização do responsável antes de reiniciar o computador do cliente.");
        var d = Device(request.DeviceId);
        Ensure(d.Mode == "WINDOWS" && d.TrustStatus == "TRUSTED" && d.Inventory != null && d.LastSeenAtUtc > DateTimeOffset.UtcNow.AddSeconds(-45), 409, "O reinício em modo SLT exige o cliente Windows autorizado e online.");
        Ensure(d.Inventory!.Capabilities.Contains("REBOOT_RESCUE"), 409, "Este cliente não pode reiniciar em modo SLT: " + (d.Inventory.Notes.FirstOrDefault(n => n.StartsWith("MODO_SLT:", StringComparison.Ordinal))?[9..].Trim() ?? "exige UEFI com Secure Boot desativado e cliente instalado como serviço/administrador."));
        var (source, reason) = RescueBootProvider(); if (source == null) throw new ApiException(409, reason);
        Ensure(ServerUrl.Length > 0 && !ServerUrl.Contains("127.0.0.1", StringComparison.Ordinal), 409, "O servidor precisa estar escutando no IP da rede local (não só em loopback).");
        var id = Wire.Id();
        var options = new RescueBootOptions(source.LoaderSha256, source.LoaderSizeBytes, "", "", request.RescueBoot?.UnattendedWrite ?? true);
        var plan = new JobPlan(id, "REBOOT_RESCUE", d.Id, "", "", 0, 512, "", $"REINICIAR {d.Hostname} MODO SLT", DateTimeOffset.UtcNow.AddMinutes(15), true, RescueBoot: options);
        var job = new Job(id, plan, "AWAITING_APPROVAL", DateTimeOffset.UtcNow); Db.Put("job", id, job);
        Db.Log(local ? d.Id : "admin", local ? "rescue.boot.local-request" : "job.planned", Wire.Serialize(plan)); return job;
    }
    // Called when the reboot is authorised: only now the single-use ticket exists.
    private Job IssueRescueTicket(Job job)
    {
        var source = RescueBootProvider().Source ?? throw new ApiException(409, "Boot pela rede parou; inicie-o e aprove novamente.");
        var options = job.Plan.RescueBoot!;
        Ensure(source.LoaderSha256 == options.LoaderSha256 && source.LoaderSizeBytes == options.LoaderSizeBytes, 409, "O carregador de boot mudou depois do planejamento; crie outro plano.");
        var d = Device(job.Plan.DeviceId);
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Db.Put("rescueticket", Wire.Hash(ticket), new RescueTicket(Wire.Hash(ticket), d.Id, d.Hostname, job.Id, DateTimeOffset.UtcNow.AddMinutes(30), options.UnattendedWrite));
        return job with { Plan = job.Plan with { RescueBoot = options with { Ticket = ticket, Script = RescueScript(source, ServerUrl, Certificates.Pin, ticket, options.UnattendedWrite) } } };
    }
    // The technician sitting at the client asked for SLT mode: physical presence is the confirmation.
    public SignedAssignment LocalRescueBoot(string deviceId)
    {
        Ensure(!Db.All<Job>("job").Any(j => Active.Contains(j.State) && j.Plan.DeviceId == deviceId), 409, "Este computador já possui tarefa ativa; aguarde a conclusão.");
        var job = IssueRescueTicket(PlanRescueBoot(new("REBOOT_RESCUE", deviceId, "", null, true, RescueBoot: new(true)), true));
        job = job with { State = "RUNNING", ApprovedAtUtc = DateTimeOffset.UtcNow }; Db.Put("job", job.Id, job);
        Db.Log(deviceId, "job.dispatched", job.Id + " local"); return Certificates.Sign(job.Plan);
    }
    public byte[] RescueLoader(string deviceId, string jobId)
    {
        var job = AgentJob(deviceId, jobId, "REBOOT_RESCUE"); var source = RescueBootProvider().Source ?? throw new ApiException(409, "Boot pela rede indisponível.");
        var bytes = File.ReadAllBytes(source.LoaderPath);
        Ensure(bytes.Length == job.Plan.RescueBoot!.LoaderSizeBytes && Convert.ToHexStringLower(SHA256.HashData(bytes)) == job.Plan.RescueBoot.LoaderSha256, 409, "Carregador de boot não confere com o plano assinado.");
        return bytes;
    }

    private static readonly string[] FormatTables = ["GPT", "MBR"], FormatSystems = ["NTFS", "EXFAT", "FAT32", "EXT4"];
    private Job PlanFormat(PlanRequest request)
    {
        Ensure(request.Consent, 400, "Autorização do responsável obrigatória para apagar e formatar o disco.");
        var d = Device(request.DeviceId);
        Ensure(d.Mode == "RESCUE" && d.TrustStatus == "TRUSTED" && d.Inventory != null && d.LastSeenAtUtc > DateTimeOffset.UtcNow.AddSeconds(-45) && d.Inventory.Capabilities.Contains("FORMAT_DISK"), 409, "Formatar exige o computador em modo SLT/Rescue, autorizado, online e com gravação habilitada.");
        var disk = d.Inventory!.Disks.SingleOrDefault(x => x.Fingerprint == request.DiskFingerprint) ?? throw new ApiException(400, "Selecione um disco inequívoco do inventário atual.");
        Ensure(!disk.Mounted && !disk.IsSystem && !disk.IsReadOnly && (disk.Serial.Length > 0 || d.Inventory.Notes.Contains("SYNTHETIC_FIXTURE")), 409, "Disco montado, protegido, em uso pelo sistema ou sem identidade suficiente.");
        var format = ValidateFormat(request.Format ?? throw new ApiException(400, "Escolha tabela de partições e sistema de arquivos."), disk);
        var id = Wire.Id();
        var plan = new JobPlan(id, "FORMAT_DISK", d.Id, disk.Fingerprint, disk.Path, disk.SizeBytes, disk.LogicalSectorBytes, "", $"FORMATAR {d.Hostname} {disk.Fingerprint[..12]}", DateTimeOffset.UtcNow.AddMinutes(15), true, Format: format);
        var job = new Job(id, plan, "AWAITING_APPROVAL", DateTimeOffset.UtcNow); Db.Put("job", id, job); Db.Log("admin", "job.planned", Wire.Serialize(plan)); return job;
    }
    public static FormatOptions ValidateFormat(FormatOptions format, DiskInfo disk)
    {
        Ensure(FormatTables.Contains(format.TableType) && FormatSystems.Contains(format.FileSystem), 400, "Tabela (GPT/MBR) ou sistema de arquivos (NTFS, EXFAT, FAT32, EXT4) não suportado.");
        var label = format.Label ?? "";
        Ensure(label.Length <= (format.FileSystem is "FAT32" or "EXFAT" ? 11 : 16) && label.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'), 400, "Rótulo: até 11 (FAT32/exFAT) ou 16 caracteres, só letras, números, - e _.");
        Ensure(disk.SizeBytes >= 64L * 1024 * 1024, 400, "Disco pequeno demais para formatar.");
        Ensure(format.TableType != "MBR" || disk.SizeBytes / disk.LogicalSectorBytes <= uint.MaxValue, 400, "Disco acima de 2 TiB exige GPT.");
        Ensure(format.FileSystem != "FAT32" || disk.SizeBytes <= 2L * 1024 * 1024 * 1024 * 1024, 400, "FAT32 acima de 2 TiB não é suportado; use exFAT ou NTFS.");
        return format with { Label = label };
    }
    private void RevalidateFormat(Job job)
    {
        var d = Device(job.Plan.DeviceId); var disk = d.Inventory?.Disks.SingleOrDefault(x => x.Fingerprint == job.Plan.DiskFingerprint);
        Ensure(d.Mode == "RESCUE" && disk != null && disk.Path == job.Plan.DiskPath && disk.SizeBytes == job.Plan.DiskSizeBytes && disk.LogicalSectorBytes == job.Plan.LogicalSectorBytes && !disk.Mounted && !disk.IsSystem && !disk.IsReadOnly, 409, "Disco mudou ou ficou indisponível depois do planejamento.");
    }
}
