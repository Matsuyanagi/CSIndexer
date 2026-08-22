using CsIndex.Query.Symbols;

namespace CsIndex.Query.Tests;

public sealed class BalancedTextScannerTests
{
    [Fact]
    public void SplitTopLevel_IgnoresSeparatorsInsideTypeAndParameterNesting()
    {
        var parts = BalancedTextScanner.SplitTopLevel(
            "N::T::M(global::System.Collections.Generic.Dictionary<string, List<int>>)",
            "::");

        Assert.Equal(
            [
                "N",
                "T",
                "M(global::System.Collections.Generic.Dictionary<string, List<int>>)",
            ],
            parts);
    }

    [Fact]
    public void SplitTopLevel_TreatsBracketPayloadAsOneAtomicSegment()
    {
        var parts = BalancedTextScanner.SplitTopLevel(
            "N::T::[explicit:System.IDisposable.Dispose].M",
            "::");

        Assert.Equal(["N", "T", "[explicit:System.IDisposable.Dispose].M"], parts);
    }

    [Fact]
    public void SplitTopLevel_BalancesNestedGenericTupleArrayAndFunctionPointerSyntax()
    {
        var parts = BalancedTextScanner.SplitTopLevel(
            "(int,string), int[,], delegate* unmanaged[Cdecl]<int,void>, int*",
            ",");

        Assert.Equal(
            [
                "(int,string)",
                " int[,]",
                " delegate* unmanaged[Cdecl]<int,void>",
                " int*",
            ],
            parts);
    }

    [Fact]
    public void SplitTopLevel_IgnoresDotsAndColonsInsideNestedBracketPayloads()
    {
        var parts = BalancedTextScanner.SplitTopLevel(
            "[explicit:global::Game.Contracts.IMap<int[,]>.Map].Local(delegate*<int,void>)",
            ".");

        Assert.Equal(
            [
                "[explicit:global::Game.Contracts.IMap<int[,]>.Map]",
                "Local(delegate*<int,void>)",
            ],
            parts);
    }

    [Theory]
    [InlineData("([)]")]
    [InlineData("<T")]
    [InlineData("T>")]
    [InlineData("(T]")]
    [InlineData("[T)")]
    public void SplitTopLevel_RejectsUnmatchedOrMisorderedDelimiters(string text)
    {
        Assert.Throws<SymbolQueryParseException>(() => BalancedTextScanner.SplitTopLevel(text, "."));
    }

    [Theory]
    [InlineData("[operator:<]")]
    [InlineData("[operator:>]")]
    [InlineData("[operator:<<]")]
    [InlineData("[operator:>>]")]
    public void SplitTopLevel_DoesNotTreatOperatorAngleTokensAsGenericNesting(string text)
    {
        var parts = BalancedTextScanner.SplitTopLevel($"N::T::{text}()", "::");

        Assert.Equal(["N", "T", $"{text}()"], parts);
    }
}
