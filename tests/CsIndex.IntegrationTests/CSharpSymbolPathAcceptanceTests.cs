using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;
using Microsoft.CodeAnalysis;

namespace CsIndex.IntegrationTests;

[Collection(CSharpSymbolPathAcceptanceCollection.Name)]
public sealed class CSharpSymbolPathAcceptanceTests(CSharpSymbolPathAcceptanceFixture fixture)
{
    [Fact]
    public async Task FixtureSmoke_IndexesStandardAndCustomDatabasesThroughTheRealCli()
    {
        await fixture.Ready;

        Assert.Equal(ExitCodes.Success, fixture.StandardIndexResult.ExitCode);
        Assert.Equal(ExitCodes.Success, fixture.CustomIndexResult.ExitCode);
        Assert.True(File.Exists(fixture.StandardDatabasePath));
        Assert.True(File.Exists(fixture.CustomDatabasePath));
    }

    [Fact]
    public async Task PF01_ConcreteCsharpAndExplicitPathsResolveTheSameUnambiguousLogicalId()
    {
        var source = await fixture.GetSymbolAsync("Acceptance.Corpus.LocalOwners::RootOne()", cancellationToken: Token);
        var csharp = fixture.FormatPath(source, fixture.FullCsharp);
        var explicitPath = fixture.FormatPath(source, fixture.FullExplicit);
        var csharpSelector = SymbolPathParser.Parse(csharp);
        var explicitSelector = SymbolPathParser.Parse(explicitPath);
        var explicitNamespace = explicitSelector.Namespace;

        Assert.Equal(SymbolPathStyle.CSharp, csharpSelector.Style);
        Assert.Equal(SymbolPathStyle.Explicit, explicitSelector.Style);
        Assert.Null(csharpSelector.Namespace);
        Assert.NotNull(explicitNamespace);
        Assert.Equal(
            csharpSelector.Type.Segments.Select(segment => segment.IdentifierPattern),
            explicitNamespace!.Segments.Select(segment => segment.IdentifierPattern)
                .Concat(explicitSelector.Type.Segments.Select(segment => segment.IdentifierPattern)));
        Assert.Equal(
            csharpSelector.ExecutableSegments.Select(segment => segment.GetType()),
            explicitSelector.ExecutableSegments.Select(segment => segment.GetType()));
        var csharpRoot = Assert.IsType<NamedExecutableSegmentSelector>(Assert.Single(csharpSelector.ExecutableSegments));
        var explicitRoot = Assert.IsType<NamedExecutableSegmentSelector>(Assert.Single(explicitSelector.ExecutableSegments));
        Assert.Equal(csharpRoot.IdentifierPattern, explicitRoot.IdentifierPattern);
        Assert.Equal(csharpRoot.Arity.GenericState, explicitRoot.Arity.GenericState);
        Assert.Equal(csharpRoot.Arity.ParameterState, explicitRoot.Arity.ParameterState);
        Assert.Equal(csharpRoot.Arity.Parameters.Select(parameter => parameter.SyntaxText),
            explicitRoot.Arity.Parameters.Select(parameter => parameter.SyntaxText));

        var csharpMatches = await ResolveAsync(csharp);
        var explicitMatches = await ResolveAsync(explicitPath);
        Assert.Equal((source.Id, source.StableKey),
            (Assert.Single(csharpMatches).Id, Assert.Single(csharpMatches).StableKey));
        Assert.Equal((source.Id, source.StableKey),
            (Assert.Single(explicitMatches).Id, Assert.Single(explicitMatches).StableKey));
    }

    [Fact]
    public async Task PF02_TwoTopLevelSeparatorsAreUnconditionallyExplicit()
    {
        var parsed = SymbolPathParser.Parse("A::B::C()");
        var matches = await ResolveAsync("A::B::C()");
        var namespaceSelector = parsed.Namespace;

        Assert.Equal(SymbolPathStyle.Explicit, parsed.Style);
        Assert.NotNull(namespaceSelector);
        Assert.Equal("A", Assert.Single(namespaceSelector!.Segments).IdentifierPattern);
        Assert.Equal("B", Assert.Single(parsed.Type.Segments).IdentifierPattern);
        Assert.Equal("C", Assert.IsType<NamedExecutableSegmentSelector>(Assert.Single(parsed.ExecutableSegments)).IdentifierPattern);
        Assert.Equal("A.B::C()", fixture.FormatPath(Assert.Single(matches), fixture.FullCsharp));
    }

    [Theory]
    [InlineData("PF03", "A.B.C::Method(global::System.String)", SymbolPathStyle.CSharp, 0, 3, "global::System.String")]
    [InlineData("PF03", "A.B::C::Method(global::System.String)", SymbolPathStyle.Explicit, 2, 1, "global::System.String")]
    [InlineData("PF03", "A.B::C::[conversion:explicit:global::System.String](global::System.Int32)", SymbolPathStyle.Explicit, 2, 1, "global::System.Int32")]
    [InlineData("PF03", "A.B::C::[explicit:global::System.IDisposable.Dispose](global::System.String)", SymbolPathStyle.Explicit, 2, 1, "global::System.String")]
    public void PF03_TopLevelSeparatorCountingIgnoresNestedGlobalQualifier(
        string rowId,
        string selector,
        SymbolPathStyle expectedStyle,
        int expectedNamespaceSegments,
        int expectedTypeSegments,
        string expectedParameterSyntax)
    {
        var parsed = SymbolPathParser.Parse(selector);

        Assert.Equal("PF03", rowId);
        Assert.Equal(expectedStyle, parsed.Style);
        Assert.Single(parsed.ExecutableSegments);
        Assert.Equal(expectedNamespaceSegments, parsed.Namespace?.Segments.Count ?? 0);
        Assert.Equal(expectedTypeSegments, parsed.Type.Segments.Count);
        var arity = parsed.ExecutableSegments[0] switch
        {
            NamedExecutableSegmentSelector named => named.Arity,
            SpecialExecutableSegmentSelector special => special.Arity,
            _ => throw new Xunit.Sdk.XunitException("PF03 requires a callable segment with parameters."),
        };
        Assert.Equal(ParameterListState.Present, arity.ParameterState);
        Assert.Equal(expectedParameterSyntax, Assert.Single(arity.Parameters).SyntaxText);
        if (parsed.ExecutableSegments[0] is SpecialExecutableSegmentSelector { ConversionTarget: { } target })
        {
            Assert.Equal("global::System.String", target.SyntaxText);
        }
    }

