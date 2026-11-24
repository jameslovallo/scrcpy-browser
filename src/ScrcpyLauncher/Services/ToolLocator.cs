namespace ScrcpyLauncher.Services;

internal static class ToolLocator
{
    /// <summary>Resolves an executable from an absolute path, the PATH, or the given fallback directories.</summary>
    public static string? Find(string nameOrPath, params string?[] fallbackDirectories)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;

        if (Path.IsPathRooted(nameOrPath))
        {
            return File.Exists(nameOrPath) ? nameOrPath : null;
        }

        var fileName = Path.HasExtension(nameOrPath) ? nameOrPath : nameOrPath + ".exe";
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in pathDirs.Concat(fallbackDirectories.OfType<string>()))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
