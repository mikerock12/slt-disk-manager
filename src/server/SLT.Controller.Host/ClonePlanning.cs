namespace Slt.Controller;

public static class ClonePlanning
{
    private static void Require(bool ok,string message)=>Controller.Ensure(ok,409,message);
    public static void ValidateLayout(DiskLayout layout,long bytes)
    {
        Require(layout.TableType is "GPT" or "MBR","Tabela de partição não suportada.");
        Require(layout.SectorSize is 512 or 4096 && bytes>0 && bytes%layout.SectorSize==0,"Geometria de layout inválida.");
        Require(layout.Partitions is {Length:>0 and <=128} && layout.Partitions.Select(p=>p.Number).Distinct().Count()==layout.Partitions.Length,"Partições ambíguas/ausentes.");
        Require(layout.FirstUsableLba>=1 && layout.LastUsableLba<bytes/layout.SectorSize && layout.LastUsableLba>=layout.FirstUsableLba,"Limites de layout inválidos.");
        long end=layout.FirstUsableLba;
        foreach(var p in layout.Partitions.OrderBy(p=>p.StartLba))
        {
            Require(p.Number>0 && p.StartLba>=end && p.LengthLba>0 && p.LengthLba<=layout.LastUsableLba+1-p.StartLba,"Partição sobreposta/fora do disco.");
            Require(new[]{p.Type,p.Uuid,p.Name,p.Attributes,p.FileSystem}.All(s=>s!=null && s.Length<=256 && !s.Any(char.IsControl)),"Metadados de partição inválidos.");end=checked(p.StartLba+p.LengthLba);
        }
    }
    public static ClonePreview Preview(DiskInfo source,DiskInfo target,string strategy,bool stream=false)
    {
        var errors=new List<string>();var warnings=new List<string>();var actions=new List<string>();
        var layout=source.Layout;DiskLayout? after=layout;int resize=0;long scratch=0;
        if(strategy is not "EXACT_RAW" and not "PRESERVE_LAYOUT" and not "FIT")errors.Add("Estratégia inválida.");
        if(source.LogicalSectorBytes!=target.LogicalSectorBytes)errors.Add("Setores lógicos diferentes.");
        if(source.Fingerprint==target.Fingerprint)errors.Add("Origem e destino têm a mesma identidade; clonagem bloqueada.");
        if(source.Mounted||source.IsSystem||target.Mounted||target.IsSystem||target.IsReadOnly)errors.Add("Origem/destino devem estar offline no Rescue; destino gravável.");
        actions.Add("Preservar todos os setores/partições, payload de boot, sistema operacional e dados da imagem original.");
        if(strategy=="EXACT_RAW" && source.SizeBytes!=target.SizeBytes)errors.Add("Clone exato exige capacidades iguais; escolha preservar layout ou ajustar.");
        if(strategy=="PRESERVE_LAYOUT" && source.SizeBytes>target.SizeBytes)errors.Add("Preservar layout não permite truncar um disco maior.");
        if(strategy!="EXACT_RAW" && layout==null)errors.Add("Layout detalhado da origem necessário para adaptar o destino.");
        if(layout!=null)
        {
            try{ValidateLayout(layout,source.SizeBytes);}catch(ApiException e){errors.Add(e.Message);}
            if(errors.Count==0 && strategy!="EXACT_RAW")
            {
                var suffix=source.SizeBytes/layout.SectorSize-1-layout.LastUsableLba;
                long last=target.SizeBytes/layout.SectorSize-1-suffix;
                after=layout with{LastUsableLba=last};
                if(strategy=="PRESERVE_LAYOUT")
                {
                    if(layout.TableType=="GPT")actions.Add("Reposicionar e validar cabeçalho GPT secundário no fim do destino.");
                    warnings.Add("Partições mantêm seus tamanhos; capacidade adicional permanece não alocada.");
                }
                if(strategy=="FIT" && stream && target.SizeBytes<source.SizeBytes)errors.Add("Reduzir para disco menor ainda exige o modo com imagem completa no servidor.");
                if(strategy=="FIT" && errors.Count==0)
                {
                    if(layout.TableType=="MBR" && layout.Partitions.Any(p=>p.Number>4 || new[]{"5","05","f","0f","85","42"}.Contains(p.Type.TrimStart('0').ToLowerInvariant())))errors.Add("MBR estendido/dinâmico não é adaptado automaticamente.");
                    var candidate=layout.Partitions.Where(p=>p.FileSystem.Equals("ntfs",StringComparison.OrdinalIgnoreCase)||p.FileSystem.Equals("ext4",StringComparison.OrdinalIgnoreCase)).OrderByDescending(p=>p.LengthLba).FirstOrDefault();
                    if(candidate==null)errors.Add("Não há partição NTFS/ext4 elegível para ajuste.");
                    else
                    {
                        resize=candidate.Number;var difference=target.SizeBytes-source.SizeBytes;
                        var deltaBytes=difference/1048576*1048576;if(difference<0 && difference%1048576!=0)deltaBytes-=1048576;
                        var delta=deltaBytes/layout.SectorSize;
                        if(layout.Partitions.Any(p=>p.StartLba>candidate.StartLba && (p.Bootable || p.Type.Equals("c12a7328-f81f-11d2-ba4b-00a0c93ec93b",StringComparison.OrdinalIgnoreCase)||p.Type.Equals("21686148-6449-6e6f-744e-656564454649",StringComparison.OrdinalIgnoreCase))))errors.Add("Ajuste não move partição de boot/ESP posterior ao volume redimensionado.");
                        if(candidate.LengthLba+delta<=0 || (candidate.LengthLba+delta)*layout.SectorSize<16L*1024*1024)errors.Add("Destino pequeno demais para manter todas as partições.");
                        else if(delta<0 && candidate.MinimumSizeBytes is {} minimum && (candidate.LengthLba+delta)*layout.SectorSize<minimum)errors.Add("Destino abaixo do mínimo estimado do sistema de arquivos.");
                        if(errors.Count==0)
                        {
                            after=after with{Partitions=layout.Partitions.Select(p=>p.Number==candidate.Number?p with{LengthLba=p.LengthLba+delta}:p.StartLba>candidate.StartLba?p with{StartLba=p.StartLba+delta}:p).ToArray()};
                            try{ValidateLayout(after,target.SizeBytes);}catch(ApiException e){errors.Add(e.Message);}
                            if(stream)
                            {
                                actions.Add($"Gravar a origem direto no destino em lotes, já deslocando as partições posteriores, incluindo recuperação/OEM; depois expandir a partição {resize} ({candidate.FileSystem}) no próprio destino.");
                                actions.Add("Conferir os dados gravados, a tabela e os filesystems no destino; a origem nunca é alterada.");
                            }
                            else
                            {
                                actions.Add($"Preparar cópia de trabalho e ajustar partição {resize} ({candidate.FileSystem}); preservar e deslocar partições posteriores, incluindo recuperação/OEM.");
                                actions.Add("Validar filesystem, tabela e conteúdo das partições preservadas antes da primeira escrita no destino.");
                                scratch=checked(source.SizeBytes+target.SizeBytes+Math.Max(layout.Partitions.Max(p=>p.LengthLba*layout.SectorSize),target.SizeBytes)+64L*1024*1024);
                                warnings.Add("Requer scratch saudável no Rescue; a redução só é autorizada se as ferramentas comprovarem viabilidade na cópia de trabalho.");
                            }
                        }
                    }
                }
            }
        }
        if(stream)warnings.Add("Em lotes: origem e destino ficam ligados até o fim; o servidor guarda só uma janela pequena e apaga cada lote depois que o destino confirma a gravação. Uma falha interrompe o clone e exige novo planejamento; não há imagem para repetir.");
        warnings.Add("Conteúdo de boot é preservado; boot no novo hardware e NVRAM/ativação não são comprovados por hashes.");
        return new(errors.ToArray(),warnings.ToArray(),actions.ToArray(),layout,after,resize,scratch,stream?0:source.SizeBytes);
    }
    public static void ValidateRestore(ImageManifest image,Device target,DiskInfo disk,CloneOptions? options)
    {
        Require(image.LogicalSectorBytes==disk.LogicalSectorBytes,"Setor lógico da imagem difere do destino.");
        if(options==null){Require(image.LogicalSizeBytes==disk.SizeBytes,"Restauração RAW exige a mesma capacidade; escolha clonagem adaptável.");return;}
        var source=new DiskInfo(image.SourceFingerprint,"image","image","image",image.LogicalSizeBytes,image.LogicalSectorBytes,image.LogicalSectorBytes,image.SourceFingerprint,false,true,false,"IMAGE",[],image.Layout);
        var preview=Preview(source,disk,options.Strategy);Require(preview.Blockers.Length==0,string.Join(" ",preview.Blockers));
        if(options.Strategy=="FIT")Require(target.Inventory!.Capabilities.Contains("RESTORE_FIT") && options.ResizePartitionNumber==preview.ResizePartitionNumber,"Rescue sem capacidade de ajuste ou partição de ajuste mudou.");
        if(options.Strategy=="PRESERVE_LAYOUT" && image.LogicalSizeBytes!=disk.SizeBytes)Require(target.Inventory!.Capabilities.Contains("RESTORE_LAYOUT"),"Rescue sem capacidade de relocação de layout.");
    }
}

