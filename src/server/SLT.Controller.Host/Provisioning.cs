using System.Security.Cryptography;
using System.Text;

namespace Slt.Controller;

// Initial accounts for operating system installation and the typed CONVERT_GPT job.
public static class Provisioning
{
    private const string CryptAlphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly string[] Reserved = ["root", "admin", "administrator", "administrador", "guest", "convidado", "daemon", "nobody", "sys", "bin"];

    // SHA-512-crypt ("$6$"), as accepted by chpasswd -e. Only the hash reaches a Linux agent.
    public static string Sha512Crypt(string password, string salt, int rounds = 5000)
    {
        var saltText = salt.Length > 16 ? salt[..16] : salt;
        var p = Encoding.UTF8.GetBytes(password); var s = Encoding.UTF8.GetBytes(saltText);
        byte[] Digest(params byte[][] parts) { using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512); foreach (var part in parts) h.AppendData(part); return h.GetHashAndReset(); }
        var b = Digest(p, s, p);
        using var a = IncrementalHash.CreateHash(HashAlgorithmName.SHA512); a.AppendData(p); a.AppendData(s);
        int i;
        for (i = p.Length; i > 64; i -= 64) a.AppendData(b);
        a.AppendData(b, 0, i);
        for (i = p.Length; i > 0; i >>= 1) a.AppendData((i & 1) != 0 ? b : p);
        var c = a.GetHashAndReset();
        byte[] Repeat(byte[] digest, int length) { var r = new byte[length]; for (var k = 0; k < length; k++) r[k] = digest[k % 64]; return r; }
        using (var dp = IncrementalHash.CreateHash(HashAlgorithmName.SHA512)) { for (i = 0; i < p.Length; i++) dp.AppendData(p); p = Repeat(dp.GetHashAndReset(), p.Length); }
        using (var ds = IncrementalHash.CreateHash(HashAlgorithmName.SHA512)) { for (i = 0; i < 16 + c[0]; i++) ds.AppendData(s); s = Repeat(ds.GetHashAndReset(), s.Length); }
        using var round = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (i = 0; i < rounds; i++)
        {
            round.AppendData((i & 1) != 0 ? p : c);
            if (i % 3 != 0) round.AppendData(s);
            if (i % 7 != 0) round.AppendData(p);
            round.AppendData((i & 1) != 0 ? c : p);
            c = round.GetHashAndReset();
        }
        var output = new StringBuilder("$6$").Append(saltText).Append('$');
        void Encode(int b2, int b1, int b0, int n) { var w = b2 << 16 | b1 << 8 | b0; for (var k = 0; k < n; k++) { output.Append(CryptAlphabet[w & 0x3f]); w >>= 6; } }
        int[] order = [0, 21, 42, 22, 43, 1, 44, 2, 23, 3, 24, 45, 25, 46, 4, 47, 5, 26, 6, 27, 48, 28, 49, 7, 50, 8, 29, 9, 30, 51, 31, 52, 10, 53, 11, 32, 12, 33, 54, 34, 55, 13, 56, 14, 35, 15, 36, 57, 37, 58, 16, 59, 17, 38, 18, 39, 60, 40, 61, 19, 62, 20, 41];
        for (i = 0; i < order.Length; i += 3) Encode(c[order[i]], c[order[i + 1]], c[order[i + 2]], 4);
        Encode(0, 0, c[63], 2);
        return output.ToString();
    }
    // The Windows plan carries the temporary password in clear text for unattend.xml, signed
    // with the plan. Admin listings never show it, and it is erased once the job is over.
    public static Job WithoutSecrets(Job job) =>
        job.Plan.Deployment?.Account is { } a && (a.Password != null || a.PasswordHash != null)
            ? job with { Plan = job.Plan with { Deployment = job.Plan.Deployment with { Account = a with { Password = a.Password == null ? null : "***", PasswordHash = a.PasswordHash == null ? null : "***" } } } }
            : job;
    public static string RandomSalt() => new(RandomNumberGenerator.GetItems(CryptAlphabet.AsSpan(2), 16));
    // Upper, lower and digit for Windows complexity; no ambiguous characters for reading aloud.
    public static string TemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789";
        var chars = RandomNumberGenerator.GetItems((upper + lower + digits).AsSpan(), 11).ToList();
        chars.Insert(RandomNumberGenerator.GetInt32(chars.Count + 1), upper[RandomNumberGenerator.GetInt32(upper.Length)]);
        chars.Insert(RandomNumberGenerator.GetInt32(chars.Count + 1), lower[RandomNumberGenerator.GetInt32(lower.Length)]);
        chars.Insert(RandomNumberGenerator.GetInt32(chars.Count + 1), digits[RandomNumberGenerator.GetInt32(digits.Length)]);
        return new string(chars.ToArray());
    }
    public static DeploymentAccount Validate(DeploymentAccount account, bool linux)
    {
        Controller.Ensure(account.ComputerName is { Length: >= 1 and <= 15 } && account.ComputerName.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') && !account.ComputerName.All(char.IsAsciiDigit) && account.ComputerName[0] != '-' && account.ComputerName[^1] != '-', 400, "Nome do computador: 1 a 15 letras, números ou hífen.");
        Controller.Ensure(account.UserName is { Length: >= 1 and <= 20 } && char.IsAsciiLetterLower(account.UserName[0]) && account.UserName.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '_' or '-') && !Reserved.Contains(account.UserName), 400, "Usuário: 1 a 20 letras minúsculas, números, _ ou -, começando por letra; nomes reservados bloqueados.");
        Controller.Ensure(account.Password == null && account.PasswordHash == null, 400, "A senha temporária é gerada pelo servidor.");
        var locale = account.Locale is "pt-BR" or "en-US" or "es-ES" ? account.Locale : "pt-BR";
        var zone = linux ? "America/Sao_Paulo" : "E. South America Standard Time";
        return account with { Locale = locale, TimeZone = zone };
    }
}