    [Fact]
    public async Task PF04_ExecutableChildrenUseDotAndLegacyColonChildrenAreRejected()
    {
        var local = await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().SameLocal()");
        var anonymous = await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().<lambda#1>");
        Assert.Equal("Acceptance.Corpus.LocalOwners::RootOne().SameLocal()",
            fixture.FormatPath(Assert.Single(local), fixture.FullCsharp));
        Assert.Equal("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().<lambda#1>",
            fixture.FormatPath(Assert.Single(anonymous), fixture.FullCsharp));

        foreach (var legacy in new[]
        {
            "Old.Type::Method()::Local()",
            "Old.Type::Method()::<lambda#1>",
        })
        {
            var exception = Assert.Throws<SymbolQueryParseException>(() => SymbolPathParser.Parse(legacy));
            const string expectedError =
                "Invalid symbol path: Hierarchy identifier 'Method()' is not a valid C# identifier.";
            Assert.Equal(expectedError, exception.Message);

            var result = await fixture.RunStandardCliAsync("symbol", "find", legacy, "--output-format", "json");
            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("PF05", "Acceptance.Signatures.Outer<T>.Inner<U>::NestedGeneric(System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<int?[]>>)", "System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<int?[]>>")]
    [InlineData("PF05", "Acceptance.Signatures.Outer<T>.Inner<U>::RankTwo(int[,])", "int[,]")]
    [InlineData("PF05", "Acceptance.Signatures.Outer<T>.Inner<U>::Pointer(int*)", "int*")]
    [InlineData("PF05", "Acceptance.Signatures.Outer<T>.Inner<U>::Tuple((int,string))", "(int,string)")]
    [InlineData("PF05", "Acceptance.Signatures.Outer<T>.Inner<U>::FunctionPointer(delegate*<int,void>)", "delegate*<int,void>")]
    [InlineData("PF05", "Acceptance.Signatures.Outer<T>.Inner<U>::NullableValue(int?)", "int?")]
    [InlineData("PF05", "Acceptance.Signatures.Outer<T>.Inner<U>::@class()", null)]
    public async Task PF05_ComplexTypeSyntaxAndEscapedIdentifiersDoNotSplitPaths(
        string rowId,
        string selector,
        string? expectedParameterSyntax)
    {
        var parsed = SymbolPathParser.Parse(selector);
        var segment = Assert.IsType<NamedExecutableSegmentSelector>(Assert.Single(parsed.ExecutableSegments));

        Assert.Equal("PF05", rowId);
        Assert.Null(parsed.Namespace);
        Assert.Equal(["Acceptance", "Signatures", "Outer", "Inner"],
            parsed.Type.Segments.Select(value => value.IdentifierPattern));
        Assert.Equal([0, 0, 1, 1], parsed.Type.Segments.Select(value => value.GenericArity));
        Assert.Equal(ParameterListState.Present, segment.Arity.ParameterState);
        if (expectedParameterSyntax is null)
        {
            Assert.Empty(segment.Arity.Parameters);
        }
        else
        {
            Assert.Equal(expectedParameterSyntax, Assert.Single(segment.Arity.Parameters).SyntaxText);
        }

        var output = await ResolveAsync(selector);
        var symbol = Assert.Single(output);
        var copied = fixture.FormatPath(symbol, fixture.FullCsharp);
        Assert.Equal(symbol.Id, Assert.Single(await ResolveAsync(copied)).Id);
    }

    [Theory]
    [InlineData("PF06", "constructor", "Acceptance.Special.SpecialHost::[constructor](int)", 1)]
    [InlineData("PF06", "static-constructor", "Acceptance.Special.SpecialHost::[static-constructor]()", 1)]
    [InlineData("PF06", "destructor", "Acceptance.Special.SpecialHost::[destructor]()", 1)]
    [InlineData("PF06", "operator", "Acceptance.Special.SpecialHost::[operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)", 1)]
    [InlineData("PF06", "checked-operator", "Acceptance.Special.SpecialHost::[checked-operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)", 1)]
    [InlineData("PF06", "conversion", "Acceptance.Special.SpecialHost::[conversion:implicit:int](Acceptance.Special.SpecialHost)", 1)]
    [InlineData("PF06", "conversion", "Acceptance.Special.SpecialHost::[conversion:explicit:string](Acceptance.Special.SpecialHost)", 1)]
    [InlineData("PF06", "checked-conversion", "Acceptance.Special.SpecialHost::[checked-conversion:explicit:double](Acceptance.Special.SpecialHost)", 1)]
    [InlineData("PF06", "get", "Acceptance.Special.SpecialHost::[get:Auto]()", 1)]
    [InlineData("PF06", "set", "Acceptance.Special.SpecialHost::[set:Auto](int)", 1)]
    [InlineData("PF06", "init", "Acceptance.Special.SpecialHost::[init:InitOnly](string)", 1)]
    [InlineData("PF06", "get", "Acceptance.Special.SpecialHost::[get:Item](int)", 1)]
    [InlineData("PF06", "set", "Acceptance.Special.SpecialHost::[set:Item](int,int)", 1)]
    [InlineData("PF06", "add", "Acceptance.Special.SpecialHost::[add:Custom](System.EventHandler?)", 1)]
    [InlineData("PF06", "remove", "Acceptance.Special.SpecialHost::[remove:Custom](System.EventHandler?)", 1)]
    [InlineData("PF06", "explicit", "Acceptance.Special.SpecialHost::[explicit:Acceptance.Special.ISpecial.Run]()", 1)]
    [InlineData("PF06", "get", "Acceptance.Special.SpecialHost::[get:Acceptance.Special.ISpecial.Value]()", 1)]
    [InlineData("PF06", "set", "Acceptance.Special.SpecialHost::[set:Acceptance.Special.ISpecial.Value](int)", 1)]
    [InlineData("PF06", "explicit", "Acceptance.Special.SpecialHost::[explicit:Acceptance.Special.ISpecial.Map]<T>(T)", 1)]
    [InlineData("PF06", "initializer", "Acceptance.Special.InitializerHost::<initializer:@field>", 1)]
    [InlineData("PF06", "top-level-statements", "Program::<top-level-statements>", 1)]
    [InlineData("PF06", "lambda", "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>", 2)]
    [InlineData("PF06", "anonymous-method", "Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>", 2)]
    public async Task PF06_EveryConcreteSpecialSegmentParsesFormatsAndRoundTrips(
        string rowId,
        string tag,
        string concretePath,
        int expectedSegmentCount)
    {
        var parsed = SymbolPathParser.Parse(concretePath);
        var selected = await ResolveAsync(concretePath);

        Assert.Equal("PF06", rowId);
        Assert.Equal(expectedSegmentCount, parsed.ExecutableSegments.Count);
        var actualTag = parsed.ExecutableSegments[^1] switch
        {
            SpecialExecutableSegmentSelector special => special.Tag,
            InitializerExecutableSegmentSelector => "initializer",
            TopLevelStatementsExecutableSegmentSelector => "top-level-statements",
            LambdaExecutableSegmentSelector => "lambda",
            AnonymousMethodExecutableSegmentSelector => "anonymous-method",
            _ => throw new Xunit.Sdk.XunitException("PF06 requires a special or synthetic final segment."),
        };
        Assert.Equal(tag, actualTag);
        var symbol = Assert.Single(selected);
        Assert.Equal(concretePath, fixture.FormatPath(symbol, fixture.FullCsharp));
        Assert.Equal(symbol.Id, Assert.Single(await ResolveAsync(concretePath)).Id);
    }

    [Fact]
    public async Task PF07_IndexedPathFormattingIsExactInAllFourStyles()
    {
        var cases = new[]
        {
            ("Acceptance.Signatures.Outer<T>.Inner<U>::Bare()", new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Bare()",
                "Acceptance.Signatures::Outer<T>.Inner<U>::Bare()",
                "Outer<T>.Inner<U>::Bare()",
                "**::Outer<T>.Inner<U>::Bare()",
            }),
            ("Acceptance.Special.SpecialHost::[constructor](int)", new[]
            {
                "Acceptance.Special.SpecialHost::[constructor](int)",
                "Acceptance.Special::SpecialHost::[constructor](int)",
                "SpecialHost::[constructor](int)",
                "**::SpecialHost::[constructor](int)",
            }),
            ("Acceptance.Corpus.LocalOwners::RootOne().SameLocal()", new[]
            {
                "Acceptance.Corpus.LocalOwners::RootOne().SameLocal()",
                "Acceptance.Corpus::LocalOwners::RootOne().SameLocal()",
                "LocalOwners::RootOne().SameLocal()",
                "**::LocalOwners::RootOne().SameLocal()",
            }),
            ("Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>", new[]
            {
                "Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>",
                "Acceptance.Special::SpecialHost::MixedAnonymous().<anonymous-method#2>",
                "SpecialHost::MixedAnonymous().<anonymous-method#2>",
                "**::SpecialHost::MixedAnonymous().<anonymous-method#2>",
            }),
            ("Acceptance.Special.SpecialHost::[explicit:Acceptance.Special.ISpecial.Map]<T>(T)", new[]
            {
                "Acceptance.Special.SpecialHost::[explicit:Acceptance.Special.ISpecial.Map]<T>(T)",
                "Acceptance.Special::SpecialHost::[explicit:Acceptance.Special.ISpecial.Map]<T>(T)",
                "SpecialHost::[explicit:Acceptance.Special.ISpecial.Map]<T>(T)",
                "**::SpecialHost::[explicit:Acceptance.Special.ISpecial.Map]<T>(T)",
            }),
        };

        foreach (var (path, expected) in cases)
        {
            var symbol = await fixture.GetSymbolAsync(path, cancellationToken: Token);
            Assert.Equal(expected[0], fixture.FormatPath(symbol, fixture.FullCsharp));
            Assert.Equal(expected[1], fixture.FormatPath(symbol, fixture.FullExplicit));
            Assert.Equal(expected[2], fixture.FormatPath(symbol, fixture.ShortCsharp));
            Assert.Equal(expected[3], fixture.FormatPath(symbol, fixture.ShortExplicit));
        }
    }

    [Fact]
    public async Task PF08_EveryConcreteEmittedFullPathCanBeParsedAndRequeried()
    {
        var symbols = await fixture.GetExecutableSymbolsAsync(cancellationToken: Token);
        Assert.NotEmpty(symbols);

        foreach (var symbol in symbols)
        {
            foreach (var style in new[] { fixture.FullCsharp, fixture.FullExplicit })
            {
                var path = fixture.FormatPath(symbol, style);
                _ = SymbolPathParser.Parse(path);
                Assert.Contains(symbol.Id, (await ResolveAsync(path)).Select(match => match.Id));
            }
        }
    }

