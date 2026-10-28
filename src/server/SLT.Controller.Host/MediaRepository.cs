using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using SLT.WindowsAgent;

namespace Slt.Controller;

public sealed class MediaRepository
{
    private readonly Controller owner;
    private static void Require(bool ok,string message)=>Controller.Ensure(ok,409,message);
    public MediaRepository(Controller owner)
    {
        this.owner=owner;
        foreach(var m in owner.Db.All<DeploymentMedia>("media").Where(m=>m.State=="IMPORTING"))
            owner.Db.Put("media",m.Id,m with{State="FAILED",Error="Importação interrompida; importe novamente."});
    }
    private string MediaPath(string id)
    {Require(Guid.TryParse(id,out _),"ID de mídia inválido.");return Path.Combine(owner.Repository,"media",id);}
    public DeploymentMedia Import(MediaImport request)
    {
        Require(OperatingSystem.IsWindows(),"Importação exige Controller Windows.");
        Require(request.Name is {Length:>0 and <=128} && !request.Name.Any(char.IsControl),"Informe um nome para a mídia.");
        Require(Path.IsPathFullyQualified(request.Path) && !request.Path.StartsWith('\\'),"Selecione mídia local com caminho completo.");
        var source=Path.GetFullPath(request.Path);var ext=Path.GetExtension(source).ToLowerInvariant();
        Require(ext is ".iso" or ".esd" or ".wim","Use ISO, WIM ou ESD.");
        using(var lease=SecuredPath.OpenAbsolute(source,true))Require(!lease.Info.IsDirectory && owner.FreeBytes()>lease.Info.Length+64L*1024*1024,"Mídia inválida ou espaço insuficiente.");
        var m=new DeploymentMedia(Wire.Id(),request.Name,"",0,"",[],[],"IMPORTING",DateTimeOffset.UtcNow);
        Directory.CreateDirectory(MediaPath(m.Id));owner.Db.Put("media",m.Id,m);owner.Db.Log("admin","media.import.started",m.Id);
        _=Task.Run(()=>ImportWork(m,source,ext));return m;
    }
    private void ImportWork(DeploymentMedia media,string source,string ext)
    {
        try
        {
            using var lease=SecuredPath.OpenAbsolute(source,true);var info=lease.Info;
            var path=Path.Combine(MediaPath(media.Id),"install"+(ext==".iso"?".esd":ext));
            WindowsImage[]? linux=null;
            if(ext==".iso")
            {
                var seven=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"7-Zip","7z.exe");
                Require(File.Exists(seven),"Instale 7-Zip para extrair ISO, ou importe diretamente sources/install.esd ou install.wim.");
                linux=ReadLinuxIso(seven,source);
            }
            if(linux!=null)
            {
                // Layered Ubuntu-family live ISO: the whole verified ISO is the deployment payload.
                path=Path.Combine(MediaPath(media.Id),"media.iso");
                using var input=lease.ReadStream();using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);
            }
            else if(ext==".iso")
            {
                var seven=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"7-Zip","7z.exe");
                string? member=null;
                foreach(var candidate in new[]{"sources/install.esd","sources/install.wim"})
                {
                    using var list=Process.Start(Start(seven,["l","-slt",source,candidate])) ?? throw new IOException("7-Zip não iniciou.");
                    list.StandardInput.Close();var output=ReadLimited(list.StandardOutput,1024*1024);list.WaitForExit();
                    Require(list.ExitCode==0,"7-Zip não conseguiu ler a ISO.");
                    if(output.Replace('\\','/').Contains("Path = "+candidate,StringComparison.OrdinalIgnoreCase)){member=candidate;break;}
                }
                Require(member!=null,"ISO sem sources/install.esd ou install.wim; mídia dividida não suportada.");
                path=Path.Combine(MediaPath(media.Id),Path.GetFileName(member!));
                using var process=Process.Start(Start(seven,["e","-y","-bd","-bso0","-bsp0","-o"+MediaPath(media.Id),source,member!])) ?? throw new IOException("7-Zip não iniciou.");
                process.StandardInput.Close();
                var diagnostics=process.StandardOutput.ReadToEndAsync();var deadline=DateTime.UtcNow.AddMinutes(30);
                try
                {
                    while(!process.WaitForExit(500))
                    {
                        Require(DateTime.UtcNow<deadline,"Extração excedeu 30 minutos.");
                        Require(!File.Exists(path)||new FileInfo(path).Length<=info.Length,"Extração ultrapassou o limite da ISO.");
                    }
                    _=diagnostics.GetAwaiter().GetResult();Require(process.ExitCode==0 && File.Exists(path),"Extração de mídia falhou.");
                }
                finally {if(!process.HasExited){process.Kill(true);process.WaitForExit();}}
            }
            else {using var input=lease.ReadStream();using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);}
            // ISO lease remains open without share-write/delete until extraction finishes.
            using var file=File.OpenRead(path);var images=linux??ReadImages(file);file.Position=0;
            var chunks=new List<ImageChunk>();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long offset=0;var buffer2=new byte[Controller.ChunkSize];
            while(offset<file.Length){var count=(int)Math.Min(buffer2.Length,file.Length-offset);file.ReadExactly(buffer2.AsSpan(0,count));hash.AppendData(buffer2,0,count);chunks.Add(new(chunks.Count,offset,count,Convert.ToHexStringLower(SHA256.HashData(buffer2.AsSpan(0,count)))));offset+=count;}
            media=media with{FileName=Path.GetFileName(path),SizeBytes=file.Length,Sha256=Convert.ToHexStringLower(hash.GetHashAndReset()),Chunks=chunks,Images=images,State="AVAILABLE",Kind=linux!=null?"LINUX":"WINDOWS"};
            File.WriteAllText(Path.Combine(MediaPath(media.Id),"manifest.json"),Wire.Serialize(media));
            lock(owner.Gate){owner.Db.ImportChunks(media.Id,"media",chunks);owner.Db.Put("media",media.Id,media with{Chunks=[]});owner.Db.Log("system","media.import.completed",media.Id);}
        }
        catch(Exception e){lock(owner.Gate){owner.Db.Put("media",media.Id,media with{State="FAILED",Error=e.Message});owner.Db.Log("system","media.import.failed",$"{media.Id} {e.Message}");}}
    }
    private static string? ReadIsoText(string seven,string iso,string member)
    {
        using var process=Process.Start(Start(seven,["e","-so",iso,member])) ?? throw new IOException("7-Zip não iniciou.");
        process.StandardInput.Close();var text=ReadLimited(process.StandardOutput,64*1024);process.WaitForExit();
        return process.ExitCode==0 && text.Length>0 ? text : null;
    }
    // Ubuntu-family layered live ISO (casper): name, layer size and default source.
    public static WindowsImage[]? ReadLinuxIso(string seven,string iso)
    {
        var sources=ReadIsoText(seven,iso,"casper/install-sources.yaml");var info=ReadIsoText(seven,iso,".disk/info");
        if(sources==null||info==null)return null;
        var lines=sources.Split('\n').Select(l=>l.Trim()).ToArray();
        var squash=lines.FirstOrDefault(l=>l.StartsWith("path:",StringComparison.Ordinal))?[5..].Trim();
        var size=lines.FirstOrDefault(l=>l.StartsWith("size:",StringComparison.Ordinal))?[5..].Trim();
        Require(squash is {Length:>0 and <=64} && squash.EndsWith(".squashfs",StringComparison.Ordinal) && squash.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch is '.' or '-' or '_'),"ISO Linux sem camada squashfs padrão reconhecida.");
        Require(long.TryParse(size,out var bytes)&&bytes is >0 and <1L<<40,"Tamanho da camada Linux inválido.");
        var name=info.Split('\n')[0].Trim();Require(name.Length is >0 and <=200 && !name.Any(char.IsControl),"Identificação da ISO Linux inválida.");
        var version=System.Text.RegularExpressions.Regex.Match(name,@"\b\d{2}\.\d{2}(\.\d+)?\b").Value;
        return [new(1,name,"Linux em camadas (casper/"+squash+"); instalação pelo SLT Rescue","AMD64",version,Math.Max(16L*1024*1024*1024,bytes+bytes/2+4L*1024*1024*1024),"pt-BR")];
    }
    private static ProcessStartInfo Start(string exe,string[] args)
    {var p=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardInput=true};foreach(var a in args)p.ArgumentList.Add(a);return p;}
    private static string ReadLimited(StreamReader reader,int maximum)
    {var b=new System.Text.StringBuilder();var c=new char[4096];int n;while((n=reader.Read(c,0,c.Length))>0){Require(b.Length+n<=maximum,"Listagem de mídia excessiva.");b.Append(c,0,n);}return b.ToString();}
    public static WindowsImage[] ReadImages(Stream stream)
    {
        var header=new byte[208];stream.Position=0;stream.ReadExactly(header);
        Require(header.AsSpan(0,8).SequenceEqual("MSWIM\0\0\0"u8) && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))>=208,"Cabeçalho WIM/ESD inválido.");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(40))==1 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(42))==1,"Mídia WIM dividida não suportada.");
        var count=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));Require(count is >0 and <=1000,"Número de edições inválido.");
        var packed=BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));var size=(long)(packed&0x00ffffffffffffffUL);var offset=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(80));var original=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(88));
        Require((packed>>56&~2UL)==0 && size is >=4 and <=8388608 && size==original && size%2==0 && offset>=208 && offset<=stream.Length-size,"XML da mídia comprimido/fora do arquivo ou excessivo.");
        var bytes=new byte[(int)size];stream.Position=offset;stream.ReadExactly(bytes);Require(bytes[0]==255&&bytes[1]==254,"XML WIM deve ser UTF-16LE.");
        using var reader=XmlReader.Create(new MemoryStream(bytes),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8388608});
        var document=XDocument.Load(reader);Require(document.Root?.Name.LocalName=="WIM","XML WIM inválido.");
        string Text(XElement? el,string name)=>el?.Element(name)?.Value??"";
        string Safe(string value){Require(value.Length<=1024&&!value.Any(ch=>char.IsControl(ch)&&ch!='\t'),"Metadados de edição inválidos.");return value;}
        var result=new List<WindowsImage>();
        foreach(var element in document.Root!.Elements("IMAGE"))
        {
            Require(int.TryParse(element.Attribute("INDEX")?.Value,out var index)&&index>0 && !result.Any(i=>i.Index==index),"Índice de edição inválido.");
            var windows=element.Element("WINDOWS");Require(windows!=null,"Edição sem metadados Windows.");
            Require(long.TryParse(Text(element,"TOTALBYTES"),out var total)&&total>0 && total<1L<<40,"Tamanho de edição inválido.");
            var arch=Text(windows,"ARCH") switch{"9"=>"AMD64","0"=>"X86","12"=>"ARM64",_=>"UNKNOWN"};
            var version=windows!.Element("VERSION");var language=windows.Element("LANGUAGES")?.Element("DEFAULT")?.Value??"";
            Require(language.Length<=16 && language.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch=='-'),"Idioma de edição inválido.");
            result.Add(new(index,Safe(Text(element,"NAME")),Safe(Text(element,"DESCRIPTION")),arch,Safe(string.Join('.',new[]{"MAJOR","MINOR","BUILD","SPBUILD"}.Select(n=>Text(version,n)))),Math.Max(32L*1024*1024*1024,total+8L*1024*1024*1024),language));
        }
        Require(result.Count==count,"Contagem de edições difere do cabeçalho.");return result.ToArray();
    }
    // Microsoft tools are catalog-signed and cannot be verified offline in WinPE, so the tool
    // comes from the operator's legitimate Windows media and travels with its signed SHA256.
    public (string Path,string Sha256) Tool(string mediaId,int index,string name)
    {
        Require(name is "MBR2GPT.EXE","Ferramenta não permitida.");
        var media=Get(mediaId);Require(media.State=="AVAILABLE"&&media.Kind=="WINDOWS"&&media.Images.Any(i=>i.Index==index),"Mídia Windows/edição indisponível para a ferramenta.");
        var dir=Path.Combine(MediaPath(media.Id),"tools",index.ToString(System.Globalization.CultureInfo.InvariantCulture));var path=Path.Combine(dir,name);
        if(!File.Exists(path))
        {
            var seven=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"7-Zip","7z.exe");
            Require(File.Exists(seven),"Instale 7-Zip para extrair a ferramenta da mídia Windows.");
            Directory.CreateDirectory(dir);
            using var process=Process.Start(Start(seven,["e","-y","-bso0","-bsp0","-o"+dir,Path.Combine(MediaPath(media.Id),media.FileName),$"{index}/Windows/System32/{name}"])) ?? throw new IOException("7-Zip não iniciou.");
            process.StandardInput.Close();_=ReadLimited(process.StandardOutput,1024*1024);
            if(!process.WaitForExit(120000)){process.Kill(true);process.WaitForExit();}
            Require(process.ExitCode==0&&File.Exists(path),"A mídia Windows não contém "+name+" nesta edição.");
        }
        Require(new FileInfo(path).Length is >0 and <=16*1024*1024,"Ferramenta extraída com tamanho inválido.");
        using var file=File.OpenRead(path);return (path,Convert.ToHexStringLower(SHA256.HashData(file)));
    }
    public byte[] ToolDownload(string device,string jobId)
    {
        var job=owner.AgentJob(device,jobId,"CONVERT_GPT");var options=job.Plan.Deployment!;var tool=Tool(options.MediaId,options.ImageIndex,"MBR2GPT.EXE");
        var bytes=File.ReadAllBytes(tool.Path);Require(Convert.ToHexStringLower(SHA256.HashData(bytes))==options.ToolSha256,"Ferramenta alterada após o planejamento; execução bloqueada.");return bytes;
    }
    public DeploymentMedia Get(string id)=>owner.Db.Get<DeploymentMedia>("media",id)??throw new ApiException(404,"Mídia não encontrada.");
    public DeploymentMedia Manifest(string device,string jobId)
    {var job=owner.AgentJob(device,jobId,"DEPLOY_WINDOWS","DEPLOY_LINUX");var media=Get(job.Plan.Deployment!.MediaId);Require(media.State=="AVAILABLE","Mídia indisponível.");return media with{Chunks=owner.Db.Chunks(media.Id,"media")};}
    public byte[] Download(string device,string jobId,int seq)
    {
        var job=owner.AgentJob(device,jobId,"DEPLOY_WINDOWS","DEPLOY_LINUX");var media=Get(job.Plan.Deployment!.MediaId);Require(media.State=="AVAILABLE","Mídia indisponível.");
        var chunk=owner.Db.GetChunk(media.Id,"media",seq)??throw new ApiException(404,"Chunk fora da mídia.");
        using var file=File.OpenRead(Path.Combine(MediaPath(media.Id),media.FileName));file.Position=chunk.OffsetBytes;var bytes=new byte[chunk.SizeBytes];file.ReadExactly(bytes);
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes))==chunk.Sha256,"Mídia alterada/corrompida; download bloqueado.");return bytes;
    }
}

