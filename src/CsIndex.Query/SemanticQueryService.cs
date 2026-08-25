using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

public sealed class SemanticQueryService
{
    private static readonly IReadOnlySet<ReferenceKind> CallKinds =
        new HashSet<ReferenceKind> { ReferenceKind.Invocation, ReferenceKind.ObjectCreation };

    internal Action<IReadOnlyCollection<long>>? EndpointHydrationObserver { get; init; }

    private static readonly SymbolPathFormatter PathFormatter = new();
    private static readonly SymbolPathFormatOptions DefaultPathFormat = new();

    private readonly QueryRepository repository;
    private readonly SymbolPathResolver _symbolPathResolver;
    private readonly ExecutableTargetResolver _executableTargetResolver;
    private readonly AsyncPathResolver _asyncPathResolver;
    private readonly CallerTreeBuilder _callerTreeBuilder;

    public SemanticQueryService(QueryRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        this.repository = repository;
        _symbolPathResolver = new SymbolPathResolver(repository);
        _executableTargetResolver = new ExecutableTargetResolver(
            _symbolPathResolver,
            new MethodTargetResolver(repository));
        _asyncPathResolver = new AsyncPathResolver(repository);
        _callerTreeBuilder = new CallerTreeBuilder(repository);
    }

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

    public Task<QueryContext> FindSymbolsAsync(
        string queryText,
        FunctionTargetFilter filter,
        string? profileName = null,
        bool sourceOnly = false,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default) =>
        FindSymbolsAsync(
            queryText,
            filter,
            profileName,
            sourceOnly,
            includeOverrides,
            includeSourceText: false,
            cancellationToken);

    public Task<QueryContext> FindSymbolsAsync(
        string queryText,
        FunctionTargetFilter filter,
        string? profileName,
        bool sourceOnly,
        bool includeOverrides,
        bool includeSourceText,
        CancellationToken cancellationToken) =>
        FindSymbolsAsync(
            CreateStrictSelectionRequest(queryText, filter),
            profileName,
            sourceOnly,
            includeOverrides,
            includeSourceText,
            cancellationToken);

    public async Task<QueryContext> SearchSymbolsAsync(
        SymbolSelectionRequest request,
        bool showSource = false,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var context = await FindSymbolsAsync(
            request,
            profileName,
            sourceOnly: false,
            includeOverrides: false,
            includeSourceText: showSource,
            cancellationToken);
        return context with { ShowSource = showSource };
    }

    public async Task<QueryContext> FindSymbolsAsync(
        SymbolSelectionRequest request,
        string? profileName,
        bool sourceOnly,
        bool includeOverrides,
        bool includeSourceText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var matches = await _executableTargetResolver.ResolveAsync(
            profile.Id,
            request,
            sourceOnly,
            includeOverrides,
            cancellationToken);
        var context = new QueryContext(
            profile,
            SymbolCanonicalComparer.OrderSymbols(matches, cancellationToken));
        return await AttachPreferredSourceIfRequestedAsync(
            context,
            includeSourceText,
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
        var context = await FindSymbolsAsync(
            CreateStrictSelectionRequest(queryText, filter),
            profileName,
            sourceOnly: true,
            includeOverrides: false,
            includeSourceText: true,
            cancellationToken);
        return context with { ShowSource = true };
    }

    public async Task<QueryContext> SearchSourceAsync(
        SymbolSelectionRequest request,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Conditions.Any(condition =>
                condition.Category is ConditionCategory.Include or ConditionCategory.Exclude))
        {
            throw new SymbolQueryParseException(
                "source search requires at least one include or exclude condition.");
        }

        var context = await FindSymbolsAsync(
            request,
            profileName,
            sourceOnly: true,
            includeOverrides: false,
            includeSourceText: true,
            cancellationToken);
        return context with { ShowSource = true };
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
        return new QueryContext(
            profile,
            SymbolCanonicalComparer.OrderSymbols(symbols, cancellationToken));
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
        var paths = IndexPathResolver.CreateForQuery(
            repository.DatabasePath,
            profile.IndexRootAnchor,
            baseDirectory: null);
        var storedDocumentPath = paths.NormalizeLocationInputToStoredPath(parsed.Path);
        var documents = await repository.FindDocumentsAsync(
            profile.Id,
            storedDocumentPath,
            cancellationToken);
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
        var offset = SourcePositionResolver.ResolveLineColumn(
            paths.ToAbsolutePath(document.Path),
            parsed.Line,
            parsed.Column);
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
        var hydration = await HydrateCallResultAsync(
            context.Profile.Id,
            calls,
            CallerScope.Direct,
            [],
            cancellationToken);
        var orderedCalls = SymbolCanonicalComparer.OrderCalls(
            calls,
            hydration.SymbolsById,
            cancellationToken);
        return new CallResult(context, orderedCalls, hydration.EffectiveCallers, [], hydration.SymbolsById);
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

        var hydration = await HydrateCallResultAsync(
            context.Profile.Id,
            calls,
            callerScope,
            possibleTargets,
            cancellationToken);
        var orderedCalls = SymbolCanonicalComparer.OrderCalls(
            calls,
            hydration.SymbolsById,
            cancellationToken);
        var orderedTargets = SymbolCanonicalComparer.OrderRelations(
            possibleTargets,
            hydration.SymbolsById,
            cancellationToken);
        return new CallResult(
            context,
            orderedCalls,
            hydration.EffectiveCallers,
            orderedTargets,
            hydration.SymbolsById);
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
        var hydration = await HydrateCallResultAsync(
            context.Profile.Id,
            calls,
            CallerScope.Direct,
            [],
            cancellationToken);
        var orderedCalls = SymbolCanonicalComparer.OrderCalls(
            calls,
            hydration.SymbolsById,
            cancellationToken);
        return new CallResult(context, orderedCalls, [], [], hydration.SymbolsById);
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
        var symbolsById = await HydrateRelationEndpointsAsync(
            context.Profile.Id,
            relations,
            cancellationToken);
        var orderedRelations = SymbolCanonicalComparer.OrderRelations(
            relations,
            symbolsById,
            cancellationToken);
        return new RelationResult(context, orderedRelations, symbolsById);
    }

