using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

public sealed class SemanticQueryService(QueryRepository repository)
{
    private static readonly IReadOnlySet<ReferenceKind> CallKinds =
        new HashSet<ReferenceKind> { ReferenceKind.Invocation, ReferenceKind.ObjectCreation };

    private readonly SymbolQueryParser _parser = new();
    private readonly ExecutableTargetResolver _executableTargetResolver = new(repository);
    private readonly AsyncPathResolver _asyncPathResolver = new(repository);
    private readonly CallerTreeBuilder _callerTreeBuilder = new(repository);

    public Task<QueryContext> FindSymbolsAsync(
        string queryText,
        string? profileName = null,
        bool sourceOnly = false,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default) =>
        FindSymbolsAsync(
            queryText,
            filter: default,
            profileName,
            sourceOnly,
            includeOverrides,
            cancellationToken);

    public async Task<QueryContext> FindSymbolsAsync(
        string queryText,
        FunctionTargetFilter filter,
        string? profileName = null,
        bool sourceOnly = false,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        if (ExecutableTargetResolver.IsLambdaTargetQuery(queryText))
        {
            var lambdaTargets = await _executableTargetResolver.ResolveAsync(
                profile.Id,
                queryText,
                sourceOnly,
                includeOverrides,
                filter,
                cancellationToken);
            return new QueryContext(profile, lambdaTargets);
        }

        var query = _parser.Parse(queryText);
        if (includeOverrides && !query.IsMethodQuery)
        {
            throw new SymbolQueryParseException("--include-overrides requires a method query.");
        }

        if (query.IsMethodQuery)
        {
            var methodTargets = await _executableTargetResolver.ResolveAsync(
                profile.Id,
                queryText,
                sourceOnly,
                includeOverrides,
                filter,
                cancellationToken);
            return new QueryContext(profile, methodTargets);
        }

        var candidates = await repository.FindSymbolCandidatesAsync(
            profile.Id,
            query.MethodName,
            query.TypeSimpleName,
            IndexedSymbolKind.Type,
            sourceOnly,
            cancellationToken);
        var matches = new List<StoredSymbol>(candidates.Count);
        foreach (var symbol in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isMatch = SymbolMatcher.IsMatch(query, symbol);
            cancellationToken.ThrowIfCancellationRequested();
            if (isMatch && filter.Matches(symbol))
            {
                matches.Add(symbol);
            }
        }

        return new QueryContext(profile, matches);
    }

    public async Task<QueryContext> SearchSymbolsAsync(
        SymbolSearchRequest request,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSearchRequest(request);
        if (CanUseExactSearch(request))
        {
            var exact = await FindSymbolsAsync(
                request.Pattern!,
                new FunctionTargetFilter(request.Kind, request.AsyncStatus),
                profileName,
                sourceOnly: false,
                includeOverrides: false,
                cancellationToken);
            return exact with { ShowSource = request.ShowSource };
        }

        return await SearchStoredExecutableSymbolsAsync(
            request,
            profileName,
            sourceOnly: false,
            cancellationToken);
    }

