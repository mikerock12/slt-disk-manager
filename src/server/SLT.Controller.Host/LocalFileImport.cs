using System.Security.Cryptography;
using SLT.WindowsAgent;

namespace Slt.Controller;

public sealed partial class FileRepository
{
    public FileBundle Import(LocalImport request)
    {
        Require(OperatingSystem.IsWindows(),409,"Importação local exige o Controller Windows.");
        Require(request.Consent && request.Name is {Length:>0 and <=128} && !request.Name.Any(char.IsControl),400,"Informe nome e autorização para importar arquivos locais.");
        Require(Path.IsPathFullyQualified(request.Path) && !request.Path.StartsWith('\\'),400,"Escolha arquivo/pasta local com caminho completo.");
        var path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path));
        Require(!FileSafety.Under(path,owner.Repository) && !FileSafety.Under(owner.Repository,path),400,"Escolha origem separada do repositório operacional.");
        using(var lease=SecuredPath.OpenAbsolute(path)) Relative(Path.GetFileName(path),false);
        var bundle=new FileBundle(1,Wire.Id(),request.Name,"CREATING","SERVER","local",DateTimeOffset.UtcNow,0,[]);
        Directory.CreateDirectory(BundlePath(bundle.Id));Persist(bundle);Db.Log("admin","filebundle.import.started",bundle.Id);
        _=Task.Run(()=>ImportWork(bundle,path));return bundle;
    }
    private void ImportWork(FileBundle bundle,string source)
    {
        try
        {
            var entries=new List<BundleEntry>();long total=0;
            void Visit(string path,string relative)
            {
                Relative(relative,false);Require(entries.Count<100000,400,"Importação excede 100000 entradas.");
                using var lease=SecuredPath.OpenAbsolute(path,readFile:!Directory.Exists(path));var info=lease.Info;
                var entry=new BundleEntry(Wire.Id(),relative,info.IsDirectory,info.IsDirectory?0:info.Length,info.LastWriteTimeUtc,[]);
                entries.Add(entry);
                if(info.IsDirectory)
                {
                    var children=Directory.GetFileSystemEntries(path).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach(var child in children)Visit(child,relative+"/"+Path.GetFileName(child));
                    Require(lease.Info.LastWriteTimeUtc==info.LastWriteTimeUtc,409,"Pasta alterada durante leitura; importe novamente.");return;
                }
                total=checked(total+info.Length);Require(total<=1L<<40,400,"Importação excede 1 TiB.");
                using var input=lease.ReadStream();var buffer=new byte[Controller.ChunkSize];long offset=0;int seq=0;
                while(offset<info.Length)
                {
                    var n=(int)Math.Min(buffer.Length,info.Length-offset);input.ReadExactly(buffer.AsSpan(0,n));
                    var chunk=new ImageChunk(seq,offset,n,Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0,n))));
                    lock(owner.Gate){Require(owner.FreeBytes()>n+64L*1024*1024,409,"Repositório sem espaço para importação.");}
                    var dest=DataPath(bundle.Id,entry.Id,seq);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using(var output=new FileStream(dest,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.WriteThrough)){output.Write(buffer,0,n);output.Flush(true);}
                    using(var verify=File.OpenRead(dest))Require(Convert.ToHexStringLower(SHA256.HashData(verify))==chunk.Sha256,409,"Persistência do arquivo falhou.");
                    lock(owner.Gate)Db.AddChunk(bundle.Id,entry.Id,chunk);entry.Chunks.Add(chunk);offset+=n;seq++;
                }
                Require(input.ReadByte()==-1,409,"Arquivo cresceu durante leitura.");
                var after=lease.Info;Require(after.Length==info.Length && after.LastWriteTimeUtc==info.LastWriteTimeUtc,409,"Arquivo alterado durante leitura.");
            }
            Visit(source,Path.GetFileName(source));
            bundle=bundle with{Entries=entries,TotalBytes=total};Validate(bundle,true);
            File.WriteAllText(Path.Combine(BundlePath(bundle.Id),"manifest.json"),Wire.Serialize(bundle with{State="AVAILABLE"}));
            lock(owner.Gate){Persist(bundle with{State="AVAILABLE"});Db.Log("system","filebundle.import.completed",bundle.Id);}
        }
        catch(Exception e){lock(owner.Gate){Persist(bundle with{State="FAILED"});Db.Log("system","filebundle.import.failed",$"{bundle.Id} {e.Message}");}}
    }
    private string State(string id)=>Db.Get<string>("bundle-state",id)??Get(id).State;
    private void Persist(FileBundle bundle){Db.Put("filebundle",bundle.Id,bundle);Db.Put("bundle-state",bundle.Id,bundle.State);}
}
