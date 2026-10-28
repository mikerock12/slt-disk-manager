using System.Security.Cryptography;

namespace Slt.Controller;

// Clone in batches: the source Rescue uploads 4 MiB chunks while the target Rescue writes
// them. The server keeps at most WindowChunks chunk files; each one is deleted as soon as the
// target acknowledges it durably written. Only hashes stay in the catalog.
public sealed partial class Controller
{
    public int StreamWindowChunks { get; set; } = 64;
    private static readonly string[] FinishedJob = ["COMPLETED", "FAILED", "CANCELLED", "INTERRUPTED"];
    public string StreamDir(string id)
    {
        Ensure(Guid.TryParse(id, out _), 400, "ID de transmissão inválido."); return Path.Combine(Repository, "streams", id);
    }
    private string StreamChunkPath(string id, int seq) => Path.Combine(StreamDir(id), $"{seq:D8}.bin");
    public CloneStream GetStream(string id) => Db.Get<CloneStream>("stream", id) ?? throw new ApiException(404, "Transmissão não encontrada.");
    private static int TotalChunks(CloneStream s) => checked((int)((s.LogicalSizeBytes + ChunkSize - 1) / ChunkSize));
    private long ReservedStreamBytes() => Db.All<CloneStream>("stream").Where(s => s.State == "OPEN").Sum(s => (long)s.WindowChunks * ChunkSize);
    private static bool SameLayout(DiskLayout? a, DiskLayout? b) => Wire.Serialize(a) == Wire.Serialize(b);

