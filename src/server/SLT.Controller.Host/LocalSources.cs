namespace SLT.WindowsAgent;

// Controller and Windows client share the same native no-follow handle lease.
public record FileRootConfiguration(string Path);
public static class FileSafety
{
    public static void ValidateRelative(string path, bool allowEmpty = false) => Slt.Controller.FileRepository.Relative(path, allowEmpty);
    public static bool Under(string path, string root) => Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
