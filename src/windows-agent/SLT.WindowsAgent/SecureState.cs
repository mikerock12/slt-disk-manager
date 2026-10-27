using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SLT.WindowsAgent;

public sealed class SecureState
{
    public string DirectoryPath { get; }
    public SecureState(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(DirectoryPath);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Diretório de credenciais não pode ser link/reparse point.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Usuário Windows desconhecido.");
            foreach (var sid in new[] { current, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string FilePath(string name)
    {
        if (name != Path.GetFileName(name)) throw new ArgumentException("Nome de estado inválido.");
        return Path.Combine(DirectoryPath, name);
    }
    public T? Read<T>(string name) => File.Exists(FilePath(name)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath(name)), Wire.Json) : default;
    public void Write<T>(string name, T value) => WriteText(name, JsonSerializer.Serialize(value, Wire.Json));
    public void WriteText(string name, string value)
    {
        var destination = FilePath(name);
        var temporary = FilePath(name + ".tmp-" + Guid.NewGuid().ToString("N"));
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(value);
            writer.Flush();
            stream.Flush(true);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, destination, true);
    }

    // Persist BEFORE executing. A previously seen job can never execute automatically again.
    public bool TryClaimJob(string jobId)
    {
        if (!Guid.TryParse(jobId, out var id)) throw new InvalidOperationException("Job deve ter UUID válido.");
        try
        {
            using var file = new FileStream(FilePath("job-" + id.ToString("N") + ".claimed"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            file.WriteByte(1);
            file.Flush(true);
            return true;
        }
        catch (IOException) when (File.Exists(FilePath("job-" + id.ToString("N") + ".claimed"))) { return false; }
    }
}
