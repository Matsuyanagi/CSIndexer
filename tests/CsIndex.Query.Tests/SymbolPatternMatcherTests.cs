using System.Globalization;
using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query.Tests;

public sealed class SymbolPatternMatcherTests
{
    [Theory]
    [InlineData("*.Gamer::Play")]
    [InlineData("Tokyo.*::Play")]
    [InlineData("Tokyo.Gamer::P*l*y")]
    public void WildcardPattern_MatchesParameterlessMethodDisplay(string pattern)
    {
        var matcher = new SymbolPatternMatcher(Request(pattern));

        Assert.True(matcher.IsMatch(Symbol("Tokyo.Gamer::Play()")));
    }

    [Fact]
    public void WildcardPattern_TreatsCharactersOtherThanStarAsLiterals()
    {
        var matcher = new SymbolPatternMatcher(Request("Tokyo.Gamer::P.lay"));

        Assert.False(matcher.IsMatch(Symbol("Tokyo.Gamer::Play()")));
        Assert.True(matcher.IsMatch(Symbol("Tokyo.Gamer::P.lay()", name: "P.lay")));
    }

    [Fact]
    public void PatternWithoutParameterList_MatchesEveryOverload()
    {
        var matcher = new SymbolPatternMatcher(Request("Tokyo.Gamer::Play"));

        Assert.True(matcher.IsMatch(Symbol("Tokyo.Gamer::Play()")));
        Assert.True(matcher.IsMatch(Symbol("Tokyo.Gamer::Play(System.String)")));
    }

    [Fact]
    public void PatternWithParameterList_MatchesOnlyTheCompleteSignature()
    {
        var matcher = new SymbolPatternMatcher(Request("Tokyo.Gamer::Play(System.String)"));

        Assert.False(matcher.IsMatch(Symbol("Tokyo.Gamer::Play()")));
        Assert.True(matcher.IsMatch(Symbol("Tokyo.Gamer::Play(System.String)")));
    }

    [Fact]
    public void ComponentPatterns_AreCombinedWithAndSemantics()
    {
        var matcher = new SymbolPatternMatcher(Request(
            pattern: null,
            namespacePattern: "Tokyo.*",
            typePattern: "Gamer",
            methodPattern: "Play"));

        Assert.True(matcher.IsMatch(Symbol("Tokyo.Area.Gamer::Play()", namespaceName: "Tokyo.Area")));
        Assert.False(matcher.IsMatch(Symbol("Fukuoka.Gamer::Play()", namespaceName: "Fukuoka")));
        Assert.False(matcher.IsMatch(Symbol(
            "Tokyo.Area.Player::Play()",
            namespaceName: "Tokyo.Area",
            typeSimpleName: "Player")));
        Assert.False(matcher.IsMatch(Symbol(
            "Tokyo.Area.Gamer::Pause()",
            namespaceName: "Tokyo.Area",
            name: "Pause")));
    }

    [Fact]
    public void RegexPattern_MatchesTheAnchoredRequirementExample()
    {
        var matcher = new SymbolPatternMatcher(Request(
            "^(Tokyo|Fukuoka)\\.Gamer::P[lr]ay$",
            useRegex: true));

        Assert.True(matcher.IsMatch(Symbol("Tokyo.Gamer::Play()")));
        Assert.True(matcher.IsMatch(Symbol("Fukuoka.Gamer::Pray()", namespaceName: "Fukuoka", name: "Pray")));
        Assert.False(matcher.IsMatch(Symbol("Sapporo.Gamer::Play()", namespaceName: "Sapporo")));
    }

    [Fact]
    public void RegexPattern_UsesCultureInvariantIgnoreCaseMatching()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var matcher = new SymbolPatternMatcher(Request(
                "^Tokyo\\.I::Play$",
                useRegex: true,
                ignoreCase: true));

