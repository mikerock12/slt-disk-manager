internal static class TestCleanup
{
    // Cleanup is diagnostic: a locked fixture must never turn the result into an
    // unhandled exception or mask a failed assertion. Preserve it for inspection.
    public static bool TryRemove(string directory, Action<string> warning, int attempts = 3)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        const string prefix = "slt-controller-tests-";
        var name = Path.GetFileName(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(target), temporaryRoot, comparison) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(name[prefix.Length..], out _))
        {
            warning("Limpeza recusada fora do diretório temporário exclusivo de testes: " + target);
            return false;
        }

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt + 1 >= Math.Max(1, attempts))
                {
                    warning($"Pasta temporária preservada porque ainda está em uso ou sem acesso: {target}. {error.Message}");
                    return false;
                }
                Thread.Sleep(100);
            }
        }
        return false;
    }
}
