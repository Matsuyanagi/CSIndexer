using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;
using Microsoft.CodeAnalysis;

namespace CsIndex.IntegrationTests;

public sealed class FunctionTargetFilterTests(SemanticIndexFixture fixture)
    : IClassFixture<SemanticIndexFixture>
{
    [Fact]
    public async Task SearchSymbols_ExplicitAllMatchesOmissionAndDirectStatusExcludesTypes()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var omitted = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AsyncPlayer::*"),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var explicitAll = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AsyncPlayer::*", AsyncStatusFilter.All),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var asyncOnly = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AsyncPlayer::*", AsyncStatusFilter.Async),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var syncOnly = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AsyncPlayer::*", AsyncStatusFilter.Sync),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(omitted.MatchedSymbols.Select(symbol => symbol.Id), explicitAll.MatchedSymbols.Select(symbol => symbol.Id));
        Assert.NotEmpty(explicitAll.MatchedSymbols);
        Assert.All(explicitAll.MatchedSymbols, symbol => Assert.NotEqual(IndexedSymbolKind.Type, symbol.Kind));
        Assert.NotEmpty(asyncOnly.MatchedSymbols);
        Assert.NotEmpty(syncOnly.MatchedSymbols);
        Assert.All(asyncOnly.MatchedSymbols, symbol => Assert.NotEqual(AsyncRole.None, symbol.AsyncRole));
        Assert.All(syncOnly.MatchedSymbols, symbol => Assert.Equal(AsyncRole.None, symbol.AsyncRole));
    }

    [Fact]
    public async Task ListSymbols_ExplicitAllMatchesOmissionAndComposesSyncWithAsyncInvolvement()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var omitted = await fixture.Query.ListSymbolsAsync(
            kind: null,
            asyncInvolved: false,
            cancellationToken: cancellationToken);
        var explicitAll = await fixture.Query.ListSymbolsAsync(
            kind: null,
            asyncStatus: AsyncStatusFilter.All,
            asyncInvolved: false,
            cancellationToken: cancellationToken);
        var syncInvolved = await fixture.Query.ListSymbolsAsync(
            kind: null,
            asyncStatus: AsyncStatusFilter.Sync,
            asyncInvolved: true,
            cancellationToken: cancellationToken);

        Assert.Equal(omitted.MatchedSymbols.Select(symbol => symbol.Id), explicitAll.MatchedSymbols.Select(symbol => symbol.Id));
        Assert.Contains(syncInvolved.MatchedSymbols, symbol => FormatPath(symbol) == "Alpha.AsyncGraph::Start()");
        Assert.All(syncInvolved.MatchedSymbols, symbol =>
        {
            Assert.True(symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda);
            Assert.Equal(AsyncRole.None, symbol.AsyncRole);
            Assert.NotNull(symbol.AsyncInvolvementDepth);
        });
    }

    [Theory]
    [InlineData("Alpha.FunctionKinds::Regular()", "Alpha.FunctionKinds::Regular()", MethodKind.Ordinary)]
    [InlineData("Alpha.FunctionKinds::[constructor]()", "Alpha.FunctionKinds::[constructor]()", MethodKind.Constructor)]
    [InlineData("Alpha.FunctionKinds::LocalOwner().Local()", "Alpha.FunctionKinds::LocalOwner().Local()", MethodKind.LocalFunction)]
    [InlineData("Alpha.FunctionKinds::[get:Value]()", "Alpha.FunctionKinds::[get:Value]()", MethodKind.PropertyGet)]
    [InlineData(
        "Alpha.FunctionKinds::[operator:+](Alpha.FunctionKinds,Alpha.FunctionKinds)",
        "Alpha.FunctionKinds::[operator:+](Alpha.FunctionKinds,Alpha.FunctionKinds)",
        MethodKind.UserDefinedOperator)]
    [InlineData(
        "Alpha.FunctionKinds::[conversion:implicit:int](Alpha.FunctionKinds)",
        "Alpha.FunctionKinds::[conversion:implicit:int](Alpha.FunctionKinds)",
        MethodKind.Conversion)]
    public async Task MethodKindFilter_IncludesEveryExecutableStoredAsMethod(
        string query,
        string expectedDisplayName,
        MethodKind expectedMethodKind)
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindDefinitionsAsync(
            query,
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.All),
            cancellationToken: TestContext.Current.CancellationToken);

        var definition = Assert.Single(result.Definitions).Symbol;
        Assert.Equal(expectedDisplayName, FormatPath(definition));
        Assert.Equal(IndexedSymbolKind.Method, definition.Kind);
        Assert.Equal((int)expectedMethodKind, definition.MethodKind);
    }

    [Fact]
    public async Task CanonicalParameterQueryMatchesDisplaySpellingWhileTypeKeyRemainsOpaque()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = await fixture.Repository.GetProfileAsync(cancellationToken: cancellationToken);
        var stored = Assert.Single(await fixture.Repository.FindLogicalSymbolCandidatesAsync(
            profile.Id,
            name: "Select",
            typeSimpleName: "HidingMiddle",
            kind: IndexedSymbolKind.Method,
            sourceOnly: true,
            cancellationToken));
        var parameter = Assert.Single(stored.Parameters);
        Assert.Equal("System::String", parameter.TypeKey);
        Assert.Equal("string", parameter.TypeDisplay);

        var result = await fixture.Query.FindDefinitionsAsync(
            "Alpha.HidingMiddle::Select(string)",
            cancellationToken: cancellationToken);

        Assert.Equal(stored.Id, Assert.Single(result.Definitions).Symbol.Id);
    }

    [Fact]
    public async Task CanonicalLambdaQueryResolvesCanonicalSemanticPath()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.ShowSourceAsync(
            "Alpha.LambdaPlayer::Execute().<lambda#1>",
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "Alpha.LambdaPlayer::Execute().<lambda#1>",
            FormatPath(Assert.Single(result.MatchedSymbols)));
    }

    [Fact]
    public async Task SourceQueries_FilterOnlyTheSourceBackedResultSymbols()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        const string lambdaQuery = "Alpha.LambdaPlayer::Execute().<lambda#1>";

        var shownLambda = await fixture.Query.ShowSourceAsync(
            lambdaQuery,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var hiddenByMethodFilter = await fixture.Query.ShowSourceAsync(
            lambdaQuery,
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var searchedLambdas = await fixture.Query.SearchSourceAsync(
            Request(
                selector: null,
                kind: IndexedSymbolKind.Lambda,
                includes: ["LambdaTarget()"]),
            cancellationToken: cancellationToken);

        var lambda = Assert.Single(shownLambda.MatchedSymbols);
        Assert.Equal("Alpha.LambdaPlayer::Execute().<lambda#1>", FormatPath(lambda));
        Assert.NotNull(lambda.NormalizedSource);
        Assert.Empty(hiddenByMethodFilter.MatchedSymbols);
        Assert.NotEmpty(searchedLambdas.MatchedSymbols);
        Assert.All(searchedLambdas.MatchedSymbols, symbol => Assert.Equal(IndexedSymbolKind.Lambda, symbol.Kind));
        Assert.Contains(
            searchedLambdas.MatchedSymbols,
            symbol => FormatPath(symbol) == "Alpha.CallerGraph::LambdaOwner().<lambda#1>");
    }

    [Fact]
    public async Task DefinitionQueries_FilterTheResolvedDefinitionOnly()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var lambdaDefinition = await fixture.Query.FindDefinitionsAsync(
            "Alpha.LambdaPlayer::Execute().<lambda#1>",
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var omitted = await fixture.Query.FindDefinitionsAsync(
            "Alpha.AClass::Play()",
            cancellationToken: cancellationToken);
        var explicitAll = await fixture.Query.FindDefinitionsAsync(
            "Alpha.AClass::Play()",
            filter: new(null, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var methodAt = await fixture.Query.FindDefinitionAtAsync(
            fixture.GetLocation("a.Play()"),
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var lambdaAt = await fixture.Query.FindDefinitionAtAsync(
            fixture.GetLocation("a.Play()"),
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);

        Assert.NotEmpty(lambdaDefinition.Definitions);
        Assert.All(lambdaDefinition.Definitions, row => Assert.Equal(IndexedSymbolKind.Lambda, row.Symbol.Kind));
        Assert.Equal(omitted.Definitions.Select(row => row.Symbol.Id), explicitAll.Definitions.Select(row => row.Symbol.Id));
        Assert.Equal("Alpha.AClass::Play()", FormatPath(Assert.Single(methodAt.Definitions).Symbol));
        Assert.Empty(lambdaAt.Definitions);
    }

    [Fact]
    public async Task ReferenceAndCallerFilters_ApplyToTheCalleeTargetNotItsLambdaCaller()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var filter = new FunctionTargetFilter(IndexedSymbolKind.Method, AsyncStatusFilter.Sync);

        var references = await fixture.Query.FindReferencesAsync(
            "Alpha.LambdaPlayer::Play()",
            GeneratedFilter.Include,
            filter: filter,
            cancellationToken: cancellationToken);
        var callers = await fixture.Query.FindCallersAsync(
            "Alpha.LambdaPlayer::Play()",
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            filter: filter,
            cancellationToken: cancellationToken);
        var lambdaReferences = await fixture.Query.FindReferencesAsync(
            "Alpha.LambdaPlayer::Execute().<lambda#1>",
            GeneratedFilter.Include,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);

        Assert.Contains(references.Calls, call =>
            FormatPath(references.SymbolsById[call.CallerSymbolId]) ==
            "Alpha.LambdaPlayer::Execute().<lambda#1>");
        var caller = Assert.Single(callers.EffectiveCallers);
        Assert.Equal(IndexedSymbolKind.Lambda, caller.Kind);
        Assert.Equal("Alpha.LambdaPlayer::Execute().<lambda#1>", FormatPath(caller));
        Assert.Empty(lambdaReferences.Calls);
    }

    [Fact]
    public async Task CalleeFilter_AppliesToTheLambdaRootNotReturnedMethodCallees()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var lambdaCallees = await fixture.Query.FindCalleesAsync(
            "Alpha.LambdaPlayer::Execute().<lambda#1>",
            GeneratedFilter.Include,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var excludedRoot = await fixture.Query.FindCalleesAsync(
            "Alpha.LambdaPlayer::Execute().<lambda#1>",
            GeneratedFilter.Include,
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);

        Assert.Equal(IndexedSymbolKind.Lambda, Assert.Single(lambdaCallees.Selection.Roots).Symbol.Kind);
        var callee = Assert.Single(lambdaCallees.Calls);
        Assert.Equal(
            "Alpha.LambdaPlayer::Play()",
            FormatPath(lambdaCallees.SymbolsById[callee.CalleeDefinitionId!.Value]));
        Assert.Empty(excludedRoot.Selection.Roots);
        Assert.Empty(excludedRoot.Calls);
    }

    [Fact]
    public async Task OverrideExpansion_FiltersLogicalRootsBeforeExpansionAndKeepsRelationNodes()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var syncDefinitions = await fixture.Query.FindDefinitionsAsync(
            "Alpha.AsyncOverrideBase::Run()",
            includeOverrides: true,
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.Sync),
            cancellationToken: cancellationToken);
        var asyncDefinitions = await fixture.Query.FindDefinitionsAsync(
            "Alpha.AsyncOverrideBase::Run()",
            includeOverrides: true,
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.Async),
            cancellationToken: cancellationToken);
        var overrideRelations = await fixture.Query.FindOverridesAsync(
            "Alpha.AsyncOverrideBase::Run()",
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.Sync),
            cancellationToken: cancellationToken);

        Assert.Equal(
            ["Alpha.AsyncOverrideBase::Run()", "Alpha.AsyncOverrideDerived::Run()"],
            syncDefinitions.Definitions.Select(row => FormatPath(row.Symbol)));
        Assert.Empty(asyncDefinitions.Definitions);
        Assert.Equal("Alpha.AsyncOverrideBase::Run()", FormatPath(Assert.Single(overrideRelations.Selection.Roots).Symbol));
        Assert.Contains(
            overrideRelations.Relations,
            relation => FormatPath(overrideRelations.SymbolsById[relation.SourceSymbolId]) ==
                "Alpha.AsyncOverrideDerived::Run()");

        var includeOverridesError = await Assert.ThrowsAsync<SymbolQueryParseException>(() =>
            fixture.Query.FindDefinitionsAsync(
                "Alpha.AsyncOverrideBase::Run()",
                includeOverrides: true,
                filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
                cancellationToken: cancellationToken));
        var overridesError = await Assert.ThrowsAsync<SymbolQueryParseException>(() =>
            fixture.Query.FindOverridesAsync(
                "Alpha.AsyncOverrideBase::Run()",
                filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
                cancellationToken: cancellationToken));

        Assert.Contains("--kind lambda", includeOverridesError.Message, StringComparison.Ordinal);
        Assert.Contains("--include-overrides", includeOverridesError.Message, StringComparison.Ordinal);
        Assert.Equal("--kind lambda is not applicable to overrides.", overridesError.Message);
    }

    [Fact]
    public async Task CallResultHydratesTheCompleteEndpointUnionInExactlyOneBatch()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var hydrationBatches = new List<long[]>();
        var query = new SemanticQueryService(fixture.Repository)
        {
            EndpointHydrationObserver = ids =>
                hydrationBatches.Add(ids.Order().ToArray()),
        };

        var result = await query.FindCallersAsync(
            "Alpha.BaseClass::Run()",
            GeneratedFilter.Include,
            DispatchSearchMode.Virtual,
            CallerScope.Both,
            cancellationToken: cancellationToken);

        var expectedIds = result.Calls
            .SelectMany(call => new long?[]
            {
                call.CallerSymbolId,
                call.CallerContainingSymbolId,
                call.CalleeSymbolId,
                call.CalleeDefinitionId,
            })
            .Concat(result.PossibleRuntimeTargets.SelectMany(relation =>
                new long?[] { relation.SourceSymbolId, relation.TargetSymbolId }))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(expectedIds, Assert.Single(hydrationBatches));
        Assert.All(expectedIds, id => Assert.True(result.SymbolsById.ContainsKey(id)));
    }

    [Fact]
    public async Task RelationResultHydratesTheCompleteEndpointUnionInExactlyOneBatch()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var hydrationBatches = new List<long[]>();
        var query = new SemanticQueryService(fixture.Repository)
        {
            EndpointHydrationObserver = ids =>
                hydrationBatches.Add(ids.Order().ToArray()),
        };

        var result = await query.FindOverridesAsync(
            "Alpha.BaseClass::Run()",
            cancellationToken: cancellationToken);
        var expectedIds = result.Relations
            .SelectMany(relation => new[] { relation.SourceSymbolId, relation.TargetSymbolId })
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(expectedIds, Assert.Single(hydrationBatches));
        Assert.All(expectedIds, id => Assert.True(result.SymbolsById.ContainsKey(id)));
    }

    private static SymbolSelectionRequest Request(string? selector) =>
        Request(selector, AsyncStatusFilter.All, asyncStatusSpecified: false);

    private static SymbolSelectionRequest Request(
        string? selector,
        AsyncStatusFilter asyncStatus) =>
        Request(selector, asyncStatus, asyncStatusSpecified: true);

    private static SymbolSelectionRequest Request(
        string? selector,
        AsyncStatusFilter asyncStatus,
        bool asyncStatusSpecified) => new(
        selector,
        Conditions: [],
        Case: new SymbolCaseOptions(),
        FunctionFilter: new FunctionTargetFilter(null, asyncStatus),
        KindSpecified: false,
        AsyncStatusSpecified: asyncStatusSpecified);

    private static SymbolSelectionRequest Request(
        string? selector,
        IndexedSymbolKind kind,
        IReadOnlyList<string> includes) => new(
        selector,
        Conditions: includes.Select(value =>
            new TypedCondition(ConditionCategory.Include, ConditionSyntax.Glob, value)).ToArray(),
        Case: new SymbolCaseOptions(),
        FunctionFilter: new FunctionTargetFilter(kind, AsyncStatusFilter.All),
        KindSpecified: true,
        AsyncStatusSpecified: false);

    private static string FormatPath(StoredSymbol symbol) =>
        new SymbolPathFormatter().Format(
            Assert.IsType<SymbolPathData>(symbol.Path),
            new SymbolPathFormatOptions());
}