    [Theory]
    [InlineData("PF09", "A.B.C", "Invalid symbol path: expected exactly one or two top-level '::' separators.")]
    [InlineData("PF09", "A::B::C::D()", "Invalid symbol path: expected exactly one or two top-level '::' separators.")]
    [InlineData("PF09", "::B::C()", "Invalid symbol path: Namespace selector cannot be empty.")]
    [InlineData("PF09", "A::::C()", "Invalid symbol path: Type selector cannot be empty.")]
    [InlineData("PF09", "A::B::", "Invalid symbol path: Executable selector cannot be empty.")]
    [InlineData("PF09", "A.B::C()..D()", "Invalid symbol path: Executable segment cannot be empty.")]
    [InlineData("PF09", "A.B::C(", "Invalid symbol path delimiters: unmatched '(' delimiter.")]
    [InlineData("PF09", "A.B::[constructor()", "Invalid symbol path delimiters: unmatched '[' delimiter.")]
    [InlineData("PF09", "A.B::C<System.String()", "Invalid symbol path delimiters: unmatched '<' delimiter.")]
    [InlineData("PF09", "A.B::C((int,string)", "Invalid symbol path delimiters: unmatched '(' delimiter.")]
    [InlineData("PF09", "A.B::C(delegate*<int,void)", "Invalid symbol path delimiters: misordered ')' delimiter at offset 25.")]
    [InlineData("PF09", "A.B::[not-a-tag]()", "Invalid symbol path: unknown or malformed special callable tag '[not-a-tag]'.")]
    [InlineData("PF09", "A.B::[conversion:implicit:]()", "Invalid symbol path: Conversion target cannot be empty.")]
    [InlineData("PF09", "A.B::C().<lambda#0>", "Invalid symbol path: Lambda ordinal must be a positive integer or '*'.")]
    [InlineData("PF09", "A.B::C()::D()", "Invalid symbol path: Hierarchy identifier 'C()' is not a valid C# identifier.")]
    [InlineData("PF09", "A.B::C<System.String>()", "Invalid symbol path: Named callable generic list placeholder 'System.String' is not a valid C# identifier.")]
    public void PF09_MalformedPathsReportStableErrorCategories(
        string rowId,
        string malformed,
        string expectedMessage)
    {
        var exception = Assert.Throws<SymbolQueryParseException>(() => SymbolPathParser.Parse(malformed));

        Assert.Equal("PF09", rowId);
        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task RC01_NamespaceTypeBoundaryIsNeverGuessed()
    {
        var candidates = await ResolveAsync("Namespace1.Namespace2.Class1.Class2::Method()");
        var paths = candidates.Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp)).ToArray();

