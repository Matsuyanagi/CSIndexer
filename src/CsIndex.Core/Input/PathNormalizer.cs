namespace CsIndex.Core.Input;

public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    public static string NormalizeForComparison(string path) =>
        Normalize(path).ToUpperInvariant();

    public static bool HasPathSegment(string rootPath, string filePath, string segment)
    {
        var relativePath = Path.GetRelativePath(rootPath, filePath);
        return relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
    }
}
