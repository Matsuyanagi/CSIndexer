using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
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
        Assert.All(noArguments.Calls, call =>
            Assert.Contains("AClass::Play", CalleeName(noArguments, call)));
        Assert.All(bClass.Calls, call =>
            Assert.Contains("BClass::Play", CalleeName(bClass, call)));
    }

    [Fact]
    public async Task DefinitionAtInvocationFindsCorrectOverload()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindDefinitionAtAsync(
            fixture.GetLocation("a.Play()"),
            cancellationToken: TestContext.Current.CancellationToken);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("Alpha.AClass::Play()", FormatPath(definition));
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
        Assert.Contains(result.MatchedSymbols, symbol => FormatPath(symbol) == "GameNS.Player::Play()");
        Assert.Contains(result.MatchedSymbols, symbol => FormatPath(symbol) == "PianoNS.Player::Play()");
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
            interfaceResult.MatchedSymbols.Select(symbol => FormatPath(symbol)));

        var concreteResult = await fixture.Query.FindSymbolsAsync(
            "Alpha.Pianist::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ["Alpha.Pianist::Play()", "Alpha.ProPianist::Play()"],
            concreteResult.MatchedSymbols.Select(symbol => FormatPath(symbol)));
    }

    [Fact]
    public async Task IncludeOverridesDoesNotCreateInheritedAliasRoots()
    {
        await fixture.BuildTask;
        var result = await fixture.Query.FindDefinitionsAsync(
            "Alpha.D1::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Definitions);
    }

    [Fact]
    public async Task HidingMethodResolvesToItsRealDeclarationOnly()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindDefinitionsAsync(
            "Alpha.HidingPlayer::Play()", includeOverrides: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("Alpha.HidingPlayer::Play()", FormatPath(definition));
    }

    [Fact]
    public async Task TypeOnlyPositionalSelectorsAreRejectedBeforeOverrideExpansion()
    {
        await fixture.BuildTask;

        var exception = await Assert.ThrowsAsync<CsIndex.Query.Symbols.SymbolQueryParseException>(
            () => fixture.Query.FindSymbolsAsync(
                "Alpha.D1", includeOverrides: true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(
            "Invalid symbol path: expected exactly one or two top-level '::' separators.",
            exception.Message);
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
        Assert.Contains(interfaceCallers.Calls, call => CalleeName(interfaceCallers, call)!.Contains("IPlayable"));
        Assert.Contains(interfaceCallers.Calls, call => CalleeName(interfaceCallers, call)!.Contains("Pianist"));
        Assert.Contains(interfaceCallers.Calls, call => CalleeName(interfaceCallers, call)!.Contains("Game"));
        Assert.DoesNotContain(interfaceCallers.Calls, call => CalleeName(interfaceCallers, call)!.Contains("Baseball"));

        var concreteCallers = await fixture.Query.FindCallersAsync(
            "Alpha.Pianist::Play()", GeneratedFilter.Include,
            DispatchSearchMode.Static, CallerScope.Direct,
            includeOverrides: true,
            cancellationToken: cancellationToken);
        Assert.DoesNotContain(concreteCallers.Calls, call =>
            CalleeName(concreteCallers, call)!.Contains("IPlayable"));
        Assert.DoesNotContain(concreteCallers.Calls, call =>
            CalleeName(concreteCallers, call)!.Contains("Game"));

        var exactCallers = await fixture.Query.FindCallersAsync(
            "Alpha.Pianist::Play()", GeneratedFilter.Include,
            DispatchSearchMode.Static, CallerScope.Direct,
            cancellationToken: cancellationToken);
        Assert.DoesNotContain(exactCallers.Calls, call =>
            CalleeName(exactCallers, call)!.Contains("ProPianist"));

        var references = await fixture.Query.FindReferencesAsync(
            "Alpha.Pianist::Play()", GeneratedFilter.Include,
            includeOverrides: true,
            cancellationToken: cancellationToken);
        Assert.Contains(references.Calls, call => CalleeName(references, call)!.Contains("ProPianist"));

        var callees = await fixture.Query.FindCalleesAsync(
            "Alpha.InheritedBase::Play()", GeneratedFilter.Include,
            includeOverrides: true,
            cancellationToken: cancellationToken);
        Assert.Contains(callees.Calls, call => CalleeName(callees, call)!.Contains("BaseBody"));
        Assert.Contains(callees.Calls, call => CalleeName(callees, call)!.Contains("D2Body"));
        Assert.Contains(callees.Calls, call => CalleeName(callees, call)!.Contains("OtherBody"));
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
        Assert.Contains("<lambda#1>", CallerName(lambda, lambda.Calls[0]));
        Assert.Single(local.Calls);
        Assert.Contains("Local", CallerName(local, local.Calls[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalFunctionExactTargetWinsOverInheritedSameNameMethod(bool includeOverrides)
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var symbols = await fixture.Query.FindSymbolsAsync(
            "Alpha.LocalPlayer::Execute().Local()",
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var definitions = await fixture.Query.FindDefinitionsAsync(
            "Alpha.LocalPlayer::Execute().Local()",
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var references = await fixture.Query.FindReferencesAsync(
            "Alpha.LocalPlayer::Execute().Local()",
            GeneratedFilter.Include,
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var callers = await fixture.Query.FindCallersAsync(
            "Alpha.LocalPlayer::Execute().Local()",
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);
        var callees = await fixture.Query.FindCalleesAsync(
            "Alpha.LocalPlayer::Execute().Local()",
            GeneratedFilter.Include,
            includeOverrides: includeOverrides,
            cancellationToken: cancellationToken);

        const string localPath = "Alpha.LocalPlayer::Execute().Local()";
        Assert.Equal(localPath, FormatPath(Assert.Single(symbols.MatchedSymbols)));
        Assert.Equal(localPath, FormatPath(Assert.Single(definitions.Definitions)));
        Assert.Equal(localPath, FormatPath(Assert.Single(references.Context.MatchedSymbols)));
        Assert.Equal(localPath, FormatPath(Assert.Single(callers.Context.MatchedSymbols)));
        Assert.Equal(localPath, FormatPath(Assert.Single(callees.Context.MatchedSymbols)));
        Assert.Single(references.Calls);
        Assert.Single(callers.Calls);
        var callee = Assert.Single(callees.Calls);
        Assert.Contains("LocalPlayer::Play", CalleeName(callees, callee));
        Assert.DoesNotContain("InheritedLocalBody", CalleeName(callees, callee));
    }

    [Fact]
    public async Task ExactRootsDoNotUseSameNamedReceiverFallbackAndSharedTypeAncestorsRemainValid()
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
            await index.SaveAsync(CreateDuplicateReceiverSnapshot(), cancellationToken);
            var query = new SemanticQueryService(index.CreateQueryRepository());

            var exactResult = await query.FindSymbolsAsync(
                "Duplicate.Receiver::Execute().Local()",
                includeOverrides: true,
                cancellationToken: cancellationToken);
            var sharedAncestorResult = await query.FindSymbolsAsync(
                "Duplicate.Receiver::**.CycleLocal()",
                cancellationToken: cancellationToken);

            Assert.Equal(
                ["Duplicate.Receiver::Execute().Local()"],
                exactResult.MatchedSymbols.Select(symbol => FormatPath(symbol)));
            Assert.DoesNotContain(
                exactResult.MatchedSymbols,
                symbol => symbol.TypeSimpleName is "LocalBaseA" or "LocalBaseB");
            Assert.Equal(
                [
                    "Duplicate.Receiver::CycleOwnerA().CycleLocal()",
                    "Duplicate.Receiver::CycleOwnerB().CycleLocal()",
                ],
                sharedAncestorResult.MatchedSymbols.Select(symbol => FormatPath(symbol)));
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
    public async Task MalformedPersistedContainmentCycleThrowsClearInvariantFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "csindex-containment-cycle-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var index = new SqliteIndex(Path.Combine(root, "index.sqlite"));
            await index.SaveAsync(
                CreateDuplicateReceiverSnapshot(malformedContainmentCycle: true),
                cancellationToken);
            var query = new SemanticQueryService(index.CreateQueryRepository());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => query.FindSymbolsAsync(
                "Duplicate.Receiver::**.CycleLocal()",
                cancellationToken: cancellationToken));

            Assert.Contains(
                "Stored containment chain for candidate symbol ID",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains("cycle detected at symbol ID", exception.Message, StringComparison.Ordinal);
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
            FormatPath(symbol) == "Alpha.AClass::Play()" && symbol.Kind == IndexedSymbolKind.Method);
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
            FormatPath(symbol) == "Alpha.AsyncPlayer::Sync()");
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
        Assert.Contains(result.Calls, call => CalleeName(result, call)?.Contains("DirectCall") == true);
        Assert.Contains(result.Calls, call => CalleeName(result, call)?.Contains("OuterLambdaCall") == true);
        Assert.Contains(result.Calls, call => call.ReferenceKind == ReferenceKind.ObjectCreation);
        Assert.Contains(result.Calls, call => CalleeName(result, call)?.Contains("FirstNestedLambdaCall") == true);
        Assert.Contains(result.Calls, call => CalleeName(result, call)?.Contains("SecondNestedLambdaCall") == true);
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
        Assert.Contains("DirectCall", CalleeName(result, call));
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
            "Alpha.Converter::Convert<T>(object)",
            GeneratedFilter.Include,
            cancellationToken: cancellationToken);

        Assert.Single(extension.Calls);
        Assert.Contains("PlayerExtensions::PlayExt", CalleeName(extension, extension.Calls[0]));
        Assert.Equal(2, generic.Calls.Count(call => call.ReferenceKind == ReferenceKind.Invocation));
        Assert.All(
            generic.Calls,
            call => Assert.Equal("Alpha.Converter::Convert<T>(object)", CalleeName(generic, call)));
    }

    [Fact]
    public async Task ConstructorsMethodGroupsAndNameOfAreClassifiedSeparately()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var created = await fixture.Query.FindCalleesAsync(
            "Alpha.DistinctCaller::Execute(Alpha.AClass,Alpha.BClass)",
            GeneratedFilter.Include,
            includeLambdaCalls: false,
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

        Assert.Single(created.Calls, call => call.ReferenceKind == ReferenceKind.ObjectCreation);
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
        Assert.Contains("PlayWindows", CalleeName(callees, callees.Calls[0]));
        Assert.DoesNotContain(callees.Calls, call => CalleeName(callees, call)?.Contains("PlayOther") == true);
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

        Assert.True(await index.IsCacheValidAsync(".", fingerprint, requestHash, cancellationToken));
        var result = await fixture.Query.FindDefinitionsAsync(
            "Alpha.AClass::Play()",
            cancellationToken: cancellationToken);
        Assert.Single(result.Definitions);
    }

    private static IndexSnapshot CreateDuplicateReceiverSnapshot(bool malformedContainmentCycle = false)
    {
        var snapshot = new IndexSnapshot
        {
            InputRoot = ".",
            IndexRootAnchor = ".",
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

        AddType("a-base", "LocalBaseA", "project-a");
        AddMethod("a-base-local", "LocalBaseA", "a-base", "Local", "project-a");
        AddType("a-receiver", "Receiver", "project-a");
        AddMethod("a-owner", "Receiver", "a-receiver", "Execute", "project-a");
        AddMethod("a-local", "Receiver", "a-owner", "Local", "project-a", "Execute().Local()");
        AddMethod(
            "cycle-owner-a",
            "Receiver",
            malformedContainmentCycle ? "cycle-owner-b" : "a-receiver",
            "CycleOwnerA",
            "project-a");
        AddMethod(
            "cycle-owner-b",
            "Receiver",
            malformedContainmentCycle ? "cycle-owner-a" : "a-receiver",
            "CycleOwnerB",
            "project-a");
        AddMethod(
            "cycle-local",
            "Receiver",
            "cycle-owner-a",
            "CycleLocal",
            "project-a",
            "CycleOwnerA().CycleLocal()");
        AddMethod(
            "cycle-local-b",
            "Receiver",
            "cycle-owner-b",
            "CycleLocal",
            "project-a",
            "CycleOwnerB().CycleLocal()");
        AddRelation("a-receiver", "a-base");

        AddType("b-base", "LocalBaseB", "project-b");
        AddMethod("b-base-local", "LocalBaseB", "b-base", "Local", "project-b");
        AddType("b-receiver", "Receiver", "project-b");
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
                Key = $"{key}|document:{fileName}",
                ProjectKey = key,
                NormalizedPath = fileName,
                ContentHash = HashUtilities.Sha256(fileName),
                IsGenerated = false,
                GenerationKind = GenerationKind.None,
            });
        }

        void AddType(string stableKey, string typeName, string projectKey)
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
                Path = new SymbolPathData(
                    "Duplicate",
                    typeName,
                    typeName,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    CallablePathSegmentKind.Named),
            };
        }

        void AddMethod(
            string stableKey,
            string typeName,
            string containingSymbolKey,
            string methodName,
            string projectKey,
            string? executableDisplayPath = null)
        {
            var executablePath = executableDisplayPath ?? $"{methodName}()";
            var source = $"{methodName}()";
            var sourceStart = snapshot.Declarations.Count * 10;
            var document = snapshot.Documents.Single(value => value.ProjectKey == projectKey);
            var declarationKey =
                $"{stableKey}|declaration:{document.NormalizedPath}:{sourceStart}:{source.Length}:{(int)DeclarationRole.Ordinary}";
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
                PreferredDeclarationKey = declarationKey,
                ContainingSymbolKey = containingSymbolKey,
                ParameterCount = 0,
                Accessibility = (int)IndexedAccessibility.Public,
                Path = new SymbolPathData(
                    "Duplicate",
                    typeName,
                    typeName,
                    executablePath,
                    executablePath,
                    $"{methodName}()",
                    $"{methodName}()",
                    CallablePathSegmentKind.Named),
            };
            snapshot.Declarations[declarationKey] = new SymbolDeclarationData
            {
                Key = declarationKey,
                SymbolKey = stableKey,
                DocumentKey = document.Key,
                Role = DeclarationRole.Ordinary,
                SourceStart = sourceStart,
                SourceLength = source.Length,
                NormalizedSource = source,
                NormalizedSourceHash = HashUtilities.Sha256(source),
                IsGenerated = false,
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

    private static string FormatPath(StoredSymbol symbol) =>
        new SymbolPathFormatter().Format(
            Assert.IsType<SymbolPathData>(symbol.Path),
            new SymbolPathFormatOptions());

    private static string? CalleeName(CallResult result, StoredCall call)
    {
        var id = call.CalleeDefinitionId ?? call.CalleeSymbolId;
        return id is long endpointId && result.SymbolsById.TryGetValue(endpointId, out var symbol)
            ? FormatPath(symbol)
            : call.UnresolvedName;
    }

    private static string CallerName(CallResult result, StoredCall call) =>
        FormatPath(result.SymbolsById[call.CallerSymbolId]);
}
