using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

public sealed class SemanticQueryService(QueryRepository repository)
{
    private static readonly IReadOnlySet<ReferenceKind> CallKinds =
        new HashSet<ReferenceKind> { ReferenceKind.Invocation, ReferenceKind.ObjectCreation };

    private readonly SymbolQueryParser _parser = new();
    private readonly MethodTargetResolver _methodTargetResolver = new(repository);

    public async Task<QueryContext> FindSymbolsAsync(
        string queryText,
        string? profileName = null,
        bool sourceOnly = false,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var query = _parser.Parse(queryText);
        if (includeOverrides && !query.IsMethodQuery)
        {
            throw new SymbolQueryParseException("--include-overrides requires a method query.");
        }

        if (query.IsMethodQuery)
        {
            var methodTargets = await _methodTargetResolver.ResolveAsync(
                profile.Id,
                query,
                includeOverrides,
                sourceOnly,
                cancellationToken);
            return new QueryContext(profile, methodTargets);
        }

        var candidates = await repository.FindSymbolCandidatesAsync(
            profile.Id,
            query.MethodName,
            query.TypeSimpleName,
            query.IsMethodQuery ? IndexedSymbolKind.Method : IndexedSymbolKind.Type,
            sourceOnly,
            cancellationToken);
        var matches = candidates.Where(symbol => SymbolMatcher.IsMatch(query, symbol)).ToArray();
        return new QueryContext(profile, matches);
    }

    public async Task<QueryContext> ListSymbolsAsync(
        IndexedSymbolKind? kind,
        bool asyncInvolved,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        var symbols = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            kind,
            asyncInvolved,
            cancellationToken);
        return new QueryContext(profile, symbols);
    }

    public async Task<DefinitionResult> FindDefinitionsAsync(
        string queryText,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
            cancellationToken);
        return new DefinitionResult(context, context.MatchedSymbols);
    }

    public Task<DefinitionResult> FindDefinitionsAsync(
        string queryText,
        string? profileName,
        CancellationToken cancellationToken) =>
        FindDefinitionsAsync(queryText, profileName, includeOverrides: false, cancellationToken);

    public async Task<DefinitionResult> FindDefinitionAtAsync(
        string location,
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
        var definitions = targetId is null
            ? []
            : await repository.GetSymbolsByIdsAsync(profile.Id, [targetId.Value], cancellationToken);
        return new DefinitionResult(new QueryContext(profile, definitions), definitions);
    }

    public async Task<CallResult> FindReferencesAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
            cancellationToken);
        var calls = await repository.GetCallsByCalleeAsync(
            context.Profile.Id,
            context.MatchedSymbols.Select(symbol => symbol.Id),
            generatedFilter,
            cancellationToken: cancellationToken);
        return new CallResult(context, calls, [], []);
    }

    public async Task<CallResult> FindCallersAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        DispatchSearchMode dispatchMode,
        CallerScope callerScope,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
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

    public async Task<CallResult> FindCalleesAsync(
        string queryText,
        GeneratedFilter generatedFilter,
        bool includeLambdaCalls = true,
        string? profileName = null,
        bool includeOverrides = false,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides,
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

    public async Task<RelationResult> FindOverridesAsync(
        string queryText,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var context = await FindTargetSymbolsAsync(
            queryText,
            profileName,
            includeOverrides: false,
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

    private async Task<QueryContext> FindTargetSymbolsAsync(
        string queryText,
        string? profileName,
        bool includeOverrides,
        CancellationToken cancellationToken)
    {
        var sourceContext = await FindSymbolsAsync(
            queryText,
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
            profileName,
            sourceOnly: false,
            includeOverrides,
            cancellationToken);
        return metadataContext with
        {
            MatchedSymbols = metadataContext.MatchedSymbols
                .Where(symbol => !symbol.StableKey.Contains("|constructed:", StringComparison.Ordinal) &&
                                 !symbol.StableKey.Contains("|reduced:", StringComparison.Ordinal))
                .ToArray(),
        };
    }
}
