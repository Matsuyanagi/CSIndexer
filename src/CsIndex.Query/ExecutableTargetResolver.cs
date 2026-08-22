using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

public readonly record struct FunctionTargetFilter(
    IndexedSymbolKind? Kind,
    AsyncStatusFilter AsyncStatus)
{
    public bool Matches(StoredSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (Kind is not null && symbol.Kind != Kind)
        {
            return false;
        }

        if (AsyncStatus is not (AsyncStatusFilter.Async or AsyncStatusFilter.Sync))
        {
            return true;
        }

        if (symbol.Kind is not (IndexedSymbolKind.Method or IndexedSymbolKind.Lambda))
        {
            return false;
        }

        return AsyncStatus == AsyncStatusFilter.Async
            ? symbol.AsyncRole != AsyncRole.None
            : symbol.AsyncRole == AsyncRole.None;
    }
}

internal sealed class ExecutableTargetResolver(QueryRepository repository)
{
    private const string LegacyLambdaChildDelimiter = "::<lambda#";
    private const string SemanticLambdaChildDelimiter = ".<lambda#";

    private readonly SymbolQueryParser _parser = new();
    private readonly MethodTargetResolver _methodTargetResolver = new(repository);

    internal static bool IsLambdaTargetQuery(string queryText) =>
        queryText?.Contains(LegacyLambdaChildDelimiter, StringComparison.Ordinal) == true;

    public async Task<IReadOnlyList<StoredSymbol>> ResolveAsync(
        long profileId,
        string queryText,
        bool sourceOnly,
        bool includeOverrides,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (includeOverrides && filter.Kind == IndexedSymbolKind.Lambda)
        {
            throw new SymbolQueryParseException("--kind lambda cannot be combined with --include-overrides.");
        }

        if (IsLambdaTargetQuery(queryText))
        {
            if (includeOverrides)
            {
                throw new SymbolQueryParseException("--include-overrides requires an exact method query.");
            }

            return await ResolveLambdasAsync(
                profileId,
                queryText,
                sourceOnly,
                filter,
                cancellationToken);
        }

        var query = _parser.Parse(queryText);
        if (!query.IsMethodQuery)
        {
            if (includeOverrides)
            {
                throw new SymbolQueryParseException("--include-overrides requires a method query.");
            }

            return [];
        }

        var targets = await _methodTargetResolver.ResolveAsync(
            profileId,
            query,
            includeOverrides,
            sourceOnly,
            cancellationToken);
        return FilterTargets(targets, sourceOnly, filter, cancellationToken);
    }

    private async Task<IReadOnlyList<StoredSymbol>> ResolveLambdasAsync(
        long profileId,
        string queryText,
        bool sourceOnly,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken)
    {
        var candidates = await repository.FindExecutableSymbolsAsync(
            profileId,
            sourceOnly,
            cancellationToken);
        // Task 6 replaces this legacy-input bridge with structured path selectors.
        // The leading wildcard preserves the legacy matcher's suffix semantics.
        var matcherPattern = "*" + queryText.Replace(
            LegacyLambdaChildDelimiter,
            SemanticLambdaChildDelimiter,
            StringComparison.Ordinal);
        var matcher = new SymbolPatternMatcher(new SymbolSearchRequest(
            Pattern: matcherPattern,
            NamespacePattern: null,
            TypePattern: null,
            MethodPattern: null,
            Kind: filter.Kind,
            UseRegex: false,
            IgnoreCase: false,
            Includes: [],
            Excludes: [],
            ShowSource: false,
            AsyncStatus: filter.AsyncStatus));
        var matches = new List<StoredSymbol>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Kind == IndexedSymbolKind.Lambda &&
                matcher.IsMatch(candidate) &&
                (!sourceOnly || IsSourceBackedExecutable(candidate)))
            {
                matches.Add(candidate);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return matches;
    }

    private static IReadOnlyList<StoredSymbol> FilterTargets(
        IEnumerable<StoredSymbol> targets,
        bool sourceOnly,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken)
    {
        var matches = new List<StoredSymbol>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.Matches(target) && (!sourceOnly || IsSourceBackedExecutable(target)))
            {
                matches.Add(target);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return matches;
    }

    private static bool IsSourceBackedExecutable(StoredSymbol symbol) =>
        (symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda) &&
        symbol.PreferredDeclarationId is not null &&
        symbol.PreferredDocumentPath is not null;
}
