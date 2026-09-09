using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

public sealed class SymbolSourceQueryTests(SemanticIndexFixture fixture)
    : IClassFixture<SemanticIndexFixture>
{
    [Fact]
    public async Task ExactSearch_PreservesTheExistingResolverResults()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var expected = await fixture.Query.FindSymbolsAsync(
            "Alpha.AClass::Play",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var actual = await fixture.Query.SearchSymbolsAsync(
            Request("Alpha.AClass::Play"),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(
            expected.MatchedSymbols.Select(symbol => symbol.Id),
            actual.MatchedSymbols.Select(symbol => symbol.Id));
    }

    [Fact]
    public async Task PatternSearch_MatchesWildcardAndTypedComponentRequests()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var wildcard = await fixture.Query.SearchSymbolsAsync(
            Request("*.Gamer::Play"),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var components = await fixture.Query.SearchSymbolsAsync(
            Request(null, namespacePattern: "Tokyo", typePattern: "Gamer", methodPattern: "Play"),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(
            ["Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(string)"],
            wildcard.MatchedSymbols.Select(FormatPath));
        Assert.Equal(
            wildcard.MatchedSymbols.Select(FormatPath),
            components.MatchedSymbols.Select(FormatPath));
    }

    [Fact]
    public async Task LambdaPatterns_MatchSuffixOwnerSuffixAndFullName()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var suffix = await fixture.Query.SearchSymbolsAsync(
            Request(null, typePattern: "LambdaSearch", methodPattern: "**.<lambda#1>", kind: IndexedSymbolKind.Lambda),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var ownerSuffix = await fixture.Query.SearchSymbolsAsync(
            Request(null, methodPattern: "**.Function().<lambda#2>", kind: IndexedSymbolKind.Lambda),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var fullName = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.LambdaSearch::Function().<lambda#2>", kind: IndexedSymbolKind.Lambda),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var nestedOwnerSuffix = await fixture.Query.SearchSymbolsAsync(
            Request(null, methodPattern: "**.Function().<lambda#2>.<lambda#1>", kind: IndexedSymbolKind.Lambda),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var nestedFullName = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.LambdaSearch::Function().<lambda#2>.<lambda#1>", kind: IndexedSymbolKind.Lambda),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(
            new[]
            {
                "Tokyo.LambdaSearch::<initializer:Changed>.<lambda#1>",
                "Tokyo.LambdaSearch::<initializer:Field>.<lambda#1>",
                "Tokyo.LambdaSearch::<initializer:Property>.<lambda#1>",
                "Tokyo.LambdaSearch::Function().<lambda#1>",
                "Tokyo.LambdaSearch::Function().<lambda#2>.<lambda#1>",
            }.Order(StringComparer.Ordinal),
            suffix.MatchedSymbols.Select(FormatPath));
        Assert.Equal(
            ["Tokyo.LambdaSearch::Function().<lambda#2>"],
            ownerSuffix.MatchedSymbols.Select(FormatPath));
        Assert.Equal(
            ["Tokyo.LambdaSearch::Function().<lambda#2>"],
            fullName.MatchedSymbols.Select(FormatPath));
        Assert.Equal(
            ["Tokyo.LambdaSearch::Function().<lambda#2>.<lambda#1>"],
            nestedOwnerSuffix.MatchedSymbols.Select(FormatPath));
        Assert.Equal(
            nestedOwnerSuffix.MatchedSymbols.Select(symbol => symbol.Id),
            nestedFullName.MatchedSymbols.Select(symbol => symbol.Id));
    }

    [Fact]
    public async Task NameAndSourceFilters_ExcludeBeforeRequiringAllIncludes()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.SearchSymbolsAsync(
            Request(
                "Tokyo.Gamer::Play",
                includes: ["PrintVar(", "\"required\""],
                excludes: ["BlockedMarker("]),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Tokyo.Gamer::Play()"], result.MatchedSymbols.Select(FormatPath));
        Assert.False(result.ShowSource);
    }

    [Fact]
    public async Task ShowSource_ReturnsSourceBackedExecutableOverloadsAndRequestsPresentation()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.ShowSourceAsync(
            "Tokyo.Gamer::Play",
            fixture.PrimaryProfileName,
            TestContext.Current.CancellationToken);

        Assert.True(result.ShowSource);
        Assert.Equal(
            ["Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(string)"],
            result.MatchedSymbols.Select(FormatPath));
        Assert.All(result.MatchedSymbols, symbol => Assert.NotNull(symbol.NormalizedSource));
    }

    [Fact]
    public async Task ShowSource_PreservesExactQueryWhitespaceWhileRestrictingToSourceExecutables()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.ShowSourceAsync(
            " Tokyo.Gamer::Play ",
            fixture.PrimaryProfileName,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(string)"],
            result.MatchedSymbols.Select(FormatPath));
        Assert.All(result.MatchedSymbols, symbol =>
        {
            Assert.True(symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda);
            Assert.NotNull(symbol.DocumentPath);
            Assert.NotNull(symbol.NormalizedSource);
        });
    }

    [Fact]
    public async Task ShowSource_ExtendedPatternReturnsOnlySourceBackedExecutables()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.ShowSourceAsync(
            "Tokyo.Gamer::*",
            fixture.PrimaryProfileName,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "Tokyo.Gamer::Play()",
                "Tokyo.Gamer::Play(string)",
                "Tokyo.Gamer::PrintVar(string)",
            ],
            result.MatchedSymbols.Select(FormatPath));
        Assert.All(result.MatchedSymbols, symbol => Assert.NotNull(symbol.NormalizedSource));
    }

    [Fact]
    public async Task SearchSource_FiltersNormalizedSourceAndRequestsPresentation()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.SearchSourceAsync(
            Request(
                null,
                includes: ["PrintVar(", "\"required\""],
                excludes: ["BlockedMarker("]),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.ShowSource);
        Assert.Contains(result.MatchedSymbols, symbol => FormatPath(symbol) == "Tokyo.SourceBodies::Match()");
        Assert.DoesNotContain(result.MatchedSymbols, symbol => FormatPath(symbol) == "Tokyo.SourceBodies::Excluded()");
    }

    [Fact]
    public async Task SearchSource_UsesOnePhysicalDeclarationAndOnePayloadForSharedDocument()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var payloadReads = 0;
        var repository = fixture.Repository;
        repository.NormalizedSourcePayloadReadObserver = _ => payloadReads++;
        var query = new SemanticQueryService(repository);

        var result = await query.SearchSourceAsync(
            Request(
                null,
                typePattern: "SourceBodies",
                includes: ["\"required\""],
                excludes: ["BlockedMarker("]),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(["Tokyo.SourceBodies::Match()"], result.MatchedSymbols.Select(FormatPath));
        Assert.Equal(1, payloadReads);
    }

    [Fact]
    public async Task SearchSource_PreservesLiteralTextButDoesNotSearchRemovedComments()
    {
        await fixture.BuildTask;

        var literal = await fixture.Query.SearchSourceAsync(
            Request(null, includes: ["/*keep*/ //keep"]),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);
        var comment = await fixture.Query.SearchSourceAsync(
            Request(null, includes: ["comment-only-marker"]),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(literal.MatchedSymbols, symbol => FormatPath(symbol) == "Tokyo.SourceBodies::Match()");
        Assert.DoesNotContain(comment.MatchedSymbols, symbol => FormatPath(symbol) == "Tokyo.SourceBodies::Match()");
    }

    [Fact]
    public async Task SourceFilters_ExcludeSourceLessSymbolsOnlyWhenCriteriaArePresent()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var allMethods = await fixture.Query.SearchSymbolsAsync(
            Request(null, kind: IndexedSymbolKind.Method),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var sourceLess = allMethods.MatchedSymbols.First(symbol => symbol.PreferredDeclarationId is null);
        var filtered = await fixture.Query.SearchSymbolsAsync(
            Request(FormatPath(sourceLess), includes: ["unreachable source term"]),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.DoesNotContain(filtered.MatchedSymbols, symbol => symbol.Id == sourceLess.Id);
    }

    [Fact]
    public async Task SearchSymbols_IsProfileScoped()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var primary = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.SecondaryOnly::Play()"),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var secondary = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.SecondaryOnly::Play()"),
            profileName: fixture.SecondaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Empty(primary.MatchedSymbols);
        Assert.Equal(["Tokyo.SecondaryOnly::Play()"], secondary.MatchedSymbols.Select(FormatPath));
    }

    [Fact]
    public async Task SearchSymbols_UsesCanonicalDeterministicOrdering()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.SearchSymbolsAsync(
            Request("*::Play"),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);
        var expected = result.MatchedSymbols
            .OrderBy(FormatPath, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.DocumentPath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.SourceStart ?? -1)
            .ThenBy(symbol => symbol.Id)
            .Select(symbol => symbol.Id);

        Assert.Equal(expected, result.MatchedSymbols.Select(symbol => symbol.Id));
    }

    [Fact]
    public async Task SearchSource_RejectsAnUnboundedQuery()
    {
        await fixture.BuildTask;

        await Assert.ThrowsAsync<SymbolQueryParseException>(() => fixture.Query.SearchSourceAsync(
            Request(null),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchSource_PropagatesCancellation()
    {
        await fixture.BuildTask;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Query.SearchSourceAsync(
            Request(null, includes: ["PrintVar("]),
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellation.Token));
    }

    private static SymbolSelectionRequest Request(
        string? selector,
        string? namespacePattern = null,
        string? typePattern = null,
        string? methodPattern = null,
        IndexedSymbolKind? kind = null,
        IReadOnlyList<string>? includes = null,
        IReadOnlyList<string>? excludes = null)
    {
        var conditions = new List<TypedCondition>();
        Add(ConditionCategory.Namespace, namespacePattern);
        Add(ConditionCategory.Type, typePattern);
        Add(ConditionCategory.Method, methodPattern);
        conditions.AddRange((includes ?? []).Select(value =>
            new TypedCondition(ConditionCategory.Include, ConditionSyntax.Glob, value)));
        conditions.AddRange((excludes ?? []).Select(value =>
            new TypedCondition(ConditionCategory.Exclude, ConditionSyntax.Glob, value)));
        return new SymbolSelectionRequest(
            selector,
            conditions,
            new SymbolCaseOptions(),
            new FunctionTargetFilter(kind, AsyncStatusFilter.All),
            KindSpecified: kind is not null,
            AsyncStatusSpecified: false);

        void Add(ConditionCategory category, string? value)
        {
            if (value is not null)
            {
                conditions.Add(new TypedCondition(category, ConditionSyntax.Glob, value));
            }
        }
    }

    private static string FormatPath(StoredSymbol symbol) =>
        new SymbolPathFormatter().Format(
            Assert.IsType<SymbolPathData>(symbol.Path),
            new SymbolPathFormatOptions());
}
