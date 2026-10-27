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
            "CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT,json TEXT NOT NULL); PRAGMA user_version=1;");
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
    public void Dispose() => connection.Dispose();
}
