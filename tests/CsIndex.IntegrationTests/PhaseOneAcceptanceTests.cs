using CsIndex.Core.Caching;
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
    public async Task IncludeOverridesExpandsInterfaceAndConcreteTargetsDownward()
    {
        await fixture.BuildTask;
        var interfaceResult = await fixture.Query.FindSymbolsAsync(
            "Alpha.IPlayable::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ["Alpha.D2::Play()", "Alpha.Game::Play()", "Alpha.IPlayable::Play()",
             "Alpha.InheritedBase::Play()", "Alpha.Pianist::Play()", "Alpha.ProPianist::Play()"],
            interfaceResult.MatchedSymbols.Select(symbol => symbol.DisplayName).Order(StringComparer.Ordinal));

        var concreteResult = await fixture.Query.FindSymbolsAsync(
            "Alpha.Pianist::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ["Alpha.Pianist::Play()", "Alpha.ProPianist::Play()"],
            concreteResult.MatchedSymbols.Select(symbol => symbol.DisplayName).Order());
    }

    [Fact]
    public async Task IncludeOverridesResolvesInheritedAliasWithinReceiverBranch()
    {
        await fixture.BuildTask;
        var result = await fixture.Query.FindDefinitionsAsync(
            "Alpha.D1::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ["Alpha.D2::Play()", "Alpha.InheritedBase::Play()"],
            result.Definitions.Select(symbol => symbol.DisplayName).Order());
        Assert.DoesNotContain(result.Definitions, symbol => symbol.TypeSimpleName == "D1");
        Assert.DoesNotContain(result.Definitions, symbol => symbol.TypeSimpleName == "OtherBranch");
    }

    [Fact]
    public async Task DerivedInterfaceAliasDoesNotIncludeBaseInterfaceSiblingImplementations()
    {
        await fixture.BuildTask;
        var result = await fixture.Query.FindSymbolsAsync(
            "Alpha.IAdvancedPlayable::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(result.MatchedSymbols, symbol => symbol.TypeSimpleName == "InheritedBase");
        Assert.Contains(result.MatchedSymbols, symbol => symbol.TypeSimpleName == "D2");
        Assert.DoesNotContain(result.MatchedSymbols, symbol => symbol.TypeSimpleName == "Game");
    }

    [Fact]
    public async Task InheritedAliasRequiresOverrideExpansion()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindSymbolsAsync(
            "Alpha.D1::Play()",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.MatchedSymbols);
    }

    [Fact]
    public async Task HidingMethodResolvesToItsRealDeclarationOnly()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindDefinitionsAsync(
            "Alpha.HidingPlayer::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("Alpha.HidingPlayer::Play()", definition.DisplayName);
    }

    [Fact]
    public async Task SameNameDeclarationSuppressesDeeperBaseOverloads()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var visible = await fixture.Query.FindDefinitionsAsync(
            "Alpha.HidingLeaf::Select(string)", includeOverrides: true,
            cancellationToken: cancellationToken);
        var hidden = await fixture.Query.FindDefinitionsAsync(
            "Alpha.HidingLeaf::Select(int)", includeOverrides: true,
            cancellationToken: cancellationToken);

        var definition = Assert.Single(visible.Definitions);
        Assert.Equal("Alpha.HidingMiddle::Select(System.String)", definition.DisplayName);
        Assert.Empty(hidden.Definitions);
    }

    [Fact]
    public async Task IncludeOverridesRejectsTypeQueries()
    {
        await fixture.BuildTask;

        var exception = await Assert.ThrowsAsync<CsIndex.Query.Symbols.SymbolQueryParseException>(
            () => fixture.Query.FindSymbolsAsync(
                "Alpha.D1", includeOverrides: true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("--include-overrides requires a method query.", exception.Message);
    }

    [Fact]
    public async Task OverrideAwareCallQueriesUseExpandedAndExactTargets()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var interfaceCallers = await fixture.Query.FindCallersAsync(
            "Alpha.IPlayable::Play()", GeneratedFilter.Include,
            DispatchSearchMode.Static, CallerScope.Direct,
            includeOverrides: true,
            cancellationToken: cancellationToken);
        Assert.Contains(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("IPlayable"));
        Assert.Contains(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("Pianist"));
        Assert.Contains(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("Game"));
        Assert.DoesNotContain(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("Baseball"));

        var concreteCallers = await fixture.Query.FindCallersAsync(
            "Alpha.Pianist::Play()", GeneratedFilter.Include,
            DispatchSearchMode.Static, CallerScope.Direct,
            includeOverrides: true,
            cancellationToken: cancellationToken);
        Assert.DoesNotContain(concreteCallers.Calls, call =>
            call.CalleeDefinitionDisplayName!.Contains("IPlayable"));
        Assert.DoesNotContain(concreteCallers.Calls, call =>
            call.CalleeDefinitionDisplayName!.Contains("Game"));

        var exactCallers = await fixture.Query.FindCallersAsync(
            "Alpha.Pianist::Play()", GeneratedFilter.Include,
            DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);
        Assert.DoesNotContain(exactCallers.Calls, call =>
            call.CalleeDefinitionDisplayName!.Contains("ProPianist"));

        var references = await fixture.Query.FindReferencesAsync(
            "Alpha.Pianist::Play()", GeneratedFilter.Include,
            includeOverrides: true,
            cancellationToken: cancellationToken);
        Assert.Contains(references.Calls, call => call.CalleeDefinitionDisplayName!.Contains("ProPianist"));

        var callees = await fixture.Query.FindCalleesAsync(
            "Alpha.D1::Play()", GeneratedFilter.Include,
            includeOverrides: true,
            cancellationToken: cancellationToken);
        Assert.Contains(callees.Calls, call => call.CalleeDefinitionDisplayName!.Contains("BaseBody"));
        Assert.Contains(callees.Calls, call => call.CalleeDefinitionDisplayName!.Contains("D2Body"));
        Assert.DoesNotContain(callees.Calls, call => call.CalleeDefinitionDisplayName!.Contains("OtherBody"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalFunctionExactTargetWinsOverInheritedSameNameMethod(bool includeOverrides)
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var symbols = await fixture.Query.FindSymbolsAsync(
            "Alpha.LocalPlayer::Local()",
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var definitions = await fixture.Query.FindDefinitionsAsync(
            "Alpha.LocalPlayer::Local()",
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var references = await fixture.Query.FindReferencesAsync(
            "Alpha.LocalPlayer::Local()",
            GeneratedFilter.Include,
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var callers = await fixture.Query.FindCallersAsync(
            "Alpha.LocalPlayer::Local()",
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var callees = await fixture.Query.FindCalleesAsync(
            "Alpha.LocalPlayer::Local()",
            GeneratedFilter.Include,
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);

        Assert.Equal("Alpha.LocalPlayer::Local()", Assert.Single(symbols.MatchedSymbols).DisplayName);
        Assert.Equal("Alpha.LocalPlayer::Local()", Assert.Single(definitions.Definitions).DisplayName);
        Assert.Equal("Alpha.LocalPlayer::Local()", Assert.Single(references.Context.MatchedSymbols).DisplayName);
        Assert.Equal("Alpha.LocalPlayer::Local()", Assert.Single(callers.Context.MatchedSymbols).DisplayName);
        Assert.Equal("Alpha.LocalPlayer::Local()", Assert.Single(callees.Context.MatchedSymbols).DisplayName);
        Assert.Single(references.Calls);
        Assert.Single(callers.Calls);
        var callee = Assert.Single(callees.Calls);
        Assert.Contains("LocalPlayer::Play", callee.CalleeDefinitionDisplayName);
        Assert.DoesNotContain("InheritedLocalBody", callee.CalleeDefinitionDisplayName);
    }

    [Fact]
    public async Task OverrideAwareExactTargetsSuppressInheritedFallbackPerReceiverTypeId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "csindex-duplicate-receiver-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "index.sqlite");
            var index = new SqliteIndex(databasePath);
            await index.SaveAsync(CreateDuplicateReceiverSnapshot(root), cancellationToken);
            var query = new SemanticQueryService(index.CreateQueryRepository());

            var result = await query.FindSymbolsAsync(
                "Duplicate.Receiver::Local()",
                includeOverrides: true,
                cancellationToken: cancellationToken);
            var cyclicResult = await query.FindSymbolsAsync(
                    "Duplicate.Receiver::CycleLocal()",
                    includeOverrides: true,
                    cancellationToken: cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Equal(
                ["Duplicate.LocalBaseB::Local()", "Duplicate.Receiver::Local()"],
                result.MatchedSymbols.Select(symbol => symbol.DisplayName));
            Assert.DoesNotContain(
                result.MatchedSymbols,
                symbol => symbol.DisplayName == "Duplicate.LocalBaseA::Local()");
            Assert.Equal(
                "Duplicate.Receiver::CycleLocal()",
                Assert.Single(cyclicResult.MatchedSymbols).DisplayName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

    private static IndexSnapshot CreateDuplicateReceiverSnapshot(string root)
    {
        var snapshot = new IndexSnapshot
        {
            InputRoot = root,
            InputFingerprint = HashUtilities.Sha256("duplicate-receiver-input"),
            RequestHash = HashUtilities.Sha256("duplicate-receiver-request"),
            Profile = new AnalysisProfileData
            {
                Name = "duplicate-receiver",
                InputMode = InputMode.Solution,
                OperatingSystem = "Windows",
                Architecture = "x64",
                PreprocessorSymbols = [],
                ProfileHash = HashUtilities.Sha256("duplicate-receiver-profile"),
            },
        };

        AddProject("project-a", "AssemblyA", "A.cs");
        AddProject("project-b", "AssemblyB", "B.cs");

        AddType("a-base", "LocalBaseA", "project-a", 0);
        AddMethod("a-base-local", "LocalBaseA", "a-base", "Local", "project-a", 20);
        AddType("a-receiver", "Receiver", "project-a", 40);
        AddMethod("a-owner", "Receiver", "a-receiver", "Execute", "project-a", 60);
        AddMethod("a-local", "Receiver", "a-owner", "Local", "project-a", 80);
        AddMethod("cycle-owner-a", "Receiver", "cycle-owner-b", "CycleOwnerA", "project-a", 100);
        AddMethod("cycle-owner-b", "Receiver", "cycle-owner-a", "CycleOwnerB", "project-a", 120);
        AddMethod("cycle-local", "Receiver", "cycle-owner-a", "CycleLocal", "project-a", 140);
        AddRelation("a-receiver", "a-base");

        AddType("b-base", "LocalBaseB", "project-b", 0);
        AddMethod("b-base-local", "LocalBaseB", "b-base", "Local", "project-b", 20);
        AddType("b-receiver", "Receiver", "project-b", 40);
        AddRelation("b-receiver", "b-base");

        return snapshot;

        void AddProject(string key, string assemblyName, string fileName)
        {
            snapshot.Projects.Add(new ProjectData
            {
                Key = key,
                Name = key,
                AssemblyName = assemblyName,
                Fingerprint = HashUtilities.Sha256(key),
            });
            snapshot.Documents.Add(new DocumentData
            {
                Key = $"{key}|source",
                ProjectKey = key,
                NormalizedPath = Path.Combine(root, fileName),
                ContentHash = HashUtilities.Sha256(fileName),
                IsGenerated = false,
                GenerationKind = GenerationKind.None,
            });
        }

        void AddType(string stableKey, string typeName, string projectKey, int sourceStart)
        {
            snapshot.Symbols[stableKey] = new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = projectKey,
                Kind = IndexedSymbolKind.Type,
                Name = typeName,
                NamespaceName = "Duplicate",
                TypeSimpleName = typeName,
                TypeMetadataName = typeName,
                FullyQualifiedName = $"Duplicate.{typeName}",
                DisplayName = $"Duplicate.{typeName}",
                TypeKind = (int)IndexedTypeKind.Class,
                Accessibility = (int)IndexedAccessibility.Public,
                SourceDocumentKey = $"{projectKey}|source",
                SourceStart = sourceStart,
                SourceLength = typeName.Length,
            };
        }

        void AddMethod(
            string stableKey,
            string typeName,
            string containingSymbolKey,
            string methodName,
            string projectKey,
            int sourceStart)
        {
            snapshot.Symbols[stableKey] = new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = projectKey,
                Kind = IndexedSymbolKind.Method,
                Name = methodName,
                NamespaceName = "Duplicate",
                TypeSimpleName = typeName,
                TypeMetadataName = typeName,
                FullyQualifiedName = $"Duplicate.{typeName}.{methodName}()",
                DisplayName = $"Duplicate.{typeName}::{methodName}()",
                ContainingSymbolKey = containingSymbolKey,
                ParameterCount = 0,
                Accessibility = (int)IndexedAccessibility.Public,
                SourceDocumentKey = $"{projectKey}|source",
                SourceStart = sourceStart,
                SourceLength = methodName.Length,
            };
        }

        void AddRelation(string sourceSymbolKey, string targetSymbolKey)
        {
            snapshot.Relations.Add(new SymbolRelationData
            {
                SourceSymbolKey = sourceSymbolKey,
                TargetSymbolKey = targetSymbolKey,
                RelationKind = SymbolRelationKind.Inherits,
            });
        }
    }
}