public sealed partial class Controller
{
    private Job PlanConversion(PlanRequest request)
    {
        Ensure(request.Consent, 400, "Autorização do responsável obrigatória para converter a tabela de partições.");
        var d = Device(request.DeviceId);
        Ensure(d.Mode == "WINPE" && d.TrustStatus == "TRUSTED" && d.LastSeenAtUtc > DateTimeOffset.UtcNow.AddSeconds(-45) && d.Inventory?.Capabilities.Contains("CONVERT_GPT") == true, 409, "Conversão exige agente WinPE iniciado em UEFI, autorizado e online.");
        var disk = d.Inventory!.Disks.SingleOrDefault(x => x.Fingerprint == request.DiskFingerprint) ?? throw new ApiException(409, "Disco não encontrado.");
        Ensure(!disk.IsSystem && !disk.IsReadOnly && disk.Serial.Length > 0 && disk.Path.StartsWith(@"\\.\PhysicalDrive", StringComparison.Ordinal), 409, "Disco protegido ou sem identidade física suficiente.");
        var source = request.Deployment ?? throw new ApiException(400, "Escolha a mídia Windows (edição) que fornece o MBR2GPT.");
        var tool = Media.Tool(source.MediaId, source.ImageIndex, "MBR2GPT.EXE");
        var options = new DeploymentOptions(source.MediaId, source.ImageIndex, "GPT_UEFI", false, null, tool.Sha256);
        var id = Wire.Id();
        var plan = new JobPlan(id, "CONVERT_GPT", d.Id, disk.Fingerprint, disk.Path, disk.SizeBytes, disk.LogicalSectorBytes, "", $"CONVERTER {d.Hostname} {disk.Fingerprint[..12]}", DateTimeOffset.UtcNow.AddHours(24), true, Deployment: options);
        var job = new Job(id, plan, "AWAITING_APPROVAL", DateTimeOffset.UtcNow); Db.Put("job", id, job); Db.Log("admin", "conversion.planned", Wire.Serialize(plan)); return job;
    }
    private void RevalidateConversion(Job job)
    {
        var d = Device(job.Plan.DeviceId); var disk = d.Inventory?.Disks.SingleOrDefault(x => x.Fingerprint == job.Plan.DiskFingerprint);
        Ensure(d.Mode == "WINPE" && d.Inventory?.Capabilities.Contains("CONVERT_GPT") == true && disk != null && disk.Path == job.Plan.DiskPath && disk.SizeBytes == job.Plan.DiskSizeBytes && !disk.IsSystem && !disk.IsReadOnly, 409, "Identidade do disco ou modo UEFI mudou.");
    }
}