    private object PlanStreamClone(CloneRequest request, Device source, Device target, DiskInfo a, DiskInfo b)
    {
        var preview = ClonePlanning.Preview(a, b, request.Strategy, stream: true);
        Ensure(preview.Blockers.Length == 0, 409, string.Join(" ", preview.Blockers));
        Ensure(source.Inventory!.Capabilities.Contains("CLONE_STREAM") && target.Inventory!.Capabilities.Contains("CLONE_STREAM"), 409, "Origem e destino precisam do Rescue com clonagem em lotes (CLONE_STREAM).");
        var required = request.Strategy switch { "FIT" => "STREAM_FIT", "PRESERVE_LAYOUT" when a.SizeBytes != b.SizeBytes => "STREAM_LAYOUT", _ => null };
        Ensure(required == null || target.Inventory!.Capabilities.Contains(required), 409, $"Rescue destino não oferece {required} para gravar em lotes.");
        var window = (long)StreamWindowChunks * ChunkSize;
        Ensure(StreamWindowChunks > 0 && FreeBytes() - ReservedStreamBytes() > window + 64L * 1024 * 1024, 409, "Servidor sem espaço livre para a janela de transmissão em lotes.");
        var streamId = Wire.Id();
        var send = Plan(new("CAPTURE", source.Id, a.Fingerprint, null, true), streamId);
        var receive = Plan(new("RESTORE", target.Id, b.Fingerprint, null, true, CloneOptions: new(request.Strategy, preview.ResizePartitionNumber)), streamId);
        var sessionId = Wire.Id();
        Directory.CreateDirectory(StreamDir(streamId));
        Db.Put("stream", streamId, new CloneStream(streamId, sessionId, send.Id, receive.Id, source.Id, a.Fingerprint, a.SizeBytes, a.LogicalSectorBytes, a.Layout,
            "OPEN", StreamWindowChunks, -1, false, false, DateTimeOffset.UtcNow));
        preview = preview with { ServerBytesRequired = window };
        var session = new CloneSession(sessionId, source.Id, a.Fingerprint, target.Id, b.Fingerprint, request.Strategy, send.Id, receive.Id, "",
            "AWAITING_SOURCE_APPROVAL", DateTimeOffset.UtcNow, preview, "STREAM", streamId);
        Db.Put("clone", sessionId, session); Db.Log("admin", "clone.stream.planned", $"{sessionId} stream={streamId} window={StreamWindowChunks}");
        return new { session, captureJob = send, targetJob = receive, preview };
    }
    private CloneSession StreamCloneState(CloneSession session)
    {
        var send = GetJob(session.CaptureJobId); var receive = GetJob(session.TargetJobId!);
        string[] failed = ["FAILED", "CANCELLED", "INTERRUPTED"];
        var state = receive.State == "COMPLETED" ? "COMPLETED" : failed.Contains(send.State) || failed.Contains(receive.State) ? "FAILED"
            : send.State == "AWAITING_APPROVAL" ? "AWAITING_SOURCE_APPROVAL" : receive.State == "AWAITING_APPROVAL" ? "AWAITING_TARGET_APPROVAL" : "STREAMING";
        return session with { State = state };
    }
    private void ValidateStreamJob(Job job, Device device, DiskInfo disk)
    {
        var s = GetStream(job.Plan.StreamId!); Ensure(s.State == "OPEN", 409, "Transmissão em lotes encerrada; planeje um novo clone.");
        if (job.Plan.Kind == "CAPTURE")
        {
            Ensure(s.SourceJobId == job.Id && s.SourceDeviceId == device.Id && s.SourceFingerprint == disk.Fingerprint, 409, "Origem não corresponde à transmissão planejada."); return;
        }
        Ensure(job.Plan.Kind == "RESTORE" && s.TargetJobId == job.Id && !disk.IsReadOnly, 409, "Destino não corresponde à transmissão planejada.");
        var source = new DiskInfo(s.SourceFingerprint, "stream", "stream", "stream", s.LogicalSizeBytes, s.LogicalSectorBytes, s.LogicalSectorBytes, s.SourceFingerprint, false, true, false, "STREAM", [], s.Layout);
        var options = job.Plan.CloneOptions ?? new("EXACT_RAW");
        var preview = ClonePlanning.Preview(source, disk, options.Strategy, stream: true); Ensure(preview.Blockers.Length == 0, 409, string.Join(" ", preview.Blockers));
        Ensure(options.Strategy != "FIT" || options.ResizePartitionNumber == preview.ResizePartitionNumber, 409, "Partição de ajuste mudou após o planejamento.");
    }
    private CloneStream OpenStream(Job job)
    {
        var s = GetStream(job.Plan.StreamId!); Ensure(s.State == "OPEN", 409, "Transmissão em lotes encerrada: " + (s.Error ?? s.State)); return s;
    }
    private static void PersistChunk(string path, byte[] bytes, string hash)
    {
        var temp = path + ".incoming";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.WriteThrough)) { fs.Write(bytes); fs.Flush(true); }
        File.Move(temp, path, true);
        using var persisted = File.OpenRead(path);
        Ensure(Convert.ToHexStringLower(SHA256.HashData(persisted)) == hash, 409, "Falha de integridade após persistência do chunk.");
    }
    public StreamStatus StreamSourceStart(string deviceId, string jobId, StreamStart request)
    {
        var j = AgentJob(deviceId, jobId, "CAPTURE"); Ensure(j.Plan.StreamId != null, 409, "Tarefa não é clone em lotes.");
        var s = OpenStream(j);
        if (!s.SourceStarted)
        {
            if (request.Layout != null) { ClonePlanning.ValidateLayout(request.Layout, s.LogicalSizeBytes); Ensure(request.Layout.SectorSize == s.LogicalSectorBytes, 400, "Setor do layout não corresponde ao disco."); }
            var exact = (GetJob(s.TargetJobId).Plan.CloneOptions?.Strategy ?? "EXACT_RAW") == "EXACT_RAW";
            if (!(exact && request.Layout == null) && !SameLayout(s.Layout, request.Layout))
            {
                FailStream(s, "Layout da origem mudou desde o planejamento."); throw new ApiException(409, "Layout da origem mudou desde o planejamento; nada foi gravado no destino.");
            }
            s = s with { SourceStarted = true }; Db.Put("stream", s.Id, s); Db.Log(deviceId, "clone.stream.started", s.Id);
        }
        return Status(s);
    }
    private ImageChunk StreamUpload(Job j, int seq, byte[] bytes, string sha)
    {
        var s = OpenStream(j); Ensure(s.SourceJobId == j.Id && s.SourceStarted, 409, "Inicie a transmissão com o layout conferido.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Ensure(SafeEquals(hash, sha), 400, "SHA256 não corresponde aos dados recebidos.");
        var offset = (long)seq * ChunkSize;
        Ensure(seq >= 0 && offset < s.LogicalSizeBytes && bytes.Length == (int)Math.Min(ChunkSize, s.LogicalSizeBytes - offset), 400, "Tamanho/offset de chunk inválido.");
        if (Db.GetChunk(s.Id, "stream", seq) is {} old) { Ensure(old.Sha256 == hash, 409, "Chunk duplicado divergente."); return old; }
        Ensure(seq == Db.NextChunk(s.Id, "stream"), 409, "Chunk fora de ordem; aguarde recibo persistido.");
        if (seq - s.AckedThrough - 1 >= s.WindowChunks) throw new ApiException(429, "Janela de transmissão cheia; aguardando o destino gravar os lotes anteriores.");
        Ensure(FreeBytes() > bytes.Length + 16L * 1024 * 1024, 409, "Repositório sem espaço.");
        PersistChunk(StreamChunkPath(s.Id, seq), bytes, hash);
        var chunk = new ImageChunk(seq, offset, bytes.Length, hash); Db.AddChunk(s.Id, "stream", chunk); return chunk;
    }
    private ImageManifest StreamSourceComplete(Job j, DiskLayout? layout)
    {
        var s = OpenStream(j); Ensure(s.SourceJobId == j.Id && s.SourceStarted, 409, "Transmissão não iniciada.");
        var exact = (GetJob(s.TargetJobId).Plan.CloneOptions?.Strategy ?? "EXACT_RAW") == "EXACT_RAW";
        if (!(exact && layout == null) && !SameLayout(s.Layout, layout))
        {
            FailStream(s, "Layout da origem mudou durante o envio."); throw new ApiException(409, "Layout da origem mudou durante o envio; clone interrompido.");
        }
        var manifest = new ImageManifest(1, s.Id, "stream", "STREAMED", s.SourceDeviceId, s.SourceFingerprint, s.LogicalSizeBytes, s.LogicalSectorBytes, s.CreatedAtUtc, Db.Chunks(s.Id, "stream"), s.Layout);
        ValidateChunks(manifest, false);
        Db.Put("stream", s.Id, s with { SourceComplete = true }); Db.Log(j.Plan.DeviceId, "clone.stream.source-complete", s.Id);
        return manifest with { Chunks = [] };
    }
    private StreamStatus Status(CloneStream s)
    {
        var next = Db.NextChunk(s.Id, "stream"); var pending = new List<ImageChunk>();
        for (var seq = s.AckedThrough + 1; seq < next; seq++) pending.Add(Db.GetChunk(s.Id, "stream", seq) ?? throw new ApiException(409, "Catálogo da transmissão incompleto."));
        return new(s.Id, s.State, s.LogicalSizeBytes, s.LogicalSectorBytes, s.SourceFingerprint, s.Layout, TotalChunks(s), next, s.AckedThrough, s.SourceStarted, s.SourceComplete, pending);
    }
    public StreamStatus StreamStatusFor(string deviceId, string jobId)
    {
        var j = AgentJob(deviceId, jobId, "RESTORE"); Ensure(j.Plan.StreamId != null, 409, "Tarefa não é clone em lotes.");
        return Status(OpenStream(j));
    }
    private byte[] StreamDownload(Job j, int seq)
    {
        var s = OpenStream(j); Ensure(s.TargetJobId == j.Id, 403, "Transmissão de outro destino.");
        Ensure(seq > s.AckedThrough, 409, "Lote já confirmado e descartado no servidor.");
        var chunk = Db.GetChunk(s.Id, "stream", seq) ?? throw new ApiException(409, "Lote ainda não recebido da origem.");
        var bytes = File.ReadAllBytes(StreamChunkPath(s.Id, seq));
        Ensure(bytes.Length == chunk.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(bytes)) == chunk.Sha256, 409, "Lote corrompido no servidor; escrita bloqueada."); return bytes;
    }
    public StreamStatus StreamAcknowledge(string deviceId, string jobId, StreamAck ack)
    {
        var j = AgentJob(deviceId, jobId, "RESTORE"); Ensure(j.Plan.StreamId != null, 409, "Tarefa não é clone em lotes.");
        var s = OpenStream(j); Ensure(s.TargetJobId == j.Id, 403, "Transmissão de outro destino.");
        Ensure(ack.Through >= s.AckedThrough && ack.Through < Db.NextChunk(s.Id, "stream"), 400, "Confirmação fora dos lotes recebidos.");
        // Only after the target fsync'ed these chunks: discard their bytes, keep their hashes.
        for (var seq = s.AckedThrough + 1; seq <= ack.Through; seq++) File.Delete(StreamChunkPath(s.Id, seq));
        s = s with { AckedThrough = ack.Through }; Db.Put("stream", s.Id, s); return Status(s);
    }
    private JobResult StreamResult(Job j, JobResult result)
    {
        if (!result.Success || Db.Get<CloneStream>("stream", j.Plan.StreamId!) is not {} s) return result;
        if (j.Plan.Kind == "CAPTURE") Ensure(s.SourceComplete, 409, "Envio em lotes não finalizado.");
        else Ensure(s.SourceComplete && s.AckedThrough == TotalChunks(s) - 1, 409, "Destino não confirmou todos os lotes.");
        return result;
    }
    private void StreamFinished(Job j, string state, string message)
    {
        if (Db.Get<CloneStream>("stream", j.Plan.StreamId!) is not {} s || s.State != "OPEN") return;
        if (state == "COMPLETED")
        {
            if (j.Id != s.TargetJobId) return;
            Db.Put("stream", s.Id, s with { State = "COMPLETED" }); DeleteStreamData(s.Id); Db.Log("system", "clone.stream.completed", s.Id); return;
        }
        FailStream(s, $"{(j.Id == s.SourceJobId ? "Origem" : "Destino")} {state}: {message}");
    }
    private void FailStream(CloneStream s, string reason)
    {
        Db.Put("stream", s.Id, s with { State = "FAILED", Error = reason }); DeleteStreamData(s.Id); Db.Log("system", "clone.stream.failed", $"{s.Id} {reason}");
        foreach (var id in new[] { s.SourceJobId, s.TargetJobId })
            if (Db.Get<Job>("job", id) is {} other && !FinishedJob.Contains(other.State) && !other.CancelRequested) Cancel(id);
    }
    private void DeleteStreamData(string id)
    {
        try { if (Directory.Exists(StreamDir(id))) Directory.Delete(StreamDir(id), true); }
        catch (IOException e) { Db.Log("system", "clone.stream.cleanup-failed", $"{id} {e.Message}"); }
        catch (UnauthorizedAccessException e) { Db.Log("system", "clone.stream.cleanup-failed", $"{id} {e.Message}"); }
    }
    private void InterruptStreams()
    {
        foreach (var s in Db.All<CloneStream>("stream").Where(s => s.State == "OPEN"))
        {
            Db.Put("stream", s.Id, s with { State = "FAILED", Error = "Servidor reiniciado; lotes descartados." }); DeleteStreamData(s.Id);
            Db.Log("system", "clone.stream.interrupted", s.Id);
        }
    }
}
