namespace CsIndex.Query.Symbols;

public static class SourceTextFilter
{
    public static bool IsMatch(
        string? source,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes,
        StringComparison comparison,
        CancellationToken cancellationToken,
        Func<string, string, StringComparison, bool>? contains = null)
    {
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(excludes);
        cancellationToken.ThrowIfCancellationRequested();

        if (source is null)
        {
            return false;
        }

        contains ??= static (text, term, stringComparison) => text.Contains(term, stringComparison);
        foreach (var exclude in excludes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isExcluded = contains(source, exclude, comparison);
            cancellationToken.ThrowIfCancellationRequested();
            if (isExcluded)
            {
                return false;
            }
        }

        foreach (var include in includes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isIncluded = contains(source, include, comparison);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isIncluded)
            {
                return false;
            }
        }

        return true;
    }
}
