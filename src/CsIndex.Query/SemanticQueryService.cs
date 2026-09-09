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
    private readonly string? baseDirectory;
    private readonly SymbolPathResolver _symbolPathResolver;
    private readonly ExecutableTargetResolver _executableTargetResolver;
    private readonly AsyncPathResolver _asyncPathResolver;
    private readonly CallerTreeBuilder _callerTreeBuilder;

    public SemanticQueryService(QueryRepository repository, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        this.repository = repository;
        this.baseDirectory = baseDirectory;
        _symbolPathResolver = new SymbolPathResolver(repository);
        _executableTargetResolver = new ExecutableTargetResolver(_symbolPathResolver);
        _asyncPathResolver = new AsyncPathResolver(repository);
        _callerTreeBuilder = new CallerTreeBuilder(repository);
    }

    public async Task<RootSelection> SelectRootsAsync(
        SymbolSelectionRequest request,
        string? profileName,
        bool sourceOnly,
        GeneratedFilter rootGeneratedFilter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var roots = await _executableTargetResolver.ResolveAsync(
            profile.Id,
            request,
            sourceOnly,
            cancellationToken);
        var byId = new Dictionary<long, ResolvedLogicalRoot>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var include = rootGeneratedFilter switch
            {
                GeneratedFilter.Include => true,
                GeneratedFilter.Exclude => root.Symbol.PreferredIsGenerated == false,
                GeneratedFilter.Only => root.Symbol.PreferredIsGenerated == true,
                _ => throw new ArgumentOutOfRangeException(nameof(rootGeneratedFilter)),
            };
            if (include)
            {
                byId.TryAdd(root.Symbol.Id, root);
            }
        }

        var orderedSymbols = SymbolCanonicalComparer.OrderSymbols(
            byId.Values.Select(root => root.Symbol),
            cancellationToken);
        var orderedRoots = new List<ResolvedLogicalRoot>(orderedSymbols.Count);
        foreach (var symbol in orderedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            orderedRoots.Add(byId[symbol.Id]);
        }

        return new RootSelection(
            profile,
            orderedRoots);
    }

    public async Task<SourceSearchResult> SelectSourceRowsAsync(
        SymbolSelectionRequest request,
        string? profileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var rows = await _symbolPathResolver.ResolveDeclarationRowsAsync(
            profile.Id,
            request,
            cancellationToken);
        var result = new List<DeclarationResultRow>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new DeclarationResultRow(row.Symbol, row.Declaration));
        }

        return new SourceSearchResult(profile, result);
    }

    public async Task<IReadOnlyList<LogicalSymbolResultRow>> LoadLogicalRowsAsync(
        RootSelection selection,
        bool includeSourceText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        var roots = selection.Roots;
        var declarations = await repository.GetPreferredDeclarationsAsync(
            selection.Profile.Id,
            roots.Select(root => root.Symbol.Id),
            includeSourceText,
            cancellationToken);
        var byId = declarations.ToDictionary(declaration => declaration.SymbolId);
        var result = new List<LogicalSymbolResultRow>(roots.Count);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byId.TryGetValue(root.Symbol.Id, out var preferred);
            var symbol = preferred is null
                ? root.Symbol
                : ApplyPreferredDeclaration(root.Symbol, preferred);
            result.Add(new LogicalSymbolResultRow(symbol, preferred));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public Task<RootSelection> ExpandOverrideRootsAsync(
        RootSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Roots.Any(root => root.Symbol.Kind != IndexedSymbolKind.Method))
        {
            throw new SymbolQueryParseException(
                "--include-overrides requires an exact method query.");
        }

        return new MethodTargetResolver(repository).ExpandAsync(selection, cancellationToken);
    }

    public async Task<DefinitionResult> FindDefinitionsAsync(
        RootSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        var roots = selection.Roots;
        var declarations = await repository.GetDeclarationsAsync(
            selection.Profile.Id,
            roots.Select(root => root.Symbol.Id),
            includeSourceText: false,
            cancellationToken);
        var declarationsById = new Dictionary<long, List<StoredDeclaration>>();
        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declarationsById.TryGetValue(declaration.SymbolId, out var rowsForSymbol))
            {
                rowsForSymbol = [];
                declarationsById.Add(declaration.SymbolId, rowsForSymbol);
            }

            rowsForSymbol.Add(declaration);
        }

        var rows = new List<DeclarationResultRow>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declarationsById.TryGetValue(root.Symbol.Id, out var symbolDeclarations))
            {
                continue;
            }

            var orderedDeclarations = SymbolCanonicalComparer.OrderDefinitionDeclarations(
                symbolDeclarations,
                cancellationToken);
            foreach (var declaration in orderedDeclarations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new DeclarationResultRow(root.Symbol, declaration));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new DefinitionResult(selection, rows);
    }

    public async Task<CallResult> FindReferencesAsync(
        RootSelection selection,
        GeneratedFilter generatedFilter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        var calls = await repository.GetCallsByCalleeAsync(
            selection.Profile.Id,
            selection.Roots.Select(root => root.Symbol.Id),
            generatedFilter,
            includeSourceText: false,
            cancellationToken: cancellationToken);
        var hydration = await HydrateCallResultAsync(
            selection.Profile.Id,
            calls,
            CallerScope.Direct,
            [],
            cancellationToken);
        var orderedCalls = SymbolCanonicalComparer.OrderCalls(
            calls,
            hydration.SymbolsById,
            cancellationToken);
        return new CallResult(selection, orderedCalls, hydration.EffectiveCallers, [], hydration.SymbolsById);
    }

    public async Task<CallResult> FindCallersAsync(
        RootSelection selection,
        GeneratedFilter generatedFilter,
        DispatchSearchMode dispatchMode,
        CallerScope callerScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        var rootIds = selection.Roots.Select(root => root.Symbol.Id).ToArray();
        var calls = await repository.GetCallsByCalleeAsync(
            selection.Profile.Id,
            rootIds,
            generatedFilter,
            CallKinds,
            includeSourceText: false,
            cancellationToken: cancellationToken);
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
                selection.Profile.Id,
                rootIds,
                kinds,
                cancellationToken);
        }

        var hydration = await HydrateCallResultAsync(
            selection.Profile.Id,
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
            selection,
            orderedCalls,
            hydration.EffectiveCallers,
            orderedTargets,
            hydration.SymbolsById);
    }

    public async Task<CallResult> FindCalleesAsync(
        RootSelection selection,
        GeneratedFilter generatedFilter,
        bool includeLambdaCalls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        var rootIds = selection.Roots.Select(root => root.Symbol.Id).ToArray();
        var calls = includeLambdaCalls
            ? await repository.GetCallsByCallerIncludingLambdaDescendantsAsync(
                selection.Profile.Id,
                rootIds,
                generatedFilter,
                CallKinds,
                includeSourceText: false,
                cancellationToken: cancellationToken)
            : await repository.GetCallsByCallerAsync(
                selection.Profile.Id,
                rootIds,
                generatedFilter,
                CallKinds,
                includeSourceText: false,
                cancellationToken: cancellationToken);
        var hydration = await HydrateCallResultAsync(
            selection.Profile.Id,
            calls,
            CallerScope.Direct,
            [],
            cancellationToken);
        var orderedCalls = SymbolCanonicalComparer.OrderCalls(
            calls,
            hydration.SymbolsById,
            cancellationToken);
        return new CallResult(selection, orderedCalls, [], [], hydration.SymbolsById);
    }

    public async Task<RelationResult> FindOverridesAsync(
        RootSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Roots.Any(root => root.Symbol.Kind != IndexedSymbolKind.Method))
        {
            throw new SymbolQueryParseException("--kind lambda is not applicable to overrides.");
        }

        var relations = await repository.GetRelationsByTargetAsync(
            selection.Profile.Id,
            selection.Roots.Select(root => root.Symbol.Id),
            new HashSet<SymbolRelationKind> { SymbolRelationKind.Overrides },
            cancellationToken);
        var symbolsById = await HydrateRelationEndpointsAsync(
            selection.Profile.Id,
            relations,
            cancellationToken);
        var orderedRelations = SymbolCanonicalComparer.OrderRelations(
            relations,
            symbolsById,
            cancellationToken);
        return new RelationResult(selection, orderedRelations, symbolsById);
    }

    public async Task<AsyncPathResult> FindAsyncPathAsync(
        RootSelection selection,
        int maxNodes,
        CancellationToken cancellationToken = default)
    {
        ValidateMaxNodes(maxNodes);
        var root = RequireSingleGraphRoot(selection, "async path");
        return await _asyncPathResolver.ResolveAsync(selection, root, maxNodes, cancellationToken);
    }

    public async Task<CallerTreeResult> FindCallerTreeAsync(
        RootSelection selection,
        int depth,
        int maxNodes,
        CancellationToken cancellationToken = default)
    {
        ValidateDepth(depth);
        ValidateMaxNodes(maxNodes);
        var root = RequireSingleGraphRoot(selection, "caller tree");
        return await _callerTreeBuilder.BuildAsync(selection, root, depth, maxNodes, cancellationToken);
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
        var selection = await SelectRootsAsync(
            request,
            profileName,
            sourceOnly,
            GeneratedFilter.Include,
            cancellationToken);
        if (includeOverrides)
        {
            selection = await ExpandOverrideRootsAsync(selection, cancellationToken);
        }

        var rows = await LoadLogicalRowsAsync(selection, includeSourceText, cancellationToken);
        return new QueryContext(
            selection.Profile,
            rows.Select(row => row.Symbol).ToArray());
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
        if (request.Selector is null &&
            request.Conditions.Count == 0 &&
            !request.KindSpecified &&
            !request.AsyncStatusSpecified)
        {
            throw new SymbolQueryParseException(
                "source search requires a bounded selector, condition, kind, or async status.");
        }

        var result = await SelectSourceRowsAsync(request, profileName, cancellationToken);
        var symbols = result.Matches
            .Select(row => ApplyPreferredDeclaration(row.Symbol, row.Declaration))
            .ToArray();
        return new QueryContext(result.Profile, symbols, ShowSource: true);
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
        var selection = await ResolveSingleSourceSelectionAsync(
            queryText,
            profileName,
            filter,
            cancellationToken);
        return await FindAsyncPathAsync(selection, maxNodes, cancellationToken);
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
        var selection = await ResolveSingleSourceSelectionAsync(
            queryText,
            profileName,
            filter,
            cancellationToken);
        return await FindCallerTreeAsync(selection, depth, maxNodes, cancellationToken);
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
        var selection = await ResolveTargetSelectionAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            GeneratedFilter.Include,
            cancellationToken);
        return await FindDefinitionsAsync(selection, cancellationToken);
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
            baseDirectory);
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
                "Document path is ambiguous because the selected profile contains the same stored path in multiple projects: " +
                storedDocumentPath);
        }

        var document = documents[0];
        var offset = SourcePositionResolver.ResolveLineColumn(
            paths.ToAbsolutePath(document.Path),
            parsed.Line,
            parsed.Column);
        var call = await repository.FindCallAtAsync(profile.Id, document.Id, offset, cancellationToken);
        if (call is null)
        {
            return new DefinitionResult(new RootSelection(profile, []), []);
        }

        var targetId = call.CalleeDefinitionId ?? call.CalleeSymbolId;
        var candidates = targetId is null
            ? []
            : await repository.GetSymbolsByIdsAsync(profile.Id, [targetId.Value], cancellationToken);
        var filtered = FilterSymbols(candidates, filter, cancellationToken);
        var selection = new RootSelection(
            profile,
            filtered.Select(symbol => new ResolvedLogicalRoot(symbol, [])).ToArray());
        return await FindDefinitionsAsync(selection, cancellationToken);
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
        var selection = await ResolveTargetSelectionAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            generatedFilter,
            cancellationToken);
        return await FindReferencesAsync(selection, generatedFilter, cancellationToken);
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
        var selection = await ResolveTargetSelectionAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            generatedFilter,
            cancellationToken);
        return await FindCallersAsync(
            selection,
            generatedFilter,
            dispatchMode,
            callerScope,
            cancellationToken);
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
        var selection = await ResolveTargetSelectionAsync(
            queryText,
            profileName,
            includeOverrides,
            filter,
            generatedFilter,
            cancellationToken);
        return await FindCalleesAsync(selection, generatedFilter, includeLambdaCalls, cancellationToken);
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
        if (filter.Kind is { } requestedKind && requestedKind != IndexedSymbolKind.Method)
        {
            throw new SymbolQueryParseException("--kind lambda is not applicable to overrides.");
        }

        var selection = await ResolveTargetSelectionAsync(
            queryText,
            profileName,
            includeOverrides: false,
            filter,
            GeneratedFilter.Include,
            cancellationToken);
        return await FindOverridesAsync(selection, cancellationToken);
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

    private static StoredSymbol ApplyPreferredDeclaration(
        StoredSymbol symbol,
        StoredDeclaration declaration) =>
        symbol with
        {
            PreferredDeclaration = declaration,
            PreferredDocumentPath = declaration.DocumentPath,
            PreferredSourceStart = declaration.SourceStart,
            PreferredIsGenerated = declaration.IsGenerated,
            DocumentPath = declaration.DocumentPath,
            SourceStart = declaration.SourceStart,
            IsGenerated = declaration.IsGenerated,
        };

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

    private async Task<RootSelection> ResolveSingleSourceSelectionAsync(
        string queryText,
        string? profileName,
        FunctionTargetFilter filter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selection = await SelectRootsAsync(
            CreateStrictSelectionRequest(queryText, filter),
            profileName,
            sourceOnly: true,
            GeneratedFilter.Include,
            cancellationToken);
        if (selection.Roots.Count == 0)
        {
            throw new SymbolQueryParseException(
                $"No source-backed executable matches graph query: {queryText}");
        }

        if (selection.Roots.Count > 1)
        {
            throw new SymbolQueryParseException(
                $"Graph query is ambiguous for '{queryText}'. Candidates: " +
                DescribeAmbiguousGraphRootCandidates(
                    selection.Roots.Select(root => root.Symbol).ToArray(),
                    cancellationToken));
        }

        return selection;
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

    private static StoredSymbol RequireSingleGraphRoot(
        RootSelection selection,
        string graphName)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Roots.Count == 0)
        {
            throw new SymbolQueryParseException(
                $"No source-backed executable matches {graphName} query.");
        }

        if (selection.Roots.Count > 1)
        {
            throw new SymbolQueryParseException(
                $"{char.ToUpperInvariant(graphName[0])}{graphName[1..]} query requires exactly one selected root.");
        }

        return selection.Roots[0].Symbol;
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

    private async Task<RootSelection> ResolveTargetSelectionAsync(
        string queryText,
        string? profileName,
        bool includeOverrides,
        FunctionTargetFilter filter,
        GeneratedFilter rootGeneratedFilter,
        CancellationToken cancellationToken)
    {
        if (includeOverrides &&
            filter.Kind is { } requestedKind &&
            requestedKind != IndexedSymbolKind.Method)
        {
            throw new SymbolQueryParseException(
                requestedKind == IndexedSymbolKind.Lambda
                    ? "--kind lambda cannot be combined with --include-overrides."
                    : "--include-overrides requires an exact method query.");
        }

        var request = CreateStrictSelectionRequest(queryText, filter);
        var selection = await SelectRootsAsync(
            request,
            profileName,
            sourceOnly: true,
            rootGeneratedFilter,
            cancellationToken);
        if (selection.Roots.Count == 0)
        {
            selection = await SelectRootsAsync(
                request,
                profileName,
                sourceOnly: false,
                rootGeneratedFilter,
                cancellationToken);
        }

        if (includeOverrides)
        {
            selection = await ExpandOverrideRootsAsync(selection, cancellationToken);
        }

        return selection;
    }
}