    public async Task<ConditionsResult> GetConditionsAsync(
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var symbols = await repository.GetConditionalSymbolsAsync(profile, cancellationToken);
        return new ConditionsResult(profile, symbols);
    }

    private async Task<(IReadOnlyList<StoredSymbol> EffectiveCallers, IReadOnlyDictionary<long, StoredSymbol> SymbolsById)> HydrateCallResultAsync(
        long profileId,
        IReadOnlyList<StoredCall> calls,
        CallerScope callerScope,
        IReadOnlyList<StoredRelation> possibleTargets,
        CancellationToken cancellationToken)
    {
        var requiredIds = new HashSet<long>();
        foreach (var call in calls)
        {
            requiredIds.Add(call.CallerSymbolId);
            if (call.CallerContainingSymbolId is long containingId)
            {
                requiredIds.Add(containingId);
            }

            if (call.CalleeSymbolId is long calleeId)
            {
                requiredIds.Add(calleeId);
            }

            if (call.CalleeDefinitionId is long definitionId)
            {
                requiredIds.Add(definitionId);
            }
        }

        foreach (var relation in possibleTargets)
        {
            requiredIds.Add(relation.SourceSymbolId);
            requiredIds.Add(relation.TargetSymbolId);
        }

        EndpointHydrationObserver?.Invoke(requiredIds);
        var symbols = await repository.GetSymbolsByIdsAsync(profileId, requiredIds, cancellationToken);
        var symbolsById = symbols.ToDictionary(symbol => symbol.Id);
        foreach (var requiredId in requiredIds)
        {
            if (!symbolsById.ContainsKey(requiredId))
            {
                throw new InvalidOperationException(
                    $"Endpoint symbol ID {requiredId} could not be resolved in the selected profile.");
            }
        }

        var effectiveCallerIds = callerScope switch
        {
            CallerScope.Direct => calls.Select(call => call.CallerSymbolId),
            CallerScope.Containing => calls.Select(call => call.CallerContainingSymbolId ?? call.CallerSymbolId),
            CallerScope.Both => calls.Select(call => call.CallerSymbolId)
                .Concat(calls.Select(call => call.CallerContainingSymbolId ?? call.CallerSymbolId)),
            _ => throw new ArgumentOutOfRangeException(nameof(callerScope)),
        };
        var effectiveCallers = SymbolCanonicalComparer.OrderSymbols(
            effectiveCallerIds
                .Distinct()
                .Select(id => symbolsById[id]),
            cancellationToken);
        return (effectiveCallers, symbolsById);
    }

    private async Task<IReadOnlyDictionary<long, StoredSymbol>> HydrateRelationEndpointsAsync(
        long profileId,
        IReadOnlyList<StoredRelation> relations,
        CancellationToken cancellationToken)
    {
        var requiredIds = relations
            .SelectMany(relation => new[] { relation.SourceSymbolId, relation.TargetSymbolId })
            .Distinct()
            .ToArray();
        EndpointHydrationObserver?.Invoke(requiredIds);
        var symbols = await repository.GetSymbolsByIdsAsync(profileId, requiredIds, cancellationToken);
        var symbolsById = symbols.ToDictionary(symbol => symbol.Id);
        foreach (var requiredId in requiredIds)
        {
            if (!symbolsById.ContainsKey(requiredId))
            {
                throw new InvalidOperationException(
                    $"Relation endpoint symbol ID {requiredId} could not be resolved in the selected profile.");
            }
        }

        return symbolsById;
    }

