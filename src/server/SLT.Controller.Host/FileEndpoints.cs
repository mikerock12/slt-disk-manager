namespace Slt.Controller;

public static class FileEndpoints
{
    public static void Map(WebApplication app,Controller c)
    {
        T Locked<T>(Func<T> action){lock(c.Gate)return action();}
        string Device(HttpContext context)=>(string)context.Items["deviceId"]!;
        app.MapGet("/api/v1/jobs/{id}",(string id)=>Locked(()=>c.GetJob(id)));
        app.MapPost("/api/v1/files/list",(FileListRequest request)=>Locked(()=>c.Files.List(request)));
        app.MapGet("/api/v1/file-bundles",()=>Locked(()=>c.Db.FileCatalog()));
        app.MapGet("/api/v1/file-bundles/{id}",(string id)=>Locked(()=>c.Files.Get(id)));
        app.MapPost("/api/v1/file-bundles/import",(LocalImport request)=>Locked(()=>c.Files.Import(request)));
        app.MapPost("/api/v1/media/import",(MediaImport request)=>Locked(()=>c.Media.Import(request)));
        app.MapGet("/api/v1/media",()=>Locked(()=>c.Db.All<DeploymentMedia>("media").Select(m=>m with{Chunks=[]}).ToArray()));
        app.MapGet("/agent/v1/jobs/{id}/media",(string id,HttpContext context)=>Locked(()=>c.Media.Manifest(Device(context),id)));
        app.MapGet("/agent/v1/jobs/{id}/media/chunks/{seq:int}",(string id,int seq,HttpContext context)=>Locked(()=>Results.Bytes(c.Media.Download(Device(context),id,seq),"application/octet-stream")));
        app.MapPost("/api/v1/file-bundles/{id}/verify",(string id)=>Locked(()=>c.Files.Verify(id)));
        app.MapPost("/agent/v1/jobs/{id}/bundle",(string id,BundleStart request,HttpContext context)=>Locked(()=>c.Files.Begin(Device(context),id,request)));
        app.MapPost("/agent/v1/jobs/{id}/bundle-complete",(string id,HttpContext context)=>Locked(()=>c.Files.Complete(Device(context),id)));
        app.MapGet("/agent/v1/jobs/{id}/bundle",(string id,HttpContext context)=>Locked(()=>c.Files.Manifest(Device(context),id)));
        app.MapPut("/agent/v1/jobs/{id}/bundle/{fileId}/chunks/{seq:int}",async(string id,string fileId,int seq,HttpContext context)=>
        {var bytes=await ReadChunk(context);return Locked(()=>c.Files.Upload(Device(context),id,fileId,seq,bytes,context.Request.Headers["X-Content-SHA256"].ToString()));});
        app.MapGet("/agent/v1/jobs/{id}/bundle/{fileId}/chunks/{seq:int}",(string id,string fileId,int seq,HttpContext context)=>Locked(()=>Results.Bytes(c.Files.Download(Device(context),id,fileId,seq),"application/octet-stream")));
        app.MapPost("/api/v1/clones/plan",(CloneRequest request)=>Locked(()=>c.PlanClone(request)));
        app.MapGet("/api/v1/clones",()=>Locked(()=>c.Db.All<CloneSession>("clone").Select(c.CloneState).ToArray()));
        app.MapPost("/api/v1/clones/{id}/prepare-target",(string id)=>Locked(()=>c.PrepareCloneTarget(id)));
        app.MapGet("/api/v1/images/{id}",(string id)=>Locked(()=>c.GetImage(id)));
    }
    public static async Task<byte[]> ReadChunk(HttpContext context)
    {
        using var stream=new MemoryStream();var buffer=new byte[65536];int read;
        while((read=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0)
        {if(stream.Length+read>Controller.ChunkSize)throw new ApiException(413,"Chunk excede 4 MiB.");stream.Write(buffer,0,read);}
        return stream.ToArray();
    }
}