        Assert.Equal(
        [
            "Namespace1.Namespace2.Class1.Class2::Method()",
            "Wider.Namespace1.Namespace2.Class1.Class2::Method()",
        ], paths);
        Assert.DoesNotContain("Namespace1.Namespace2.Namespace3.Class2::Method()", paths);
        Assert.Empty(await ResolveAsync("Namespace1.Namespace2.Class1::Class2::Method()"));
    }

    [Fact]
    public async Task RC02_CsharpFullOwnerRemainsSuffixMatching()
    {
        var paths = (await ResolveAsync("Namespace1.Namespace2.Class1.Class2::Method()"))
            .Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp))
            .ToArray();

        Assert.Equal(
        [
            "Namespace1.Namespace2.Class1.Class2::Method()",
            "Wider.Namespace1.Namespace2.Class1.Class2::Method()",
        ],
        paths);
    }

    [Fact]
    public async Task RC03_ExplicitOwnerBoundaryNeverFallsBack()
    {
        var exact = await ResolveAsync("Namespace1.Namespace2::Class1.Class2::Method()");
        var wrongBoundary = await ResolveAsync("Namespace1.Namespace2.Class1::Class2::Method()");

        Assert.Equal("Namespace1.Namespace2.Class1.Class2::Method()", fixture.FormatPath(Assert.Single(exact), fixture.FullCsharp));
        Assert.Empty(wrongBoundary);
    }

    [Fact]
    public async Task RC04_NamespaceOmissionReturnsEveryNamespaceMatch()
    {
        var matches = await ResolveAsync("Class1.Class2::Method");
        var paths = matches.Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp)).ToArray();

        Assert.Equal(
        [
            "Namespace1.Namespace2.Class1.Class2::Method()",
            "Wider.Namespace1.Namespace2.Class1.Class2::Method()",
        ],
        paths);
        Assert.Equal(matches.Select(symbol => symbol.Id),
            matches.OrderBy(symbol => symbol, SymbolCanonicalComparer.Instance).Select(symbol => symbol.Id));
        Assert.All(matches, symbol => Assert.Contains("src/Corpus/Corpus.csproj", symbol.StableKey, StringComparison.Ordinal));
        Assert.DoesNotContain(matches,
            symbol => fixture.FormatPath(symbol, fixture.FullCsharp).Contains("Namespace3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RC05_GlobalAndEscapedGlobalNamespacesResolveDistinctly()
    {
        var global = await ResolveAsync("global::GlobalOwner::GlobalMethod()");
        var literal = await ResolveAsync("@global::LiteralGlobalOwner::LiteralGlobalMethod()");

        Assert.NotEqual(Assert.Single(global).Id, Assert.Single(literal).Id);
        Assert.Equal("GlobalOwner::GlobalMethod()", fixture.FormatPath(Assert.Single(global), fixture.FullCsharp));
        Assert.Equal("@global.LiteralGlobalOwner::LiteralGlobalMethod()", fixture.FormatPath(Assert.Single(literal), fixture.FullCsharp));
        Assert.Equal("global::GlobalOwner::GlobalMethod()", fixture.FormatPath(Assert.Single(global), fixture.FullExplicit));
        Assert.Equal("@global::LiteralGlobalOwner::LiteralGlobalMethod()", fixture.FormatPath(Assert.Single(literal), fixture.FullExplicit));
    }

    [Fact]
    public async Task RC06_EveryLocalAndAnonymousChildRequiresImmediateContainment()
    {
        var directLocal = Assert.Single(await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().SameLocal()"));
        var nestedLocal = Assert.Single(await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().SameLocal()"));
        var deepOnly = Assert.Single(await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().DeepOnly()"));
        var anonymous = Assert.Single(await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().<lambda#1>"));

        Assert.NotEqual(directLocal.Id, nestedLocal.Id);
        Assert.Equal("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().DeepOnly()",
            fixture.FormatPath(deepOnly, fixture.FullCsharp));
        Assert.Equal("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().<lambda#1>",
            fixture.FormatPath(anonymous, fixture.FullCsharp));
        Assert.Empty(await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().DeepOnly()"));
        Assert.Empty(await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().<lambda#1>"));
    }

    [Fact]
    public async Task RC07_SameNamedLocalsDoNotLeakAcrossOwners()
    {
        var rootOne = await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().SameLocal()");
        var rootTwo = await ResolveAsync("Acceptance.Corpus.LocalOwners::RootTwo().SameLocal()");
        var nested = await ResolveAsync("Acceptance.Corpus.LocalOwners::RootOne().NestedOwner().SameLocal()");
        var wildcard = await ResolveAsync("Acceptance.Corpus.LocalOwners::*().SameLocal()");

        Assert.NotEqual(Assert.Single(rootOne).Id, Assert.Single(rootTwo).Id);
        Assert.NotEqual(Assert.Single(rootOne).Id, Assert.Single(nested).Id);
        Assert.Equal([Assert.Single(rootOne).Id, Assert.Single(rootTwo).Id], wildcard.Select(symbol => symbol.Id));
        Assert.DoesNotContain(Assert.Single(nested).Id, wildcard.Select(symbol => symbol.Id));
    }

    [Fact]
    public async Task RC08_ConcreteAndWildcardAnonymousOrdinalsResolve()
    {
        var first = await ResolveAsync("Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>");
        var second = await ResolveAsync("Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>");
        var third = await ResolveAsync("Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#3>");
        var wildcard = await ResolveAsync("Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#*>");
        var everyAnonymousChild = await ResolveAsync("Acceptance.Special.SpecialHost::MixedAnonymous().*");

        Assert.Single(first);
        Assert.Single(second);
        Assert.Single(third);
        Assert.NotEqual(Assert.Single(first).Id, Assert.Single(second).Id);
        Assert.Equal([Assert.Single(first).Id, Assert.Single(third).Id], wildcard.Select(symbol => symbol.Id));
        Assert.Equal(
        [
            "Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>",
            "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>",
            "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#3>",
        ], everyAnonymousChild.Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp)));
    }

    [Fact]
    public async Task RC09_CopiedPathFromEveryOutputStyleIsAccepted()
    {
        var paths = new[]
        {
            "Acceptance.Corpus.LocalOwners::RootOne()",
            "Acceptance.Signatures.Outer<T>.Inner<U>::Bare()",
            "Acceptance.Corpus.LocalOwners::RootOne().SameLocal()",
            "Acceptance.Special.SpecialHost::[constructor](int)",
        };

        foreach (var path in paths)
        {
            var source = await fixture.GetSymbolAsync(path, cancellationToken: Token);
            foreach (var style in new[] { fixture.FullCsharp, fixture.FullExplicit, fixture.ShortCsharp, fixture.ShortExplicit })
            {
                var copied = fixture.FormatPath(source, style);
                var copiedMatches = await ResolveAsync(copied);
                Assert.Equal([source.Id], copiedMatches.Select(symbol => symbol.Id));
            }
        }
    }

    [Fact]
    public async Task RC10_MultiProjectAndProfileCandidatesRemainDeterministic()
    {
        var repository = fixture.CreateRepository();
        var primary = await repository.GetProfileAsync("primary", Token);
        var secondary = await repository.GetProfileAsync("secondary", Token);
        var query = fixture.CreateQueryService();
        var primarySymbols = (await query.FindSymbolsAsync(
            "Acceptance.Duplicates.Twin::Same()", profileName: "primary", cancellationToken: Token)).MatchedSymbols;
        var primaryExplicit = (await query.FindSymbolsAsync(
            "Acceptance.Duplicates::Twin::Same()", profileName: "primary", cancellationToken: Token)).MatchedSymbols;
        var secondarySymbols = (await query.FindSymbolsAsync(
            "Acceptance.Duplicates.Twin::Same()", profileName: "secondary", cancellationToken: Token)).MatchedSymbols;
        var secondaryExplicit = (await query.FindSymbolsAsync(
            "Acceptance.Duplicates::Twin::Same()", profileName: "secondary", cancellationToken: Token)).MatchedSymbols;

        Assert.NotEqual(primary.Id, secondary.Id);
        Assert.Equal(2, primarySymbols.Count);
        Assert.Equal(2, secondarySymbols.Count);
        Assert.Equal(primarySymbols.Select(symbol => symbol.Id), primaryExplicit.Select(symbol => symbol.Id));
        Assert.Equal(secondarySymbols.Select(symbol => symbol.Id), secondaryExplicit.Select(symbol => symbol.Id));
        Assert.Equal(2, primarySymbols.Select(symbol => symbol.Id).Distinct().Count());
        Assert.Equal(2, secondarySymbols.Select(symbol => symbol.Id).Distinct().Count());
        Assert.Empty(primarySymbols.Select(symbol => symbol.Id).Intersect(secondarySymbols.Select(symbol => symbol.Id)));
        Assert.Equal(
        [
            "src/DuplicateA/Twin.cs",
            "src/DuplicateB/Twin.cs",
        ], primarySymbols.Select(symbol => symbol.PreferredDocumentPath));
        Assert.Equal(primarySymbols.Select(symbol => symbol.PreferredDocumentPath),
            secondarySymbols.Select(symbol => symbol.PreferredDocumentPath));
        Assert.Equal(primarySymbols.Select(symbol => symbol.Id),
            primarySymbols.OrderBy(symbol => symbol, SymbolCanonicalComparer.Instance).Select(symbol => symbol.Id));

        var strictIdsByProfile = new Dictionary<string, long[]>(StringComparer.Ordinal)
        {
            ["primary"] = primarySymbols.Select(symbol => symbol.Id).ToArray(),
            ["secondary"] = secondarySymbols.Select(symbol => symbol.Id).ToArray(),
        };
        var caseVariedSelectors = new[]
        {
            "acceptance.duplicates.twin::same()",
            "acceptance.duplicates::twin::same()",
        };
        var presentationCases = new[]
        {
            (Style: "csharp", ShortNames: false),
            (Style: "explicit", ShortNames: false),
            (Style: "csharp", ShortNames: true),
            (Style: "explicit", ShortNames: true),
        };
        foreach (var (profileName, strictIds) in strictIdsByProfile)
        {
            foreach (var caseVariedSelector in caseVariedSelectors)
            {
                var request = new SymbolSelectionRequest(
                    caseVariedSelector,
                    Conditions: [],
                    Case: new SymbolCaseOptions(CaseMode.Ignore, CaseMode.Ignore, CaseMode.Ignore),
                    FunctionFilter: new FunctionTargetFilter(null, AsyncStatusFilter.All),
                    KindSpecified: false,
                    AsyncStatusSpecified: false);
                var caseVaried = await query.SelectRootsAsync(
                    request,
                    profileName,
                    sourceOnly: true,
                    rootGeneratedFilter: GeneratedFilter.Include,
                    cancellationToken: Token);
                Assert.Equal(strictIds, caseVaried.Roots.Select(root => root.Symbol.Id));

                foreach (var presentation in presentationCases)
                {
                    var arguments = new List<string>
                    {
                        "symbol", "find", caseVariedSelector,
                        "--namespace-case", "ignore",
                        "--type-case", "ignore",
                        "--method-case", "ignore",
                        "--symbol-path-style", presentation.Style,
                        "--profile", profileName,
                        "--output-format", "json",
                    };
                    if (presentation.ShortNames)
                    {
                        arguments.Add("--short-names");
                    }

                    var presented = await fixture.RunStandardCliAsync([.. arguments]);
                    Assert.True(
                        presented.ExitCode == ExitCodes.Success,
                        $"{profileName}/{caseVariedSelector}/{presentation} failed: {presented.StandardError}");
                    using var document = JsonDocument.Parse(presented.StandardOutput);
                    Assert.Equal(
                        strictIds,
                        document.RootElement.GetProperty("matched").EnumerateArray()
                            .Select(value => value.GetProperty("id").GetInt64()));
                }
            }
        }
        Assert.All(primarySymbols, symbol => Assert.Contains("profile:primary", symbol.StableKey, StringComparison.Ordinal));
        Assert.All(secondarySymbols, symbol => Assert.Contains("profile:secondary", symbol.StableKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SI01_IndexedMethodArityStatesFollowTheThreeStateRules()
    {
        var cases = new[]
        {
            (Selector: "Acceptance.Signatures.Outer<T>.Inner<U>::Bare", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Bare()",
                "Acceptance.Signatures.Outer<T>.Inner<U>::Bare(int)",
            }),
            (Selector: "Acceptance.Signatures.Outer<T>.Inner<U>::Bare()", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Bare()",
            }),
            (Selector: "Acceptance.Signatures.Outer<T>.Inner<U>::Generic", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic()",
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic(int)",
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>()",
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>(V)",
            }),
            (Selector: "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>()",
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>(V)",
            }),
            (Selector: "Acceptance.Signatures.Outer<T>.Inner<U>::Generic()", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic()",
            }),
            (Selector: "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>()", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>()",
            }),
            (Selector: "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>(V)", Expected: new[]
            {
                "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<V>(V)",
            }),
        };

        foreach (var (selector, expected) in cases)
        {
            var parsed = SymbolPathParser.Parse(selector);
            var matches = await ResolveAsync(selector);
            Assert.IsType<NamedExecutableSegmentSelector>(Assert.Single(parsed.ExecutableSegments));
            Assert.Equal(expected.Length, matches.Count);
            Assert.True(expected.ToHashSet(StringComparer.Ordinal).SetEquals(
                matches.Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp))));
        }
    }

    [Fact]
    public async Task SI02_NestedLocalOmissionStatesAreIndependent()
    {
        var bothRoots = await ResolveAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates.First<V>");
        Assert.Equal(
        [
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>()",
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>(V)",
        ], bothRoots.Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp)));

        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>()",
            fixture.FormatPath(Assert.Single(await ResolveAsync(
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>")), fixture.FullCsharp));
        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>(V)",
            fixture.FormatPath(Assert.Single(await ResolveAsync(
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>")), fixture.FullCsharp));
        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>().Leaf()",
            fixture.FormatPath(Assert.Single(await ResolveAsync(
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>().Leaf")), fixture.FullCsharp));
        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>(V).Leaf(int)",
            fixture.FormatPath(Assert.Single(await ResolveAsync(
                "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>(V).Leaf(int)")), fixture.FullCsharp));

        Assert.Empty(await ResolveAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>(V)"));
        Assert.Empty(await ResolveAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates(int).First<V>()"));
        Assert.Empty(await ResolveAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>().Leaf(int)"));
    }

    [Fact]
    public async Task SI03_AliasAndFrameworkSpellingsShareSemanticIdentity()
    {
        const string aliases =
            "bool,byte,sbyte,short,ushort,int,uint,long,ulong,nint,nuint,char,float,double,decimal,string,object";
        const string frameworks =
            "System.Boolean,System.Byte,System.SByte,System.Int16,System.UInt16,System.Int32,System.UInt32,System.Int64,System.UInt64,System.IntPtr,System.UIntPtr,System.Char,System.Single,System.Double,System.Decimal,System.String,System.Object";
        var alias = Assert.Single(await ResolveAsync(
            $"Acceptance.Signatures.Outer<T>.Inner<U>::AliasMatrix({aliases})"));
        var framework = Assert.Single(await ResolveAsync(
            $"Acceptance.Signatures.Outer<T>.Inner<U>::AliasMatrix({frameworks})"));
        Assert.Equal(alias.Id, framework.Id);
        Assert.Equal($"Acceptance.Signatures.Outer<T>.Inner<U>::AliasMatrix({aliases})",
            fixture.FormatPath(alias, fixture.FullCsharp));

        var pointerAlias = Assert.Single(await ResolveAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::FunctionPointer(delegate*<int,void>)"));
        var pointerFramework = Assert.Single(await ResolveAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::FunctionPointer(delegate*<System.Int32,System.Void>)"));
        Assert.Equal(pointerAlias.Id, pointerFramework.Id);
        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::FunctionPointer(delegate*<int, void>)",
            fixture.FormatPath(pointerAlias, fixture.FullCsharp));
    }

    [Fact]
    public async Task SI04_NonAliasTypesAreFullyQualifiedInCanonicalOutput()
    {
        var paths = new[]
        {
            "Acceptance.Special.SpecialHost::[conversion:explicit:System.Guid](Acceptance.Special.SpecialHost)",
            "Acceptance.Special.SpecialHost::[explicit:Acceptance.Special.ISpecial.Map]<T>(T)",
            "Acceptance.Signatures.Outer<T>.Inner<U>::NestedGeneric(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int? []>>)",
            "Acceptance.Signatures.Outer<T>.Inner<U>::NonAliasShapes((Acceptance.Special.SpecialHost, System.Collections.Generic.List<Acceptance.Special.SpecialHost>),delegate*<Acceptance.Special.SpecialHost, Acceptance.Special.SpecialHost>)",
        };

        foreach (var expectedFullCsharp in paths)
        {
            var matches = await ResolveAsync(expectedFullCsharp);
            Assert.True(matches.Count == 1,
                $"SI04 expected one indexed match for '{expectedFullCsharp}', but found {matches.Count}.");
            var symbol = matches[0];
            Assert.Equal(expectedFullCsharp, fixture.FormatPath(symbol, fixture.FullCsharp));
            foreach (var options in new[]
                     {
                         fixture.FullCsharp, fixture.FullExplicit, fixture.ShortCsharp, fixture.ShortExplicit,
                     })
            {
                var displayed = fixture.FormatPath(symbol, options);
                foreach (var requiredName in expectedFullCsharp.Contains("NonAliasShapes", StringComparison.Ordinal)
                             ? new[] { "Acceptance.Special.SpecialHost", "System.Collections.Generic.List" }
                             : expectedFullCsharp.Contains("NestedGeneric", StringComparison.Ordinal)
                                 ? new[] { "System.Collections.Generic.Dictionary", "System.Collections.Generic.List" }
                                 : expectedFullCsharp.Contains("[explicit:", StringComparison.Ordinal)
                                     ? new[] { "Acceptance.Special.ISpecial" }
                                     : new[] { "System.Guid", "Acceptance.Special.SpecialHost" })
                {
                    Assert.Contains(requiredName, displayed, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task SI05_GenericPlaceholderNamesNormalizeByScopeAndOrdinal()
    {
        const string sourceSelector = "Acceptance.Signatures.Outer<T>.Inner<U>::Scope<V>(T,V)";
        const string renamedSelector = "Acceptance.Signatures.Outer<X>.Inner<Y>::Scope<Z>(X,Z)";
        var parsed = SymbolPathParser.Parse(renamedSelector);
        var executable = Assert.IsType<NamedExecutableSegmentSelector>(Assert.Single(parsed.ExecutableSegments));
        var typeParameter = executable.Arity.Parameters[0].GenericPlaceholders["X"];
        var methodParameter = executable.Arity.Parameters[1].GenericPlaceholders["Z"];

        Assert.Equal((CanonicalGenericPlaceholderScope.Type, 0), (typeParameter.Scope, typeParameter.Ordinal));
        Assert.Equal((CanonicalGenericPlaceholderScope.Method, 0), (methodParameter.Scope, methodParameter.Ordinal));
        var original = Assert.Single(await ResolveAsync(sourceSelector));
        var equivalent = Assert.Single(await ResolveAsync(renamedSelector));
        Assert.Equal(original.Id, equivalent.Id);
        Assert.Equal(sourceSelector, fixture.FormatPath(original, fixture.FullCsharp));
        Assert.Empty(await ResolveAsync(
            "Acceptance.Signatures.Outer<X>.Inner<Y>::Scope<Z>(Z,X)"));
        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::LocalStates().First<V>()",
            fixture.FormatPath(Assert.Single(await ResolveAsync(
                "Acceptance.Signatures.Outer<X>.Inner<Y>::LocalStates().First<Z>()")), fixture.FullCsharp));
    }

    [Theory]
    [InlineData("SI06", "Acceptance.Signatures.Outer<T>.Inner<U>::Generic<System.String>()", "Invalid symbol path: Named callable generic list placeholder 'System.String' is not a valid C# identifier.")]
    [InlineData("SI06", "Acceptance.Signatures.Outer<System.String>.Inner<U>::Bare()", "Invalid symbol path: Hierarchy generic list placeholder 'System.String' is not a valid C# identifier.")]
    public async Task SI06_ConstructedGenericMethodSyntaxIsRejectedNotReinterpreted(
        string rowId,
        string selector,
        string expectedMessage)
    {
        var parserFailure = Assert.Throws<SymbolQueryParseException>(() => SymbolPathParser.Parse(selector));
        var cli = await fixture.RunStandardCliAsync("symbol", "find", selector, "--output-format", "json");

        Assert.Equal("SI06", rowId);
        Assert.Equal(expectedMessage, parserFailure.Message);
        Assert.Equal(ExitCodes.InvalidArguments, cli.ExitCode);
        Assert.Empty(cli.StandardOutput);
        Assert.Contains(expectedMessage, cli.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SI07", "Acceptance.Signatures.Outer<T>.Inner<U>::ByValue(int)", "Acceptance.Signatures.Outer<T>.Inner<U>::ByValue(ref int)")]
    [InlineData("SI07", "Acceptance.Signatures.Outer<T>.Inner<U>::ByRef(ref int)", "Acceptance.Signatures.Outer<T>.Inner<U>::ByRef(int)")]
    [InlineData("SI07", "Acceptance.Signatures.Outer<T>.Inner<U>::ByOut(out int)", "Acceptance.Signatures.Outer<T>.Inner<U>::ByOut(in int)")]
    [InlineData("SI07", "Acceptance.Signatures.Outer<T>.Inner<U>::ByIn(in int)", "Acceptance.Signatures.Outer<T>.Inner<U>::ByIn(out int)")]
    [InlineData("SI07", "Acceptance.Signatures.Outer<T>.Inner<U>::ByRefReadonly(ref readonly int)", "Acceptance.Signatures.Outer<T>.Inner<U>::ByRefReadonly(ref int)")]
    public async Task SI07_RefModesSelectExactlyOneOverload(string rowId, string selector, string wrongModeSelector)
    {
        var matches = await ResolveAsync(selector);

        Assert.Equal("SI07", rowId);
        Assert.Single(matches);
        Assert.Equal(selector, fixture.FormatPath(Assert.Single(matches), fixture.FullCsharp));
        Assert.Empty(await ResolveAsync(wrongModeSelector));
    }

    [Fact]
    public async Task SI08_NullableReferenceAnnotationIsDisplayOnlyIdentityMetadata()
    {
        var plain = await ResolveAsync("Acceptance.Signatures.Outer<T>.Inner<U>::NullableReference(string)");
        var nullable = await ResolveAsync("Acceptance.Signatures.Outer<T>.Inner<U>::NullableReference(string?)");
        var value = await ResolveAsync("Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int)");
        var valueNullable = await ResolveAsync("Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int?)");

        Assert.Equal(Assert.Single(plain).Id, Assert.Single(nullable).Id);
        Assert.NotEqual(Assert.Single(value).Id, Assert.Single(valueNullable).Id);
        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::NullableReference(string?)",
            fixture.FormatPath(Assert.Single(nullable), fixture.FullCsharp));
    }

    [Fact]
    public async Task SI09_ValueNullableArrayPointerTupleAndFunctionPointerShapesStayDistinct()
    {
        var indexedPairs = new[]
        {
            (First: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int)",
                Second: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int?)"),
            (First: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int[])",
                Second: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int[, ])"),
            (First: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int*)",
                Second: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(int**)"),
            (First: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape((int, string))",
                Second: "Acceptance.Signatures.Outer<T>.Inner<U>::Shape((int, (string, int)))"),
        };
        foreach (var (firstSelector, secondSelector) in indexedPairs)
        {
            var firstSymbol = Assert.Single(await ResolveAsync(firstSelector));
            var secondSymbol = Assert.Single(await ResolveAsync(secondSelector));
            Assert.NotEqual(firstSymbol.Id, secondSymbol.Id);
            Assert.Equal(firstSelector, fixture.FormatPath(firstSymbol, fixture.FullCsharp));
            Assert.Equal(secondSelector, fixture.FormatPath(secondSymbol, fixture.FullCsharp));
        }

        var representative = Assert.Single(await ResolveAsync(
            "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(delegate*<int, void>)"));
        Assert.Equal(
            "Acceptance.Signatures.Outer<T>.Inner<U>::Shape(delegate*<int, void>)",
            fixture.FormatPath(representative, fixture.FullCsharp));

        var isolated = await fixture.IndexFunctionPointerOverloadsAsync();
        Assert.True(
            isolated.IndexResult.ExitCode == ExitCodes.Success,
            $"SI09 expected the real CLI to index same-owner function-pointer overloads, but exit code was " +
            $"{isolated.IndexResult.ExitCode}. stderr: {isolated.IndexResult.StandardError}");

        var expectedFunctionPointers = new[]
        {
            "Acceptance.FunctionPointers.CollisionHost::Shape(delegate*<int, void>)",
            "Acceptance.FunctionPointers.CollisionHost::Shape(delegate*<long, void>)",
            "Acceptance.FunctionPointers.CollisionHost::Shape(delegate*<ref int, void>)",
        };
        var isolatedQuery = fixture.CreateQueryService(isolated.DatabasePath, isolated.WorkspacePath);
        var functionPointerSymbols = new List<StoredSymbol>();
        foreach (var expectedPath in expectedFunctionPointers)
        {
            var result = await isolatedQuery.FindSymbolsAsync(expectedPath, cancellationToken: Token);
            var symbol = Assert.Single(result.MatchedSymbols);
            Assert.Equal(expectedPath, fixture.FormatPath(symbol, fixture.FullCsharp));
            functionPointerSymbols.Add(symbol);
        }
        Assert.Equal(3, functionPointerSymbols.Select(symbol => symbol.Id).Distinct().Count());
    }

    [Fact]
    public async Task SI10_ConversionTargetTypeParticipatesInLogicalIdentity()
    {
        var implicitInt = await ResolveAsync("Acceptance.Special.SpecialHost::[conversion:implicit:int](Acceptance.Special.SpecialHost)");
        var explicitString = await ResolveAsync("Acceptance.Special.SpecialHost::[conversion:explicit:string](Acceptance.Special.SpecialHost)");
        var explicitDouble = await ResolveAsync("Acceptance.Special.SpecialHost::[conversion:explicit:double](Acceptance.Special.SpecialHost)");
        var checkedDouble = await ResolveAsync("Acceptance.Special.SpecialHost::[checked-conversion:explicit:double](Acceptance.Special.SpecialHost)");
        var omittedParameters = await ResolveAsync("Acceptance.Special.SpecialHost::[conversion:explicit:string]");

        var symbols = new[]
        {
            Assert.Single(implicitInt), Assert.Single(explicitString),
            Assert.Single(explicitDouble), Assert.Single(checkedDouble),
        };
        Assert.Equal(4, symbols.Select(symbol => symbol.Id).Distinct().Count());
        Assert.Equal(4, symbols.Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp)).Distinct().Count());
        Assert.Equal(Assert.Single(explicitString).Id, Assert.Single(omittedParameters).Id);
    }

    [Fact]
    public async Task CE01_RealIndexContainsEverySourceDeclaredCallableCategory()
    {
        var expected = new[]
        {
            (Path: "Acceptance.Signatures.Outer<T>.Inner<U>::Bare()", File: "Signatures.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Corpus.LocalOwners::RootOne().SameLocal()", File: "Ambiguity.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[constructor](int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[static-constructor]()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.PrimaryClass::[constructor](int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.PrimaryStruct::[constructor](int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.PrimaryRecord::[constructor](int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[destructor]()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[checked-operator:+](Acceptance.Special.SpecialHost,Acceptance.Special.SpecialHost)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[conversion:implicit:int](Acceptance.Special.SpecialHost)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[explicit:Acceptance.Special.ISpecial.Run]()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.ISpecial::Run()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.AbstractHost::Bodyless()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.ExternHost::GetCurrentThreadId()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Partials.PartialHost::DefinitionOnly()", File: "Partials.Definition.cs", Role: DeclarationRole.PartialDefinition),
            (Path: "Acceptance.Special.SpecialHost::[get:Auto]()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[set:Auto](int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[init:InitOnly](string)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[get:Body]()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[set:Body](int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[get:Item](int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[set:Item](int,int)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[add:Custom](System.EventHandler?)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[remove:Custom](System.EventHandler?)", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::[get:Expression]()", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#3>", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.InitializerHost::<initializer:@field>", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.InitializerHost::<initializer:Property>", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Acceptance.Special.InitializerHost::<initializer:Changed>", File: "SpecialCallables.cs", Role: DeclarationRole.Ordinary),
            (Path: "Program::<top-level-statements>", File: "TopLevel.cs", Role: DeclarationRole.Ordinary),
        };

        foreach (var (path, file, role) in expected)
        {
            var symbol = await fixture.GetSymbolAsync(path, cancellationToken: Token);
            Assert.Equal(path, fixture.FormatPath(symbol, fixture.FullCsharp));
            var declarations = await fixture.GetDeclarationsAsync(symbol, cancellationToken: Token);
            Assert.Contains(declarations, declaration =>
                declaration.Role == role &&
                declaration.DocumentPath.EndsWith(file, StringComparison.Ordinal) &&
                declaration.SourceLength > 0 &&
                declaration.NormalizedSource is not null);
        }
    }

    [Fact]
    public async Task CE02_CompilerOnlyCallablesAreAbsentFromTheRealIndex()
    {
        var repository = fixture.CreateRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: Token);
        var symbols = await repository.FindSymbolCandidatesAsync(
            profile.Id, sourceOnly: false, cancellationToken: Token);
        var paths = symbols.Where(symbol => symbol.Path is not null)
            .Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp))
            .ToArray();
        var stableKeys = symbols.Select(symbol => symbol.StableKey).ToArray();

        foreach (var forbiddenPath in new[]
                 {
                     "Acceptance.Special.ImplicitDefaultHost::[constructor]()",
                     "Acceptance.Special.SpecialHost::[add:FieldLike](System.EventHandler?)",
                     "Acceptance.Special.SpecialHost::[remove:FieldLike](System.EventHandler?)",
                     "Acceptance.Special.PrimaryRecord::[get:Value]()",
                     "Acceptance.Special.PrimaryRecord::[set:Value](int)",
                 })
        {
            Assert.DoesNotContain(forbiddenPath, paths);
        }

        foreach (var forbiddenToken in new[]
                 {
                     "k__BackingField", "MoveNext", "DisplayClass", "<>c", "EqualityContract",
                     "PrintMembers", "<Clone>$", "GetHashCode", "Deconstruct",
                 })
        {
            Assert.DoesNotContain(paths, path => path.Contains(forbiddenToken, StringComparison.Ordinal));
            Assert.DoesNotContain(stableKeys, key => key.Contains(forbiddenToken, StringComparison.Ordinal));
        }

        AssertPrimaryRecordSynthesizedEqualityIsAbsent(symbols);
    }

    [Fact]
    public async Task CE03_ExpressionAndAutoAccessorExtractionIsExplicit()
    {
        var paths = (await fixture.GetExecutableSymbolsAsync(cancellationToken: Token))
            .Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp))
            .ToArray();

        Assert.Contains("Acceptance.Special.SpecialHost::[get:Expression]()", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[get:Auto]()", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[set:Auto](int)", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[init:InitOnly](string)", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[get:Item](int)", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[set:Item](int,int)", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[get:Body]()", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[set:Body](int)", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[add:Custom](System.EventHandler?)", paths);
        Assert.Contains("Acceptance.Special.SpecialHost::[remove:Custom](System.EventHandler?)", paths);
        Assert.DoesNotContain("Acceptance.Special.PrimaryRecord::[get:Value]()", paths);
        Assert.DoesNotContain("Acceptance.Special.SpecialHost::[add:FieldLike](System.EventHandler?)", paths);
    }

    [Fact]
    public async Task CE04_PrimaryConstructorsAndRecordSynthesisAreDistinguished()
    {
        var symbols = await fixture.GetExecutableSymbolsAsync(cancellationToken: Token);
        var paths = symbols
            .Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp))
            .ToArray();

        Assert.Contains("Acceptance.Special.PrimaryClass::[constructor](int)", paths);
        Assert.Contains("Acceptance.Special.PrimaryStruct::[constructor](int)", paths);
        Assert.Contains("Acceptance.Special.PrimaryRecord::[constructor](int)", paths);
        foreach (var synthesized in new[]
                 {
                     "PrimaryRecord::[get:Value]", "PrimaryRecord::EqualityContract",
                     "PrimaryRecord::PrintMembers", "PrimaryRecord::GetHashCode",
                     "PrimaryRecord::Deconstruct", "PrimaryRecord::<Clone>$",
                 })
        {
            Assert.DoesNotContain(paths, path => path.Contains(synthesized, StringComparison.Ordinal));
        }


        AssertPrimaryRecordSynthesizedEqualityIsAbsent(symbols);
    }

    [Fact]
    public async Task CE05_LambdaAndAnonymousMethodsShareOneSourceOrderCounter()
    {
        var symbols = new[]
        {
            await fixture.GetSymbolAsync(
                "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>", cancellationToken: Token),
            await fixture.GetSymbolAsync(
                "Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>", cancellationToken: Token),
            await fixture.GetSymbolAsync(
                "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#3>", cancellationToken: Token),
        };

        Assert.Equal(
        [
            "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#1>",
            "Acceptance.Special.SpecialHost::MixedAnonymous().<anonymous-method#2>",
            "Acceptance.Special.SpecialHost::MixedAnonymous().<lambda#3>",
        ],
        symbols.OrderBy(symbol => symbol.PreferredSourceStart)
            .Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp)));
        Assert.Equal(3, symbols.Select(symbol => symbol.Id).Distinct().Count());
    }

    [Fact]
    public async Task CE06_NestedAnonymousCountersResetPerImmediateOwner()
    {
        var expected = new[]
        {
            "Acceptance.Special.SpecialHost::NestedAnonymous().<lambda#1>",
            "Acceptance.Special.SpecialHost::NestedAnonymous().<anonymous-method#2>",
            "Acceptance.Special.SpecialHost::NestedAnonymous().<lambda#1>.<lambda#1>",
            "Acceptance.Special.SpecialHost::NestedAnonymous().<lambda#1>.<anonymous-method#2>",
            "Acceptance.Special.SpecialHost::NestedAnonymous().LocalOwner().<anonymous-method#1>",
            "Acceptance.Special.SpecialHost::NestedAnonymous().<anonymous-method#2>.<lambda#1>",
        };
        var symbols = new List<StoredSymbol>();
        foreach (var path in expected)
        {
            var symbol = Assert.Single(await ResolveAsync(path));
            Assert.Equal(path, fixture.FormatPath(symbol, fixture.FullCsharp));
            symbols.Add(symbol);
        }

        Assert.Equal(expected.Length, symbols.Select(symbol => symbol.Id).Distinct().Count());
        Assert.True(symbols[0].PreferredSourceStart < symbols[1].PreferredSourceStart);
        Assert.True(symbols[2].PreferredSourceStart < symbols[3].PreferredSourceStart);
        Assert.Empty(await ResolveAsync(
            "Acceptance.Special.SpecialHost::NestedAnonymous().<lambda#1>.<lambda#2>"));
        Assert.Empty(await ResolveAsync(
            "Acceptance.Special.SpecialHost::NestedAnonymous().LocalOwner().<anonymous-method#2>"));
    }

    [Fact]
    public async Task CE07_PartialPairCreatesOneLogicalSymbolAndTwoRoleRows()
    {
        var partial = await fixture.GetSymbolAsync("Acceptance.Partials.PartialHost::PairedPartial()", cancellationToken: Token);
        var declarations = await fixture.GetDeclarationsAsync(partial, cancellationToken: Token);
        var (definition, implementation) = await fixture.GetPairedPartialMethodsAsync(Token);
        var canonicalizer = new SymbolCanonicalizer(new AnalysisProfileData
        {
            Name = "acceptance-normalization",
            InputMode = InputMode.Solution,
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        });

        Assert.Equal(2, declarations.Count);
        Assert.True(SymbolEqualityComparer.Default.Equals(
            canonicalizer.NormalizeLogicalMethod(definition),
            canonicalizer.NormalizeLogicalMethod(implementation)));
        Assert.Equal([DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation], declarations.Select(value => value.Role));
        Assert.Equal(2, declarations.Select(value => value.Id).Distinct().Count());
        Assert.NotEqual(declarations[0].DocumentPath, declarations[1].DocumentPath);
        Assert.EndsWith("Partials.Definition.cs", declarations[0].DocumentPath, StringComparison.Ordinal);
        Assert.EndsWith("Partials.Implementation.cs", declarations[1].DocumentPath, StringComparison.Ordinal);
        Assert.Contains("PARTIAL-DEFINITION-MARKER", Assert.IsType<string>(declarations[0].NormalizedSource), StringComparison.Ordinal);
        Assert.Contains("PARTIAL-IMPLEMENTATION-MARKER", Assert.IsType<string>(declarations[1].NormalizedSource), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CE08_PartialImplementationIsThePreferredDeclaration()
    {
        const string selector = "Acceptance.Partials.PartialHost::PairedPartial()";
        var query = fixture.CreateQueryService();
        var partial = await fixture.GetSymbolAsync(selector, cancellationToken: Token);
        var declarations = await fixture.GetDeclarationsAsync(partial, cancellationToken: Token);
        var implementation = Assert.Single(declarations,
            declaration => declaration.Role == DeclarationRole.PartialImplementation);
        var source = await query.ShowSourceAsync(selector, cancellationToken: Token);
        var sourceSymbol = Assert.Single(source.MatchedSymbols);
        var definitions = await query.FindDefinitionsAsync(selector, cancellationToken: Token);
        var callees = await query.FindCalleesAsync(
            selector,
            GeneratedFilter.Include,
            cancellationToken: Token);
        var callerTree = await query.FindCallerTreeAsync(
            selector,
            depth: 1,
            maxNodes: 20,
            cancellationToken: Token);
        var relationProjection = Assert.Single(await query.LoadLogicalRowsAsync(
            callees.Selection,
            includeSourceText: true,
            cancellationToken: Token)).Symbol;
        var graphProjection = Assert.Single(await query.LoadLogicalRowsAsync(
            callerTree.Selection,
            includeSourceText: true,
            cancellationToken: Token)).Symbol;

        Assert.Equal(implementation.Id, partial.PreferredDeclarationId);
        AssertPreferredImplementationProjection(sourceSymbol, implementation);
        Assert.Equal(implementation.Id, Assert.IsType<StoredDeclaration>(sourceSymbol.PreferredDeclaration).Id);
        Assert.Contains(
            "PARTIAL-IMPLEMENTATION-MARKER",
            Assert.IsType<string>(sourceSymbol.NormalizedSource),
            StringComparison.Ordinal);
        Assert.DoesNotContain("PARTIAL-DEFINITION-MARKER", sourceSymbol.NormalizedSource, StringComparison.Ordinal);

        var relationRoot = Assert.Single(callees.Selection.Roots).Symbol;
        Assert.Equal(partial.Id, relationRoot.Id);
        AssertPreferredImplementationProjection(relationRoot, implementation);
        AssertPreferredImplementationSourceProjection(relationProjection, implementation);

        Assert.Equal(partial.Id, callerTree.Root.Id);
        AssertPreferredImplementationProjection(callerTree.Root, implementation);
        AssertPreferredImplementationProjection(
            Assert.Single(callerTree.Selection.Roots).Symbol,
            implementation);
        AssertPreferredImplementationSourceProjection(graphProjection, implementation);

        AssertPreferredImplementationProjection(
            Assert.Single(definitions.Selection.Roots).Symbol,
            implementation);
        Assert.Equal(
            [DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation],
            definitions.Definitions.Select(row => row.Declaration.Role));
        Assert.Equal(
            declarations.Select(declaration => declaration.Id),
            definitions.Definitions.Select(row => row.Declaration.Id));
        Assert.Equal(
            implementation.Id,
            Assert.Single(definitions.Definitions,
                row => row.Declaration.Role == DeclarationRole.PartialImplementation).Declaration.Id);
    }

    [Fact]
    public async Task CE09_DefinitionOnlyPartialRemainsAValidLogicalCallable()
    {
        var partial = await fixture.GetSymbolAsync("Acceptance.Partials.PartialHost::DefinitionOnly()", cancellationToken: Token);
        var declarations = await fixture.GetDeclarationsAsync(partial, cancellationToken: Token);

        var definition = Assert.Single(declarations);
        Assert.Equal(DeclarationRole.PartialDefinition, definition.Role);
        Assert.Equal(definition.Id, partial.PreferredDeclarationId);
        Assert.DoesNotContain(declarations, declaration => declaration.Role == DeclarationRole.PartialImplementation);
        Assert.Equal("Acceptance.Partials.PartialHost::DefinitionOnly()", fixture.FormatPath(partial, fixture.FullCsharp));
    }

    [Fact]
    public async Task CE10_PartialLogicalImplementationIdentityIsUsedOnceAcrossRelations()
    {
        var query = fixture.CreateQueryService();
        var partial = await fixture.GetSymbolAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()", cancellationToken: Token);
        var references = await query.FindReferencesAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            GeneratedFilter.Include,
            cancellationToken: Token);
        var callers = await query.FindCallersAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            GeneratedFilter.Include,
            DispatchSearchMode.Static,
            CallerScope.Direct,
            cancellationToken: Token);
        var callees = await query.FindCalleesAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            GeneratedFilter.Include,
            cancellationToken: Token);
        var overrides = await query.FindOverridesAsync(
            "Acceptance.Partials.PartialBase::PairedPartial()", cancellationToken: Token);
        var interfaceExpansion = await query.FindSymbolsAsync(
            "Acceptance.Partials.IPartialContract::PairedPartial()",
            includeOverrides: true,
            cancellationToken: Token);
        var callerTree = await query.FindCallerTreeAsync(
            "Acceptance.Partials.PartialHost::PairedPartial()",
            depth: 2,
            cancellationToken: Token);

        foreach (var selection in new[]
                 {
                     references.Selection, callers.Selection, callees.Selection, callerTree.Selection,
                 })
        {
            Assert.Equal(partial.Id, Assert.Single(selection.Roots).Symbol.Id);
        }
        Assert.Single(references.Calls, call => call.CalleeSymbolId == partial.Id);
        Assert.Single(callers.Calls, call => call.CalleeSymbolId == partial.Id);
        Assert.Single(callees.Calls, call => call.CallerSymbolId == partial.Id);
        Assert.Single(overrides.Relations, relation => relation.SourceSymbolId == partial.Id);
        Assert.Single(interfaceExpansion.MatchedSymbols, symbol => symbol.Id == partial.Id);
        Assert.Equal(partial.Id, callerTree.Root.Id);
        Assert.Equal(1, callerTree.Nodes.Count(node => node.Symbol.Id == partial.Id));
    }

    [Fact]
    public async Task CE11_DefinitionReturnsBothRolesWhileSymbolListReturnsOneCallable()
    {
        var definition = await fixture.RunStandardCliAsync(
            "definition", "Acceptance.Partials.PartialHost::PairedPartial()", "--output-format", "json");
        var list = await fixture.RunStandardCliAsync(
            "symbol", "list",
            "--namespace-literal", "Acceptance.Partials",
            "--type-literal", "PartialHost",
            "--method-literal", "PairedPartial()",
            "--output-format", "json");

        Assert.Equal(ExitCodes.Success, definition.ExitCode);
        Assert.Equal(ExitCodes.Success, list.ExitCode);
        using var definitionDocument = JsonDocument.Parse(definition.StandardOutput);
        var definitionRoot = definitionDocument.RootElement;
        var definitionMatch = Assert.Single(definitionRoot.GetProperty("matched").EnumerateArray());
        var definitions = definitionRoot.GetProperty("definitions").EnumerateArray().ToArray();
        Assert.Equal(["partial-definition", "partial-implementation"],
            definitions.Select(value => value.GetProperty("declarationRole").GetString()));
        Assert.Equal(2, definitions.Select(value =>
            value.GetProperty("location").GetProperty("path").GetString()).Distinct(StringComparer.Ordinal).Count());

        using var listDocument = JsonDocument.Parse(list.StandardOutput);
        var listed = Assert.Single(listDocument.RootElement.GetProperty("symbols").EnumerateArray());
        Assert.Equal(definitionMatch.GetProperty("id").GetInt64(), listed.GetProperty("id").GetInt64());
        Assert.False(listed.TryGetProperty("declarationRole", out _));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void AssertPrimaryRecordSynthesizedEqualityIsAbsent(
        IReadOnlyList<StoredSymbol> symbols)
    {
        var recordSymbols = symbols
            .Where(symbol =>
                symbol.NamespaceName.Equals("Acceptance.Special", StringComparison.Ordinal) &&
                symbol.TypeSimpleName?.Equals("PrimaryRecord", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(recordSymbols);
        Assert.DoesNotContain(recordSymbols, symbol => symbol.Name is "Equals" or "op_Equality" or "op_Inequality");

        var recordPaths = recordSymbols
            .Where(symbol => symbol.Path is not null)
            .Select(symbol => fixture.FormatPath(symbol, fixture.FullCsharp))
            .ToArray();
        Assert.DoesNotContain(recordPaths, path => path.Contains("PrimaryRecord::Equals(", StringComparison.Ordinal));
        Assert.DoesNotContain(recordPaths, path => path.Contains("PrimaryRecord::[operator:==]", StringComparison.Ordinal));
        Assert.DoesNotContain(recordPaths, path => path.Contains("PrimaryRecord::[operator:!=]", StringComparison.Ordinal));

        foreach (var stableIdentity in new[]
                 {
                     "Acceptance.Special.PrimaryRecord.Equals",
                     "Acceptance.Special.PrimaryRecord.op_Equality",
                     "Acceptance.Special.PrimaryRecord.op_Inequality",
                 })
        {
            Assert.DoesNotContain(
                symbols,
                symbol => symbol.StableKey.Contains(stableIdentity, StringComparison.Ordinal));
        }
    }

    private static void AssertPreferredImplementationProjection(
        StoredSymbol symbol,
        StoredDeclaration implementation)
    {
        Assert.Equal(implementation.Id, symbol.PreferredDeclarationId);
        Assert.Equal(implementation.DocumentPath, symbol.PreferredDocumentPath);
        Assert.Equal(implementation.SourceStart, symbol.PreferredSourceStart);
        Assert.Equal(implementation.DocumentPath, symbol.DocumentPath);
        Assert.Equal(implementation.SourceStart, symbol.SourceStart);
    }

    private static void AssertPreferredImplementationSourceProjection(
        StoredSymbol symbol,
        StoredDeclaration implementation)
    {
        AssertPreferredImplementationProjection(symbol, implementation);
        Assert.Equal(implementation.Id, Assert.IsType<StoredDeclaration>(symbol.PreferredDeclaration).Id);
        Assert.Contains(
            "PARTIAL-IMPLEMENTATION-MARKER",
            Assert.IsType<string>(symbol.NormalizedSource),
            StringComparison.Ordinal);
        Assert.DoesNotContain("PARTIAL-DEFINITION-MARKER", symbol.NormalizedSource, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<StoredSymbol>> ResolveAsync(string selector) =>
        (await fixture.CreateQueryService().FindSymbolsAsync(selector, cancellationToken: Token)).MatchedSymbols;
}
