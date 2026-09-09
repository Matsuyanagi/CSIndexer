using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query.Tests;

public sealed class StructuralGlobMatcherTests
{
    [Fact]
    public void MatchComponent_EmbeddedStarDoesNotCrossAComponent()
    {
        Assert.True(StructuralGlobMatcher.MatchComponent(
            "Play*",
            "Player",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.False(StructuralGlobMatcher.MatchComponent(
            "Play*",
            "X.Play",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MatchComponent_BacktracksAcrossConsecutiveStars()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(StructuralGlobMatcher.MatchComponent(
            "a*ab",
            "aaab",
            StringComparison.Ordinal,
            cancellationToken));
        Assert.True(StructuralGlobMatcher.MatchComponent(
            "*ab*ab",
            "zzabxxab",
            StringComparison.Ordinal,
            cancellationToken));
        Assert.True(StructuralGlobMatcher.MatchComponent(
            "a**b***c",
            "axxbzzc",
            StringComparison.Ordinal,
            cancellationToken));
        Assert.False(StructuralGlobMatcher.MatchComponent(
            "a*b*c",
            "ac",
            StringComparison.Ordinal,
            cancellationToken));
        Assert.True(StructuralGlobMatcher.MatchComponent(
            "A***B",
            "a---b",
            StringComparison.OrdinalIgnoreCase,
            cancellationToken));
    }

    [Fact]
    public void MatchComponent_AllocationIsBounded()
    {
        const long maximumAllocatedBytes = 8 * 1024;
        const string pattern = "a*a*a*a*a*a*a*a*b";
        var candidate = new string('a', 4_096) + "b";

        _ = StructuralGlobMatcher.MatchComponent(
            pattern,
            candidate,
            StringComparison.Ordinal,
            CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var matched = StructuralGlobMatcher.MatchComponent(
            pattern,
            candidate,
            StringComparison.Ordinal,
            CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(matched);
        Assert.True(
            allocated <= maximumAllocatedBytes,
            $"Expected at most {maximumAllocatedBytes} bytes but allocated {allocated} bytes.");
    }

    [Fact]
    public void MatchHierarchy_WholeStarConsumesExactlyOneLevel()
    {
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["src", "*", "Player"],
            ["src", "a", "Player"],
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.False(StructuralGlobMatcher.MatchHierarchy(
            ["src", "*", "Player"],
            ["src", "a", "b", "Player"],
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StructuredExecutableGlob_WholeStarConsumesExactlyOneSegment()
    {
        var compiled = CompileMethod("Root().*.Tail()");

        Assert.True(compiled.MatchesLogical(
            Symbol("Root().Middle().Tail()"),
            TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesLogical(
            Symbol("Root().First().Second().Tail()"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StructuredExecutableMatching_DoesNotSplitBalancedMethodPunctuation()
    {
        var display = "Root(System.Collections.Generic.Dictionary<string,Game.Player>).Local()";
        var identity = "Root(System.Collections.Generic::Dictionary<System::String,Game::Player>).Local()";
        var compiled = CompileMethod(display, ConditionSyntax.Literal);

        Assert.True(compiled.MatchesLogical(
            Symbol(display, identity),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MatchHierarchy_WholeDoubleStarConsumesZeroOneOrManyLevels()
    {
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["src", "**", "Player"],
            ["src", "Player"],
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["src", "**", "Player"],
            ["src", "a", "Player"],
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["src", "**", "Player"],
            ["src", "a", "b", "Player"],
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MatchHierarchy_EmbeddedDoubleStarIsAnOrdinaryStar()
    {
        Assert.True(StructuralGlobMatcher.MatchHierarchy(
            ["Na**me"],
            ["Name"],
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.False(StructuralGlobMatcher.MatchHierarchy(
            ["Na**me"],
            ["Na", "me"],
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MatchFile_NormalizesOnlyPatternSeparatorsAndDistinguishesRecursiveStar()
    {
        Assert.True(StructuralGlobMatcher.MatchFile(
            "src\\Player.cs",
            "src/Player.cs",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.False(StructuralGlobMatcher.MatchFile(
            "src/Player.cs",
            "src\\Player.cs",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.False(StructuralGlobMatcher.MatchFile(
            "src\\*\\Player.cs",
            "src/a/b/Player.cs",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.True(StructuralGlobMatcher.MatchFile(
            "src\\**\\Player.cs",
            "src/a/b/Player.cs",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void MatchSource_StarSpansEveryRequiredNewline(string newline)
    {
        Assert.True(StructuralGlobMatcher.MatchSource(
            "before*after",
            $"before{newline}after",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.True(StructuralGlobMatcher.MatchSource(
            "needle",
            $"prefix{newline}needle suffix",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MatchSource_IsUnanchoredAndCaseAware()
    {
        Assert.True(StructuralGlobMatcher.MatchSource(
            "need*",
            "prefix needed suffix",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.True(StructuralGlobMatcher.MatchSource(
            "need**suffix",
            "prefix need middle suffix tail",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.False(StructuralGlobMatcher.MatchSource(
            "need",
            "NEED",
            StringComparison.Ordinal,
            TestContext.Current.CancellationToken));
        Assert.True(StructuralGlobMatcher.MatchSource(
            "need",
            "NEED",
            StringComparison.OrdinalIgnoreCase,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MatchSource_BacktracksAcrossConsecutiveStarsWithoutAnchoring()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(StructuralGlobMatcher.MatchSource(
            "a*ab",
            "prefix aaab suffix",
            StringComparison.Ordinal,
            cancellationToken));
        Assert.True(StructuralGlobMatcher.MatchSource(
            "ab**cd***ef",
            "prefix abXXcdYYef suffix",
            StringComparison.Ordinal,
            cancellationToken));
        Assert.False(StructuralGlobMatcher.MatchSource(
            "ab*cd*ef",
            "prefix abXXcd suffix",
            StringComparison.Ordinal,
            cancellationToken));
    }

    [Fact]
    public void MatchSource_AllocationIsBounded()
    {
        const long maximumAllocatedBytes = 8 * 1024;
        const string pattern = "a*a*a*a*a*a*a*a*b";
        var source = "prefix " + new string('a', 4_096) + "b suffix";

        _ = StructuralGlobMatcher.MatchSource(
            pattern,
            source,
            StringComparison.Ordinal,
            CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var matched = StructuralGlobMatcher.MatchSource(
            pattern,
            source,
            StringComparison.Ordinal,
            CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(matched);
        Assert.True(
            allocated <= maximumAllocatedBytes,
            $"Expected at most {maximumAllocatedBytes} bytes but allocated {allocated} bytes.");
    }

    [Fact]
    public void StructuredExecutableMatching_KeepsOperatorStarAndSyntheticOrdinalsTyped()
    {
        var multiplication = CompileMethod("[operator:*](int,int)", ConditionSyntax.Literal);
        var lambda = CompileMethod("Root().<lambda#*>");
        var anonymousMethod = CompileMethod("Root().<anonymous-method#*>");

        Assert.True(multiplication.MatchesLogical(
            Symbol(
                "[operator:*](int,int)",
                "[operator:*](System::Int32,System::Int32)"),
            TestContext.Current.CancellationToken));
        Assert.True(lambda.MatchesLogical(
            Symbol("Root().<lambda#2>"),
            TestContext.Current.CancellationToken));
        Assert.True(anonymousMethod.MatchesLogical(
            Symbol("Root().<anonymous-method#3>"),
            TestContext.Current.CancellationToken));
    }

    private static CompiledTypedConditions CompileMethod(
        string pattern,
        ConditionSyntax syntax = ConditionSyntax.Glob) =>
        TypedConditionCompiler.Compile(new SymbolSelectionRequest(
            Selector: null,
            Conditions: [new(ConditionCategory.Method, syntax, pattern)],
            Case: new(),
            FunctionFilter: new(null, AsyncStatusFilter.All),
            KindSpecified: false,
            AsyncStatusSpecified: false));

    private static StoredSymbol Symbol(
        string executableDisplayPath,
        string? executableIdentityPath = null)
    {
        executableIdentityPath ??= executableDisplayPath;
        var path = new SymbolPathData(
            "Game",
            "Host",
            "Host",
            executableDisplayPath,
            executableIdentityPath,
            executableDisplayPath,
            executableIdentityPath,
            CallablePathSegmentKind.Named);
        return new StoredSymbol(
            Id: 1,
            StableKey: "symbol",
            Kind: IndexedSymbolKind.Method,
            Name: "Run",
            NamespaceName: "Game",
            TypeSimpleName: "Host",
            TypeMetadataName: "Host",
            ContainingSymbolId: null,
            Arity: 0,
            ParameterCount: 0,
            MethodKind: 0,
            IsStatic: false,
            IsAbstract: false,
            IsVirtual: false,
            IsOverride: false,
            AsyncRole: AsyncRole.None,
            AsyncInvolvementDepth: null,
            AsyncNextSymbolId: null,
            ReturnTypeKey: null,
            DocumentPath: null,
            SourceStart: null,
            SourceLength: null,
            IsGenerated: false,
            AssemblyName: null,
            Parameters: [],
            TypeKind: null,
            Accessibility: null)
        {
            Path = path,
        };
    }
}
