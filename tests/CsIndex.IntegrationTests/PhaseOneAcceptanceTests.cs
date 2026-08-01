using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

public sealed class PhaseOneAcceptanceTests(SemanticIndexFixture fixture)
    : IClassFixture<SemanticIndexFixture>
{
    [Fact]
    public async Task OverloadResolutionAndTypeIdentityAreSemantic()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var noArguments = await fixture.Query.FindCallersAsync(
            "Alpha.AClass::Play()", GeneratedFilter.Include, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);
        var stringArgument = await fixture.Query.FindCallersAsync(
            "Alpha.AClass::Play(string)", GeneratedFilter.Include, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);
        var bClass = await fixture.Query.FindCallersAsync(
            "Alpha.BClass::Play()", GeneratedFilter.Include, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);

        Assert.Equal(2, noArguments.Calls.Count);
        Assert.Single(stringArgument.Calls);
        Assert.Single(bClass.Calls);
        Assert.All(noArguments.Calls, call => Assert.Contains("AClass::Play", call.CalleeDefinitionDisplayName));
        Assert.All(bClass.Calls, call => Assert.Contains("BClass::Play", call.CalleeDefinitionDisplayName));
    }

    [Fact]
    public async Task DefinitionAtInvocationFindsCorrectOverload()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindDefinitionAtAsync(
            fixture.GetLocation("a.Play()"),
            cancellationToken: TestContext.Current.CancellationToken);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("Alpha.AClass::Play()", definition.DisplayName);
        Assert.NotNull(definition.DocumentPath);
    }

    [Fact]
    public async Task NamespaceOmissionExpandsAllCandidates()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindSymbolsAsync(
            "Player::Play()",
            sourceOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.MatchedSymbols.Count);
        Assert.Contains(result.MatchedSymbols, symbol => symbol.DisplayName == "GameNS.Player::Play()");
        Assert.Contains(result.MatchedSymbols, symbol => symbol.DisplayName == "PianoNS.Player::Play()");
    }

    [Fact]
    public async Task CommentsDoNotCreateCalls()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindCallersAsync(
            "Alpha.CommentPlayer::Play()", GeneratedFilter.Include, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Calls);
    }

    [Fact]
    public async Task PhysicalGeneratedCodeCanBeIncludedOrExcluded()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var included = await fixture.Query.FindCallersAsync(
            "GameNS.Player::Play()", GeneratedFilter.Include, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);
        var excluded = await fixture.Query.FindCallersAsync(
            "GameNS.Player::Play()", GeneratedFilter.Exclude, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);
        var only = await fixture.Query.FindCallersAsync(
            "GameNS.Player::Play()", GeneratedFilter.Only, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);

        Assert.Single(included.Calls);
        Assert.Empty(excluded.Calls);
        Assert.Single(only.Calls);
        Assert.True(only.Calls[0].IsGenerated);
    }

    [Fact]
    public async Task LambdaAndLocalFunctionAreIndependentCallers()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var lambda = await fixture.Query.FindCallersAsync(
            "Alpha.LambdaPlayer::Play()", GeneratedFilter.Include, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);
        var local = await fixture.Query.FindCallersAsync(
            "Alpha.LocalPlayer::Play()", GeneratedFilter.Include, DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);

        Assert.Single(lambda.Calls);
        Assert.Contains("<lambda#1>", lambda.Calls[0].CallerDisplayName);
        Assert.Single(local.Calls);
        Assert.Contains("Local", local.Calls[0].CallerDisplayName);
    }

    [Fact]
    public async Task ListSymbolsDefaultsToFunctionKindsAndCanLimitToLambdas()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var functions = await fixture.Query.ListSymbolsAsync(
            kind: null,
            asyncInvolved: false,
            cancellationToken: cancellationToken);
        var lambdas = await fixture.Query.ListSymbolsAsync(
            IndexedSymbolKind.Lambda,
            asyncInvolved: false,
            cancellationToken: cancellationToken);

        Assert.Contains(functions.MatchedSymbols, symbol =>
            symbol.DisplayName == "Alpha.AClass::Play()" && symbol.Kind == IndexedSymbolKind.Method);
        Assert.Contains(functions.MatchedSymbols, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda && symbol.TypeSimpleName == "LambdaPlayer");
        Assert.All(functions.MatchedSymbols, symbol =>
            Assert.True(symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda));
        Assert.NotEmpty(lambdas.MatchedSymbols);
        Assert.All(lambdas.MatchedSymbols, symbol => Assert.Equal(IndexedSymbolKind.Lambda, symbol.Kind));
    }

    [Fact]
    public async Task ListSymbolsAsyncInvolvedExcludesNonInvolvedSymbols()
    {
        await fixture.BuildTask;

        var symbols = await fixture.Query.ListSymbolsAsync(
            kind: null,
            asyncInvolved: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(symbols.MatchedSymbols, symbol => symbol.Name == "ExecuteAsync");
        Assert.Contains(symbols.MatchedSymbols, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda && symbol.TypeSimpleName == "AsyncPlayer");
        Assert.DoesNotContain(symbols.MatchedSymbols, symbol =>
            symbol.DisplayName == "Alpha.AsyncPlayer::Sync()");
        Assert.All(symbols.MatchedSymbols, symbol => Assert.NotNull(symbol.AsyncInvolvementDepth));
    }

    [Fact]
    public async Task CalleesIncludeNestedLambdaCallsByDefault()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindCalleesAsync(
            "Alpha.DescendantCallees::Execute()",
            GeneratedFilter.Include,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Calls.Count);
        Assert.Contains(result.Calls, call => call.CalleeDefinitionDisplayName?.Contains("DirectCall") == true);
        Assert.Contains(result.Calls, call => call.CalleeDefinitionDisplayName?.Contains("OuterLambdaCall") == true);
        Assert.Contains(result.Calls, call => call.CalleeDefinitionDisplayName?.Contains("InnerCreated::.ctor") == true);
        Assert.Contains(result.Calls, call => call.CalleeDefinitionDisplayName?.Contains("FirstNestedLambdaCall") == true);
        Assert.Contains(result.Calls, call => call.CalleeDefinitionDisplayName?.Contains("SecondNestedLambdaCall") == true);
    }

    [Fact]
    public async Task CalleesCanBeLimitedToDirectCalls()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindCalleesAsync(
            "Alpha.DescendantCallees::Execute()",
            GeneratedFilter.Include,
            includeLambdaCalls: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var call = Assert.Single(result.Calls);
        Assert.Contains("DirectCall", call.CalleeDefinitionDisplayName);
    }

    [Fact]
    public async Task ExtensionAndConstructedGenericTargetsRetainOriginalDefinitions()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var extension = await fixture.Query.FindCallersAsync(
            "Alpha.PlayerExtensions::PlayExt(Alpha.Player)",
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            cancellationToken: cancellationToken);
        var generic = await fixture.Query.FindReferencesAsync(
            "Alpha.Converter::Convert(System.Object)",
            GeneratedFilter.Include,
            cancellationToken: cancellationToken);

        Assert.Single(extension.Calls);
        Assert.Contains("PlayerExtensions::PlayExt", extension.Calls[0].CalleeDefinitionDisplayName);
        Assert.Equal(2, generic.Calls.Count(call => call.ReferenceKind == ReferenceKind.Invocation));
        Assert.Contains(generic.Calls, call => call.CalleeDisplayName?.Contains("System.Int32", StringComparison.Ordinal) == true);
        Assert.Contains(generic.Calls, call => call.CalleeDisplayName?.Contains("System.String", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ConstructorsMethodGroupsAndNameOfAreClassifiedSeparately()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var constructor = await fixture.Query.FindCallersAsync(
            "Alpha.AClass::.ctor()",
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            cancellationToken: cancellationToken);
        var references = await fixture.Query.FindReferencesAsync(
            "Alpha.ReferenceKinds::Target()",
            GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        var callers = await fixture.Query.FindCallersAsync(
            "Alpha.ReferenceKinds::Target()",
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            cancellationToken: cancellationToken);

        Assert.Single(constructor.Calls);
        Assert.Contains(references.Calls, call => call.ReferenceKind is ReferenceKind.MethodGroup or ReferenceKind.DelegateCreation);
        Assert.Contains(references.Calls, call => call.ReferenceKind == ReferenceKind.NameOf);
        Assert.Empty(callers.Calls);
    }

    [Fact]
    public async Task OverridesAndVirtualDispatchCandidatesAreRecorded()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var overrides = await fixture.Query.FindOverridesAsync(
            "Alpha.BaseClass::Run()",
            cancellationToken: cancellationToken);
        var callers = await fixture.Query.FindCallersAsync(
            "Alpha.BaseClass::Run()", GeneratedFilter.Include, DispatchSearchMode.Virtual, CallerScope.Direct,
            cancellationToken: cancellationToken);

        Assert.Equal(2, overrides.Relations.Count);
        Assert.Single(callers.Calls);
        Assert.Equal(2, callers.PossibleRuntimeTargets.Count);
    }

    [Fact]
    public async Task DirectoryProfileIndexesOnlyActiveConditionalBranch()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var callees = await fixture.Query.FindCalleesAsync(
            "Alpha.PlatformPlayer::Execute()",
            GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        var conditions = await fixture.Query.GetConditionsAsync(cancellationToken: cancellationToken);

        Assert.Single(callees.Calls);
        Assert.Contains("PlayWindows", callees.Calls[0].CalleeDefinitionDisplayName);
        Assert.DoesNotContain(callees.Calls, call => call.CalleeDefinitionDisplayName?.Contains("PlayOther") == true);
        Assert.Contains(conditions.Symbols, symbol => symbol.SymbolName == "WINDOWS" && symbol.IsDefined);
        Assert.Contains("WINDOWS", conditions.Profile.PreprocessorSymbols);
        Assert.DoesNotContain("NET10_0", conditions.Profile.PreprocessorSymbols);
    }

    [Fact]
    public async Task CacheAndQueriesUsePersistedDatabase()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = new IndexOptions { InputPath = fixture.RootPath, ForcedMode = InputMode.Directory };
        var coordinator = CsIndex.Core.Analysis.AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(input, options, cancellationToken);
        var requestHash = CsIndex.Core.Caching.RequestHasher.Build(input, options);
        var index = new SqliteIndex(fixture.DatabasePath);

        Assert.True(await index.IsCacheValidAsync(fixture.RootPath, fingerprint, requestHash, cancellationToken));
        var result = await fixture.Query.FindDefinitionsAsync(
            "Alpha.AClass::Play()",
            cancellationToken: cancellationToken);
        Assert.Single(result.Definitions);
    }
}
