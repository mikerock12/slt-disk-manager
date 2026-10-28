using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Slt.Controller;

public sealed class Certificates
{
    public X509Certificate2 Ca { get; }
    public X509Certificate2 Server { get; }
    public string Pin => Convert.ToHexStringLower(SHA256.HashData(Server.RawData));
    public string CaPem => Ca.ExportCertificatePem();
    public Certificates(string directory, IPAddress bind)
    {
        Ca = LoadOrCreate(Path.Combine(directory, "ca.pfx"), () =>
        {
            using var key = RSA.Create(3072);
            var req = new CertificateRequest("CN=SLT Local Authority", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        });
        Server = LoadOrCreate(Path.Combine(directory, "server.pfx"), () =>
        {
            using var key = RSA.Create(3072);
            var req = new CertificateRequest("CN=SLT Controller", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(bind);
            req.CertificateExtensions.Add(san.Build());
            using var issued = req.Create(Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), RandomNumberGenerator.GetBytes(16));
            return issued.CopyWithPrivateKey(key);
        });
    }
    private static X509Certificate2 LoadOrCreate(string path, Func<X509Certificate2> create)
    {
        // Windows SChannel requires an imported key container for TLS; ephemeral
        // CNG keys fail the handshake before any HTTP request reaches Kestrel.
        var flags = X509KeyStorageFlags.Exportable | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null, flags);
        using var certificate = create(); File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(path, null, flags);
    }
    public string Issue(string csrPem, string deviceId)
    {
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        using var rsa = csr.PublicKey.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize < 2048) throw new ApiException(400, "CSR deve usar RSA >= 2048 bits.");
        var request = new CertificateRequest(new X500DistinguishedName($"CN={deviceId}"), csr.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var cert = request.Create(Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(16));
        return cert.ExportCertificatePem();
    }
    public bool ValidateClient(X509Certificate2 cert)
    {
        using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Ca); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(cert) && !cert.HasPrivateKey;
    }
    public SignedAssignment Sign(JobPlan plan)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(Wire.Serialize(plan)); using var key = Server.GetRSAPrivateKey()!;
        return new(Convert.ToBase64String(payload), Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }
}
