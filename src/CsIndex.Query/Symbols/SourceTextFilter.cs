namespace CsIndex.Query.Symbols;

public static class SourceTextFilter
{
    public static bool IsMatch(
        string? source,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes,
        StringComparison comparison,
        Func<string, string, StringComparison, bool>? contains = null)
    {
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(excludes);
        if (source is null)
        {
            return false;
        }

        contains ??= static (text, term, stringComparison) => text.Contains(term, stringComparison);
        if (excludes.Any(exclude => contains(source, exclude, comparison)))
        {
            return false;
        }

        return includes.All(include => contains(source, include, comparison));
    }
}
