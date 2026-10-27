using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SLT.WindowsAgent;

public static class Enrollment
{
    public static async Task<(string DeviceId, X509Certificate2 Certificate)> GetIdentity(AgentOptions options, SecureState state,
        Action<string> log, CancellationToken ct)
    {
        var identity = state.Read<Identity>("identity.json");
        if (identity is not null && File.Exists(state.FilePath("client-cert.pem")) && File.Exists(state.FilePath("client-key.pem")))
        {
            if (!Guid.TryParse(identity.DeviceId, out _)) throw new InvalidOperationException("Identidade persistida inválida.");
            var existing = X509Certificate2.CreateFromPemFile(state.FilePath("client-cert.pem"), state.FilePath("client-key.pem"));
            if (existing.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException("Certificado expirado; revogue no servidor e faça novo pareamento com diretório de estado novo.");
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
            var pending = state.Read<EnrollmentPending>("enrollment.json");
            if (pending is null || pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                var csr = new CertificateRequest("CN=SLT-Windows-Enrollment", actualKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var created = await transport.Post<EnrollmentCreated>("/agent/v1/enroll",
                    new EnrollmentRequest(Environment.MachineName, "WINDOWS", csr.CreateSigningRequestPem(), secret), ct);
                pending = new(created.Id, secret, created.Code, created.ExpiresAtUtc);
                state.Write("enrollment.json", pending);
            }
            log($"PAREAMENTO: computador {Environment.MachineName}; código {pending.Code}; solicitação {pending.Id}; validade {pending.ExpiresAtUtc:O}.");
            log("Confira este código fisicamente com o operador do servidor antes de aprovar.");
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
                    File.Delete(state.FilePath("enrollment.json"));
                    return (response.DeviceId, certificate);
                }
                if (response?.Status is "EXPIRED" or "REJECTED")
                {
                    File.Delete(state.FilePath("enrollment.json"));
                    throw new InvalidOperationException("Pareamento expirado ou rejeitado. Reinicie para criar nova solicitação.");
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            throw new InvalidOperationException("Pareamento expirou. Reinicie para criar nova solicitação.");
        }
        finally { if (!ReferenceEquals(actualKey, key)) actualKey.Dispose(); }
    }
}
