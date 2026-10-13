namespace EastwardLib.Utils;

public static class SafePath
{
    /// <summary>Create a file's parent directory and return its absolute path.</summary>
    public static string PrepareFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetFileName(path) is "" or "." or "..")
        {
            throw new ArgumentException("An output file path is required", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (Path.EndsInDirectorySeparator(fullPath) || string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("An output file path is required", nameof(path));
        }

        Directory.CreateDirectory(directory);
        return fullPath;
    }

    public static string ResolveWithin(string directory, string relativePath)
    {
        var name = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':') ||
            name.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe resource path: {relativePath}");
        }

        var root = Path.GetFullPath(directory);
        var target = Path.GetFullPath(Path.Combine(root, name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidDataException($"Resource escapes output directory: {relativePath}");
        }

        return target;
    }
}