    private async Task<QueryContext> AttachPreferredSourceAsync(
        QueryContext context,
        CancellationToken cancellationToken)
    {
        var symbols = await AttachPreferredDeclarationsAsync(
            context.Profile.Id,
            context.MatchedSymbols,
            includeSourceText: true,
            cancellationToken);
        return context with
        {
            MatchedSymbols = SymbolCanonicalComparer.OrderSymbols(symbols, cancellationToken),
        };
    }

    private Task<QueryContext> AttachPreferredSourceIfRequestedAsync(
        QueryContext context,
        bool includeSourceText,
        CancellationToken cancellationToken) =>
        includeSourceText
            ? AttachPreferredSourceAsync(context, cancellationToken)
            : Task.FromResult(context);

    private async Task<IReadOnlyList<StoredSymbol>> AttachPreferredDeclarationsAsync(
        long profileId,
        IReadOnlyList<StoredSymbol> symbols,
        bool includeSourceText,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
        {
            return symbols;
        }

        var declarations = await repository.GetPreferredDeclarationsAsync(
            profileId,
            symbols.Select(symbol => symbol.Id),
            includeSourceText,
            cancellationToken);
        var bySymbolId = declarations.ToDictionary(declaration => declaration.SymbolId);
        return symbols.Select(symbol =>
        {
            if (!bySymbolId.TryGetValue(symbol.Id, out var declaration))
            {
                return symbol;
            }

            return symbol with
            {
                PreferredDeclaration = declaration,
                PreferredDocumentPath = declaration.DocumentPath,
                PreferredSourceStart = declaration.SourceStart,
                PreferredIsGenerated = declaration.IsGenerated,
                DocumentPath = declaration.DocumentPath,
                SourceStart = declaration.SourceStart,
                IsGenerated = declaration.IsGenerated,
            };
        }).ToArray();
    }

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
        return SymbolCanonicalComparer.OrderSymbols(matches, cancellationToken);
    }

    private static SymbolSelectionRequest CreateStrictSelectionRequest(
        string selector,
        FunctionTargetFilter filter)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new SymbolQueryParseException(
                "Invalid symbol path: Symbol path cannot be empty.");
        }

        return new SymbolSelectionRequest(
            selector,
            Conditions: [],
            Case: new SymbolCaseOptions(),
            FunctionFilter: filter,
            KindSpecified: filter.Kind is not null,
            AsyncStatusSpecified: filter.AsyncStatus != AsyncStatusFilter.All);
    }

    private async Task<(StoredProfile Profile, StoredSymbol Root)> ResolveSingleSourceExecutableAsync(
        string queryText,
        string? profileName,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var matches = await _executableTargetResolver.ResolveAsync(
            profile.Id,
            CreateStrictSelectionRequest(queryText, filter),
            sourceOnly: true,
            includeOverrides: false,
            cancellationToken);
        var orderedMatches = SymbolCanonicalComparer.OrderSymbols(matches, cancellationToken);
        if (orderedMatches.Count == 0)
        {
            throw new SymbolQueryParseException(
                $"No source-backed executable matches graph query: {queryText}");
        }

        if (orderedMatches.Count > 1)
        {
            throw new SymbolQueryParseException(
                $"Graph query is ambiguous for '{queryText}'. Candidates: " +
                DescribeAmbiguousGraphRootCandidates(orderedMatches, cancellationToken));
        }

        return (profile, orderedMatches[0]);
    }

    private static string DescribeAmbiguousGraphRootCandidates(
        IReadOnlyList<StoredSymbol> candidates,
        CancellationToken cancellationToken)
    {
        var displayNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var formattedPath = FormatPath(candidate);
            displayNameCounts.TryGetValue(formattedPath, out var count);
            displayNameCounts[formattedPath] = count + 1;
        }

        var descriptions = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var formattedPath = FormatPath(candidate);
            descriptions.Add(displayNameCounts[formattedPath] == 1
                ? formattedPath
                : $"{formattedPath} [document: {candidate.PreferredDocumentPath ?? "<missing>"}; symbol ID: {candidate.Id}]");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return string.Join(", ", descriptions);
    }

    private static string FormatPath(StoredSymbol symbol) =>
        PathFormatter.Format(
            symbol.Path ?? throw new InvalidOperationException(
                $"Stored symbol ID {symbol.Id} has no semantic path data."),
            DefaultPathFormat);

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
        return metadataContext;
    }
}
