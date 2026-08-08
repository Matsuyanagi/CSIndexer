using CsIndex.Query.Symbols;

namespace CsIndex.Query.Tests;

public sealed class SourceTextFilterTests
{
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
            comparison: StringComparison.Ordinal);

        Assert.True(matched);
        Assert.False(SourceTextFilter.IsMatch(
            "public void Play(){PrintVar(value);}",
            includes: ["PrintVar(", "return;"],
            excludes: [],
            comparison: StringComparison.Ordinal));
    }

    [Fact]
    public void Excludes_RejectWhenAnyTermMatches()
    {
        var matched = SourceTextFilter.IsMatch(
            "public void Play(){ObsoleteApi();}",
            includes: [],
            excludes: ["Debug.", "ObsoleteApi("],
            comparison: StringComparison.Ordinal);

        Assert.False(matched);
    }

    [Fact]
    public void IncludeOnly_FilterAcceptsMatchingSource()
    {
        var matched = SourceTextFilter.IsMatch(
            "public void Play(){PrintVar(value);}",
            includes: ["PrintVar("],
            excludes: [],
            comparison: StringComparison.Ordinal);

        Assert.True(matched);
    }

    [Fact]
    public void ExcludeOnly_FilterAcceptsSourceWithoutExcludedTerms()
    {
        var matched = SourceTextFilter.IsMatch(
            "public void Play(){PrintVar(value);}",
            includes: [],
            excludes: ["Debug.", "ObsoleteApi("],
            comparison: StringComparison.Ordinal);

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
            comparison: StringComparison.Ordinal));
        Assert.True(SourceTextFilter.IsMatch(
            source,
            includes: ["printvar("],
            excludes: [],
            comparison: StringComparison.OrdinalIgnoreCase));
    }
}
