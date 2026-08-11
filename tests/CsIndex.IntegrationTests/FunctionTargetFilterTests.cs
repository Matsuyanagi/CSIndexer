using CsIndex.Core.Model;
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
            Request("Alpha.AsyncPlayer"),
            fixture.PrimaryProfileName,
            cancellationToken);
        var explicitAll = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AsyncPlayer", asyncStatus: AsyncStatusFilter.All),
            fixture.PrimaryProfileName,
            cancellationToken);
        var asyncOnly = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AsyncPlayer", asyncStatus: AsyncStatusFilter.Async),
            fixture.PrimaryProfileName,
            cancellationToken);
        var syncOnly = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AsyncPlayer", asyncStatus: AsyncStatusFilter.Sync),
            fixture.PrimaryProfileName,
            cancellationToken);

        Assert.Equal(omitted.MatchedSymbols.Select(symbol => symbol.Id), explicitAll.MatchedSymbols.Select(symbol => symbol.Id));
        Assert.Equal(IndexedSymbolKind.Type, Assert.Single(explicitAll.MatchedSymbols).Kind);
        Assert.Empty(asyncOnly.MatchedSymbols);
        Assert.Empty(syncOnly.MatchedSymbols);
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
        Assert.Contains(syncInvolved.MatchedSymbols, symbol => symbol.DisplayName == "Alpha.AsyncGraph::Start()");
        Assert.All(syncInvolved.MatchedSymbols, symbol =>
        {
            Assert.True(symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda);
            Assert.Equal(AsyncRole.None, symbol.AsyncRole);
            Assert.NotNull(symbol.AsyncInvolvementDepth);
        });
    }

    [Theory]
    [InlineData("Alpha.FunctionKinds::Regular()", "Alpha.FunctionKinds::Regular()", MethodKind.Ordinary)]
    [InlineData("Alpha.FunctionKinds::.ctor()", "Alpha.FunctionKinds::.ctor()", MethodKind.Constructor)]
    [InlineData("Alpha.FunctionKinds::Local()", "Alpha.FunctionKinds::Local()", MethodKind.LocalFunction)]
    [InlineData("Alpha.FunctionKinds::get_Value()", "Alpha.FunctionKinds::get_Value()", MethodKind.PropertyGet)]
    [InlineData(
        "Alpha.FunctionKinds::op_Addition(Alpha.FunctionKinds,Alpha.FunctionKinds)",
        "Alpha.FunctionKinds::op_Addition(Alpha.FunctionKinds,Alpha.FunctionKinds)",
        MethodKind.UserDefinedOperator)]
    [InlineData(
        "Alpha.FunctionKinds::op_Implicit(Alpha.FunctionKinds)",
        "Alpha.FunctionKinds::op_Implicit(Alpha.FunctionKinds)",
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

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(expectedDisplayName, definition.DisplayName);
        Assert.Equal(IndexedSymbolKind.Method, definition.Kind);
        Assert.Equal((int)expectedMethodKind, definition.MethodKind);
    }

    [Fact]
    public async Task SourceQueries_FilterOnlyTheSourceBackedResultSymbols()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        const string lambdaQuery = "Alpha.LambdaPlayer::Execute()::<lambda#1>";

        var shownLambda = await fixture.Query.ShowSourceAsync(
            lambdaQuery,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var hiddenByMethodFilter = await fixture.Query.ShowSourceAsync(
            lambdaQuery,
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var searchedLambdas = await fixture.Query.SearchSourceAsync(
            includes: ["LambdaTarget()"],
            excludes: [],
            ignoreCase: false,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);

        var lambda = Assert.Single(shownLambda.MatchedSymbols);
        Assert.Equal(lambdaQuery, lambda.DisplayName);
        Assert.NotNull(lambda.NormalizedSource);
        Assert.Empty(hiddenByMethodFilter.MatchedSymbols);
        Assert.NotEmpty(searchedLambdas.MatchedSymbols);
        Assert.All(searchedLambdas.MatchedSymbols, symbol => Assert.Equal(IndexedSymbolKind.Lambda, symbol.Kind));
        Assert.Contains(
            searchedLambdas.MatchedSymbols,
            symbol => symbol.DisplayName == "Alpha.CallerGraph::LambdaOwner()::<lambda#1>");
    }

    [Fact]
    public async Task DefinitionQueries_FilterTheResolvedDefinitionOnly()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var lambdaDefinition = await fixture.Query.FindDefinitionsAsync(
            "::<lambda#1>",
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
        Assert.All(lambdaDefinition.Definitions, symbol => Assert.Equal(IndexedSymbolKind.Lambda, symbol.Kind));
        Assert.Equal(omitted.Definitions.Select(symbol => symbol.Id), explicitAll.Definitions.Select(symbol => symbol.Id));
        Assert.Equal("Alpha.AClass::Play()", Assert.Single(methodAt.Definitions).DisplayName);
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
            "Alpha.LambdaPlayer::Execute()::<lambda#1>",
            GeneratedFilter.Include,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);

        Assert.Contains(references.Calls, call =>
            call.CallerDisplayName == "Alpha.LambdaPlayer::Execute()::<lambda#1>");
        var caller = Assert.Single(callers.EffectiveCallers);
        Assert.Equal(IndexedSymbolKind.Lambda, caller.Kind);
        Assert.Equal("Alpha.LambdaPlayer::Execute()::<lambda#1>", caller.DisplayName);
        Assert.Empty(lambdaReferences.Calls);
    }

    [Fact]
    public async Task CalleeFilter_AppliesToTheLambdaRootNotReturnedMethodCallees()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var lambdaCallees = await fixture.Query.FindCalleesAsync(
            "Alpha.LambdaPlayer::Execute()::<lambda#1>",
            GeneratedFilter.Include,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);
        var excludedRoot = await fixture.Query.FindCalleesAsync(
            "Alpha.LambdaPlayer::Execute()::<lambda#1>",
            GeneratedFilter.Include,
            filter: new(IndexedSymbolKind.Method, AsyncStatusFilter.All),
            cancellationToken: cancellationToken);

        Assert.Equal(IndexedSymbolKind.Lambda, Assert.Single(lambdaCallees.Context.MatchedSymbols).Kind);
        var callee = Assert.Single(lambdaCallees.Calls);
        Assert.Equal("Alpha.LambdaPlayer::Play()", callee.CalleeDefinitionDisplayName);
        Assert.Empty(excludedRoot.Context.MatchedSymbols);
        Assert.Empty(excludedRoot.Calls);
    }

    [Fact]
    public async Task OverrideExpansion_FiltersRealTargetsAfterExpansionAndKeepsRelationNodes()
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

        Assert.Equal(["Alpha.AsyncOverrideBase::Run()"], syncDefinitions.Definitions.Select(symbol => symbol.DisplayName));
        Assert.Equal(["Alpha.AsyncOverrideDerived::Run()"], asyncDefinitions.Definitions.Select(symbol => symbol.DisplayName));
        Assert.Equal("Alpha.AsyncOverrideBase::Run()", Assert.Single(overrideRelations.Context.MatchedSymbols).DisplayName);
        Assert.Contains(
            overrideRelations.Relations,
            relation => relation.SourceDisplayName == "Alpha.AsyncOverrideDerived::Run()");

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

    private static SymbolSearchRequest Request(
        string? pattern,
        AsyncStatusFilter asyncStatus = AsyncStatusFilter.All) => new(
        pattern,
        NamespacePattern: null,
        TypePattern: null,
        MethodPattern: null,
        Kind: null,
        UseRegex: false,
        IgnoreCase: false,
        Includes: [],
        Excludes: [],
        ShowSource: false,
        asyncStatus);
}
