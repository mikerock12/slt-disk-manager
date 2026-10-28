using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SLT.WindowsAgent;

public sealed class PinnedTransport : IDisposable, IFileJobTransport
{
    private readonly HttpClient client;
    private readonly string pin;
    private readonly X509Certificate2? clientIdentity;
    public X509Certificate2? ServerCertificate { get; private set; }
    public PinnedTransport(AgentOptions options, X509Certificate2? identity = null)
    {
        pin = options.ServerPin;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false,
            ClientCertificateOptions = ClientCertificateOption.Manual, SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
        if (identity is not null)
        {
            // SChannel cannot authenticate with a PEM-created ephemeral private key.
            // Import in the current Windows account's protected key provider; no PFX is written to disk.
            var pfx = identity.Export(X509ContentType.Pfx);
            try
            {
                clientIdentity = X509CertificateLoader.LoadPkcs12(pfx, null,
                    OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            handler.ClientCertificates.Add(clientIdentity);
        }
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null || !VerifyPin(certificate, pin, DateTimeOffset.UtcNow)) return false;
            ServerCertificate?.Dispose();
            ServerCertificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
            return true;
        };
        client = new HttpClient(handler) { BaseAddress = options.Server, Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("User-Agent", "SLT.WindowsAgent/0.1.0");
    }
    public static bool VerifyPin(X509Certificate2 certificate, string expected, DateTimeOffset now)
    {
        if (certificate.NotBefore.ToUniversalTime() > now.UtcDateTime || certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime)
            return false;
        byte[] bytes;
        try { bytes = Convert.FromHexString(expected); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), bytes);
    }
    public async Task<T> Post<T>(string path, object body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, body, Wire.Json, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Wire.Json, ct)) ?? throw new HttpRequestException("Resposta vazia do servidor.");
    }
    public async Task<T?> Get<T>(string path, CancellationToken ct, string? enrollmentSecret = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (enrollmentSecret is not null) request.Headers.Add("X-Enrollment-Secret", enrollmentSecret);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Wire.Json, ct);
    }
    public async Task<byte[]> DownloadBytes(string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        if (response.Content.Headers.ContentLength > FileJobExecutor.ChunkSize) throw new IOException("Chunk excede limite.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await body.ReadAsync(buffer, ct)) != 0)
        {
            if (result.Length + count > FileJobExecutor.ChunkSize) throw new IOException("Chunk excede limite.");
            await result.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return result.ToArray();
    }
    public async Task<FileChunk> UploadBytes(string path, byte[] bytes, string sha256, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("X-Content-SHA256", sha256);
        using var response = await client.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<FileChunk>(Wire.Json, ct) ?? throw new IOException("Recibo de chunk vazio.");
    }
    async Task<FileBundle> IFileJobTransport.Publish(string path, object body, CancellationToken ct) => await Post<FileBundle>(path, body, ct);
    async Task<FileBundle> IFileJobTransport.Manifest(string path, CancellationToken ct) => await Get<FileBundle>(path, ct) ?? throw new IOException("Bundle vazio.");
    async Task<bool> IFileJobTransport.Progress(string path, JobProgress progress, CancellationToken ct) => (await Post<ProgressResponse>(path, progress, ct)).CancelRequested;
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        // Never echo arbitrary server bodies or request secrets into service logs.
        await response.Content.LoadIntoBufferAsync(64 * 1024, ct);
        throw new HttpRequestException($"Servidor recusou operação: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }
    public JobPlan VerifyAssignment(SignedAssignment assignment, string deviceId, DateTimeOffset now)
        => VerifySignedAssignment(assignment, ServerCertificate ?? throw new InvalidOperationException("Sessão TLS fixada ainda não estabelecida."), deviceId, now);

    public static JobPlan VerifySignedAssignment(SignedAssignment assignment, X509Certificate2 serverCertificate, string deviceId, DateTimeOffset now)
    {
        if (assignment.Payload.Length > 4 * 1024 * 1024 || assignment.Signature.Length > 4096)
            throw new InvalidOperationException("Plano excede limite permitido.");
        var payload = Convert.FromBase64String(assignment.Payload);
        var signature = Convert.FromBase64String(assignment.Signature);
        using var key = serverCertificate.GetRSAPublicKey() ?? throw new InvalidOperationException("Certificado fixado não possui RSA.");
        if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("Assinatura do plano inválida.");
        return ValidatePlan(JsonSerializer.Deserialize<JobPlan>(payload, Wire.Json) ?? throw new InvalidOperationException("Plano vazio."), deviceId, now);
    }
    public static JobPlan ValidatePlan(JobPlan plan, string deviceId, DateTimeOffset now)
    {
        if (!Guid.TryParse(plan.Id, out _) || plan.DeviceId != deviceId || !plan.Consent ||
            plan.ExpiresAtUtc <= now || plan.ExpiresAtUtc > now.AddHours(24))
            throw new InvalidOperationException("Plano expirado ou com identidade/autorização inválida.");
        if (plan.Kind is not ("INVENTORY" or "SMART" or "FILE_LIST" or "FILE_BACKUP" or "FILE_RESTORE"))
            throw new InvalidOperationException("Tipo de job não permitido no agente Windows.");
        if (plan.Kind.StartsWith("FILE_", StringComparison.Ordinal))
        {
            if (plan.File is null) throw new InvalidOperationException("Job de arquivo sem escopo assinado.");
            FileSafety.ValidateRequest(plan.Kind, plan.File);
        }
        return plan;
    }
    public void Dispose() { client.Dispose(); ServerCertificate?.Dispose(); clientIdentity?.Dispose(); }
}
