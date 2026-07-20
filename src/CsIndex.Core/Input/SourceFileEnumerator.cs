namespace CsIndex.Core.Input;

public sealed record SourceFileEnumeration(
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyList<string> ExcludedFiles);

public sealed class SourceFileEnumerator
{
    public SourceFileEnumeration Enumerate(string rootPath, IEnumerable<string> excludes)
    {
        var root = PathNormalizer.Normalize(rootPath);
        var matcher = new GlobMatcher(excludes);
        var included = new List<string>();
        var excluded = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = PathNormalizer.Normalize(filePath);
            var relative = Path.GetRelativePath(root, normalized);
            if (PathNormalizer.HasPathSegment(root, normalized, "obj") || matcher.IsMatch(relative))
            {
                excluded.Add(normalized);
            }
            else
            {
                included.Add(normalized);
            }
        }

        included.Sort(StringComparer.OrdinalIgnoreCase);
        excluded.Sort(StringComparer.OrdinalIgnoreCase);
        return new SourceFileEnumeration(included, excluded);
    }
}
