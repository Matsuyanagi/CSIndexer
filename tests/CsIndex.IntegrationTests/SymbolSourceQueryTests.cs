using CsIndex.Core.Model;
using CsIndex.Query.Symbols;

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
            fixture.PrimaryProfileName,
            cancellationToken);

        Assert.Equal(
            expected.MatchedSymbols.Select(symbol => symbol.Id),
            actual.MatchedSymbols.Select(symbol => symbol.Id));
    }

    [Fact]
    public async Task PatternSearch_MatchesWildcardComponentAndRegexRequests()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var wildcard = await fixture.Query.SearchSymbolsAsync(
            Request("*.Gamer::Play"),
            fixture.PrimaryProfileName,
            cancellationToken);
        var components = await fixture.Query.SearchSymbolsAsync(
            Request(null, namespacePattern: "Tokyo", typePattern: "Gamer", methodPattern: "Play"),
            fixture.PrimaryProfileName,
            cancellationToken);
        var regex = await fixture.Query.SearchSymbolsAsync(
            Request("^(Tokyo|Fukuoka)\\.Gamer::P[lr]ay$", useRegex: true),
            fixture.PrimaryProfileName,
            cancellationToken);

        Assert.Equal(
            ["Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(string)"],
            wildcard.MatchedSymbols.Select(symbol => symbol.DisplayName));
        Assert.Equal(
            wildcard.MatchedSymbols.Select(symbol => symbol.DisplayName),
            components.MatchedSymbols.Select(symbol => symbol.DisplayName));
        Assert.Equal(
            ["Fukuoka.Gamer::Pray()", "Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(string)"],
            regex.MatchedSymbols.Select(symbol => symbol.DisplayName));
    }

    [Fact]
    public async Task LambdaPatterns_MatchSuffixOwnerSuffixAndFullName()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var suffix = await fixture.Query.SearchSymbolsAsync(
            Request("*.<lambda#1>", typePattern: "LambdaSearch", kind: IndexedSymbolKind.Lambda),
            fixture.PrimaryProfileName,
            cancellationToken);
        var ownerSuffix = await fixture.Query.SearchSymbolsAsync(
            Request("*Function().<lambda#2>", kind: IndexedSymbolKind.Lambda),
            fixture.PrimaryProfileName,
            cancellationToken);
        var fullName = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.LambdaSearch::Function().<lambda#2>", kind: IndexedSymbolKind.Lambda),
            fixture.PrimaryProfileName,
            cancellationToken);
        var nestedOwnerSuffix = await fixture.Query.SearchSymbolsAsync(
            Request("*Function().<lambda#2>.<lambda#1>", kind: IndexedSymbolKind.Lambda),
            fixture.PrimaryProfileName,
            cancellationToken);
        var nestedFullName = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.LambdaSearch::Function().<lambda#2>.<lambda#1>", kind: IndexedSymbolKind.Lambda),
            fixture.PrimaryProfileName,
            cancellationToken);

        Assert.Equal(
            new[]
            {
                "Tokyo.LambdaSearch::<initializer:Changed>.<lambda#1>",
                "Tokyo.LambdaSearch::<initializer:Field>.<lambda#1>",
                "Tokyo.LambdaSearch::<initializer:Property>.<lambda#1>",
                "Tokyo.LambdaSearch::Function().<lambda#1>",
                "Tokyo.LambdaSearch::Function().<lambda#2>.<lambda#1>",
            }.Order(StringComparer.Ordinal),
            suffix.MatchedSymbols.Select(symbol => symbol.DisplayName));
        Assert.Equal(
            ["Tokyo.LambdaSearch::Function().<lambda#2>"],
            ownerSuffix.MatchedSymbols.Select(symbol => symbol.DisplayName));
        Assert.Equal(
            ["Tokyo.LambdaSearch::Function().<lambda#2>"],
            fullName.MatchedSymbols.Select(symbol => symbol.DisplayName));
        Assert.Equal(
            ["Tokyo.LambdaSearch::Function().<lambda#2>.<lambda#1>"],
            nestedOwnerSuffix.MatchedSymbols.Select(symbol => symbol.DisplayName));
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
            fixture.PrimaryProfileName,
            TestContext.Current.CancellationToken);

        Assert.Equal(["Tokyo.Gamer::Play()"], result.MatchedSymbols.Select(symbol => symbol.DisplayName));
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
            result.MatchedSymbols.Select(symbol => symbol.DisplayName));
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
            result.MatchedSymbols.Select(symbol => symbol.DisplayName));
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
            result.MatchedSymbols.Select(symbol => symbol.DisplayName));
        Assert.All(result.MatchedSymbols, symbol => Assert.NotNull(symbol.NormalizedSource));
    }

    [Fact]
    public async Task SearchSource_FiltersNormalizedSourceAndRequestsPresentation()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.SearchSourceAsync(
            includes: ["PrintVar(", "\"required\""],
            excludes: ["BlockedMarker("],
            ignoreCase: false,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.ShowSource);
        Assert.Contains(result.MatchedSymbols, symbol => symbol.DisplayName == "Tokyo.SourceBodies::Match()");
        Assert.DoesNotContain(result.MatchedSymbols, symbol => symbol.DisplayName == "Tokyo.SourceBodies::Excluded()");
    }

    [Fact]
    public async Task SearchSource_PreservesLiteralTextButDoesNotSearchRemovedComments()
    {
        await fixture.BuildTask;

        var literal = await fixture.Query.SearchSourceAsync(
            includes: ["/*keep*/ //keep"],
            excludes: [],
            ignoreCase: false,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);
        var comment = await fixture.Query.SearchSourceAsync(
            includes: ["comment-only-marker"],
            excludes: [],
            ignoreCase: false,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(literal.MatchedSymbols, symbol => symbol.DisplayName == "Tokyo.SourceBodies::Match()");
        Assert.DoesNotContain(comment.MatchedSymbols, symbol => symbol.DisplayName == "Tokyo.SourceBodies::Match()");
    }

    [Fact]
    public async Task SourceFilters_ExcludeSourceLessSymbolsOnlyWhenCriteriaArePresent()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var allMethods = await fixture.Query.SearchSymbolsAsync(
            Request(null, kind: IndexedSymbolKind.Method),
            fixture.PrimaryProfileName,
            cancellationToken);
        var sourceLess = allMethods.MatchedSymbols.First(symbol => symbol.NormalizedSource is null);
        var filtered = await fixture.Query.SearchSymbolsAsync(
            Request(sourceLess.DisplayName, includes: ["unreachable source term"]),
            fixture.PrimaryProfileName,
            cancellationToken);

        Assert.DoesNotContain(filtered.MatchedSymbols, symbol => symbol.Id == sourceLess.Id);
    }

    [Fact]
    public async Task SearchSymbols_IsProfileScoped()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var primary = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.SecondaryOnly::Play()"),
            fixture.PrimaryProfileName,
            cancellationToken);
        var secondary = await fixture.Query.SearchSymbolsAsync(
            Request("Tokyo.SecondaryOnly::Play()"),
            fixture.SecondaryProfileName,
            cancellationToken);

        Assert.Empty(primary.MatchedSymbols);
        Assert.Equal(["Tokyo.SecondaryOnly::Play()"], secondary.MatchedSymbols.Select(symbol => symbol.DisplayName));
    }

    [Fact]
    public async Task SearchSymbols_UsesCanonicalDeterministicOrdering()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.SearchSymbolsAsync(
            Request("*::Play"),
            fixture.PrimaryProfileName,
            TestContext.Current.CancellationToken);
        var expected = result.MatchedSymbols
            .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
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
            includes: [],
            excludes: [],
            ignoreCase: false,
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
            includes: ["PrintVar("],
            excludes: [],
            ignoreCase: false,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellation.Token));
    }

    private static SymbolSearchRequest Request(
        string? pattern,
        string? namespacePattern = null,
        string? typePattern = null,
        string? methodPattern = null,
        IndexedSymbolKind? kind = null,
        bool useRegex = false,
        bool ignoreCase = false,
        IReadOnlyList<string>? includes = null,
        IReadOnlyList<string>? excludes = null,
        bool showSource = false) => new(
        pattern,
        namespacePattern,
        typePattern,
        methodPattern,
        kind,
        useRegex,
        ignoreCase,
        includes ?? [],
        excludes ?? [],
        showSource);
}
