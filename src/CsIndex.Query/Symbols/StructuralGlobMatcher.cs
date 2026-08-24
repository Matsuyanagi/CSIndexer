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

        var previous = new bool[candidate.Length + 1];
        previous[0] = true;
        for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = pattern[patternIndex];
            var current = new bool[candidate.Length + 1];
            if (character == '*')
            {
                current[0] = previous[0];
                for (var candidateIndex = 1; candidateIndex <= candidate.Length; candidateIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current[candidateIndex] = current[candidateIndex - 1] || previous[candidateIndex];
                }
            }
            else
            {
                for (var candidateIndex = 1; candidateIndex <= candidate.Length; candidateIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current[candidateIndex] = previous[candidateIndex - 1] &&
                        CharEquals(character, candidate[candidateIndex - 1], comparison);
                }
            }

            previous = current;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return previous[candidate.Length];
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

        // The initial row is true for every source prefix, making the match
        // unanchored while retaining a character-level star implementation.
        var previous = new bool[source.Length + 1];
        Array.Fill(previous, true);
        for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = pattern[patternIndex];
            var current = new bool[source.Length + 1];
            if (character == '*')
            {
                current[0] = previous[0];
                for (var sourceIndex = 1; sourceIndex <= source.Length; sourceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current[sourceIndex] = current[sourceIndex - 1] || previous[sourceIndex];
                }
            }
            else
            {
                for (var sourceIndex = 1; sourceIndex <= source.Length; sourceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current[sourceIndex] = previous[sourceIndex - 1] &&
                        CharEquals(character, source[sourceIndex - 1], comparison);
                }
            }

            previous = current;
            if (!previous.Any(value => value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return previous.Any(value => value);
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

    private static bool CharEquals(char left, char right, StringComparison comparison) =>
        string.Equals(left.ToString(), right.ToString(), comparison);

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