            Assert.False(matcher.IsMatch(Symbol(
                "Tokyo.ı::Play()",
                typeSimpleName: "ı")));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void InvalidRegex_ReportsTheAffectedPattern()
    {
        var exception = Assert.Throws<SymbolQueryParseException>(
            () => new SymbolPatternMatcher(Request("(", useRegex: true)));

        Assert.Contains("pattern", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegexTimeout_IsReportedInsteadOfRunningUnbounded()
    {
        var matcher = new SymbolPatternMatcher(
            Request("^(a+)+$", useRegex: true),
            TimeSpan.FromMilliseconds(1));
        var symbol = Symbol(new string('a', 20_000) + "!", kind: IndexedSymbolKind.Lambda);

        var exception = Assert.Throws<SymbolQueryParseException>(() => matcher.IsMatch(symbol));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LambdaSuffixPattern_MatchesAnyOwner()
    {
        var matcher = new SymbolPatternMatcher(Request("::<lambda#1>"));

        Assert.True(matcher.IsMatch(Symbol(
            "Tokyo.Gamer::Function()::<lambda#1>",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#1>")));
    }

    [Fact]
    public void LambdaSuffixPattern_UsesIgnoreCaseForTheMarkerAndCanonicalName()
    {
        var matcher = new SymbolPatternMatcher(Request("::<LAMBDA#1>", ignoreCase: true));

        Assert.True(matcher.IsMatch(Symbol(
            "Tokyo.Gamer::Function()::<lambda#1>",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#1>")));
    }

    [Fact]
    public void NonLambdaPattern_DoesNotMatchAnUnrelatedLambda()
    {
        var matcher = new SymbolPatternMatcher(Request("*.Gamer::Play"));

        Assert.False(matcher.IsMatch(Symbol(
            "Alpha.AsyncPlayer::WithAsyncLambda()::<lambda#1>",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#1>")));
    }

    [Fact]
    public void LambdaOwnerSuffixPattern_MatchesTheOwningFunction()
    {
        var matcher = new SymbolPatternMatcher(Request("Function()::<lambda#2>"));

        Assert.True(matcher.IsMatch(Symbol(
            "Tokyo.Gamer::Function()::<lambda#2>",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#2>")));
        Assert.False(matcher.IsMatch(Symbol(
            "Tokyo.Gamer::Other()::<lambda#2>",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#2>")));
    }

    [Fact]
    public void FullLambdaPattern_MatchesTheCanonicalDisplayName()
    {
        var matcher = new SymbolPatternMatcher(Request("Tokyo.Gamer::Function()::<lambda#2>"));

        Assert.True(matcher.IsMatch(Symbol(
            "Tokyo.Gamer::Function()::<lambda#2>",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#2>")));
    }

    [Fact]
    public void KindConstraint_RejectsOtherExecutableKinds()
    {
        var matcher = new SymbolPatternMatcher(Request("Tokyo.Gamer::Play", kind: IndexedSymbolKind.Lambda));

        Assert.False(matcher.IsMatch(Symbol("Tokyo.Gamer::Play()")));
    }

    [Fact]
    public void AsyncStatus_MatchesDirectRoleForExecutableSymbols()
    {
        Assert.True(Matcher(AsyncStatusFilter.Async).IsMatch(Method(asyncRole: AsyncRole.ReturnsAwaitable)));
        Assert.False(Matcher(AsyncStatusFilter.Async).IsMatch(Method(asyncRole: AsyncRole.None)));
        Assert.True(Matcher(AsyncStatusFilter.Sync).IsMatch(Method(asyncRole: AsyncRole.None, depth: 1)));
        Assert.True(Matcher(AsyncStatusFilter.Async).IsMatch(Lambda(asyncRole: AsyncRole.DeclaredAsync)));
        Assert.False(Matcher(AsyncStatusFilter.Sync).IsMatch(Type(asyncRole: AsyncRole.None)));
        Assert.True(Matcher(AsyncStatusFilter.All).IsMatch(Type(asyncRole: AsyncRole.None)));
    }

    [Fact]
    public void KindAndAsyncStatus_AreCombinedWithAndSemantics()
    {
        var matcher = Matcher(AsyncStatusFilter.Async, IndexedSymbolKind.Method);

        Assert.True(matcher.IsMatch(Method(asyncRole: AsyncRole.ReturnsAwaitable)));
        Assert.False(matcher.IsMatch(Lambda(asyncRole: AsyncRole.ReturnsAwaitable)));
    }

    [Fact]
    public void RegexMode_LeavesRegexAsteriskAsRegexSyntax()
    {
        var matcher = new SymbolPatternMatcher(Request("^Tokyo\\.Gamer::P.*$", useRegex: true));

        Assert.True(matcher.IsMatch(Symbol("Tokyo.Gamer::Play()")));
    }

    private static SymbolSearchRequest Request(
        string? pattern,
        string? namespacePattern = null,
        string? typePattern = null,
        string? methodPattern = null,
        IndexedSymbolKind? kind = null,
        bool useRegex = false,
        bool ignoreCase = false,
        AsyncStatusFilter asyncStatus = AsyncStatusFilter.All) => new(
        pattern,
        namespacePattern,
        typePattern,
        methodPattern,
        kind,
        useRegex,
        ignoreCase,
        [],
        [],
        ShowSource: false,
        AsyncStatus: asyncStatus);

    private static SymbolPatternMatcher Matcher(
        AsyncStatusFilter asyncStatus,
        IndexedSymbolKind? kind = null) =>
        new(Request(pattern: null, kind: kind, asyncStatus: asyncStatus));

    private static StoredSymbol Method(AsyncRole asyncRole, int? depth = null) =>
        Symbol("Tokyo.Gamer::Play()", asyncRole: asyncRole, depth: depth);

    private static StoredSymbol Lambda(AsyncRole asyncRole, int? depth = null) =>
        Symbol(
            "Tokyo.Gamer::Play()::<lambda#1>",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#1>",
            asyncRole: asyncRole,
            depth: depth);

    private static StoredSymbol Type(AsyncRole asyncRole) =>
        Symbol(
            "Tokyo.Gamer",
            kind: IndexedSymbolKind.Type,
            name: "Gamer",
            asyncRole: asyncRole);

    private static StoredSymbol Symbol(
        string displayName,
        IndexedSymbolKind kind = IndexedSymbolKind.Method,
        string namespaceName = "Tokyo",
        string? typeSimpleName = "Gamer",
        string name = "Play",
        AsyncRole asyncRole = AsyncRole.None,
        int? depth = null) => new StoredSymbol(
        Id: 1,
        StableKey: "symbol-key",
        Kind: kind,
        Name: name,
        NamespaceName: namespaceName,
        TypeSimpleName: typeSimpleName,
        TypeMetadataName: null,
        FullyQualifiedName: displayName,
        DisplayName: displayName,
        ContainingSymbolId: null,
        Arity: 0,
        ParameterCount: null,
        MethodKind: null,
        IsStatic: false,
        IsAbstract: false,
        IsVirtual: false,
        IsOverride: false,
        AsyncRole: asyncRole,
        AsyncInvolvementDepth: depth,
        AsyncNextSymbolId: null,
        ReturnTypeKey: null,
        NormalizedSource: null,
        NormalizedSourceHash: null,
        DocumentPath: null,
        SourceStart: null,
        SourceLength: null,
        IsGenerated: false,
        AssemblyName: null,
        Parameters: [],
        TypeKind: null,
        Accessibility: null) with
        {
            Path = CreatePath(displayName, kind, namespaceName, typeSimpleName),
        };

    private static SymbolPathData CreatePath(
        string displayName,
        IndexedSymbolKind kind,
        string namespaceName,
        string? typeSimpleName)
    {
        var separator = displayName.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
        {
            return new SymbolPathData(
                string.Empty,
                displayName,
                displayName,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                CallablePathSegmentKind.Named);
        }

        var executable = displayName[(separator + 2)..];
        return new SymbolPathData(
            namespaceName,
            typeSimpleName ?? displayName,
            typeSimpleName ?? displayName,
            executable,
            executable,
            executable,
            executable,
            kind == IndexedSymbolKind.Lambda
                ? CallablePathSegmentKind.Lambda
                : CallablePathSegmentKind.Named);
    }
}
