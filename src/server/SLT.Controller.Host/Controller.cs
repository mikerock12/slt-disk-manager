using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Slt.Controller;

public sealed partial class Controller
{
    public readonly object Gate = new();
    public Store Db { get; }
    public Certificates Certificates { get; }
    public string Repository { get; }
    public FileRepository Files { get; }
    public MediaRepository Media { get; }
    public const int ChunkSize = 4 * 1024 * 1024;
    private static readonly string[] Active = ["QUEUED", "RUNNING", "CANCELLING"];
    public Controller(Store db, Certificates certificates, string repository)
    {
        Db = db; Certificates = certificates; Repository = repository; Directory.CreateDirectory(Repository);
        Files = new FileRepository(this);
        Media = new MediaRepository(this);
        foreach (var job in db.All<Job>("job").Where(j => Active.Contains(j.State)))
        {
            db.Put("job", job.Id, Provisioning.WithoutSecrets(job with { State = "INTERRUPTED", Result = new(false, "Servidor reiniciado; replaneje após conferir o destino. Nenhuma escrita foi reenviada.", "") }));
            db.Log("system", "job.interrupted", job.Id);
        }
        foreach (var image in db.All<ImageManifest>("image").Where(i => i.State is "VERIFYING" or "CREATING"))
        {
            db.Put("image", image.Id, image with { State = "FAILED" });
            db.Log("system", "image.interrupted", image.Id);
        }
        foreach(var image in db.All<ImageManifest>("image").Where(i=>i.Chunks.Count>0))
        { db.ImportChunks(image.Id,"disk",image.Chunks);db.Put("image",image.Id,image with{Chunks=[]}); }
        InterruptStreams();
    }
    internal static void Ensure(bool condition, int status, string error) { if (!condition) throw new ApiException(status, error); }
    public Enrollment Enroll(EnrollmentRequest request)
    {
        Ensure(!string.IsNullOrWhiteSpace(request.Hostname) && request.Hostname.Length <= 128, 400, "Hostname inválido.");
        Ensure(request.Mode is "WINDOWS" or "RESCUE" or "WINPE", 400, "Modo inválido.");
        Ensure(request.Secret is { Length: 64 } && request.Secret.All(Uri.IsHexDigit), 400, "Secret de pareamento deve ter 32 bytes aleatórios hex.");
        Ensure(request.CsrPem is { Length: < 12000 }, 400, "CSR inválido.");
        _ = CertificateRequest.LoadSigningRequestPem(request.CsrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        Ensure(Db.All<Enrollment>("enrollment").Count(e => e.Status == "PENDING" && e.ExpiresAtUtc > DateTimeOffset.UtcNow) < 100, 429, "Limite de pareamentos pendentes.");
        var e = new Enrollment(Wire.Id(), request.Hostname, request.Mode, request.CsrPem, Wire.Hash(request.Secret),
            RandomNumberGenerator.GetInt32(10000000, 100000000).ToString(), DateTimeOffset.UtcNow.AddMinutes(10), "PENDING");
        Db.Put("enrollment", e.Id, e); Db.Log("bootstrap", "enrollment.request", $"{e.Id} {e.Hostname} {e.Mode}"); return e;
    }
    public Enrollment GetEnrollment(string id, string secret)
    {
        var e = Db.Get<Enrollment>("enrollment", id) ?? throw new ApiException(404, "Pareamento não encontrado.");
        Ensure(SafeEquals(Wire.Hash(secret), e.SecretHash), 403, "Prova de pareamento inválida.");
        if (e.ExpiresAtUtc <= DateTimeOffset.UtcNow) return e with { Status = "EXPIRED", CertificatePem = null };
        return e;
    }
    public Enrollment ApproveEnrollment(string id, string code)
    {
        var e = Db.Get<Enrollment>("enrollment", id) ?? throw new ApiException(404, "Pareamento não encontrado.");
        Ensure(e.Status == "PENDING" && e.ExpiresAtUtc > DateTimeOffset.UtcNow, 409, "Pareamento expirado ou já processado.");
        Ensure(SafeEquals(e.Code, code), 400, "Código não corresponde ao exibido no computador cliente.");
        var deviceId = Wire.Id(); var pem = Certificates.Issue(e.CsrPem, deviceId);
        using var cert = X509Certificate2.CreateFromPem(pem); Db.SetCredential(deviceId, cert.Thumbprint);
        Db.Put("device", deviceId, new Device(deviceId, e.Hostname, e.Mode, "TRUSTED", DateTimeOffset.MinValue, null));
        e = e with { Status = "APPROVED", DeviceId = deviceId, CertificatePem = pem };
        Db.Put("enrollment", id, e); Db.Log("admin", "enrollment.approved", $"{id} device={deviceId}"); return e;
    }
    public string Authenticate(X509Certificate2? cert)
    {
        Ensure(cert != null && Certificates.ValidateClient(cert), 401, "Certificado cliente válido obrigatório.");
        var id = Db.Identify(cert!.Thumbprint) ?? throw new ApiException(401, "Certificado não cadastrado.");
        Ensure(Db.Get<Device>("device", id)?.TrustStatus == "TRUSTED", 403, "Agente revogado."); return id;
    }
    public void Inventory(string id, Inventory inventory)
    {
        var d = Device(id); Ensure(inventory.DeviceId == id && inventory.Mode == d.Mode && inventory.AgentVersion == "0.1.0", 400, "Identidade, modo ou versão incompatível.");
        Ensure(inventory.Disks is { Length: <= 256 } && inventory.Capabilities is { Length: <= 32 }, 400, "Inventário inválido.");
        foreach (var disk in inventory.Disks)
        {
            Ensure(disk.Path is { Length: > 0 and < 1024 } && disk.Model != null && disk.Serial != null && disk.SizeBytes > 0 && disk.LogicalSectorBytes is 512 or 4096,
                400, "Geometria ou identidade de disco inválida.");
            Ensure(disk.Fingerprint == disk.ComputeFingerprint(), 400, "Fingerprint de disco não corresponde ao inventário.");
        }
        Ensure(inventory.Disks.Select(x => x.Fingerprint).Distinct().Count() == inventory.Disks.Length, 400, "Discos ambíguos.");
        Ensure(inventory.Disks.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() == inventory.Disks.Length, 400, "Caminhos de discos duplicados.");
        foreach(var disk in inventory.Disks) if(disk.Layout != null) ClonePlanning.ValidateLayout(disk.Layout, disk.SizeBytes);
        var roots=inventory.FileRoots ?? [];
        Ensure(roots.Length<=32 && roots.Select(r=>r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()==roots.Length && roots.All(r=>r.Id is {Length:>0 and <=48} && r.Id.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch is '-' or '_') && r.Label is {Length:>0 and <=128}),400,"Escopos de arquivos inválidos.");
        Db.Put("device", id, d with { Hostname = inventory.Hostname, LastSeenAtUtc = DateTimeOffset.UtcNow, Inventory = inventory });
    }
    public Device Device(string id) => Db.Get<Device>("device", id) ?? throw new ApiException(404, "Máquina não encontrada.");
    public void Revoke(string id)
    {
        var d = Device(id); Db.Put("device", id, d with { TrustStatus = "REVOKED" });
        foreach (var job in Db.All<Job>("job").Where(j => j.Plan.DeviceId == id && Active.Contains(j.State))) Cancel(job.Id);
        Db.Log("admin", "device.revoked", id);
    }
    public Job Plan(PlanRequest request) => Plan(request, null);
    private Job Plan(PlanRequest request, string? streamId)
    {
        if(request.Kind is "FILE_LIST" or "FILE_BACKUP" or "FILE_RESTORE") return Files.Plan(request);
        if(request.Kind is "DEPLOY_WINDOWS" or "DEPLOY_LINUX") return PlanDeployment(request);
        if(request.Kind == "CONVERT_GPT") return PlanConversion(request);
        if(request.Kind == "RESTORE" && request.CloneOptions == null) request = request with { CloneOptions = new("EXACT_RAW") };
        Ensure(request.Kind is "INVENTORY" or "SMART" or "CAPTURE" or "RESTORE", 400, "Operação não implementada nesta versão.");
        Ensure(request.Consent, 400, "Registre autorização do responsável antes de criar uma tarefa.");
        var d = Device(request.DeviceId);
        Ensure(d.TrustStatus == "TRUSTED" && d.Inventory != null && d.LastSeenAtUtc > DateTimeOffset.UtcNow.AddSeconds(-45), 409, "Agente offline ou não autorizado.");
        Ensure(d.Inventory!.Capabilities.Contains(request.Kind), 409, "Agente não oferece esta capacidade.");
        var disk = request.Kind == "INVENTORY" ? null : d.Inventory.Disks.SingleOrDefault(x => x.Fingerprint == request.DiskFingerprint);
        Ensure(request.Kind == "INVENTORY" || disk != null, 400, "Selecione um disco inequívoco do inventário atual.");
        var imageId = request.ImageId ?? "";
        if (request.Kind is "CAPTURE" or "RESTORE")
        {
            Ensure(d.Mode == "RESCUE" && !disk!.Mounted && !disk.IsSystem, 409, "Imagem exige Rescue offline e disco não montado/não sistema.");
            Ensure(disk!.Serial.Length > 0 || d.Inventory.Notes.Contains("SYNTHETIC_FIXTURE"), 409, "Serial ausente: identidade insuficiente para imagem física.");
        }
        if (request.Kind == "CAPTURE" && streamId == null)
        {
            imageId = Wire.Id();
            var reserved = Db.All<Job>("job").Where(j => j.Plan.Kind == "CAPTURE" && (Active.Contains(j.State) || j.State == "AWAITING_APPROVAL")).Sum(j => j.Plan.DiskSizeBytes);
            Ensure(FreeBytes() - reserved > disk!.SizeBytes + 64L * 1024 * 1024, 409, "Espaço insuficiente para imagem raw e reserva de segurança.");
        }
        if (request.Kind == "RESTORE" && streamId != null) Ensure(!disk!.IsReadOnly, 409, "Disco destino está protegido contra gravação.");
        if (request.Kind == "RESTORE" && streamId == null)
        {
            Ensure(!disk!.IsReadOnly, 409, "Disco destino está protegido contra gravação.");
            var image = GetImage(imageId);
            Ensure(image.State == "AVAILABLE", 409, "Imagem indisponível ou sendo verificada.");
            ValidateChunks(image, false);
            ClonePlanning.ValidateRestore(image,d,disk,request.CloneOptions);
            Ensure(image.SourceDeviceId != d.Id || image.SourceFingerprint != disk.Fingerprint, 409, "Origem e destino são o mesmo disco. Use outro disco físico.");
        }
        var id = Wire.Id();
        var challenge = request.Kind == "RESTORE" ? $"APAGAR {d.Hostname} {disk!.Fingerprint[..12]}" : $"AUTORIZAR {request.Kind} {id[..8]}";
        var plan = new JobPlan(id, request.Kind, d.Id, disk?.Fingerprint ?? "", disk?.Path ?? "", disk?.SizeBytes ?? 0,
            disk?.LogicalSectorBytes ?? 512, imageId, challenge, streamId!=null || request.Kind=="RESTORE" && request.CloneOptions!=null?DateTimeOffset.UtcNow.AddHours(24):DateTimeOffset.UtcNow.AddMinutes(15), true, CloneOptions:request.CloneOptions, StreamId:streamId);
        var job = new Job(id, plan, "AWAITING_APPROVAL", DateTimeOffset.UtcNow); Db.Put("job", id, job);
        Db.Log("admin", "job.planned", Wire.Serialize(plan)); return job;
    }
    private void Revalidate(Job job)
    {
        Ensure(job.Plan.ExpiresAtUtc > DateTimeOffset.UtcNow, 409, "Plano expirado; crie outro plano.");
        var d = Device(job.Plan.DeviceId);
        Ensure(d.TrustStatus == "TRUSTED" && d.LastSeenAtUtc > DateTimeOffset.UtcNow.AddSeconds(-45), 409, "Agente offline/revogado.");
        Ensure(d.Inventory?.Capabilities.Contains(job.Plan.Kind) == true, 409, "Capacidade indisponível.");
        if(job.Plan.File != null){ Files.Revalidate(job); return; }
        if(job.Plan.Kind is "DEPLOY_WINDOWS" or "DEPLOY_LINUX"){ RevalidateDeployment(job); return; }
        if(job.Plan.Kind == "CONVERT_GPT"){ RevalidateConversion(job); return; }
        if (job.Plan.Kind == "INVENTORY") return;
        var disk = d.Inventory!.Disks.SingleOrDefault(x => x.Fingerprint == job.Plan.DiskFingerprint);
        Ensure(disk != null && disk.Path == job.Plan.DiskPath && disk.SizeBytes == job.Plan.DiskSizeBytes && disk.LogicalSectorBytes == job.Plan.LogicalSectorBytes, 409, "Disco mudou após planejamento.");
        if (job.Plan.Kind is "CAPTURE" or "RESTORE")
            Ensure(d.Mode == "RESCUE" && !disk!.Mounted && !disk.IsSystem && (job.Plan.Kind != "RESTORE" || !disk.IsReadOnly), 409, "Disco indisponível para operação offline.");
        if (job.Plan.StreamId != null) { ValidateStreamJob(job, d, disk!); return; }
        if (job.Plan.Kind == "RESTORE")
        {
            var image = GetImage(job.Plan.ImageId); Ensure(image.State == "AVAILABLE", 409, "Imagem indisponível ou em verificação.");
            ValidateChunks(image, false);
            ClonePlanning.ValidateRestore(image,d,disk!,job.Plan.CloneOptions);
        }
    }
    public Job Approve(string id, string challenge)
    {
        var job = GetJob(id); Ensure(job.State == "AWAITING_APPROVAL", 409, "Tarefa já aprovada ou encerrada.");
        Ensure(SafeEquals(job.Plan.Challenge, challenge), 400, "Confirmação digitada incorreta."); Revalidate(job);
        Ensure(!Db.All<Job>("job").Any(j => j.Id != id && Active.Contains(j.State) && j.Plan.DeviceId == job.Plan.DeviceId), 409, "Máquina já possui tarefa ativa; aguarde conclusão.");
        if (job.Plan.Kind == "CAPTURE" && job.Plan.StreamId == null)
        {
            var p = job.Plan;
            var image = new ImageManifest(1, p.ImageId, $"{Device(p.DeviceId).Hostname} {DateTimeOffset.Now:yyyy-MM-dd HH:mm}", "CREATING", p.DeviceId, p.DiskFingerprint, p.DiskSizeBytes, p.LogicalSectorBytes, DateTimeOffset.UtcNow, [], Device(p.DeviceId).Inventory?.Disks.Single(d=>d.Fingerprint==p.DiskFingerprint).Layout);
            Directory.CreateDirectory(ImageDir(image.Id)); Db.Put("image", image.Id, image);
        }
        if(job.Plan.Kind=="FILE_BACKUP") Files.Prepare(job);
        job = job with { State = "QUEUED", ApprovedAtUtc = DateTimeOffset.UtcNow }; Db.Put("job", id, job);
        Db.Log("admin", "job.approved", $"{id} fingerprint={job.Plan.DiskFingerprint}"); return job;
    }
    public SignedAssignment? Next(string deviceId)
    {
        var job = Db.All<Job>("job").LastOrDefault(j => j.Plan.DeviceId == deviceId && j.State == "QUEUED");
        if (job == null) return null;
        try { Revalidate(job); }
        catch (ApiException e) { Result(deviceId, job.Id, new(false, e.Message, ""), true); return null; }
        Db.Put("job", job.Id, job with { State = "RUNNING" }); Db.Log(deviceId, "job.dispatched", job.Id);
        return Certificates.Sign(job.Plan);
    }
    public Job GetJob(string id) => Db.Get<Job>("job", id) ?? throw new ApiException(404, "Tarefa não encontrada.");
    public Job AgentJob(string deviceId, string id, params string[] kinds)
    {
        var job = GetJob(id); Ensure(job.Plan.DeviceId == deviceId, 403, "Tarefa de outro agente.");
        Ensure(job.State == "RUNNING" && !job.CancelRequested, 409, "Tarefa não está ativa ou foi cancelada.");
        Ensure(kinds.Length == 0 || kinds.Contains(job.Plan.Kind), 403, "Tipo de tarefa não autorizado."); return job;
    }
    public void Progress(string deviceId, string id, Progress progress)
    {
        var j = GetJob(id); Ensure(j.Plan.DeviceId == deviceId && Active.Contains(j.State), 409, "Tarefa inválida.");
        Ensure(progress.BytesProcessed >= 0 && progress.BytesTotal >= progress.BytesProcessed, 400, "Progresso inválido.");
        Db.Put("job", id, j with { Progress = progress });
        var device = Device(deviceId); Db.Put("device", deviceId, device with { LastSeenAtUtc = DateTimeOffset.UtcNow });
    }
    public void Result(string deviceId, string id, JobResult result, bool internalCall = false)
    {
        var j = GetJob(id); Ensure(j.Plan.DeviceId == deviceId, 403, "Tarefa pertence a outro agente.");
        Ensure(Active.Contains(j.State), 409, "Resultado já reconciliado/encerrado; não repetir execução.");
        if (j.Plan.StreamId != null) result = StreamResult(j, result);
        else if (result.Success && j.Plan.Kind == "CAPTURE") Ensure(Db.Get<ImageManifest>("image", j.Plan.ImageId)?.State == "AVAILABLE", 409, "Imagem não finalizada e verificada.");
        if (result.Success && j.Plan.Kind == "FILE_BACKUP") Ensure(Files.Get(j.Plan.File!.BundleId).State == "AVAILABLE",409,"Backup não finalizado/verificado.");
        var state = j.CancelRequested ? "CANCELLED" : result.Success ? "COMPLETED" : "FAILED";
        Db.Put("job", id, Provisioning.WithoutSecrets(j with { State = state, Result = result }));
        if (j.Plan.StreamId != null) StreamFinished(j, state, result.Message);
        else if (state != "COMPLETED" && j.Plan.Kind == "CAPTURE" && Db.Get<ImageManifest>("image", j.Plan.ImageId) is {} image && image.State != "AVAILABLE")
            Db.Put("image", image.Id, image with { State = "FAILED" });
        if(state!="COMPLETED" && j.Plan.Kind=="FILE_BACKUP") Files.Fail(j.Plan.File!.BundleId);
        Db.Log(internalCall ? "system" : deviceId, $"job.{state.ToLowerInvariant()}", $"{id} {result.Message}");
    }
    public Job Cancel(string id)
    {
        var j = GetJob(id); Ensure(j.State is "AWAITING_APPROVAL" or "QUEUED" or "RUNNING" or "CANCELLING", 409, "Tarefa já encerrada.");
        j = j with { State = j.State is "RUNNING" or "CANCELLING" ? "CANCELLING" : "CANCELLED", CancelRequested = true };
        Db.Put("job", id, j.State == "CANCELLED" ? Provisioning.WithoutSecrets(j) : j); Db.Log("admin", "job.cancel.request", id);
        if (j.Plan.StreamId != null && j.State == "CANCELLED") StreamFinished(j, "CANCELLED", "Cancelada antes de iniciar.");
        return j;
    }
    public string ImageDir(string id)
    {
        Ensure(Guid.TryParse(id, out _), 400, "ID de imagem inválido."); return Path.Combine(Repository, "images", id);
    }
    private string ChunkPath(string id, int seq) => Path.Combine(ImageDir(id), $"{seq:D8}.bin");
    public ImageChunk Upload(string deviceId, string jobId, int seq, byte[] bytes, string sha, bool zero = false)
    {
        var j = AgentJob(deviceId, jobId, "CAPTURE");
        if (j.Plan.StreamId != null) return StreamUpload(j, seq, bytes, sha, zero);
        Ensure(!zero, 400, "Lote zerado só é aceito no clone em lotes.");
        var image = Db.Get<ImageManifest>("image",j.Plan.ImageId) ?? throw new ApiException(404,"Imagem não encontrada.");
        Ensure(image.State == "CREATING" && seq >= 0, 409, "Imagem não está recebendo dados.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Ensure(SafeEquals(hash, sha), 400, "SHA256 não corresponde aos dados recebidos.");
        var offset = (long)seq * ChunkSize; var size = (int)Math.Min(ChunkSize, image.LogicalSizeBytes - offset);
        Ensure(offset >= 0 && size > 0 && bytes.Length == size, 400, "Tamanho/offset de chunk inválido.");
        if (Db.GetChunk(image.Id,"disk",seq) is {} old)
        {
            Ensure(old.Sha256 == hash && File.Exists(ChunkPath(image.Id, seq)), 409, "Chunk duplicado divergente."); return old;
        }
        Ensure(seq == Db.NextChunk(image.Id,"disk"), 409, "Chunk fora de ordem; aguarde recibo persistido.");
        Ensure(FreeBytes() > bytes.Length + 16L * 1024 * 1024, 409, "Repositório sem espaço.");
        var path = ChunkPath(image.Id, seq); var temp = path + ".incoming";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.WriteThrough)) { fs.Write(bytes); fs.Flush(true); }
        File.Move(temp, path, true);
        using (var persisted = File.OpenRead(path))
            Ensure(Convert.ToHexStringLower(SHA256.HashData(persisted)) == hash, 409, "Falha de integridade após persistência do chunk.");
        var chunk = new ImageChunk(seq, offset, bytes.Length, hash); Db.AddChunk(image.Id,"disk",chunk); return chunk;
    }
    public ImageManifest Complete(string deviceId, string jobId, DiskLayout? layout = null)
    {
        var j = AgentJob(deviceId, jobId, "CAPTURE");
        if (j.Plan.StreamId != null) return StreamSourceComplete(j, layout);
        var image = GetImage(j.Plan.ImageId);
        if(layout!=null){ ClonePlanning.ValidateLayout(layout,image.LogicalSizeBytes); Ensure(layout.SectorSize==image.LogicalSectorBytes,400,"Setor do layout não corresponde à imagem."); image=image with{Layout=layout}; }
        // Each chunk was re-read and hashed before its durable receipt. Checking
        // continuity here avoids re-reading a multi-terabyte image under the DB lock.
        ValidateChunks(image, false); image = image with { State = "AVAILABLE" };
        // Hashes live in durable catalog, and manifest is promoted atomically only after all chunks validate.
        var path = Path.Combine(ImageDir(image.Id), "manifest.json"); File.WriteAllText(path + ".tmp", Wire.Serialize(image)); File.Move(path + ".tmp", path, true);
        Db.Put("image", image.Id, image with{Chunks=[]}); Db.Log(deviceId, "image.available", image.Id); return image;
    }
    public ImageManifest GetImage(string id)
    {
        var image=Db.Get<ImageManifest>("image",id) ?? throw new ApiException(404,"Imagem não encontrada.");
        return image.Chunks.Count>0 ? image : image with{Chunks=Db.Chunks(id,"disk")};
    }
    public ImageManifest Verify(string id)
    {
        var image = GetImage(id); Ensure(image.State == "AVAILABLE", 409, "Imagem não está disponível.");
        try { ValidateChunks(image); }
        catch (Exception e) when (e is ApiException or IOException)
        {
            Db.Put("image", id, image with { State = "FAILED",Chunks=[] }); Db.Log("system", "image.corrupt", id); throw new ApiException(409, "Imagem corrompida/incompleta; restauração bloqueada.");
        }
        Db.Log("admin", "image.verified", id); return image;
    }
    public ImageManifest StartVerification(string id)
    {
        var image = GetImage(id); Ensure(image.State == "AVAILABLE", 409, "Imagem indisponível ou já sendo verificada.");
        Ensure(!Db.All<Job>("job").Any(j => j.Plan.Kind == "RESTORE" && j.Plan.ImageId == id && Active.Contains(j.State)), 409, "Imagem em uso por restauração; aguarde a conclusão antes de verificar.");
        var verifying = image with { State = "VERIFYING",Chunks=[] }; Db.Put("image", id, verifying);
        Db.Log("admin", "image.verification.started", id);
        _ = Task.Run(() =>
        {
            string state = "AVAILABLE";
            try { ValidateChunks(image); } catch { state = "FAILED"; }
            lock (Gate)
            {
                Db.Put("image", id, image with { State = state,Chunks=[] });
                Db.Log("system", state == "AVAILABLE" ? "image.verified" : "image.corrupt", id);
            }
        });
        return verifying;
    }
    public void ValidateChunks(ImageManifest image, bool deep = true)
    {
        long total = 0; int sequence = 0;
        foreach (var c in image.Chunks)
        {
            Ensure(c.Sequence == sequence++ && c.OffsetBytes == total && c.SizeBytes > 0 && c.SizeBytes <= ChunkSize, 409, "Manifesto não contíguo.");
            Ensure(c.SizeBytes==Math.Min(ChunkSize,image.LogicalSizeBytes-total) && c.Sha256 is {Length:64} && c.Sha256.All(Uri.IsHexDigit),409,"Tamanho/hash do chunk inválido.");
            if(deep){using var fs = File.OpenRead(ChunkPath(image.Id, c.Sequence));
                Ensure(fs.Length == c.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(fs)) == c.Sha256, 409, "Integridade de chunk inválida.");}
            total += c.SizeBytes;
        }
        Ensure(total == image.LogicalSizeBytes && total > 0, 409, "Imagem incompleta.");
    }
    public ImageManifest DownloadManifest(string deviceId, string jobId)
    {
        var j = AgentJob(deviceId, jobId, "RESTORE"); Ensure(j.Plan.StreamId == null, 409, "Clone em lotes não tem manifesto completo; consulte /stream.");
        var m = GetImage(j.Plan.ImageId);
        Ensure(m.State == "AVAILABLE", 409, "Imagem indisponível."); return m;
    }
    public byte[] Download(string deviceId, string jobId, int seq)
    {
        var j=AgentJob(deviceId,jobId,"RESTORE");
        if(j.Plan.StreamId!=null)return StreamDownload(j,seq);
        var image=Db.Get<ImageManifest>("image",j.Plan.ImageId) ?? throw new ApiException(404,"Imagem não encontrada.");
        Ensure(image.State=="AVAILABLE",409,"Imagem indisponível.");
        var chunk=Db.GetChunk(image.Id,"disk",seq) ?? throw new ApiException(404,"Chunk inválido.");
        var bytes = File.ReadAllBytes(ChunkPath(image.Id, seq));
        Ensure(bytes.Length==chunk.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(bytes)) == chunk.Sha256, 409, "Chunk corrompido; escrita bloqueada."); return bytes;
    }
    public long FreeBytes() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Repository))!).AvailableFreeSpace;
    public static bool SafeEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