public sealed partial class Controller
{
    public object PlanClone(CloneRequest request)
    {
        Ensure(request.Consent,400,"Autorização do responsável obrigatória.");Ensure(request.SourceDeviceId!=request.TargetDeviceId || request.SourceDiskFingerprint!=request.TargetDiskFingerprint,409,"Origem e destino são o mesmo disco.");
        var source=Device(request.SourceDeviceId);var target=Device(request.TargetDeviceId);
        Ensure(source.Mode=="RESCUE" && target.Mode=="RESCUE" && source.TrustStatus=="TRUSTED" && target.TrustStatus=="TRUSTED",409,"Origem/destino devem estar pareados em Rescue.");
        Ensure(source.LastSeenAtUtc>DateTimeOffset.UtcNow.AddSeconds(-45) && target.LastSeenAtUtc>DateTimeOffset.UtcNow.AddSeconds(-45),409,"Origem/destino offline.");
        var a=source.Inventory?.Disks.SingleOrDefault(d=>d.Fingerprint==request.SourceDiskFingerprint) ?? throw new ApiException(404,"Disco origem não encontrado.");
        var b=target.Inventory?.Disks.SingleOrDefault(d=>d.Fingerprint==request.TargetDiskFingerprint) ?? throw new ApiException(404,"Disco destino não encontrado.");
        var transfer=request.Transfer ?? "IMAGE";
        Ensure(transfer is "IMAGE" or "STREAM",400,"Transferência deve ser IMAGE ou STREAM.");
        if(transfer=="STREAM")return PlanStreamClone(request,source,target,a,b);
        var preview=ClonePlanning.Preview(a,b,request.Strategy);
        Ensure(preview.Blockers.Length==0,409,string.Join(" ",preview.Blockers));
        Ensure(target.Inventory!.Capabilities.Contains("RESTORE") && (request.Strategy!="FIT"||target.Inventory.Capabilities.Contains("RESTORE_FIT")) && (request.Strategy!="PRESERVE_LAYOUT"||a.SizeBytes==b.SizeBytes||target.Inventory.Capabilities.Contains("RESTORE_LAYOUT")),409,"Agente destino não oferece a estratégia solicitada.");
        var capture=Plan(new("CAPTURE",source.Id,a.Fingerprint,null,true));
        var session=new CloneSession(Wire.Id(),source.Id,a.Fingerprint,target.Id,b.Fingerprint,request.Strategy,capture.Id,null,capture.Plan.ImageId,"AWAITING_SOURCE_APPROVAL",DateTimeOffset.UtcNow,preview);
        Db.Put("clone",session.Id,session);Db.Log("admin","clone.planned",session.Id);return new{session,captureJob=capture,preview};
    }
    public CloneSession CloneState(CloneSession session)
    {
        if(session.Transfer=="STREAM")return StreamCloneState(session);
        var capture=GetJob(session.CaptureJobId);var state=capture.State switch{"AWAITING_APPROVAL"=>"AWAITING_SOURCE_APPROVAL","COMPLETED"=>"IMAGE_READY","FAILED" or "CANCELLED" or "INTERRUPTED"=>"FAILED",_=>"CAPTURING"};
        if(session.TargetJobId!=null)state=GetJob(session.TargetJobId).State switch{"AWAITING_APPROVAL"=>"AWAITING_TARGET_APPROVAL","COMPLETED"=>"COMPLETED","FAILED" or "CANCELLED" or "INTERRUPTED"=>"FAILED",_=>"RESTORING"};
        return session with{State=state};
    }
    public Job PrepareCloneTarget(string id)
    {
        var session=Db.Get<CloneSession>("clone",id) ?? throw new ApiException(404,"Clone não encontrado.");
        Ensure(session.Transfer!="STREAM",409,"Clone em lotes já planeja o destino junto com a origem; autorize a tarefa do destino.");
        Ensure(session.TargetJobId==null && GetJob(session.CaptureJobId).State=="COMPLETED",409,"Captura ainda não concluída ou destino já planejado.");
        var image=GetImage(session.ImageId);var target=Device(session.TargetDeviceId);var disk=target.Inventory?.Disks.SingleOrDefault(d=>d.Fingerprint==session.TargetDiskFingerprint) ?? throw new ApiException(409,"Disco destino mudou.");
        var source=new DiskInfo(image.SourceFingerprint,"image","image","image",image.LogicalSizeBytes,image.LogicalSectorBytes,image.LogicalSectorBytes,image.SourceFingerprint,false,true,false,"IMAGE",[],image.Layout);
        var preview=ClonePlanning.Preview(source,disk,session.Strategy);Ensure(preview.Blockers.Length==0,409,string.Join(" ",preview.Blockers));
        var job=Plan(new("RESTORE",target.Id,disk.Fingerprint,image.Id,true,CloneOptions:new(session.Strategy,preview.ResizePartitionNumber)));
        Db.Put("clone",id,session with{TargetJobId=job.Id,Preview=preview});return job;
    }
}
