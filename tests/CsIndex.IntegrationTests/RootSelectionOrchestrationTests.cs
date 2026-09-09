using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

public sealed class RootSelectionOrchestrationTests(
    SemanticIndexFixture semanticFixture,
    SymbolResolutionFixture resolutionFixture)
    : IClassFixture<SemanticIndexFixture>, IClassFixture<SymbolResolutionFixture>
{
    // Catches moving namespace/type/method/file/source predicates into traversal.
    [Fact]
    public async Task SelectRoots_IsTraversalFree_AndSecondaryRowsSurviveTraversal()
    {
        await semanticFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = semanticFixture.Repository;
        var traversalEvents = 0;
        repository.TraversalObserver = _ => traversalEvents++;
        var query = new SemanticQueryService(repository);

        var selection = await query.SelectRootsAsync(
            Request(
                "GameNS.Player::Play()",
                [Condition(ConditionCategory.File, "Main.cs")]),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);

        Assert.Equal(["GameNS.Player::Play()"], selection.Roots.Select(root => FormatPath(root.Symbol)));
        Assert.Equal(0, traversalEvents);

        var references = await query.FindReferencesAsync(
            selection,
            GeneratedFilter.Include,
            cancellationToken);
        Assert.True(traversalEvents > 0);
        Assert.Contains(
            references.Calls,
            call => call.DocumentPath == "GeneratedCaller.g.cs" &&
                FormatPath(references.SymbolsById[call.CallerSymbolId]) ==
                    "GeneratedCode.GeneratedCaller::Execute(GameNS.Player)");

        var callers = await query.FindCallersAsync(
            selection,
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            cancellationToken: cancellationToken);
        Assert.Contains(
            callers.EffectiveCallers,
            caller => FormatPath(caller) == "GeneratedCode.GeneratedCaller::Execute(GameNS.Player)");

        var callerTree = await query.FindCallerTreeAsync(
            selection,
            depth: 1,
            maxNodes: 20,
            cancellationToken: cancellationToken);
        Assert.Contains(
            callerTree.Nodes,
            node => node.Symbol.DocumentPath == "GeneratedCaller.g.cs" &&
                FormatPath(node.Symbol) == "GeneratedCode.GeneratedCaller::Execute(GameNS.Player)");
    }

    // Catches graph traversal before root cardinality validation and partial-root splitting.
    [Fact]
    public async Task GraphSelection_PreservesCardinalityAndRejectsTraversalBeforeAnyEvent()
    {
        await semanticFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = semanticFixture.Repository;
        var traversalEvents = 0;
        repository.TraversalObserver = _ => traversalEvents++;
        var query = new SemanticQueryService(repository);

        var ambiguous = await query.SelectRootsAsync(
            Request("Alpha.AClass::Play"),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        Assert.Equal(2, ambiguous.Roots.Count);

        await Assert.ThrowsAsync<SymbolQueryParseException>(() => query.FindCallerTreeAsync(
            ambiguous,
            depth: 2,
            maxNodes: 20,
            cancellationToken: cancellationToken));
        Assert.Equal(0, traversalEvents);

        var missing = await query.SelectRootsAsync(
            Request("Alpha.AClass::Missing()"),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        Assert.Empty(missing.Roots);
        await Assert.ThrowsAsync<SymbolQueryParseException>(() => query.FindAsyncPathAsync(
            missing,
            maxNodes: 20,
            cancellationToken));
        Assert.Equal(0, traversalEvents);

        await resolutionFixture.BuildTask;
        var partialQuery = new SemanticQueryService(resolutionFixture.Repository);
        var partial = await partialQuery.SelectRootsAsync(
            Request(
                "Partials::PartialHost::PartialWork()",
                [Condition(ConditionCategory.File, "PartialDefinition.cs")]),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: false,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        Assert.Single(partial.Roots);
        Assert.Single(partial.Roots[0].MatchingDeclarations);
    }

    // Catches projecting only MatchingDeclarations instead of all declarations for a logical root.
    [Fact]
    public async Task DefinitionProjection_UsesAllDeclarationsAndPreferredProjectionUsesImplementation()
    {
        await resolutionFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var query = new SemanticQueryService(resolutionFixture.Repository);
        var selection = await query.SelectRootsAsync(
            Request(
                "Partials::PartialHost::PartialWork()",
                [Condition(ConditionCategory.File, "PartialDefinition.cs")]),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: false,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);

        var definitions = await query.FindDefinitionsAsync(selection, cancellationToken);
        Assert.Equal(
            [DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation],
            definitions.Definitions.Select(row => row.Declaration.Role));
        Assert.Equal(
            ["PartialDefinition.cs", "PartialImplementation.cs"],
            definitions.Definitions.Select(row => row.Declaration.DocumentPath));

        var logicalRows = await query.LoadLogicalRowsAsync(
            selection,
            includeSourceText: false,
            cancellationToken);
        var logical = Assert.Single(logicalRows);
        Assert.Equal(DeclarationRole.PartialImplementation, logical.PreferredDeclaration?.Role);
        Assert.Equal("Partials.PartialHost::PartialWork()", FormatPath(logical.Symbol));

        var definitionOnly = await query.SelectRootsAsync(
            Request(
                "Partials::PartialHost::DefinitionOnly()",
                [Condition(ConditionCategory.File, "PartialDefinition.cs")]),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: false,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var definitionOnlyRow = Assert.Single(await query.LoadLogicalRowsAsync(
            definitionOnly,
            includeSourceText: false,
            cancellationToken));
        Assert.Equal(DeclarationRole.PartialDefinition, definitionOnlyRow.PreferredDeclaration?.Role);
    }

    // Catches discarding passing physical rows during source projection.
    [Fact]
    public async Task SourceProjection_ReturnsEveryPassingPhysicalDeclarationInCanonicalOrder()
    {
        await resolutionFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var query = new SemanticQueryService(resolutionFixture.Repository);

        var result = await query.SelectSourceRowsAsync(
            Request(
                "Partials::PartialHost::PartialWork()",
                [new TypedCondition(ConditionCategory.File, ConditionSyntax.Glob, "Partial*.cs")]),
            resolutionFixture.PrimaryProfileName,
            cancellationToken);

        Assert.Equal(
            ["PartialDefinition.cs", "PartialImplementation.cs"],
            result.Matches.Select(row => row.Declaration.DocumentPath));
        Assert.Equal(
            [DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation],
            result.Matches.Select(row => row.Declaration.Role));
        Assert.All(result.Matches, row => Assert.Equal("Partials.PartialHost::PartialWork()", FormatPath(row.Symbol)));
    }

    // Catches narrowing omitted/all to Method/Lambda and applying async involvement to direct async status.
    [Fact]
    public async Task Selection_UsesAllExecutableKindsAndDirectAsyncRole()
    {
        await resolutionFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var resolutionQuery = new SemanticQueryService(resolutionFixture.Repository);
        var all = await resolutionQuery.SelectRootsAsync(
            Request(null),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);

        Assert.Contains(all.Roots, root => root.Symbol.Kind == IndexedSymbolKind.Method);
        Assert.Contains(all.Roots, root => root.Symbol.Kind == IndexedSymbolKind.Lambda);
        Assert.Contains(all.Roots, root => root.Symbol.Kind == IndexedSymbolKind.Initializer);
        Assert.Contains(all.Roots, root => root.Symbol.Kind == IndexedSymbolKind.TopLevelStatements);
        Assert.DoesNotContain(all.Roots, root => root.Symbol.Kind == IndexedSymbolKind.Type);

        var methods = await resolutionQuery.SelectRootsAsync(
            Request(
                "Catalog::SpecialHost::*",
                kind: IndexedSymbolKind.Method,
                kindSpecified: true),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        Assert.Contains(methods.Roots, root => FormatPath(root.Symbol) == "Catalog.SpecialHost::[get:Value]()");
        Assert.Contains(methods.Roots, root => FormatPath(root.Symbol) == "Catalog.SpecialHost::[static-constructor]()");
        Assert.DoesNotContain(methods.Roots, root => root.Symbol.Kind == IndexedSymbolKind.Lambda);

        var lambdas = await resolutionQuery.SelectRootsAsync(
            Request(null, kind: IndexedSymbolKind.Lambda, kindSpecified: true),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        Assert.Contains(
            lambdas.Roots,
            root => FormatPath(root.Symbol) ==
                "Namespace1.Namespace2.Class1.Class2::Method1().<lambda#1>");
        Assert.Contains(
            lambdas.Roots,
            root => FormatPath(root.Symbol) ==
                "Namespace1.Namespace2.Class1.Class2::Method1().<anonymous-method#2>");
        Assert.All(lambdas.Roots, root => Assert.Equal(IndexedSymbolKind.Lambda, root.Symbol.Kind));

        var syncInitializer = await resolutionQuery.SelectRootsAsync(
            Request(
                "Catalog::AsyncInitializerHost::<initializer:Factory>",
                asyncStatus: AsyncStatusFilter.Sync,
                asyncStatusSpecified: true),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var initializer = Assert.Single(syncInitializer.Roots);
        Assert.Equal(IndexedSymbolKind.Initializer, initializer.Symbol.Kind);
        Assert.Equal(AsyncRole.None, initializer.Symbol.AsyncRole);

        var asyncInitializerChild = await resolutionQuery.SelectRootsAsync(
            Request(
                "Catalog::AsyncInitializerHost::<initializer:Factory>.<lambda#1>",
                kind: IndexedSymbolKind.Lambda,
                kindSpecified: true,
                asyncStatus: AsyncStatusFilter.Async,
                asyncStatusSpecified: true),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var initializerChild = Assert.Single(asyncInitializerChild.Roots);
        Assert.Equal(IndexedSymbolKind.Lambda, initializerChild.Symbol.Kind);
        Assert.NotEqual(AsyncRole.None, initializerChild.Symbol.AsyncRole);

        await semanticFixture.BuildTask;
        var semanticQuery = new SemanticQueryService(semanticFixture.Repository);
        var asyncRoots = await semanticQuery.SelectRootsAsync(
            Request("Alpha.AsyncStatusCases::**", asyncStatus: AsyncStatusFilter.Async, asyncStatusSpecified: true),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        Assert.Contains(asyncRoots.Roots, root => FormatPath(root.Symbol) == "Alpha.AsyncStatusCases::DeclaredTaskAsync()");
        Assert.Contains(
            asyncRoots.Roots,
            root => FormatPath(root.Symbol) ==
                "Alpha.AsyncStatusCases::OuterWithAsyncLambda().<lambda#1>");
        Assert.All(asyncRoots.Roots, root => Assert.NotEqual(AsyncRole.None, root.Symbol.AsyncRole));

        var syncRoots = await semanticQuery.SelectRootsAsync(
            Request("Alpha.AsyncStatusCases::**", asyncStatus: AsyncStatusFilter.Sync, asyncStatusSpecified: true),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var syncOuter = Assert.Single(
            syncRoots.Roots,
            root => FormatPath(root.Symbol) == "Alpha.AsyncStatusCases::OuterWithAsyncLambda()");
        Assert.Equal(AsyncRole.None, syncOuter.Symbol.AsyncRole);
        Assert.DoesNotContain(
            syncRoots.Roots,
            root => FormatPath(root.Symbol) ==
                "Alpha.AsyncStatusCases::OuterWithAsyncLambda().<lambda#1>");

        var asyncTopLevel = await resolutionQuery.SelectRootsAsync(
            Request(
                "global::Program::<top-level-statements>",
                asyncStatus: AsyncStatusFilter.Async,
                asyncStatusSpecified: true),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var topLevel = Assert.Single(asyncTopLevel.Roots);
        Assert.Equal(IndexedSymbolKind.TopLevelStatements, topLevel.Symbol.Kind);
        Assert.NotEqual(AsyncRole.None, topLevel.Symbol.AsyncRole);
    }

    // Catches graph traversal narrowing source-backed executables back to Method/Lambda.
    [Fact]
    public async Task GraphResolvers_KeepInitializerAndTopLevelStatementsEligible()
    {
        await resolutionFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var query = new SemanticQueryService(resolutionFixture.Repository);

        var initializerSelection = await query.SelectRootsAsync(
            Request("Catalog::AsyncInitializerHost::<initializer:AsyncFactory>"),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var initializerPath = await query.FindAsyncPathAsync(
            initializerSelection,
            maxNodes: 20,
            cancellationToken);
        Assert.True(
            initializerPath.Found,
            $"initializer depth={initializerPath.Root.AsyncInvolvementDepth}, " +
            $"next={initializerPath.Root.AsyncNextSymbolId}");
        Assert.Equal(IndexedSymbolKind.Initializer, initializerPath.Root.Kind);
        Assert.Contains(initializerPath.Nodes, node =>
            node.Kind == IndexedSymbolKind.Method &&
            FormatPath(node) == "Catalog.AsyncInitializerHost::OriginAsync()");

        var topLevelSelection = await query.SelectRootsAsync(
            Request("global::Program::<top-level-statements>"),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var topLevelPath = await query.FindAsyncPathAsync(
            topLevelSelection,
            maxNodes: 20,
            cancellationToken);
        Assert.True(topLevelPath.Found);
        Assert.Equal(IndexedSymbolKind.TopLevelStatements, topLevelPath.Root.Kind);

        var originSelection = await query.SelectRootsAsync(
            Request("Catalog::AsyncInitializerHost::OriginAsync()"),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var initializerCallerTree = await query.FindCallerTreeAsync(
            originSelection,
            depth: 1,
            maxNodes: 20,
            cancellationToken: cancellationToken);
        Assert.Contains(initializerCallerTree.Nodes, node =>
            node.Symbol.Kind == IndexedSymbolKind.Initializer &&
            FormatPath(node.Symbol) == "Catalog.AsyncInitializerHost::<initializer:AsyncFactory>");

        var topLocalSelection = await query.SelectRootsAsync(
            Request("global::Program::<top-level-statements>.TopLocal()"),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var topLevelCallerTree = await query.FindCallerTreeAsync(
            topLocalSelection,
            depth: 1,
            maxNodes: 20,
            cancellationToken: cancellationToken);
        Assert.Contains(
            topLevelCallerTree.Nodes,
            node => node.Symbol.Kind == IndexedSymbolKind.TopLevelStatements);
    }

    // Catches applying the root generated predicate again to calls and caller documents.
    [Fact]
    public async Task GeneratedFiltering_HappensAtRootAndIndependentlyDuringTraversal()
    {
        await semanticFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = semanticFixture.Repository;
        var query = new SemanticQueryService(repository);

        var generatedRoot = await query.SelectRootsAsync(
            Request("GeneratedCode.GeneratedCaller::Execute"),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Only,
            cancellationToken);
        Assert.Single(generatedRoot.Roots);
        Assert.True(generatedRoot.Roots[0].Symbol.PreferredIsGenerated);

        var excludedGeneratedRoot = await query.SelectRootsAsync(
            Request("GeneratedCode.GeneratedCaller::Execute"),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Exclude,
            cancellationToken);
        Assert.Empty(excludedGeneratedRoot.Roots);

        var ordinaryRoot = await query.SelectRootsAsync(
            Request("GameNS.Player::Play()"),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Exclude,
            cancellationToken);
        var includeGenerated = await query.FindReferencesAsync(
            ordinaryRoot,
            GeneratedFilter.Include,
            cancellationToken);
        var excludeGenerated = await query.FindReferencesAsync(
            ordinaryRoot,
            GeneratedFilter.Exclude,
            cancellationToken);
        Assert.Contains(includeGenerated.Calls, call => call.IsGenerated);
        Assert.DoesNotContain(excludeGenerated.Calls, call => call.IsGenerated);
    }

    // Catches treating metadata's unknown generated state as source-generated or source-ordinary.
    [Fact]
    public async Task MetadataOnlyRootsWithUnknownGeneratedStateMatchOnlyInclude()
    {
        await semanticFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = semanticFixture.Repository;
        var query = new SemanticQueryService(repository);
        var profile = await repository.GetProfileAsync(
            semanticFixture.PrimaryProfileName,
            cancellationToken);
        var metadata = (await repository.FindExecutableSymbolsAsync(
                profile.Id,
                sourceOnly: false,
                cancellationToken))
            .First(symbol => symbol.DocumentPath is null && symbol.PreferredIsGenerated is null);
        var request = Request(FormatPath(metadata));

        var include = await query.SelectRootsAsync(
            request,
            semanticFixture.PrimaryProfileName,
            sourceOnly: false,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var exclude = await query.SelectRootsAsync(
            request,
            semanticFixture.PrimaryProfileName,
            sourceOnly: false,
            rootGeneratedFilter: GeneratedFilter.Exclude,
            cancellationToken);
        var only = await query.SelectRootsAsync(
            request,
            semanticFixture.PrimaryProfileName,
            sourceOnly: false,
            rootGeneratedFilter: GeneratedFilter.Only,
            cancellationToken);

        var selected = Assert.Single(include.Roots);
        Assert.Equal(metadata.Id, selected.Symbol.Id);
        Assert.Null(selected.Symbol.PreferredIsGenerated);
        Assert.Empty(exclude.Roots);
        Assert.Empty(only.Roots);
    }

    // Catches override expansion reapplying root predicates and accepting lambda roots.
    [Fact]
    public async Task OverrideExpansion_AddsNonmatchingMethodsAfterBaseSelection()
    {
        await semanticFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var query = new SemanticQueryService(semanticFixture.Repository);
        var selection = await query.SelectRootsAsync(
            Request(
                "Alpha.AsyncOverrideBase::Run()",
                [Condition(ConditionCategory.Include, "virtual")],
                kind: IndexedSymbolKind.Method,
                kindSpecified: true),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);

        Assert.Equal(["Alpha.AsyncOverrideBase::Run()"], selection.Roots.Select(root => FormatPath(root.Symbol)));
        var expanded = await query.ExpandOverrideRootsAsync(selection, cancellationToken);
        Assert.Equal(
            ["Alpha.AsyncOverrideBase::Run()", "Alpha.AsyncOverrideDerived::Run()"],
            expanded.Roots.Select(root => FormatPath(root.Symbol)));

        var lambdaSelection = await query.SelectRootsAsync(
            Request(
                "Alpha.LambdaPlayer::Execute().<lambda#1>",
                kind: IndexedSymbolKind.Lambda,
                kindSpecified: true),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        await Assert.ThrowsAsync<SymbolQueryParseException>(() => query.ExpandOverrideRootsAsync(
            lambdaSelection,
            cancellationToken));
    }

    // Catches expansion filtering again, discarding the original match rows, or loading expanded declarations with source text.
    [Fact]
    public async Task OverrideExpansion_PreservesOriginalMatchesAndLoadsAllExpandedPartialDeclarations()
    {
        await resolutionFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = resolutionFixture.Repository;
        var query = new SemanticQueryService(repository);
        var selection = await query.SelectRootsAsync(
            Request(
                "Partials::IPartialRunner::Run(int)",
                [Condition(ConditionCategory.Include, "interfaceMarker")],
                kind: IndexedSymbolKind.Method,
                kindSpecified: true),
            resolutionFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);

        var original = Assert.Single(selection.Roots);
        var originalMatches = original.MatchingDeclarations;
        Assert.Single(originalMatches);

        var expanded = await query.ExpandOverrideRootsAsync(selection, cancellationToken);
        Assert.Equal(
            ["Partials.IPartialRunner::Run(int)", "Partials.PartialRunner::Run(int)"],
            expanded.Roots.Select(root => FormatPath(root.Symbol)));

        var preservedOriginal = Assert.Single(expanded.Roots, root => root.Symbol.Id == original.Symbol.Id);
        Assert.Same(original, preservedOriginal);
        Assert.Same(originalMatches, preservedOriginal.MatchingDeclarations);

        var expandedRunner = Assert.Single(
            expanded.Roots,
            root => FormatPath(root.Symbol) == "Partials.PartialRunner::Run(int)");
        Assert.Equal(
            ["PartialDefinition.cs", "PartialImplementation.cs"],
            expandedRunner.MatchingDeclarations.Select(declaration => declaration.DocumentPath));
        Assert.All(expandedRunner.MatchingDeclarations, declaration =>
        {
            Assert.Null(declaration.NormalizedSource);
        });

        var fullDeclarations = await repository.GetDeclarationsAsync(
            expanded.Profile.Id,
            [expandedRunner.Symbol.Id],
            includeSourceText: true,
            cancellationToken);
        Assert.Equal(2, fullDeclarations.Count);
        Assert.All(fullDeclarations, declaration => Assert.NotNull(declaration.NormalizedSource));
        Assert.DoesNotContain(
            fullDeclarations,
            declaration => declaration.NormalizedSource!.Contains("interfaceMarker", StringComparison.Ordinal));
    }

    // Catches absolute-path persistence, suffix document matching, and file reads during metadata projection.
    [Fact]
    public async Task QueryBaseDirectory_IsReadTimeOnlyAndSupportsRelativeAtLocations()
    {
        await semanticFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = semanticFixture.Repository;
        var profile = await repository.GetProfileAsync(semanticFixture.PrimaryProfileName, cancellationToken);
        var storedPathBefore = (await repository.FindDocumentsAsync(
            profile.Id,
            "Main.cs",
            cancellationToken)).Single().Path;
        var caseVariantMatches = await repository.FindDocumentsAsync(
            profile.Id,
            "main.cs",
            cancellationToken);
        if (OperatingSystem.IsWindows())
        {
            Assert.Single(caseVariantMatches);
        }
        else
        {
            Assert.Empty(caseVariantMatches);
        }
        var suffixOnlyMatches = await repository.FindDocumentsAsync(
            profile.Id,
            "nested/Main.cs",
            cancellationToken);
        Assert.Empty(suffixOnlyMatches);
        var absoluteLocation = semanticFixture.GetLocation("a.Play()");
        var parsed = SourcePositionResolver.ParseAt(absoluteLocation);
        var relativeLocation = $"Main.cs:{parsed.Line}:{parsed.Column}";

        var defaultQuery = new SemanticQueryService(repository);
        var absolute = await defaultQuery.FindDefinitionAtAsync(
            absoluteLocation,
            semanticFixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        var movedRoot = Path.Combine(Path.GetTempPath(), "csindex-query-base", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(movedRoot);
        try
        {
            var movedFilePath = Path.Combine(movedRoot, "Main.cs");
            File.Copy(semanticFixture.MainSourcePath, movedFilePath);
            var movedQuery = new SemanticQueryService(repository, movedRoot);
            var relative = await movedQuery.FindDefinitionAtAsync(
                relativeLocation,
                semanticFixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
            Assert.Equal(absolute.Definitions.Select(row => row.Symbol.Id), relative.Definitions.Select(row => row.Symbol.Id));

            File.Delete(movedFilePath);

            var selected = await movedQuery.SelectRootsAsync(
                Request("Alpha.AClass::Play()"),
                semanticFixture.PrimaryProfileName,
                sourceOnly: true,
                rootGeneratedFilter: GeneratedFilter.Include,
                cancellationToken);
            var metadata = await movedQuery.LoadLogicalRowsAsync(
                selected,
                includeSourceText: false,
                cancellationToken);
            Assert.NotEmpty(metadata);

            await Assert.ThrowsAsync<FileNotFoundException>(() => movedQuery.FindDefinitionAtAsync(
                relativeLocation,
                cancellationToken: cancellationToken));
        }
        finally
        {
            if (Directory.Exists(movedRoot))
            {
                Directory.Delete(movedRoot, recursive: true);
            }
        }

        var storedPathAfter = (await repository.FindDocumentsAsync(
            profile.Id,
            "Main.cs",
            cancellationToken)).Single().Path;
        Assert.Equal(storedPathBefore, storedPathAfter);
        Assert.Equal(profile.IndexRootAnchor, (await repository.GetProfileAsync(
            semanticFixture.PrimaryProfileName,
            cancellationToken)).IndexRootAnchor);
    }

    [Fact]
    public void SourcePositionResolver_RejectsWindowsDriveRelativePathBeforeReading()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Assert.Throws<ArgumentException>(() =>
            SourcePositionResolver.ResolveOffset(@"C:relative.cs", 0));
        Assert.Equal("path", exception.ParamName);
    }

    // Catches incomplete endpoint hydration after moving traversal behind root selection.
    [Fact]
    public async Task TraversalResults_HydrateEveryFormatterEndpointInOneDictionary()
    {
        await semanticFixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var hydrationBatches = new List<long[]>();
        var query = new SemanticQueryService(semanticFixture.Repository)
        {
            EndpointHydrationObserver = ids => hydrationBatches.Add(ids.Order().ToArray()),
        };
        var selection = await query.SelectRootsAsync(
            Request("Alpha.BaseClass::Run()"),
            semanticFixture.PrimaryProfileName,
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken);
        var result = await query.FindCallersAsync(
            selection,
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

        Assert.All(expectedIds, id => Assert.True(result.SymbolsById.ContainsKey(id)));
        Assert.Equal(expectedIds, Assert.Single(hydrationBatches));
        Assert.Equal("Alpha.BaseClass::Run()", FormatPath(Assert.Single(result.Selection.Roots).Symbol));
    }

    private static SymbolSelectionRequest Request(
        string? selector,
        IReadOnlyList<TypedCondition>? conditions = null,
        IndexedSymbolKind? kind = null,
        AsyncStatusFilter asyncStatus = AsyncStatusFilter.All,
        bool kindSpecified = false,
        bool asyncStatusSpecified = false) =>
        new(
            selector,
            conditions ?? [],
            new SymbolCaseOptions(),
            new FunctionTargetFilter(kind, asyncStatus),
            kindSpecified || kind is not null,
            asyncStatusSpecified || asyncStatus != AsyncStatusFilter.All);

    private static TypedCondition Condition(ConditionCategory category, string value) =>
        new(category, ConditionSyntax.Literal, value);

    private static string FormatPath(StoredSymbol symbol) =>
        new SymbolPathFormatter().Format(
            Assert.IsType<SymbolPathData>(symbol.Path),
            new SymbolPathFormatOptions());
}
