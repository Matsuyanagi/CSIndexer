namespace CsIndex.Query.Symbols;

/// <summary>
/// Matches the small, deliberately structural glob language used by symbol
/// paths.  It is kept separate from the input-file globber because the two
/// languages have different case and recursive-star rules.
/// </summary>
internal static class StructuralGlobMatcher
{
    internal static bool MatchComponent(
        string pattern,
        string candidate,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateComparison(comparison);
        return MatchCharacters(
            pattern,
            candidate,
            comparison,
            unanchored: false,
            cancellationToken);
    }

    internal static bool MatchHierarchy(
        IReadOnlyList<string> pattern,
        IReadOnlyList<string> candidate,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateComparison(comparison);
        return MatchSequence(
            pattern,
            candidate,
            static value => value == "**",
            static (value, other, state) => MatchComponent(value, other, state.Comparison, state.CancellationToken),
            comparison,
            cancellationToken);
    }

    internal static bool MatchFile(
        string pattern,
        string candidate,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(candidate);
        var normalizedPattern = pattern.Replace('\\', '/');
        return MatchHierarchy(
            SplitPath(normalizedPattern),
            SplitPath(candidate),
            comparison,
            cancellationToken);
    }

    internal static bool MatchSource(
        string pattern,
        string source,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(source);
        ValidateComparison(comparison);
        return MatchCharacters(
            pattern,
            source,
            comparison,
            unanchored: true,
            cancellationToken);
    }

    internal static bool MatchSequence<TPattern, TCandidate, TState>(
        IReadOnlyList<TPattern> pattern,
        IReadOnlyList<TCandidate> candidate,
        Func<TPattern, bool> isRecursiveStar,
        Func<TPattern, TCandidate, TState, bool> matchesComponent,
        TState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(isRecursiveStar);
        ArgumentNullException.ThrowIfNull(matchesComponent);

        var rows = pattern.Count + 1;
        var columns = candidate.Count + 1;
        var matches = new bool[rows, columns];
        matches[0, 0] = true;
        for (var patternIndex = 1; patternIndex < rows; patternIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isRecursiveStar(pattern[patternIndex - 1]))
            {
                matches[patternIndex, 0] = matches[patternIndex - 1, 0];
            }
        }

        for (var patternIndex = 1; patternIndex < rows; patternIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patternPart = pattern[patternIndex - 1];
            for (var candidateIndex = 1; candidateIndex < columns; candidateIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches[patternIndex, candidateIndex] = isRecursiveStar(patternPart)
                    ? matches[patternIndex - 1, candidateIndex] || matches[patternIndex, candidateIndex - 1]
                    : matches[patternIndex - 1, candidateIndex - 1] &&
                      matchesComponent(patternPart, candidate[candidateIndex - 1], state);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return matches[pattern.Count, candidate.Count];
    }

    private static IReadOnlyList<string> SplitPath(string path) =>
        path.Length == 0
            ? []
            : path.Split('/', StringSplitOptions.None);

    private static bool MatchCharacters(
        string pattern,
        string candidate,
        StringComparison comparison,
        bool unanchored,
        CancellationToken cancellationToken)
    {
        var patternIndex = 0;
        var candidateIndex = 0;
        var starResumePatternIndex = unanchored ? 0 : -1;
        var starCandidateIndex = unanchored ? 0 : -1;
        cancellationToken.ThrowIfCancellationRequested();
        while (candidateIndex < candidate.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unanchored && patternIndex == pattern.Length)
            {
                return true;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    patternIndex++;
                }
                while (patternIndex < pattern.Length && pattern[patternIndex] == '*');

                starResumePatternIndex = patternIndex;
                starCandidateIndex = candidateIndex;
                if (patternIndex == pattern.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return true;
                }

                continue;
            }

            if (patternIndex < pattern.Length &&
                CharEquals(pattern, patternIndex, candidate, candidateIndex, comparison))
            {
                patternIndex++;
                candidateIndex++;
                continue;
            }

            if (starResumePatternIndex >= 0 && starCandidateIndex < candidate.Length)
            {
                patternIndex = starResumePatternIndex;
                candidateIndex = ++starCandidateIndex;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            cancellationToken.ThrowIfCancellationRequested();
            patternIndex++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return patternIndex == pattern.Length;
    }

    private static bool CharEquals(
        string left,
        int leftIndex,
        string right,
        int rightIndex,
        StringComparison comparison) =>
        comparison == StringComparison.Ordinal
            ? left[leftIndex] == right[rightIndex]
            : left.AsSpan(leftIndex, 1).Equals(right.AsSpan(rightIndex, 1), comparison);

    private static void ValidateComparison(StringComparison comparison)
    {
        if (comparison is not StringComparison.Ordinal and not StringComparison.OrdinalIgnoreCase)
        {
            throw new ArgumentOutOfRangeException(
                nameof(comparison),
                comparison,
                "Only Ordinal and OrdinalIgnoreCase comparisons are supported.");
        }
    }

    private readonly record struct MatchState(
        StringComparison Comparison,
        CancellationToken CancellationToken);

    private static bool MatchSequence(
        IReadOnlyList<string> pattern,
        IReadOnlyList<string> candidate,
        Func<string, bool> isRecursiveStar,
        Func<string, string, MatchState, bool> matchesComponent,
        StringComparison comparison,
        CancellationToken cancellationToken) =>
        MatchSequence(
            pattern,
            candidate,
            isRecursiveStar,
            matchesComponent,
            new MatchState(comparison, cancellationToken),
            cancellationToken);
}
