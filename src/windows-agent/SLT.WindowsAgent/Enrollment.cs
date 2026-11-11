using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SLT.WindowsAgent;

public static class Enrollment
{
    public static bool HasIdentity(SecureState state) =>
        state.Read<Identity>("identity.json") is not null && File.Exists(state.FilePath("client-cert.pem")) && File.Exists(state.FilePath("client-key.pem"));

    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(10);

    // A renewal interrupted between its two file replacements is completed here: both ".next"
    // files are written before either replacement starts, and the key is replaced first.
    public static void CompleteRenewal(SecureState state)
    {
        var nextKey = state.FilePath("client-key.next.pem"); var nextCert = state.FilePath("client-cert.next.pem");
        if (!File.Exists(nextCert)) { if (File.Exists(nextKey)) File.Delete(nextKey); return; }
        if (File.Exists(nextKey)) File.Move(nextKey, state.FilePath("client-key.pem"), true);
        File.Move(nextCert, state.FilePath("client-cert.pem"), true);
    }

    // Asks the server for the next certificate while the current one is still valid, so a client
    // installed as a service does not stop after 30 days. True when the identity files changed.
    public static async Task<bool> RenewIfNeeded(SecureState state, PinnedTransport transport, string deviceId, X509Certificate2 current,
        Action<string> log, CancellationToken ct, DateTimeOffset? now = null)
    {
        if (current.NotAfter.ToUniversalTime() - (now ?? DateTimeOffset.UtcNow).UtcDateTime > RenewBefore) return false;
        using var key = RSA.Create(3072);
        var csr = new CertificateRequest("CN=SLT-Windows-Renewal", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        var response = await transport.Post<RenewResponse>("/agent/v1/renew", new RenewRequest(csr), ct);
        using var issued = X509Certificate2.CreateFromPem(response.CertificatePem);
        if (issued.GetNameInfo(X509NameType.SimpleName, false) != deviceId || issued.NotAfter <= current.NotAfter) throw new InvalidOperationException("Certificado renovado não corresponde a este computador.");
        using var check = issued.CopyWithPrivateKey(key);
        state.WriteText("client-key.next.pem", key.ExportPkcs8PrivateKeyPem());
        state.WriteText("client-cert.next.pem", response.CertificatePem);
        CompleteRenewal(state);
        log($"Certificado deste cliente renovado até {issued.NotAfter.ToLocalTime():dd/MM/yyyy}.");
        return true;
    }

    public static async Task<(string DeviceId, X509Certificate2 Certificate)> GetIdentity(AgentOptions options, SecureState state,
        Action<string> log, CancellationToken ct)
    {
        CompleteRenewal(state);
        var identity = state.Read<Identity>("identity.json");
        if (identity is not null && File.Exists(state.FilePath("client-cert.pem")) && File.Exists(state.FilePath("client-key.pem")))
        {
            if (!Guid.TryParse(identity.DeviceId, out _)) throw new InvalidOperationException("Identidade persistida inválida.");
            var existing = X509Certificate2.CreateFromPemFile(state.FilePath("client-cert.pem"), state.FilePath("client-key.pem"));
            if (existing.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException("O certificado deste cliente expirou (ele ficou mais de 30 dias sem falar com o servidor). No painel do servidor, desconecte este computador; aqui, apague a pasta de estado do cliente e conecte de novo com um código.");
            return (identity.DeviceId, existing);
        }
        using var key = RSA.Create(3072);
        RSA actualKey = key;
        if (File.Exists(state.FilePath("client-key.pem")))
        {
            actualKey = RSA.Create();
            actualKey.ImportFromPem(File.ReadAllText(state.FilePath("client-key.pem")));
        }
        else state.WriteText("client-key.pem", key.ExportPkcs8PrivateKeyPem());
        try
        {
            using var transport = new PinnedTransport(options);
            var server = options.Server!.GetLeftPart(UriPartial.Authority);
            var pending = state.Read<EnrollmentPending>("enrollment.json");
            // A request made to another server (or certificate) is not resumed.
            if (pending is not null && (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow || pending.ServerUrl != server || pending.ServerPin != options.ServerPin)) pending = null;
            if (pending is null)
            {
                var csr = new CertificateRequest("CN=SLT-Windows-Enrollment", actualKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
                var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var created = await transport.Post<EnrollmentCreated>("/agent/v1/enroll",
                    new EnrollmentRequest(Environment.MachineName, "WINDOWS", csr, secret, Pairing.Commit(nonce)), ct);
                string code;
                if (created.ServerNonce is { Length: 64 } serverNonce && serverNonce.All(Uri.IsHexDigit))
                {
                    // The code binds the certificate this connection actually presented (options.ServerPin is what
                    // the TLS callback enforced) to our CSR. Another machine answering in the server's place shows
                    // the operator a different code than the one on this screen.
                    await transport.Post<object>("/agent/v1/enroll/" + Uri.EscapeDataString(created.Id) + "/reveal", new EnrollmentReveal(nonce), ct, secret);
                    code = Pairing.Code(options.ServerPin, csr, nonce, serverNonce);
                }
                else if (options.PinVerified && created.Code is { Length: 8 }) code = created.Code;
                else throw new InvalidOperationException("Este servidor é de uma versão anterior e não confirma o pareamento por código. Atualize o servidor ou informe --server e --server-pin.");
                pending = new(created.Id, secret, code, created.ExpiresAtUtc, server, options.ServerPin);
                state.Write("enrollment.json", pending);
            }
            ShowCode(log, pending, server);
            while (DateTimeOffset.UtcNow < pending.ExpiresAtUtc)
            {
                var response = await transport.Get<EnrollmentStatus>("/agent/v1/enroll/" + Uri.EscapeDataString(pending.Id), ct, pending.Secret);
                if (response?.Status == "APPROVED")
                {
                    if (!Guid.TryParse(response.DeviceId, out _) || response.CertificatePem is null || response.CaPem is null)
                        throw new InvalidOperationException("Resposta de pareamento inválida.");
                    using var publicCert = X509Certificate2.CreateFromPem(response.CertificatePem);
                    using var ca = X509Certificate2.CreateFromPem(response.CaPem);
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (!chain.Build(publicCert) || publicCert.GetNameInfo(X509NameType.SimpleName, false) != response.DeviceId)
                        throw new InvalidOperationException("Certificado emitido não comprova a identidade esperada.");
                    var certificate = publicCert.CopyWithPrivateKey(actualKey);
                    state.WriteText("client-cert.pem", response.CertificatePem);
                    state.WriteText("ca.pem", response.CaPem);
                    state.Write("identity.json", new Identity(response.DeviceId));
                    // From now on this address and certificate are the confirmed server of this client.
                    ServerLocator.Save(state, options, "");
                    File.Delete(state.FilePath("enrollment.json"));
                    log("Pareamento aprovado. Este computador está conectado ao servidor.");
                    return (response.DeviceId, certificate);
                }
                if (response?.Status is "EXPIRED" or "REJECTED")
                {
                    File.Delete(state.FilePath("enrollment.json"));
                    throw new InvalidOperationException("Pareamento expirado ou rejeitado no servidor. Abra o cliente de novo para gerar outro código.");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            File.Delete(state.FilePath("enrollment.json"));
            throw new InvalidOperationException("Pareamento expirou. Abra o cliente de novo para gerar outro código.");
        }
        finally { if (!ReferenceEquals(actualKey, key)) actualKey.Dispose(); }
    }

    private static void ShowCode(Action<string> log, EnrollmentPending pending, string server)
    {
        log($"PAREAMENTO: computador {Environment.MachineName}; código {Pairing.Spaced(pending.Code)}; servidor {server}; validade {pending.ExpiresAtUtc.ToLocalTime():HH:mm}.");
        if (!Environment.UserInteractive || Console.IsOutputRedirected) return;
        Console.WriteLine();
        Console.WriteLine("  ============================================================");
        Console.WriteLine("   SLT DISK MANAGER - CONECTAR ESTE COMPUTADOR");
        Console.WriteLine("  ------------------------------------------------------------");
        Console.WriteLine($"   Computador : {Environment.MachineName}");
        Console.WriteLine($"   Servidor   : {server}");
        Console.WriteLine();
        Console.WriteLine($"        CÓDIGO :   {Pairing.Spaced(pending.Code)}");
        Console.WriteLine();
        Console.WriteLine("   No painel do servidor, aba \"Conectar computador\", selecione");
        Console.WriteLine("   este computador e digite o código acima.");
        Console.WriteLine($"   O código vale até {pending.ExpiresAtUtc.ToLocalTime():HH:mm}. Aguardando a aprovação...");
        Console.WriteLine("  ============================================================");
        Console.WriteLine();
    }
}
