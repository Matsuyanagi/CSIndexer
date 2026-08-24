using CsIndex.Query.Symbols;

namespace CsIndex.Query.Tests;

public sealed class SourceTextFilterTests
{
    [Fact]
    public void IsMatch_ObservesCancellationBetweenExcludeAndIncludeProbes()
    {
        using var cancellation = new CancellationTokenSource();
        var evaluatedTerms = new List<string>();

        bool ProbeContains(string source, string term, StringComparison comparison)
        {
            evaluatedTerms.Add(term);
            cancellation.Cancel();
            return false;
        }

        Assert.Throws<OperationCanceledException>(() => SourceTextFilter.IsMatch(
            "source",
            includes: ["required"],
            excludes: ["blocked"],
            comparison: StringComparison.Ordinal,
            cancellationToken: cancellation.Token,
            contains: ProbeContains));

        Assert.Equal(["blocked"], evaluatedTerms);
    }

    [Fact]
    public void ExcludeMatch_ShortCircuitsBeforeAnyIncludePredicate()
    {
        var evaluatedTerms = new List<string>();
        bool ProbeContains(string source, string term, StringComparison comparison)
        {
            evaluatedTerms.Add(term);
            return source.Contains(term, comparison);
        }

        var matched = SourceTextFilter.IsMatch(
            "blocked required",
            includes: ["required"],
            excludes: ["blocked"],
            comparison: StringComparison.Ordinal,
            cancellationToken: TestContext.Current.CancellationToken,
            contains: ProbeContains);

        Assert.False(matched);
        Assert.DoesNotContain("required", evaluatedTerms);
    }

    [Fact]
    public void Includes_RequireEveryTerm()
    {
        var matched = SourceTextFilter.IsMatch(
            "public void Play(){PrintVar(value);return;}",
            includes: ["PrintVar(", "return;"],
            excludes: [],
            comparison: StringComparison.Ordinal,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(matched);
        Assert.False(SourceTextFilter.IsMatch(
            "public void Play(){PrintVar(value);}",
            includes: ["PrintVar(", "return;"],
            excludes: [],
            comparison: StringComparison.Ordinal,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Excludes_RejectWhenAnyTermMatches()
    {
        var matched = SourceTextFilter.IsMatch(
            "public void Play(){ObsoleteApi();}",
            includes: [],
            excludes: ["Debug.", "ObsoleteApi("],
            comparison: StringComparison.Ordinal,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(matched);
    }

    [Fact]
    public void IncludeOnly_FilterAcceptsMatchingSource()
    {
        var matched = SourceTextFilter.IsMatch(
            "public void Play(){PrintVar(value);}",
            includes: ["PrintVar("],
            excludes: [],
            comparison: StringComparison.Ordinal,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(matched);
    }

    [Fact]
    public void ExcludeOnly_FilterAcceptsSourceWithoutExcludedTerms()
    {
        var matched = SourceTextFilter.IsMatch(
            "public void Play(){PrintVar(value);}",
            includes: [],
            excludes: ["Debug.", "ObsoleteApi("],
            comparison: StringComparison.Ordinal,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(matched);
    }

    [Fact]
    public void IgnoreCaseComparison_ControlsSourceTermMatching()
    {
        const string source = "public void Play(){PrintVar(value);}";

        Assert.False(SourceTextFilter.IsMatch(
            source,
            includes: ["printvar("],
            excludes: [],
            comparison: StringComparison.Ordinal,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(SourceTextFilter.IsMatch(
            source,
            includes: ["printvar("],
            excludes: [],
            comparison: StringComparison.OrdinalIgnoreCase,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TypedLiteralSourceCondition_IsAnUnanchoredSubstring()
    {
        Assert.True(SourceTextFilter.IsMatch(
            "prefix required suffix",
            includes: [new(ConditionCategory.Include, ConditionSyntax.Literal, "required")],
            excludes: [],
            caseMode: CaseMode.Strict,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TypedGlobSourceCondition_IsAnUnanchoredSubstring()
    {
        Assert.True(SourceTextFilter.IsMatch(
            "prefix value\r\nnext suffix",
            includes: [new(ConditionCategory.Include, ConditionSyntax.Glob, "value*next")],
            excludes: [],
            caseMode: CaseMode.Strict,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TypedRegexSourceCondition_IsNotWholeValueAnchored()
    {
        Assert.True(SourceTextFilter.IsMatch(
            "prefix required suffix",
            includes: [new(ConditionCategory.Include, ConditionSyntax.Regex, "required")],
            excludes: [],
            caseMode: CaseMode.Strict,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TypedSourceCondition_UsesConfiguredCaseMode()
    {
        var condition = new TypedCondition(ConditionCategory.Include, ConditionSyntax.Literal, "need");
        Assert.False(SourceTextFilter.IsMatch(
            "NEED",
            [condition],
            [],
            CaseMode.Strict,
            TestContext.Current.CancellationToken));
        Assert.True(SourceTextFilter.IsMatch(
            "NEED",
            [condition],
            [],
            CaseMode.Ignore,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void TypedGlobSourceCondition_SpansAllNewlineForms(string newline)
    {
        Assert.True(SourceTextFilter.IsMatch(
            $"before{newline}after",
            [new(ConditionCategory.Include, ConditionSyntax.Glob, "before*after")],
            [],
            CaseMode.Strict,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TypedRegexSourceCondition_PropagatesTimeout()
    {
        Assert.Throws<SymbolQueryParseException>(() => SourceTextFilter.IsMatch(
            new string('a', 50_000) + "!",
            [new(ConditionCategory.Include, ConditionSyntax.Regex, "(a+)+$")],
            [],
            CaseMode.Strict,
            TestContext.Current.CancellationToken,
            TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void TypedSourceCondition_ExcludesShortCircuitBeforeIncludesAndChecksCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.False(SourceTextFilter.IsMatch(
            "blocked required",
            [new(ConditionCategory.Include, ConditionSyntax.Literal, "required")],
            [new(ConditionCategory.Exclude, ConditionSyntax.Literal, "blocked")],
            CaseMode.Strict,
            cancellation.Token));

        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceTextFilter.IsMatch(
            "blocked required",
            [],
            [],
            CaseMode.Strict,
            cancellation.Token));
    }
}
