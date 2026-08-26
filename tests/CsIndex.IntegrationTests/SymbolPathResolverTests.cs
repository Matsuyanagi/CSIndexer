using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

public sealed class SymbolPathResolverTests(SymbolResolutionFixture fixture)
    : IClassFixture<SymbolResolutionFixture>
{
    private static readonly SymbolPathFormatter Formatter = new();

    [Fact]
    public async Task CsharpSuffixReturnsEveryCompleteOwnerPathMatchInCanonicalOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        var shortRoots = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);
        var qualifiedRoots = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("Namespace1.Namespace2.Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);
        var repeated = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);

        Assert.Equal(4, shortRoots.Count);
        Assert.Equal(2, qualifiedRoots.Count);
        Assert.Contains(
            qualifiedRoots,
            root => root.Symbol.Path!.NamespacePath == "Namespace1.Namespace2");
        Assert.Contains(
            qualifiedRoots,
            root => root.Symbol.Path!.NamespacePath == "Company.Namespace1.Namespace2");
        Assert.Equal(
            shortRoots.Select(root => root.Symbol.Id),
            repeated.Select(root => root.Symbol.Id));
        var canonical = SymbolCanonicalComparer.OrderSymbols(
            shortRoots.Select(root => root.Symbol).Reverse(),
            cancellationToken);
        Assert.Equal(
            canonical.Select(symbol => symbol.Id),
            shortRoots.Select(root => root.Symbol.Id));
    }

    [Fact]
    public async Task ExplicitOwnerBoundaryIsExactAndNeverFallsBackToCsharp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        var roots = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("Namespace1.Namespace2::Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);
        var noFallback = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("A::B::C()"),
            sourceOnly: false,
            cancellationToken);

        var root = Assert.Single(roots);
        Assert.Equal("Namespace1.Namespace2", root.Symbol.Path!.NamespacePath);
        Assert.Empty(noFallback);
    }

    [Fact]
    public async Task GlobalAndEscapedGlobalAreDistinctAndRecursiveNamespaceIncludesGlobal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        var global = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("global::Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken));
        var namedGlobal = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("@global::Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken));
        var wildcard = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("**::Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);

        Assert.Equal(string.Empty, global.Symbol.Path!.NamespacePath);
        Assert.Equal("@global", namedGlobal.Symbol.Path!.NamespacePath);
        Assert.NotEqual(global.Symbol.Id, namedGlobal.Symbol.Id);
        Assert.Equal(4, wildcard.Count);
        Assert.Contains(wildcard, root => root.Symbol.Id == global.Symbol.Id);
        Assert.Contains(wildcard, root => root.Symbol.Id == namedGlobal.Symbol.Id);
    }

    [Fact]
    public async Task FormatterOutputsRoundTripAndShortFormsKeepDocumentedBreadth()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);
        var original = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("Namespace1.Namespace2::Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken));
        var path = original.Symbol.Path!;

        var csharpFull = await ResolveFormattedAsync(
            resolver,
            profile.Id,
            path,
            new(SymbolPathStyle.CSharp, ShortNames: false),
            cancellationToken);
        var explicitFull = await ResolveFormattedAsync(
            resolver,
            profile.Id,
            path,
            new(SymbolPathStyle.Explicit, ShortNames: false),
            cancellationToken);
        var csharpShort = await ResolveFormattedAsync(
            resolver,
            profile.Id,
            path,
            new(SymbolPathStyle.CSharp, ShortNames: true),
            cancellationToken);
        var explicitShort = await ResolveFormattedAsync(
            resolver,
            profile.Id,
            path,
            new(SymbolPathStyle.Explicit, ShortNames: true),
            cancellationToken);

        Assert.Contains(csharpFull, root => root.Symbol.Id == original.Symbol.Id);
        Assert.Single(explicitFull, root => root.Symbol.Id == original.Symbol.Id);
        Assert.Equal(4, csharpShort.Count);
        Assert.Equal(4, explicitShort.Count);
        Assert.Contains(csharpShort, root => root.Symbol.Id == original.Symbol.Id);
        Assert.Contains(explicitShort, root => root.Symbol.Id == original.Symbol.Id);
    }

    [Fact]
    public async Task CsharpCaseRoutingUsesCandidateNamespaceAndTypePositions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);
        var namespaceIgnore = new SymbolCaseOptions(
            Namespace: CaseMode.Ignore,
            Type: CaseMode.Strict,
            Method: CaseMode.Strict);

        var namespaceCaseMatch = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(
                "casenamespace.CaseType.InnerType::CaseTarget()",
                @case: namespaceIgnore),
            sourceOnly: false,
            cancellationToken);
        var typeCaseMismatch = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(
                "CaseNamespace.casetype.InnerType::CaseTarget()",
                @case: namespaceIgnore),
            sourceOnly: false,
            cancellationToken);
        var unsafeNameHint = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(
                "Namespace1.Namespace2::Class1.Class2::method1()",
                @case: new SymbolCaseOptions(Method: CaseMode.Ignore)),
            sourceOnly: false,
            cancellationToken);

        Assert.Single(namespaceCaseMatch);
        Assert.Empty(typeCaseMismatch);
        Assert.Single(unsafeNameHint);
    }

    [Fact]
    public async Task SignatureListStatesAreIndependentAtRootAndLocalLevels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        Assert.Equal(3, (await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root", cancellationToken)).Count);
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root<T>", cancellationToken));
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root()", cancellationToken));
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root(int)", cancellationToken));

        Assert.Equal(3, (await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root.Local", cancellationToken)).Count);
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root.Local<T>", cancellationToken));
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root.Local()", cancellationToken));
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::Root.Local(int)", cancellationToken));
        Assert.Single(await ResolveAsync(
            resolver,
            profile.Id,
            "Signatures::SignatureHost::Root<A>(A).Local<B>(B)",
            cancellationToken));
    }

    [Fact]
    public async Task SemanticTypeSpellingsAndRefModesResolveCanonicalIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        await AssertSameLogicalIdAsync(
            resolver,
            profile.Id,
            cancellationToken,
            "Signatures::SignatureHost::Alias(int)",
            "Signatures::SignatureHost::Alias(System.Int32)");
        await AssertSameLogicalIdAsync(
            resolver,
            profile.Id,
            cancellationToken,
            "Signatures::SignatureHost::Generic<T>(T)",
            "Signatures::SignatureHost::Generic<U>(U)");
        await AssertSameLogicalIdAsync(
            resolver,
            profile.Id,
            cancellationToken,
            "Signatures::SignatureHost::NullableReference(string?)",
            "Signatures::SignatureHost::NullableReference(System.String)");
        await AssertSameLogicalIdAsync(
            resolver,
            profile.Id,
            cancellationToken,
            "Signatures::SignatureHost::Arrays(int[],string[,])",
            "Signatures::SignatureHost::Arrays(System.Int32[],System.String[,])");
        await AssertSameLogicalIdAsync(
            resolver,
            profile.Id,
            cancellationToken,
            "Signatures::SignatureHost::Pointer(int*)",
            "Signatures::SignatureHost::Pointer(System.Int32*)");
        await AssertSameLogicalIdAsync(
            resolver,
            profile.Id,
            cancellationToken,
            "Signatures::SignatureHost::Tuple((int,string))",
            "Signatures::SignatureHost::Tuple((System.Int32,System.String))");
        await AssertSameLogicalIdAsync(
            resolver,
            profile.Id,
            cancellationToken,
            "Signatures::SignatureHost::FunctionPointer(delegate*<int,void>)",
            "Signatures::SignatureHost::FunctionPointer(delegate*<System.Int32,System.Void>)");

        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::RefValue(ref int)", cancellationToken));
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::OutValue(out int)", cancellationToken));
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::InValue(in int)", cancellationToken));
        Assert.Single(await ResolveAsync(resolver, profile.Id, "Signatures::SignatureHost::RefReadonly(ref readonly int)", cancellationToken));
        var nullableValue = Assert.Single(await ResolveAsync(
            resolver,
            profile.Id,
            "Signatures::SignatureHost::NullableValue(int?)",
            cancellationToken));
        var nonNullableValue = Assert.Single(await ResolveAsync(
            resolver,
            profile.Id,
            "Signatures::SignatureHost::NullableValue(int)",
            cancellationToken));
        Assert.NotEqual(nullableValue.Symbol.Id, nonNullableValue.Symbol.Id);
    }

    [Fact]
    public async Task ImmediateContainmentDistinguishesDirectAndGrandchildMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        var direct = await ResolveAsync(
            resolver,
            profile.Id,
            "Namespace1.Namespace2::Class1.Class2::Method1().Local()",
            cancellationToken);
        var recursive = await ResolveAsync(
            resolver,
            profile.Id,
            "Namespace1.Namespace2::Class1.Class2::Method1().**.Local()",
            cancellationToken);
        var otherOwner = Assert.Single(await ResolveAsync(
            resolver,
            profile.Id,
            "Namespace1.Namespace2.Namespace3::Class2::Method1().Local()",
            cancellationToken));

        var directRoot = Assert.Single(direct);
        Assert.Equal("Method1().Local()", directRoot.Symbol.Path!.ExecutableDisplayPath);
        Assert.Equal(2, recursive.Count);
        Assert.Contains(recursive, root => root.Symbol.Path!.ExecutableDisplayPath == "Method1().Local()");
        Assert.Contains(recursive, root => root.Symbol.Path!.ExecutableDisplayPath == "Method1().Outer().Local()");
        Assert.NotEqual(directRoot.Symbol.Id, otherOwner.Symbol.Id);
    }

    [Fact]
    public async Task SamePathLocalsInSiblingBlocksPersistAsDistinctLogicalRootsAndDeclarationSpans()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        var roots = await ResolveAsync(
            resolver,
            profile.Id,
            "SiblingScopes::Host::Run().Local()",
            cancellationToken);

        Assert.Equal(2, roots.Count);
        Assert.Equal(2, roots.Select(root => root.Symbol.Id).Distinct().Count());
        Assert.All(roots, root => Assert.Equal("Run().Local()", root.Symbol.Path!.ExecutableDisplayPath));
        var declarations = roots.SelectMany(root => root.MatchingDeclarations).ToArray();
        Assert.Equal(2, declarations.Length);
        Assert.Equal(2, declarations.Select(declaration => declaration.SourceStart).Distinct().Count());
    }

    [Fact]
    public async Task LambdaAndAnonymousOrdinalsStayWithinTheirImmediateOwner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        var lambdaOne = Assert.Single(await ResolveAsync(
            resolver,
            profile.Id,
            "Namespace1.Namespace2::Class1.Class2::Method1().<lambda#1>",
            cancellationToken));
        var directLambdas = await ResolveAsync(
            resolver,
            profile.Id,
            "Namespace1.Namespace2::Class1.Class2::Method1().<lambda#*>",
            cancellationToken);
        var anonymous = Assert.Single(await ResolveAsync(
            resolver,
            profile.Id,
            "Namespace1.Namespace2::Class1.Class2::Method1().<anonymous-method#2>",
            cancellationToken));
        var nested = Assert.Single(await ResolveAsync(
            resolver,
            profile.Id,
            "Namespace1.Namespace2::Class1.Class2::Method1().<lambda#3>.<lambda#1>",
            cancellationToken));

        Assert.Equal("Method1().<lambda#1>", lambdaOne.Symbol.Path!.ExecutableDisplayPath);
        Assert.Equal(2, directLambdas.Count);
        Assert.Equal("Method1().<anonymous-method#2>", anonymous.Symbol.Path!.ExecutableDisplayPath);
        Assert.Equal("Method1().<lambda#3>.<lambda#1>", nested.Symbol.Path!.ExecutableDisplayPath);
    }

    [Fact]
    public async Task EverySupportedBracketedSpecialCategoryResolves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);
        string[] selectors =
        [
            "Catalog::Number::[constructor](int)",
            "Catalog::SpecialHost::[static-constructor]()",
            "Catalog::SpecialHost::[destructor]()",
            "Catalog::Number::[operator:+](Catalog.Number,Catalog.Number)",
            "Catalog::Number::[checked-operator:+](Catalog.Number,Catalog.Number)",
            "Catalog::Number::[conversion:implicit:int](Catalog.Number)",
            "Catalog::Number::[conversion:explicit:string](Catalog.Number)",
            "Catalog::CheckedNumber::[checked-conversion:explicit:int](Catalog.CheckedNumber)",
            "Catalog::SpecialHost::[get:Value]()",
            "Catalog::SpecialHost::[set:Value](int)",
            "Catalog::SpecialHost::[init:Name](string)",
            "Catalog::SpecialHost::[add:Changed](System.EventHandler)",
            "Catalog::SpecialHost::[remove:Changed](System.EventHandler)",
            "Catalog::SpecialHost::[explicit:System.IDisposable.Dispose]()",
        ];

        foreach (var selector in selectors)
        {
            var root = Assert.Single(await ResolveAsync(
                resolver,
                profile.Id,
                selector,
                cancellationToken));
            Assert.Equal(IndexedSymbolKind.Method, root.Symbol.Kind);
        }
    }

    [Fact]
    public async Task SyntheticKindsParticipateInDirectFiltersAndWholeStarDoesNotNarrowToMethod()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);

        var initializer = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(
                "Catalog::SpecialHost::<initializer:Factory>",
                filter: new FunctionTargetFilter(null, AsyncStatusFilter.Sync),
                asyncStatusSpecified: true),
            sourceOnly: false,
            cancellationToken));
        var topLevel = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(
                "global::Program::<top-level-statements>",
                filter: new FunctionTargetFilter(null, AsyncStatusFilter.Async),
                asyncStatusSpecified: true),
            sourceOnly: false,
            cancellationToken));
        var kindConflict = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(
                "Catalog::SpecialHost::<initializer:Factory>",
                filter: new FunctionTargetFilter(IndexedSymbolKind.Method, AsyncStatusFilter.All),
                kindSpecified: true),
            sourceOnly: false,
            cancellationToken);
        var wholeStar = await ResolveAsync(
            resolver,
            profile.Id,
            "Catalog::SpecialHost::*",
            cancellationToken);

        Assert.Equal(IndexedSymbolKind.Initializer, initializer.Symbol.Kind);
        Assert.Equal(IndexedSymbolKind.TopLevelStatements, topLevel.Symbol.Kind);
        Assert.Empty(kindConflict);
        Assert.Contains(wholeStar, root => root.Symbol.Kind == IndexedSymbolKind.Initializer);
    }

    [Fact]
    public async Task DeclarationPredicatesUseOneRowAndProjectionPreservesEveryPassingRow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (profile, resolver) = await CreateResolverAsync(cancellationToken: cancellationToken);
        const string selector = "Partials::PartialHost::PartialWork()";

        var definitionOnly = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(selector, [Condition(ConditionCategory.File, "PartialDefinition.cs")]),
            sourceOnly: false,
            cancellationToken));
        var implementationOnly = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(selector, [Condition(ConditionCategory.Include, "ImplementationMarker")]),
            sourceOnly: false,
            cancellationToken));
        var splitConjunction = await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(
                selector,
                [
                    Condition(ConditionCategory.File, "PartialDefinition.cs"),
                    Condition(ConditionCategory.Include, "ImplementationMarker"),
                ]),
            sourceOnly: false,
            cancellationToken);
        var logical = Assert.Single(await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(selector, [new TypedCondition(
                ConditionCategory.File,
                ConditionSyntax.Glob,
                "Partial*.cs")]),
            sourceOnly: false,
            cancellationToken));
        var rows = await resolver.ResolveDeclarationRowsAsync(
            profile.Id,
            Request(selector, [new TypedCondition(
                ConditionCategory.File,
                ConditionSyntax.Glob,
                "Partial*.cs")]),
            cancellationToken);

        Assert.Equal(DeclarationRole.PartialDefinition, Assert.Single(definitionOnly.MatchingDeclarations).Role);
        Assert.Equal(DeclarationRole.PartialImplementation, Assert.Single(implementationOnly.MatchingDeclarations).Role);
        Assert.Equal(definitionOnly.Symbol.Id, implementationOnly.Symbol.Id);
        Assert.Empty(splitConjunction);
        Assert.Equal(2, logical.MatchingDeclarations.Count);
        Assert.Equal(
            [DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation],
            rows.Select(row => row.Declaration.Role));
        Assert.All(rows, row => Assert.Equal(logical.Symbol.Id, row.Symbol.Id));
    }

    [Fact]
    public async Task SourceCellsAreReadOnlyForIncludeOrExcludePredicates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.BuildTask;
        var profile = await fixture.GetProfileAsync(cancellationToken: cancellationToken);
        var sourceCellReads = 0;
        var repository = fixture.Repository;
        repository.NormalizedSourceCellReadObserver = () => sourceCellReads++;
        var resolver = new SymbolPathResolver(repository);

        await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request("Namespace1.Namespace2::Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);
        await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(null, [Condition(ConditionCategory.Namespace, "Namespace1.Namespace2")]),
            sourceOnly: false,
            cancellationToken);
        await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(null, [Condition(ConditionCategory.Type, "Class1.Class2")]),
            sourceOnly: false,
            cancellationToken);
        await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(null, [Condition(ConditionCategory.Method, "Method1()")]),
            sourceOnly: false,
            cancellationToken);
        await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(null, [Condition(ConditionCategory.File, "Resolution.cs")]),
            sourceOnly: false,
            cancellationToken);

        Assert.Equal(0, sourceCellReads);

        await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(null, [Condition(ConditionCategory.Include, "ImplementationMarker")]),
            sourceOnly: false,
            cancellationToken);

        Assert.True(sourceCellReads > 0);
        var readsAfterInclude = sourceCellReads;

        await resolver.ResolveLogicalRootsAsync(
            profile.Id,
            Request(null, [Condition(ConditionCategory.Exclude, "ImplementationMarker")]),
            sourceOnly: false,
            cancellationToken);

        Assert.True(sourceCellReads > readsAfterInclude);
    }

    [Fact]
    public async Task ProfilesRemainIsolatedWithDeterministicSemanticResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.BuildTask;
        var repository = fixture.Repository;
        var resolver = new SymbolPathResolver(repository);
        var primary = await repository.GetProfileAsync(fixture.PrimaryProfileName, cancellationToken);
        var secondary = await repository.GetProfileAsync(fixture.SecondaryProfileName, cancellationToken);

        var primaryRoots = await resolver.ResolveLogicalRootsAsync(
            primary.Id,
            Request("Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);
        var secondaryRoots = await resolver.ResolveLogicalRootsAsync(
            secondary.Id,
            Request("Class1.Class2::Method1()"),
            sourceOnly: false,
            cancellationToken);
        var primaryOnly = await resolver.ResolveLogicalRootsAsync(
            primary.Id,
            Request("ProfileScope::SecondaryOnly::Marker()"),
            sourceOnly: false,
            cancellationToken);
        var secondaryOnly = await resolver.ResolveLogicalRootsAsync(
            secondary.Id,
            Request("ProfileScope::SecondaryOnly::Marker()"),
            sourceOnly: false,
            cancellationToken);

        Assert.Equal(4, primaryRoots.Count);
        Assert.Equal(4, secondaryRoots.Count);
        Assert.Empty(primaryRoots.Select(root => root.Symbol.Id).Intersect(
            secondaryRoots.Select(root => root.Symbol.Id)));
        Assert.Empty(primaryOnly);
        Assert.Single(secondaryOnly);
    }

    private async Task<(StoredProfile Profile, SymbolPathResolver Resolver)> CreateResolverAsync(
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        await fixture.BuildTask;
        var repository = fixture.Repository;
        var profile = await repository.GetProfileAsync(profileName, cancellationToken);
        return (profile, new SymbolPathResolver(repository));
    }

    private static Task<IReadOnlyList<ResolvedLogicalRoot>> ResolveAsync(
        SymbolPathResolver resolver,
        long profileId,
        string selector,
        CancellationToken cancellationToken) =>
        resolver.ResolveLogicalRootsAsync(
            profileId,
            Request(selector),
            sourceOnly: false,
            cancellationToken);

    private static Task<IReadOnlyList<ResolvedLogicalRoot>> ResolveFormattedAsync(
        SymbolPathResolver resolver,
        long profileId,
        SymbolPathData path,
        SymbolPathFormatOptions options,
        CancellationToken cancellationToken) =>
        ResolveAsync(resolver, profileId, Formatter.Format(path, options), cancellationToken);

    private static async Task AssertSameLogicalIdAsync(
        SymbolPathResolver resolver,
        long profileId,
        CancellationToken cancellationToken,
        params string[] selectors)
    {
        var ids = new List<long>();
        foreach (var selector in selectors)
        {
            ids.Add(Assert.Single(await ResolveAsync(
                resolver,
                profileId,
                selector,
                cancellationToken)).Symbol.Id);
        }

        Assert.Single(ids.Distinct());
    }

    private static TypedCondition Condition(ConditionCategory category, string value) =>
        new(category, ConditionSyntax.Literal, value);

    private static SymbolSelectionRequest Request(
        string? selector,
        IReadOnlyList<TypedCondition>? conditions = null,
        SymbolCaseOptions? @case = null,
        FunctionTargetFilter? filter = null,
        bool kindSpecified = false,
        bool asyncStatusSpecified = false) =>
        new(
            selector,
            conditions ?? [],
            @case ?? new SymbolCaseOptions(),
            filter ?? new FunctionTargetFilter(null, AsyncStatusFilter.All),
            kindSpecified,
            asyncStatusSpecified);
}
