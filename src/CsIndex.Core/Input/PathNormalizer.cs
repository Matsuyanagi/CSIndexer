namespace CsIndex.Core.Input;

public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return NormalizeAbsolute(path);
    }

    public static string NormalizeForComparison(string path) =>
        Normalize(path).ToUpperInvariant();

    internal static string NormalizeAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalInput = NormalizeDevicePrefix(path);
        var fullPath = Path.GetFullPath(canonicalInput)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return TrimTrailingSeparators(fullPath);
    }

    internal static string NormalizeRelative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsRooted(path))
        {
            throw new InputResolutionException($"Stored path must be relative: {path}");
        }

        var components = path.Replace('\\', '/').Split('/', StringSplitOptions.None);
        var normalized = new List<string>(components.Length);
        foreach (var component in components)
        {
            if (component.Length == 0 || component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (normalized.Count > 0 && normalized[^1] != "..")
                {
                    normalized.RemoveAt(normalized.Count - 1);
                }
                else
                {
                    normalized.Add(component);
                }

                continue;
            }

            if (component.Contains(':', StringComparison.Ordinal))
            {
                throw new InputResolutionException($"Stored path contains an invalid rooted component: {path}");
            }

            normalized.Add(component);
        }

        return normalized.Count == 0 ? "." : string.Join('/', normalized);
    }

    internal static string ToForwardSlashes(string path) => path.Replace('\\', '/');

    internal static bool IsRooted(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var candidate = path.Replace('/', '\\');
        return Path.IsPathRooted(candidate) ||
               candidate.StartsWith("\\\\", StringComparison.Ordinal) ||
               candidate.Length >= 2 && candidate[1] == ':' && IsDriveLetter(candidate[0]);
    }

    internal static bool IsFullyQualifiedAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathFullyQualified(NormalizeDevicePrefix(path));
    }

    internal static void EnsureSameVolumeShare(string firstPath, string secondPath)
    {
        var first = NormalizeAbsolute(firstPath);
        var second = NormalizeAbsolute(secondPath);
        if (GetRootIdentity(first).Equals(GetRootIdentity(second), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InputResolutionException(
            $"Paths '{first}' and '{second}' must satisfy the same-volume/share requirement.");
    }

    internal static bool SameVolumeShare(string firstPath, string secondPath) =>
        GetRootIdentity(NormalizeAbsolute(firstPath))
            .Equals(GetRootIdentity(NormalizeAbsolute(secondPath)), StringComparison.OrdinalIgnoreCase);

    internal static string RelativePath(string basePath, string targetPath)
    {
        var baseAbsolute = NormalizeAbsolute(basePath);
        var targetAbsolute = NormalizeAbsolute(targetPath);
        EnsureSameVolumeShare(baseAbsolute, targetAbsolute);
        return NormalizeRelative(ToForwardSlashes(Path.GetRelativePath(baseAbsolute, targetAbsolute)));
    }

    private static string NormalizeDevicePrefix(string path)
    {
        var candidate = path.Replace('/', '\\');
        if (!candidate.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        var tail = candidate[4..];
        if (tail.Length >= 3 && IsDriveLetter(tail[0]) && tail[1] == ':' && IsSeparator(tail[2]))
        {
            return tail;
        }

        if (tail.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            return "\\\\" + tail[4..];
        }

        throw new InputResolutionException($"Unsupported Windows device namespace: {path}");
    }

    private static string GetRootIdentity(string normalizedPath)
    {
        if (normalizedPath.Length >= 3 && IsDriveLetter(normalizedPath[0]) &&
            normalizedPath[1] == ':' && IsSeparator(normalizedPath[2]))
        {
            return $"drive:{char.ToUpperInvariant(normalizedPath[0])}";
        }

        if (normalizedPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var components = normalizedPath[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length >= 2)
            {
                return $"unc:{components[0]}/{components[1]}";
            }
        }

        throw new InputResolutionException($"Unsupported or non-rooted Windows path: {normalizedPath}");
    }

    private static string TrimTrailingSeparators(string path)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            return normalized + Path.DirectorySeparatorChar;
        }

        return normalized.Length == 0 && path.Length > 0 ? path[..1] : normalized;
    }

    private static bool IsDriveLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsSeparator(char value) => value is '\\' or '/';

    public static bool HasPathSegment(string rootPath, string filePath, string segment)
    {
        var relativePath = Path.GetRelativePath(rootPath, filePath);
        return relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
    }
}
