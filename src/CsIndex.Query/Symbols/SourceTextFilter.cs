using System.Text.RegularExpressions;

namespace CsIndex.Query.Symbols;

public static class SourceTextFilter
{
    private static readonly TimeSpan ProductionRegexTimeout = TimeSpan.FromSeconds(2);

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

    public static bool IsMatch(
        string? source,
        IReadOnlyList<TypedCondition> includes,
        IReadOnlyList<TypedCondition> excludes,
        CaseMode caseMode,
        CancellationToken cancellationToken,
        TimeSpan? regexTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(excludes);
        var predicates = new SourcePredicateOptions(
            caseMode,
            regexTimeout ?? ProductionRegexTimeout);
        var includePredicates = includes
            .Select(condition => CompileCondition(condition, predicates))
            .ToArray();
        var excludePredicates = excludes
            .Select(condition => CompileCondition(condition, predicates))
            .ToArray();
        return IsMatch(source, includePredicates, excludePredicates, cancellationToken);
    }

    public static bool IsMatch(
        string? source,
        IReadOnlyList<TypedCondition> includes,
        IReadOnlyList<TypedCondition> excludes,
        StringComparison comparison,
        CancellationToken cancellationToken,
        TimeSpan? regexTimeout = null)
    {
        var caseMode = comparison switch
        {
            StringComparison.Ordinal => CaseMode.Strict,
            StringComparison.OrdinalIgnoreCase => CaseMode.Ignore,
            _ => throw new ArgumentOutOfRangeException(
                nameof(comparison),
                comparison,
                "Only Ordinal and OrdinalIgnoreCase comparisons are supported."),
        };
        return IsMatch(source, includes, excludes, caseMode, cancellationToken, regexTimeout);
    }

    internal static bool IsMatch(
        string? source,
        IReadOnlyList<CompiledSourcePredicate> includes,
        IReadOnlyList<CompiledSourcePredicate> excludes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(excludes);
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null)
        {
            return false;
        }

        foreach (var exclude in excludes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isExcluded = exclude.Matches(source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (isExcluded)
            {
                return false;
            }
        }

        foreach (var include in includes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isIncluded = include.Matches(source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isIncluded)
            {
                return false;
            }
        }

        return true;
    }

    internal static CompiledSourcePredicate CompileCondition(
        TypedCondition condition,
        CaseMode caseMode,
        TimeSpan regexTimeout)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ValidateCaseMode(caseMode);
        ArgumentNullException.ThrowIfNull(condition.Value);
        var comparison = ToComparison(caseMode);
        return condition.Syntax switch
        {
            ConditionSyntax.Literal => new CompiledSourcePredicate(
                (source, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var matched = source.IndexOf(condition.Value, comparison) >= 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    return matched;
                }),
            ConditionSyntax.Glob => new CompiledSourcePredicate(
                (source, cancellationToken) => StructuralGlobMatcher.MatchSource(
                    condition.Value,
                    source,
                    comparison,
                    cancellationToken)),
            ConditionSyntax.Regex => new CompiledSourcePredicate(
                CreateRegexPredicate(condition.Value, comparison, regexTimeout)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(condition),
                condition.Syntax,
                "Undefined condition syntax."),
        };
    }

    internal sealed class CompiledSourcePredicate(Func<string, CancellationToken, bool> predicate)
    {
        private readonly Func<string, CancellationToken, bool> _predicate = predicate;

        public bool Matches(string source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _predicate(source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private static Func<string, CancellationToken, bool> CreateRegexPredicate(
        string expression,
        StringComparison comparison,
        TimeSpan timeout)
    {
        var options = RegexOptions.CultureInvariant;
        if (comparison == StringComparison.OrdinalIgnoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        Regex regex;
        try
        {
            regex = new Regex(expression, options, timeout);
        }
        catch (ArgumentException exception)
        {
            throw new SymbolQueryParseException($"Invalid source regular expression: {exception.Message}");
        }

        return (source, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var matched = regex.IsMatch(source);
                cancellationToken.ThrowIfCancellationRequested();
                return matched;
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new SymbolQueryParseException(
                    $"Regular expression for source condition timed out: {exception.Message}");
            }
        };
    }

    private readonly record struct SourcePredicateOptions(CaseMode CaseMode, TimeSpan RegexTimeout);

    private static CompiledSourcePredicate CompileCondition(
        TypedCondition condition,
        SourcePredicateOptions options) =>
        CompileCondition(condition, options.CaseMode, options.RegexTimeout);

    private static StringComparison ToComparison(CaseMode caseMode) =>
        caseMode switch
        {
            CaseMode.Strict => StringComparison.Ordinal,
            CaseMode.Ignore => StringComparison.OrdinalIgnoreCase,
            _ => throw new ArgumentOutOfRangeException(nameof(caseMode), caseMode, "Undefined case mode."),
        };

    private static void ValidateCaseMode(CaseMode caseMode)
    {
        if (caseMode is not CaseMode.Strict and not CaseMode.Ignore)
        {
            throw new ArgumentOutOfRangeException(nameof(caseMode), caseMode, "Undefined case mode.");
        }
    }
}