    public Task<QueryContext> ShowSourceAsync(
        string queryText,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        ShowSourceAsync(queryText, filter: default, profileName, cancellationToken);

    public async Task<QueryContext> ShowSourceAsync(
        string queryText,
        FunctionTargetFilter filter,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new SymbolSearchRequest(
            queryText,
            NamespacePattern: null,
            TypePattern: null,
            MethodPattern: null,
            Kind: filter.Kind,
            UseRegex: false,
            IgnoreCase: false,
            Includes: [],
            Excludes: [],
            ShowSource: true,
            AsyncStatus: filter.AsyncStatus);
        if (CanUseExecutableTargetResolver(queryText))
        {
            var exact = await FindSymbolsAsync(
                queryText,
                filter,
                profileName,
                sourceOnly: true,
                includeOverrides: false,
                cancellationToken);
            return exact with { ShowSource = true };
        }

        return await SearchStoredExecutableSymbolsAsync(
            request,
            profileName,
            sourceOnly: true,
            cancellationToken);
    }

    public Task<QueryContext> SearchSourceAsync(
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes,
        bool ignoreCase,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        SearchSourceAsync(
            includes,
            excludes,
            ignoreCase,
            filter: default,
            profileName,
            cancellationToken);

    public Task<QueryContext> SearchSourceAsync(
        IReadOnlyList<string> includes,
        IReadOnlyList<string> excludes,
        bool ignoreCase,
        FunctionTargetFilter filter,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(includes);
        ArgumentNullException.ThrowIfNull(excludes);
        if (includes.Count == 0 && excludes.Count == 0)
        {
            throw new SymbolQueryParseException(
                "source search requires at least one include or exclude condition.");
        }

        return SearchStoredExecutableSymbolsAsync(
            new SymbolSearchRequest(
                Pattern: null,
                NamespacePattern: null,
                TypePattern: null,
                MethodPattern: null,
                Kind: filter.Kind,
                UseRegex: false,
                IgnoreCase: ignoreCase,
                Includes: includes,
                Excludes: excludes,
                ShowSource: true,
                AsyncStatus: filter.AsyncStatus),
            profileName,
            sourceOnly: true,
            cancellationToken);
    }

    public Task<AsyncPathResult> FindAsyncPathAsync(
        string queryText,
        int maxNodes = 500,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        FindAsyncPathAsync(queryText, filter: default, maxNodes, profileName, cancellationToken);

    public async Task<AsyncPathResult> FindAsyncPathAsync(
        string queryText,
        FunctionTargetFilter filter,
        int maxNodes = 500,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMaxNodes(maxNodes);
        var (profile, root) = await ResolveSingleSourceExecutableAsync(
            queryText,
            profileName,
            filter,
            cancellationToken);
        return await _asyncPathResolver.ResolveAsync(profile, root, maxNodes, cancellationToken);
    }

    public Task<CallerTreeResult> FindCallerTreeAsync(
        string queryText,
        int depth = 3,
        int maxNodes = 500,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        FindCallerTreeAsync(queryText, filter: default, depth, maxNodes, profileName, cancellationToken);

    public async Task<CallerTreeResult> FindCallerTreeAsync(
        string queryText,
        FunctionTargetFilter filter,
        int depth = 3,
        int maxNodes = 500,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDepth(depth);
        ValidateMaxNodes(maxNodes);
        var (profile, root) = await ResolveSingleSourceExecutableAsync(
            queryText,
            profileName,
            filter,
            cancellationToken);
        return await _callerTreeBuilder.BuildAsync(
            profile,
            root,
            depth,
            maxNodes,
            cancellationToken);
    }

    public Task<QueryContext> ListSymbolsAsync(
        IndexedSymbolKind? kind,
        bool asyncInvolved,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        ListSymbolsAsync(
            kind,
            AsyncStatusFilter.All,
            asyncInvolved,
            profileName,
            cancellationToken);

    public async Task<QueryContext> ListSymbolsAsync(
        IndexedSymbolKind? kind,
        AsyncStatusFilter asyncStatus,
        bool asyncInvolved,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var symbols = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            kind,
            asyncStatus,
            asyncInvolved,
            cancellationToken);
        return new QueryContext(profile, symbols);
    }

    public Task<DefinitionResult> FindDefinitionsAsync(
        string queryText,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default) =>
        FindDefinitionsAsync(
            queryText,
            filter: default,
            profileName,
            includeOverrides,
            cancellationToken);

    public async Task<DefinitionResult> FindDefinitionsAsync(
        string queryText,
        FunctionTargetFilter filter,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            cancellationToken);
        return new DefinitionResult(context, context.MatchedSymbols);
    }

    public Task<DefinitionResult> FindDefinitionsAsync(
        string queryText,
        string? profileName,
        CancellationToken cancellationToken) =>
        FindDefinitionsAsync(queryText, profileName, includeOverrides: false, cancellationToken);

    public Task<DefinitionResult> FindDefinitionAtAsync(
        string location,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        FindDefinitionAtAsync(location, filter: default, profileName, cancellationToken);

    public async Task<DefinitionResult> FindDefinitionAtAsync(
        string location,
        FunctionTargetFilter filter,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var parsed = SourcePositionResolver.ParseAt(location);
        var documents = await repository.FindDocumentsAsync(profile.Id, parsed.Path, cancellationToken);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException($"Document was not found in the index: {parsed.Path}");
        }

        if (documents.Count > 1)
        {
            throw new InvalidOperationException(
                $"Document path is ambiguous. Use a longer path: {string.Join(", ", documents.Select(document => document.Path))}");
        }

        var document = documents[0];
        var offset = SourcePositionResolver.ResolveLineColumn(document.Path, parsed.Line, parsed.Column);
        var call = await repository.FindCallAtAsync(profile.Id, document.Id, offset, cancellationToken);
        if (call is null)
        {
            return new DefinitionResult(new QueryContext(profile, []), []);
        }

        var targetId = call.CalleeDefinitionId ?? call.CalleeSymbolId;
        var candidates = targetId is null
            ? []
            : await repository.GetSymbolsByIdsAsync(profile.Id, [targetId.Value], cancellationToken);
        var definitions = FilterSymbols(candidates, filter, cancellationToken);
        return new DefinitionResult(new QueryContext(profile, definitions), definitions);
    }

    public Task<CallResult> FindReferencesAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default) =>
        FindReferencesAsync(
            queryText,
            generatedFilter,
            filter: default,
            profileName,
            includeOverrides,
            cancellationToken);

    public async Task<CallResult> FindReferencesAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        FunctionTargetFilter filter,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            cancellationToken);
        var calls = await repository.GetCallsByCalleeAsync(
            context.Profile.Id,
            context.MatchedSymbols.Select(symbol => symbol.Id),
            generatedFilter,
            cancellationToken: cancellationToken);
        return new CallResult(context, calls, [], []);
    }

