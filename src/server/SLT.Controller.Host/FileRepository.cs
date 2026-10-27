using System.Security.Cryptography;

namespace Slt.Controller;

public sealed partial class FileRepository
{
    private readonly Controller owner;
    private Store Db=>owner.Db;
    private static void Require(bool condition,int code,string message)=>Controller.Ensure(condition,code,message);
    public FileRepository(Controller owner)
    {
        this.owner=owner;
        foreach(var bundle in Db.All<FileBundle>("filebundle").Where(b=>b.State is "CREATING" or "VERIFYING"))
        { Persist(bundle with{State="FAILED"});Db.Log("system","filebundle.interrupted",bundle.Id); }
    }
    public static string Relative(string? path,bool allowEmpty=true)
    {
        Require(path!=null && path.Length<=2048,400,"Caminho relativo inválido.");
        if(path!.Length==0){Require(allowEmpty,400,"Informe uma pasta nova de destino.");return path;}
        var parts=path.Split('/');
        Require(parts.Length<=64 && parts.All(p=>p.Length is >0 and <=255 && p is not "." and not ".." &&
            !p.Any(ch=>ch<32 || "\\:*?\"<>|".Contains(ch)) && !p.EndsWith('.') && !p.EndsWith(' ') &&
            !System.Text.RegularExpressions.Regex.IsMatch(p.Split('.')[0].TrimEnd(' '),"^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[1-9¹²³]|LPT[1-9¹²³])$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)),400,"Caminho fora do escopo ou nome não portátil.");
        return path;
    }
    private FileRootInfo Scope(Device device,FileRequest request,bool write)
    {
        var root=device.Inventory?.FileRoots?.SingleOrDefault(r=>r.Id==request.RootId);
        Require(root!=null && (write?root.CanWrite:root.CanRead),403,"Raiz de arquivos não autorizada para esta operação.");return root!;
    }
    public Job Plan(PlanRequest request)
    {
        Require(request.Consent,400,"Registre autorização do responsável para acesso aos arquivos.");
        var d=owner.Device(request.DeviceId);Require(d.TrustStatus=="TRUSTED" && d.Inventory!=null && d.LastSeenAtUtc>DateTimeOffset.UtcNow.AddSeconds(-45),409,"Agente offline/não autorizado.");
        Require(d.Inventory!.Capabilities.Contains(request.Kind),409,"Agente não oferece esta operação de arquivos.");
        var f=request.File ?? throw new ApiException(400,"Parâmetros de arquivos obrigatórios.");f=f with{Paths=f.Paths??[]};var scope=Scope(d,f,request.Kind=="FILE_RESTORE");
        Relative(f.RelativePath,request.Kind!="FILE_RESTORE");
        Require(f.PageSize is >=1 and <=200 && (f.Cursor?.Length??0)<=4096,400,"Página/cursor inválido.");
        if(request.Kind=="FILE_BACKUP")
        {
            Require(f.Paths is {Length:>0 and <=1000},400,"Selecione até 1000 arquivos ou pastas.");
            var paths=f.Paths!.Select(p=>Relative(p,false)).ToArray();
            Require(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()==paths.Length,400,"Seleção duplicada.");
            foreach(var p in paths) Require(!paths.Any(other=>other!=p && p.StartsWith(other+"/",StringComparison.OrdinalIgnoreCase)),400,"Não selecione uma pasta junto de um item dentro dela.");
            Require(f.Name is {Length:>0 and <=128} && !f.Name.Any(char.IsControl),400,"Dê um nome ao backup.");
            f=f with{BundleId=Wire.Id(),Paths=paths};
        }
        if(request.Kind=="FILE_RESTORE")
        {
            var bundle=Get(f.BundleId);Require(bundle.State=="AVAILABLE",409,"Backup indisponível ou em verificação.");Validate(bundle,false);
        }
        var id=Wire.Id();var challenge=$"AUTORIZAR {request.Kind} {d.Hostname} {scope.Label} {id[..8]}";
        var job=new Job(id,new(id,request.Kind,d.Id,"","",0,512,"",challenge,request.Kind=="FILE_LIST"?DateTimeOffset.UtcNow.AddMinutes(15):DateTimeOffset.UtcNow.AddHours(24),true,f),"AWAITING_APPROVAL",DateTimeOffset.UtcNow);
        Db.Put("job",id,job);Db.Log("admin","filejob.planned",$"{id} {request.Kind} root={f.RootId}");return job;
    }
    public void Revalidate(Job job)
    {
        var f=job.Plan.File ?? throw new ApiException(400,"Plano de arquivos inválido.");Scope(owner.Device(job.Plan.DeviceId),f,job.Plan.Kind=="FILE_RESTORE");
        if(job.Plan.Kind=="FILE_RESTORE")Require(Get(f.BundleId).State=="AVAILABLE",409,"Backup indisponível.");
    }
    public Job List(FileListRequest request)
    {
        var job=Plan(new("FILE_LIST",request.DeviceId,"",null,request.Consent,new(request.RootId,request.RelativePath,Cursor:request.Cursor,PageSize:request.PageSize)));
        return owner.Approve(job.Id,job.Plan.Challenge);
    }
    public void Prepare(Job job)
    {
        var f=job.Plan.File!;Require(Guid.TryParse(f.BundleId,out _),400,"ID de backup inválido.");
        var bundle=new FileBundle(1,f.BundleId,f.Name,"CREATING",job.Plan.DeviceId,f.RootId,DateTimeOffset.UtcNow,0,[]);
        Persist(bundle);Directory.CreateDirectory(BundlePath(bundle.Id));
    }
    public string BundlePath(string id)
    { Require(Guid.TryParse(id,out _),400,"ID de backup inválido.");return Path.Combine(owner.Repository,"file-bundles",id); }
    private string DataPath(string bundleId,string fileId,int seq)
    { Require(Guid.TryParse(fileId,out _) && seq>=0,400,"Identidade de arquivo/chunk inválida.");return Path.Combine(BundlePath(bundleId),fileId,$"{seq:D8}.bin"); }
    public FileBundle Get(string id)=>Db.Get<FileBundle>("filebundle",id) ?? throw new ApiException(404,"Backup não encontrado.");
    public FileBundle Manifest(string deviceId,string jobId)
    { var job=owner.AgentJob(deviceId,jobId,"FILE_RESTORE");var bundle=Get(job.Plan.File!.BundleId);Require(bundle.State=="AVAILABLE",409,"Backup indisponível.");return bundle; }
    public FileBundle Begin(string deviceId,string jobId,BundleStart start)
    {
        var job=owner.AgentJob(deviceId,jobId,"FILE_BACKUP");var bundle=Get(job.Plan.File!.BundleId);
        Require(bundle.State=="CREATING" && bundle.Entries.Count==0 && start.Entries is {Count:>0 and <=100000},409,"Backup já iniciado ou manifesto inválido.");
        long total=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in start.Entries)
        {
            Relative(entry.RelativePath,false);Require(Guid.TryParse(entry.Id,out _) && ids.Add(entry.Id) && names.Add(entry.RelativePath),400,"Entradas duplicadas/inválidas.");
            Require(entry.SizeBytes>=0 && (!entry.IsDirectory || entry.SizeBytes==0) && entry.Chunks is {Count:0},400,"Metadados/tamanho de arquivo inválidos.");
            total=checked(total+entry.SizeBytes);
            Require(job.Plan.File.Paths!.Any(selected=>entry.RelativePath.Equals(selected,StringComparison.OrdinalIgnoreCase)||entry.RelativePath.StartsWith(selected+"/",StringComparison.OrdinalIgnoreCase)||(entry.IsDirectory && selected.StartsWith(entry.RelativePath+"/",StringComparison.OrdinalIgnoreCase))),403,"Arquivo fora da seleção autorizada.");
        }
        // A file can never be an ancestor directory of another entry.
        var byPath=start.Entries.ToDictionary(e=>e.RelativePath,StringComparer.OrdinalIgnoreCase);
        foreach(var e in start.Entries){var parent=e.RelativePath;while(parent.Contains('/')){parent=parent[..parent.LastIndexOf('/')];Require(byPath.TryGetValue(parent,out var p)&&p.IsDirectory,400,"Diretório ancestral ausente ou conflitante no manifesto.");}}
        Require(total==start.TotalBytes && owner.FreeBytes()>total+64L*1024*1024,409,"Tamanho total divergente ou espaço insuficiente no servidor.");
        bundle=bundle with{Entries=start.Entries,TotalBytes=total};Persist(bundle);
        foreach(var e in start.Entries)Db.Put("bundle-entry",bundle.Id+":"+e.Id,e);
        return bundle;
    }
    public ImageChunk Upload(string deviceId,string jobId,string fileId,int seq,byte[] bytes,string sha)
    {
        var job=owner.AgentJob(deviceId,jobId,"FILE_BACKUP");var bundleId=job.Plan.File!.BundleId;
        var entry=Db.Get<BundleEntry>("bundle-entry",bundleId+":"+fileId) ?? throw new ApiException(404,"Arquivo fora do manifesto.");
        Require(!entry.IsDirectory && seq>=0 && State(bundleId)=="CREATING",409,"Arquivo/backup não recebe dados.");
        var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));Require(Controller.SafeEquals(hash,sha),400,"SHA256 divergente.");
        var offset=(long)seq*Controller.ChunkSize;var size=(int)Math.Min(Controller.ChunkSize,entry.SizeBytes-offset);
        Require(size>0 && bytes.Length==size,400,"Tamanho/offset inválido.");
        if(Db.GetChunk(bundleId,fileId,seq) is {} old){Require(old.Sha256==hash,409,"Chunk duplicado divergente.");return old;}
        Require(Db.NextChunk(bundleId,fileId)==seq,409,"Chunk fora de ordem.");Require(owner.FreeBytes()>bytes.Length+16L*1024*1024,409,"Repositório cheio.");
        var path=DataPath(bundleId,fileId,seq);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var stream=new FileStream(path+".incoming",FileMode.Create,FileAccess.Write,FileShare.None,Controller.ChunkSize,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}
        File.Move(path+".incoming",path,true);using(var stream=File.OpenRead(path))Require(Convert.ToHexStringLower(SHA256.HashData(stream))==hash,409,"Falha de persistência do chunk.");
        var chunk=new ImageChunk(seq,offset,bytes.Length,hash);Db.AddChunk(bundleId,fileId,chunk);return chunk;
    }
    public FileBundle Complete(string deviceId,string jobId)
    {
        var job=owner.AgentJob(deviceId,jobId,"FILE_BACKUP");var bundle=Get(job.Plan.File!.BundleId);
        Require(bundle.State=="CREATING" && bundle.Entries.Count>0,409,"Manifesto de backup ausente.");
        bundle=bundle with{Entries=bundle.Entries.Select(e=>e with{Chunks=Db.Chunks(bundle.Id,e.Id)}).ToList()};Validate(bundle,false);
        var path=Path.Combine(BundlePath(bundle.Id),"manifest.json");File.WriteAllText(path+".incoming",Wire.Serialize(bundle with{State="AVAILABLE"}));File.Move(path+".incoming",path,true);
        bundle=bundle with{State="AVAILABLE"};Persist(bundle);Db.Log(deviceId,"filebundle.available",bundle.Id);return bundle;
    }
    public void Validate(FileBundle bundle,bool deep)
    {
        long total=0;
        foreach(var entry in bundle.Entries)
        {
            long offset=0;int sequence=0;Relative(entry.RelativePath,false);
            foreach(var chunk in entry.Chunks)
            {
                Require(!entry.IsDirectory && chunk.Sequence==sequence++ && chunk.OffsetBytes==offset && chunk.SizeBytes>0 && chunk.SizeBytes<=Controller.ChunkSize,409,"Manifesto de arquivo não contíguo.");
                using var stream=File.OpenRead(DataPath(bundle.Id,entry.Id,chunk.Sequence));
                Require(stream.Length==chunk.SizeBytes && (!deep || Convert.ToHexStringLower(SHA256.HashData(stream))==chunk.Sha256),409,"Arquivo/chunk corrompido.");offset+=chunk.SizeBytes;
            }
            Require(offset==entry.SizeBytes,409,"Arquivo incompleto.");total=checked(total+offset);
        }
        Require(total==bundle.TotalBytes,409,"Backup incompleto.");
    }
    public byte[] Download(string deviceId,string jobId,string fileId,int seq)
    {
        var job=owner.AgentJob(deviceId,jobId,"FILE_RESTORE");var id=job.Plan.File!.BundleId;Require(State(id)=="AVAILABLE",409,"Backup indisponível.");
        var chunk=Db.GetChunk(id,fileId,seq) ?? throw new ApiException(404,"Chunk não pertence ao backup.");
        var bytes=File.ReadAllBytes(DataPath(id,fileId,seq));Require(bytes.Length==chunk.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(bytes))==chunk.Sha256,409,"Chunk corrompido; gravação bloqueada.");return bytes;
    }
    public void Fail(string id){if(Db.Get<FileBundle>("filebundle",id) is {} b && b.State!="AVAILABLE")Persist(b with{State="FAILED"});}
    public FileBundle Verify(string id)
    {
        var bundle=Get(id);Require(bundle.State=="AVAILABLE",409,"Backup indisponível.");
        Require(!Db.All<Job>("job").Any(j=>j.Plan.Kind=="FILE_RESTORE" && j.Plan.File?.BundleId==id && j.State is "QUEUED" or "RUNNING" or "CANCELLING"),409,"Backup em restauração; aguarde.");
        var verifying=bundle with{State="VERIFYING"};Persist(verifying);
        _=Task.Run(()=>{var state="AVAILABLE";try{Validate(bundle,true);}catch{state="FAILED";}lock(owner.Gate){Persist(bundle with{State=state});Db.Log("system","filebundle.verified",$"{id} {state}");}});
        return verifying;
    }
}
