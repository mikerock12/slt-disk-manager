using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SLT.WindowsAgent;

public sealed record SecuredInfo(bool IsDirectory, bool IsLink, long Length, DateTimeOffset LastWriteTimeUtc, uint Links);

// Every ancestor is held without share-write/share-delete. All leaf checks inspect
// OPEN_REPARSE_POINT handles rather than dereferencing a potentially replaced path.
public sealed class SecuredPath : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    public string AbsolutePath { get; private set; } = "";
    public SafeFileHandle Handle => handles[^1];
    public SecuredInfo Info => NativeFiles.Info(Handle);
    private SecuredPath() { }

    public static SecuredPath Open(FileRootConfiguration root, string relative, bool readFile = false, bool allowLinkLeaf = false)
    {
        FileSafety.ValidateRelative(relative, true);
        var absolute = Path.GetFullPath(Path.Combine(root.Path, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!FileSafety.Under(absolute, root.Path)) throw new UnauthorizedAccessException("Caminho fora do escopo.");
        return OpenAbsolute(absolute, readFile, allowLinkLeaf);
    }
    public static SecuredPath OpenAbsolute(string absolute, bool readFile = false, bool allowLinkLeaf = false, bool deleteLeaf = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Escopos de arquivos deste agente exigem Windows.");
        absolute = Path.GetFullPath(absolute);
        if (!Path.IsPathFullyQualified(absolute) || absolute.StartsWith("\\", StringComparison.Ordinal))
            throw new InvalidDataException("Somente caminhos locais com letra de unidade.");
        var lease = new SecuredPath { AbsolutePath = absolute };
        try
        {
            var volume = Path.GetPathRoot(absolute) ?? throw new InvalidDataException("Volume inválido.");
            var components = absolute[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = volume;
            lease.Add(current, components.Length == 0 && readFile, components.Length == 0 && allowLinkLeaf, components.Length != 0, components.Length == 0 && deleteLeaf);
            for (var i = 0; i < components.Length; i++)
            {
                current = Path.Combine(current, components[i]);
                var leaf = i == components.Length - 1;
                lease.Add(current, leaf && readFile, leaf && allowLinkLeaf, !leaf, leaf && deleteLeaf);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    private void Add(string path, bool readFile, bool allowLink, bool requireDirectory, bool deleteAccess)
    {
        var handle = NativeFiles.Open(path, readFile, deleteAccess);
        handles.Add(handle);
        var info = NativeFiles.Info(handle);
        if (info.IsLink && !allowLink) throw new UnauthorizedAccessException("Reparse point/junction/symlink bloqueado.");
        if (requireDirectory && !info.IsDirectory) throw new IOException("Ancestral de caminho não é diretório.");
        if (!info.IsDirectory && !allowLink && info.Links > 1) throw new UnauthorizedAccessException("Arquivo com hard links bloqueado por isolamento de escopo.");
        var final = NativeFiles.FinalPath(handle);
        if (!Path.TrimEndingDirectorySeparator(final).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Resolução física mudou após validar o caminho.");
    }
    public FileStream ReadStream()
    {
        if (Info.IsDirectory || Info.IsLink) throw new IOException("Origem não é arquivo regular.");
        return new FileStream(Handle, FileAccess.Read, 65536, false);
    }
    public void PromoteDirectory(string target)
    {
        if (!Info.IsDirectory || Info.IsLink || !Path.GetDirectoryName(target)!.Equals(Path.GetDirectoryName(AbsolutePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Promoção exige pasta regular no mesmo diretório/volume.");
        NativeFiles.RenameWithoutReplace(Handle, target);
        AbsolutePath = target;
    }
    public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
}

internal static class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public FILETIME CreationTime;
        public FILETIME AccessTime;
        public FILETIME WriteTime;
        public uint VolumeSerial;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint IndexHigh;
        public uint IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectory(string name, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

    public static SafeFileHandle Open(string path, bool readFile, bool deleteAccess = false)
    {
        var access = (readFile ? 0x80000000u : 0x80u) | (deleteAccess ? 0x10000u : 0u);
        var handle = CreateFile(path, access, 1, IntPtr.Zero, 3,
            0x02000000u | 0x00200000u, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT; SHARE_READ only.
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Não foi possível fixar o caminho local com isolamento de gravação/renomeação."); }
        return handle;
    }
    public static SecuredInfo Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var timestamp = ((long)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime;
        return new((info.Attributes & 0x10) != 0, (info.Attributes & 0x400) != 0,
            checked(((long)info.SizeHigh << 32) | info.SizeLow), DateTime.FromFileTimeUtc(timestamp), info.NumberOfLinks);
    }
    public static string FinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
        if (length == 0 || length >= builder.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "Caminho físico indisponível.");
        var path = builder.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Volumes de rede bloqueados.");
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
    public static void CreatePrivateStaging(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            if (!CreateDirectory(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Staging deve ser uma pasta nova com ACL privada.");
        }
        finally { pinned.Free(); }
    }
    public static void AssertPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var current = WindowsIdentity.GetCurrent().User!;
        if (!current.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("Staging não pertence à identidade privada do agente.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { current.Value, "S-1-5-18", "S-1-5-32-544" };
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("ACL do staging não é privada.");
    }
    public static void RenameWithoutReplace(SafeFileHandle handle, string target)
    {
        // FILE_RENAME_INFO: BOOL, pointer-aligned HANDLE, DWORD byte length, UTF-16 filename.
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var name = Encoding.Unicode.GetBytes(target);
        var length = nameOffset + name.Length + 2; // Explicit UTF-16 terminator; FileNameLength excludes it.
        var memory = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(new byte[length], 0, memory, length);
            Marshal.WriteInt32(memory, 0, 0); // ReplaceIfExists=false.
            Marshal.WriteIntPtr(memory, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(memory, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(memory, nameOffset), name.Length);
            for (var attempt = 0; ; attempt++)
            {
                if (SetFileInformationByHandle(handle, 3, memory, (uint)length)) break;
                var error = Marshal.GetLastWin32Error();
                if (error is 32 or 33 && attempt < 3) { Thread.Sleep(50 << attempt); continue; }
                throw new Win32Exception(error, $"Promoção atômica falhou (Win32 {error}); nenhum destino existente foi substituído.");
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public static void InheritDestinationPermissions(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(false, false);
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
