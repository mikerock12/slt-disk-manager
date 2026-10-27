using Microsoft.Data.Sqlite;

namespace Slt.Controller;

// Single local database owner. Mutations are serialized by Controller.Gate.
public sealed class Store : IDisposable
{
    private readonly SqliteConnection connection;
    public Store(string directory)
    {
        Directory.CreateDirectory(directory);
        connection = new SqliteConnection($"Data Source={Path.Combine(directory, "controller.db")};Pooling=False");
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; " +
            "CREATE TABLE IF NOT EXISTS documents(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id));" +
            "CREATE TABLE IF NOT EXISTS credentials(device_id TEXT PRIMARY KEY,thumbprint TEXT NOT NULL UNIQUE);" +
            "CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT,json TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS transfer_chunks(transfer_id TEXT NOT NULL,file_id TEXT NOT NULL,sequence INTEGER NOT NULL,offset_bytes INTEGER NOT NULL,size_bytes INTEGER NOT NULL,sha256 TEXT NOT NULL,PRIMARY KEY(transfer_id,file_id,sequence)); PRAGMA user_version=2;");
    }
    private void Execute(string sql) { using var c = connection.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    public void Put<T>(string kind, string id, T value)
    {
        using var c = connection.CreateCommand();
        c.CommandText = "INSERT INTO documents(kind,id,json) VALUES($kind,$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=$json";
        c.Parameters.AddWithValue("$kind", kind); c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$json", Wire.Serialize(value)); c.ExecuteNonQuery();
    }
    public T? Get<T>(string kind, string id)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT json FROM documents WHERE kind=$kind AND id=$id";
        c.Parameters.AddWithValue("$kind", kind); c.Parameters.AddWithValue("$id", id);
        return c.ExecuteScalar() is string json ? Wire.Read<T>(json) : default;
    }
    public List<T> All<T>(string kind)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT json FROM documents WHERE kind=$kind ORDER BY rowid DESC";
        c.Parameters.AddWithValue("$kind", kind); using var reader = c.ExecuteReader(); var rows = new List<T>();
        while (reader.Read()) rows.Add(Wire.Read<T>(reader.GetString(0))); return rows;
    }
    public void SetCredential(string deviceId, string thumbprint)
    {
        using var c = connection.CreateCommand(); c.CommandText = "INSERT INTO credentials VALUES($id,$thumb)";
        c.Parameters.AddWithValue("$id", deviceId); c.Parameters.AddWithValue("$thumb", thumbprint); c.ExecuteNonQuery();
    }
    public List<FileBundle> FileCatalog()
    {
        using var c=connection.CreateCommand();
        c.CommandText="SELECT json_set(json_remove(json,'$.entries'),'$.entries',json('[]'),'$.entryCount',json_array_length(json,'$.entries')) FROM documents WHERE kind='filebundle' ORDER BY rowid DESC";
        using var reader=c.ExecuteReader();var rows=new List<FileBundle>();
        while(reader.Read())rows.Add(Wire.Read<FileBundle>(reader.GetString(0)));return rows;
    }
    public string? Identify(string thumbprint)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT device_id FROM credentials WHERE thumbprint=$thumb";
        c.Parameters.AddWithValue("$thumb", thumbprint); return c.ExecuteScalar() as string;
    }
    public List<AuditEvent> Audit()
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT json FROM audit ORDER BY id";
        using var reader = c.ExecuteReader(); var rows = new List<AuditEvent>();
        while (reader.Read()) rows.Add(Wire.Read<AuditEvent>(reader.GetString(0))); return rows;
    }
    public void Log(string actor, string action, string details)
    {
        using var last = connection.CreateCommand(); last.CommandText = "SELECT json FROM audit ORDER BY id DESC LIMIT 1";
        var previous = last.ExecuteScalar() is string json ? Wire.Read<AuditEvent>(json) : null;
        var item = new AuditEvent((previous?.Id ?? 0) + 1, DateTimeOffset.UtcNow, actor, action, details, previous?.EventHash ?? "GENESIS", "");
        item = item with { EventHash = Wire.Hash(Wire.Serialize(item)) };
        using var c = connection.CreateCommand(); c.CommandText = "INSERT INTO audit(json) VALUES($json)";
        c.Parameters.AddWithValue("$json", Wire.Serialize(item)); c.ExecuteNonQuery();
    }
    public void Backup(string path)
    {
        using var dest = new SqliteConnection($"Data Source={path};Pooling=False"); dest.Open(); connection.BackupDatabase(dest);
    }
    public ImageChunk? GetChunk(string transferId, string fileId, int sequence)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT offset_bytes,size_bytes,sha256 FROM transfer_chunks WHERE transfer_id=$t AND file_id=$f AND sequence=$s";
        c.Parameters.AddWithValue("$t", transferId); c.Parameters.AddWithValue("$f", fileId); c.Parameters.AddWithValue("$s", sequence);
        using var r = c.ExecuteReader(); return r.Read() ? new(sequence, r.GetInt64(0), r.GetInt32(1), r.GetString(2)) : null;
    }
    public int NextChunk(string transferId, string fileId)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT sequence FROM transfer_chunks WHERE transfer_id=$t AND file_id=$f ORDER BY sequence DESC LIMIT 1";
        c.Parameters.AddWithValue("$t", transferId); c.Parameters.AddWithValue("$f", fileId);
        return c.ExecuteScalar() is long last ? checked((int)last + 1) : 0;
    }
    public void AddChunk(string transferId, string fileId, ImageChunk chunk)
    {
        using var c = connection.CreateCommand(); c.CommandText = "INSERT INTO transfer_chunks VALUES($t,$f,$s,$o,$n,$h)";
        c.Parameters.AddWithValue("$t", transferId); c.Parameters.AddWithValue("$f", fileId); c.Parameters.AddWithValue("$s", chunk.Sequence);
        c.Parameters.AddWithValue("$o", chunk.OffsetBytes); c.Parameters.AddWithValue("$n", chunk.SizeBytes); c.Parameters.AddWithValue("$h", chunk.Sha256); c.ExecuteNonQuery();
    }
    public List<ImageChunk> Chunks(string transferId, string fileId)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT sequence,offset_bytes,size_bytes,sha256 FROM transfer_chunks WHERE transfer_id=$t AND file_id=$f ORDER BY sequence";
        c.Parameters.AddWithValue("$t", transferId); c.Parameters.AddWithValue("$f", fileId); using var r = c.ExecuteReader(); var chunks = new List<ImageChunk>();
        while(r.Read()) chunks.Add(new(r.GetInt32(0),r.GetInt64(1),r.GetInt32(2),r.GetString(3))); return chunks;
    }
    public void ImportChunks(string transferId,string fileId,IEnumerable<ImageChunk> chunks)
    {
        using var transaction=connection.BeginTransaction();
        foreach(var chunk in chunks){ using var c=connection.CreateCommand();c.Transaction=transaction;
            c.CommandText="INSERT OR IGNORE INTO transfer_chunks VALUES($t,$f,$s,$o,$n,$h)";
            c.Parameters.AddWithValue("$t",transferId);c.Parameters.AddWithValue("$f",fileId);c.Parameters.AddWithValue("$s",chunk.Sequence);
            c.Parameters.AddWithValue("$o",chunk.OffsetBytes);c.Parameters.AddWithValue("$n",chunk.SizeBytes);c.Parameters.AddWithValue("$h",chunk.Sha256);c.ExecuteNonQuery(); }
        transaction.Commit();
    }
    public void Dispose() => connection.Dispose();
}