    public Task<CallResult> FindCallersAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        DispatchSearchMode dispatchMode,
        CallerScope callerScope,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default) =>
        FindCallersAsync(
            queryText,
            generatedFilter,
            dispatchMode,
            callerScope,
            filter: default,
            profileName,
            includeOverrides,
            cancellationToken);

    public async Task<CallResult> FindCallersAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        DispatchSearchMode dispatchMode,
        CallerScope callerScope,
        FunctionTargetFilter filter,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            cancellationToken);
        var calls = await repository.GetCallsByCalleeAsync(
            context.Profile.Id,
            context.MatchedSymbols.Select(symbol => symbol.Id),
            generatedFilter,
            CallKinds,
            cancellationToken);
        var effectiveCallers = await ResolveEffectiveCallersAsync(
            context.Profile.Id,
            calls,
            callerScope,
            cancellationToken);
        IReadOnlyList<StoredRelation> possibleTargets = [];
        if (dispatchMode != DispatchSearchMode.Static)
        {
            var kinds = dispatchMode == DispatchSearchMode.Virtual
                ? new HashSet<SymbolRelationKind> { SymbolRelationKind.Overrides }
                : new HashSet<SymbolRelationKind>
                {
                    SymbolRelationKind.Overrides,
                    SymbolRelationKind.ExplicitlyImplements,
                    SymbolRelationKind.ImplicitlyImplements,
                };
            possibleTargets = await repository.GetRelationsByTargetAsync(
                context.Profile.Id,
                context.MatchedSymbols.Select(symbol => symbol.Id),
                kinds,
                cancellationToken);
        }

        return new CallResult(context, calls, effectiveCallers, possibleTargets);
    }

    public Task<CallResult> FindCalleesAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        bool includeLambdaCalls = true,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default) =>
        FindCalleesAsync(
            queryText,
            generatedFilter,
            filter: default,
            includeLambdaCalls,
            profileName,
            includeOverrides,
            cancellationToken);

    public async Task<CallResult> FindCalleesAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        FunctionTargetFilter filter,
        bool includeLambdaCalls = true,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            cancellationToken);
        var calls = includeLambdaCalls
            ? await repository.GetCallsByCallerIncludingLambdaDescendantsAsync(
                context.Profile.Id,
                context.MatchedSymbols.Select(symbol => symbol.Id),
                generatedFilter,
                CallKinds,
                cancellationToken)
            : await repository.GetCallsByCallerAsync(
                context.Profile.Id,
                context.MatchedSymbols.Select(symbol => symbol.Id),
                generatedFilter,
                CallKinds,
                cancellationToken);
        return new CallResult(context, calls, [], []);
    }

    public Task<CallResult> FindCalleesAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        string? profileName,
        CancellationToken cancellationToken = default) =>
        FindCalleesAsync(queryText, generatedFilter, true, profileName, includeOverrides: false, cancellationToken);

    public Task<RelationResult> FindOverridesAsync(
        string queryText,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        FindOverridesAsync(queryText, filter: default, profileName, cancellationToken);

    public async Task<RelationResult> FindOverridesAsync(
        string queryText,
        FunctionTargetFilter filter,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (filter.Kind == IndexedSymbolKind.Lambda)
        {
            throw new SymbolQueryParseException("--kind lambda is not applicable to overrides.");
        }

        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides: false,
            filter,
            cancellationToken);
        var relations = await repository.GetRelationsByTargetAsync(
            context.Profile.Id,
            context.MatchedSymbols.Select(symbol => symbol.Id),
            new HashSet<SymbolRelationKind> { SymbolRelationKind.Overrides },
            cancellationToken);
        return new RelationResult(context, relations);
    }

    public async Task<ConditionsResult> GetConditionsAsync(
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var symbols = await repository.GetConditionalSymbolsAsync(profile, cancellationToken);
        return new ConditionsResult(profile, symbols);
    }

    private async Task<IReadOnlyList<StoredSymbol>> ResolveEffectiveCallersAsync(
        long profileId,
        IReadOnlyList<StoredCall> calls,
        CallerScope callerScope,
        CancellationToken cancellationToken)
    {
        if (callerScope == CallerScope.Direct)
        {
            return await repository.GetSymbolsByIdsAsync(
                profileId,
                calls.Select(call => call.CallerSymbolId),
                cancellationToken);
        }

        var directIds = callerScope == CallerScope.Both
            ? calls.Select(call => call.CallerSymbolId)
            : [];
        var containingIds = calls.Select(call => call.CallerContainingSymbolId ?? call.CallerSymbolId);
        return await repository.GetSymbolsByIdsAsync(
            profileId,
            directIds.Concat(containingIds),
            cancellationToken);
    }

    private async Task<QueryContext> SearchStoredExecutableSymbolsAsync(
        SymbolSearchRequest request,
        string? profileName,
        bool sourceOnly,
        CancellationToken cancellationToken)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var hasSourceFilters = request.Includes.Count > 0 || request.Excludes.Count > 0;
        var candidates = await repository.FindExecutableSymbolsAsync(
            profile.Id,
            sourceOnly || hasSourceFilters,
            cancellationToken);
        var matcher = new SymbolPatternMatcher(request);
        var comparison = request.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var matches = new List<StoredSymbol>();
        foreach (var symbol in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isMatch = matcher.IsMatch(symbol);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isMatch)
            {
                continue;
            }

            if (hasSourceFilters && !SourceTextFilter.IsMatch(
                    symbol.NormalizedSource,
                    request.Includes,
                    request.Excludes,
                    comparison,
                    cancellationToken))
            {
                continue;
            }

            if (sourceOnly && !IsSourceBackedExecutable(symbol))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            matches.Add(symbol);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new QueryContext(profile, matches, request.ShowSource);
    }

    private static bool CanUseExactSearch(SymbolSearchRequest request) =>
        request.Pattern is not null &&
        !request.Pattern.Contains('*', StringComparison.Ordinal) &&
        !request.Pattern.Contains("::<lambda#", StringComparison.Ordinal) &&
        !request.UseRegex &&
        !request.IgnoreCase &&
        request.NamespacePattern is null &&
        request.TypePattern is null &&
        request.MethodPattern is null &&
        request.Kind is null &&
        request.Includes.Count == 0 &&
        request.Excludes.Count == 0;

    private bool CanUseExecutableTargetResolver(string queryText)
    {
        if (ExecutableTargetResolver.IsLambdaTargetQuery(queryText))
        {
            return true;
        }

        return !queryText.Contains('*', StringComparison.Ordinal) && _parser.Parse(queryText).IsMethodQuery;
    }

    private static bool IsSourceBackedExecutable(StoredSymbol symbol) =>
        symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda &&
        symbol.DocumentPath is not null &&
        symbol.NormalizedSource is not null;

    private static IReadOnlyList<StoredSymbol> FilterSymbols(
        IEnumerable<StoredSymbol> candidates,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var matches = new List<StoredSymbol>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.Matches(candidate))
            {
                matches.Add(candidate);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return matches;
    }

    private static void ValidateSearchRequest(SymbolSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Includes);
        ArgumentNullException.ThrowIfNull(request.Excludes);
    }

    private async Task<(StoredProfile Profile, StoredSymbol Root)> ResolveSingleSourceExecutableAsync(
        string queryText,
        string? profileName,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExecutableTargetResolver.IsLambdaTargetQuery(queryText) && !_parser.Parse(queryText).IsMethodQuery)
        {
            throw new SymbolQueryParseException("Graph queries require an exact source-backed executable query.");
        }

        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var matches = await _executableTargetResolver.ResolveAsync(
            profile.Id,
            queryText,
            sourceOnly: true,
            includeOverrides: false,
            filter,
            cancellationToken);
        if (matches.Count == 0)
        {
            throw new SymbolQueryParseException(
                $"No source-backed executable matches graph query: {queryText}");
        }

        if (matches.Count > 1)
        {
            throw new SymbolQueryParseException(
                $"Graph query is ambiguous for '{queryText}'. Candidates: " +
                DescribeAmbiguousGraphRootCandidates(matches, cancellationToken));
        }

        return (profile, matches[0]);
    }

    private static string DescribeAmbiguousGraphRootCandidates(
        IReadOnlyList<StoredSymbol> candidates,
        CancellationToken cancellationToken)
    {
        var displayNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            displayNameCounts.TryGetValue(candidate.DisplayName, out var count);
            displayNameCounts[candidate.DisplayName] = count + 1;
        }

        var descriptions = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            descriptions.Add(displayNameCounts[candidate.DisplayName] == 1
                ? candidate.DisplayName
                : $"{candidate.DisplayName} [document: {candidate.DocumentPath ?? "<missing>"}; symbol ID: {candidate.Id}]");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return string.Join(", ", descriptions);
    }

    private static void ValidateDepth(int depth)
    {
        if (depth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth cannot be negative.");
        }
    }

    private static void ValidateMaxNodes(int maxNodes)
    {
        if (maxNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes), "Maximum node count must be positive.");
        }
    }

    private async Task<QueryContext> FindTargetSymbolsAsync(
        string queryText,
        string? profileName,
        bool includeOverrides,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken)
    {
        var sourceContext = await FindSymbolsAsync(
            queryText,
            filter,
            profileName,
            sourceOnly: true,
            includeOverrides,
            cancellationToken);
        if (sourceContext.MatchedSymbols.Count > 0)
        {
            return sourceContext;
        }

        var metadataContext = await FindSymbolsAsync(
            queryText,
            filter,
            profileName,
            sourceOnly: false,
            includeOverrides,
            cancellationToken);
        var matches = new List<StoredSymbol>(metadataContext.MatchedSymbols.Count);
        foreach (var symbol in metadataContext.MatchedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isConstructedOrReduced =
                symbol.StableKey.Contains("|constructed:", StringComparison.Ordinal) ||
                symbol.StableKey.Contains("|reduced:", StringComparison.Ordinal);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isConstructedOrReduced)
            {
                matches.Add(symbol);
            }
        }

        return metadataContext with { MatchedSymbols = matches };
    }
}