public sealed partial class Controller
{
    private Job PlanDeployment(PlanRequest request)
    {
        Ensure(request.Consent,400,"Autorização do responsável obrigatória para apagar o disco.");
        var d=Device(request.DeviceId);var disk=d.Inventory?.Disks.SingleOrDefault(x=>x.Fingerprint==request.DiskFingerprint)??throw new ApiException(409,"Disco não encontrado.");
        var options=request.Deployment??throw new ApiException(400,"Escolha mídia, edição e firmware.");
        var linux=request.Kind=="DEPLOY_LINUX";string? password=null;
        if(options.Account!=null)
        {
            var account=Provisioning.Validate(options.Account,linux);password=Provisioning.TemporaryPassword();
            if(!linux && Media.Get(options.MediaId).Images.SingleOrDefault(i=>i.Index==options.ImageIndex)?.Language is {Length:>0} language)account=account with{Locale=language};
            options=options with{Account=linux?account with{PasswordHash=Provisioning.Sha512Crypt(password,Provisioning.RandomSalt())}:account with{Password=password}};
        }
        Ensure(!linux||options.Account!=null,400,"Instalação Linux exige usuário inicial.");
        ValidateDeployment(d,disk,options,request.Kind);var id=Wire.Id();
        var plan=new JobPlan(id,request.Kind,d.Id,disk.Fingerprint,disk.Path,disk.SizeBytes,disk.LogicalSectorBytes,"",$"APAGAR {d.Hostname} {disk.Fingerprint[..12]}",DateTimeOffset.UtcNow.AddHours(24),true,Deployment:options);
        var job=new Job(id,plan,"AWAITING_APPROVAL",DateTimeOffset.UtcNow);Db.Put("job",id,job);
        var masked=options.Account is {} a?options with{Account=a with{Password=a.Password==null?null:"***",PasswordHash=a.PasswordHash==null?null:"***"}}:options;
        Db.Log("admin","deployment.planned",Wire.Serialize(plan with{Deployment=masked}));
        // The temporary password is returned once to the operator and never logged.
        return job with{TemporaryPassword=password};
    }
    private void ValidateDeployment(Device d,DiskInfo disk,DeploymentOptions options,string kind)
    {
        if(kind=="DEPLOY_LINUX")
        {
            Ensure(d.Mode=="RESCUE" && d.TrustStatus=="TRUSTED" && d.LastSeenAtUtc>DateTimeOffset.UtcNow.AddSeconds(-45)&&d.Inventory?.Capabilities.Contains("DEPLOY_LINUX")==true,409,"Instalação Linux exige SLT Rescue autorizado, online e com gravação habilitada.");
            Ensure(!disk.IsSystem&&!disk.IsReadOnly&&!disk.Mounted&&disk.Serial.Length>0&&disk.Path.StartsWith("/",StringComparison.Ordinal),409,"Disco protegido, montado ou sem identidade física suficiente.");
        }
        Ensure(Media.Get(options.MediaId).Kind==(kind=="DEPLOY_LINUX"?"LINUX":"WINDOWS"),409,"Mídia não corresponde ao sistema solicitado.");
        if(kind!="DEPLOY_LINUX")Ensure(d.Mode=="WINPE" && d.TrustStatus=="TRUSTED" && d.LastSeenAtUtc>DateTimeOffset.UtcNow.AddSeconds(-45)&&d.Inventory?.Capabilities.Contains("DEPLOY_WINDOWS")==true,409,"Instalação exige agente WinPE autorizado e online.");
        if(kind!="DEPLOY_LINUX")Ensure(!disk.IsSystem&&!disk.IsReadOnly&&disk.Serial.Length>0&&disk.Model.Length>0&&disk.Path.StartsWith(@"\\.\PhysicalDrive",StringComparison.Ordinal),409,"Disco protegido ou sem identidade física suficiente.");
        Ensure(options.Firmware is "GPT_UEFI" or "MBR_BIOS",400,"Firmware inválido.");
        Ensure(options.Firmware!="MBR_BIOS"||disk.SizeBytes<=2L*1024*1024*1024*1024,409,"MBR limitado a 2 TiB neste perfil.");
        var media=Media.Get(options.MediaId);var image=media.Images.SingleOrDefault(i=>i.Index==options.ImageIndex);
        Ensure(media.State=="AVAILABLE" && image!=null && image.Architecture=="AMD64" && image.MinimumSizeBytes<=disk.SizeBytes-1024*1024*1024,409,"Mídia/edição indisponível, arquitetura ou capacidade incompatível.");
    }
    private void RevalidateDeployment(Job job)
    {
        var d=Device(job.Plan.DeviceId);var disk=d.Inventory?.Disks.SingleOrDefault(x=>x.Fingerprint==job.Plan.DiskFingerprint);
        Ensure(disk!=null&&disk.Path==job.Plan.DiskPath&&disk.SizeBytes==job.Plan.DiskSizeBytes&&disk.LogicalSectorBytes==job.Plan.LogicalSectorBytes,409,"Identidade do destino mudou.");ValidateDeployment(d,disk!,job.Plan.Deployment!,job.Plan.Kind);
    }
}
