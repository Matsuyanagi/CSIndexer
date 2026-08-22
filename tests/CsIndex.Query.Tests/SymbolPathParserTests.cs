using CsIndex.Core.Symbols;
using CsIndex.Query.Symbols;
using Microsoft.CodeAnalysis;

namespace CsIndex.Query.Tests;

public sealed class SymbolPathParserTests
{
    [Fact]
    public void Parse_CSharpPathBuildsTypeAndExecutableHierarchy()
    {
        var selector = SymbolPathParser.Parse(
            "Game.Core.Player.Inventory::Load(int).Validate(string).<lambda#1>");

        Assert.Equal(SymbolPathStyle.CSharp, selector.Style);
        Assert.Null(selector.Namespace);
        Assert.Equal(["Game", "Core", "Player", "Inventory"],
            selector.Type.Segments.Select(segment => segment.IdentifierPattern));
        Assert.Equal(
            ["Load", "Validate"],
            selector.ExecutableSegments.OfType<NamedExecutableSegmentSelector>()
                .Select(segment => segment.IdentifierPattern));
        Assert.IsType<LambdaExecutableSegmentSelector>(selector.ExecutableSegments[2]);
        Assert.Equal(1, ((LambdaExecutableSegmentSelector)selector.ExecutableSegments[2]).Ordinal);
    }

    [Fact]
    public void Parse_ExplicitPathPreservesNamespaceTypeBoundary()
    {
        var selector = SymbolPathParser.Parse(
            "Game.Core::Player.Inventory::Load(int).Validate(string).<lambda#1>");

        Assert.Equal(SymbolPathStyle.Explicit, selector.Style);
        Assert.Equal(["Game", "Core"],
            selector.Namespace!.Segments.Select(segment => segment.IdentifierPattern));
        Assert.Equal(["Player", "Inventory"],
            selector.Type.Segments.Select(segment => segment.IdentifierPattern));
    }

    [Fact]
    public void Parse_TwoSeparatorsAlwaysSelectsExplicitForm()
    {
        var selector = SymbolPathParser.Parse("A::B::C()");

        Assert.Equal(SymbolPathStyle.Explicit, selector.Style);
        Assert.Equal(["A"], selector.Namespace!.Segments.Select(segment => segment.IdentifierPattern));
        Assert.Equal(["B"], selector.Type.Segments.Select(segment => segment.IdentifierPattern));
        Assert.Equal("C", Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments[0]).IdentifierPattern);
    }

    [Fact]
    public void Parse_OneSeparatorAcceptsBareExecutableSegment()
    {
        var selector = SymbolPathParser.Parse("N::T");

        Assert.Equal(SymbolPathStyle.CSharp, selector.Style);
        Assert.Equal("N", selector.Type.Segments.Single().IdentifierPattern);
        var executable = Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments.Single());
        Assert.Equal("T", executable.IdentifierPattern);
        Assert.Equal(GenericListState.Omitted, executable.Arity.GenericState);
        Assert.Equal(ParameterListState.Omitted, executable.Arity.ParameterState);
    }

    [Fact]
    public void Parse_GlobalNamespaceAndEscapedGlobalAreDistinct()
    {
        var global = SymbolPathParser.Parse("global::Program::<top-level-statements>");
        var escaped = SymbolPathParser.Parse("@global::Program::Run()");
        var recursiveGlob = SymbolPathParser.Parse("**::Program::Run()");

        Assert.NotNull(global.Namespace);
        Assert.Empty(global.Namespace!.Segments);
        Assert.Equal("Program", global.Type.Segments[0].IdentifierPattern);
        Assert.IsType<TopLevelStatementsExecutableSegmentSelector>(global.ExecutableSegments[0]);

        Assert.Equal(["global"], escaped.Namespace!.Segments.Select(segment => segment.IdentifierPattern));
        Assert.Equal(PatternMode.Glob, escaped.Namespace.Segments[0].PatternMode);

        Assert.Equal("**", recursiveGlob.Namespace!.Segments.Single().IdentifierPattern);
    }

    [Fact]
    public void Parse_AcceptsNestedGenericArrayPointerNullableAndFunctionPointerTypes()
    {
        var selector = SymbolPathParser.Parse(
            "N::Outer<T>.Inner<U>::M(" +
            "System.Collections.Generic.List<string?>,(int,string),int[,],int*," +
            "delegate* unmanaged[Cdecl]<int,void>," +
            "System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<int?>>[])");
        var method = Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments[0]);

        Assert.Equal(6, method.Arity.Parameters.Count);
        Assert.Equal(
            [
                "System.Collections.Generic.List<string?>",
                "(int,string)",
                "int[,]",
                "int*",
                "delegate* unmanaged[Cdecl]<int,void>",
                "System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<int?>>[]",
            ],
            method.Arity.Parameters.Select(parameter => parameter.SyntaxText));
    }

    [Fact]
    public void Parse_IgnoresGlobalQualifierInsideParameterType()
    {
        var selector = SymbolPathParser.Parse(
            "N::T::M(global::System.String)." +
            "[conversion:implicit:global::System.Int32](global::System.String)." +
            "[explicit:global::System.IDisposable.Dispose]()");

        Assert.Equal(SymbolPathStyle.Explicit, selector.Style);
        var method = Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments[0]);
        Assert.Equal("global::System.String", method.Arity.Parameters.Single().SyntaxText);

        var conversion = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[1]);
        Assert.Equal("global::System.Int32", conversion.ConversionTarget!.SyntaxText);

        var explicitMethod = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[2]);
        Assert.Equal("global::System.IDisposable", explicitMethod.Member!.ContainingTypePattern);
        Assert.Equal("Dispose", explicitMethod.Member.MemberPattern);
    }

    [Fact]
    public void Parse_ParsesExplicitInterfacePayloadWithBalancedDotsAndColons()
    {
        var selector = SymbolPathParser.Parse("N::T::[explicit:System.IDisposable.Dispose]()");
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[0]);

        Assert.Equal("explicit", special.Tag);
        Assert.NotNull(special.Member);
        Assert.Equal("System.IDisposable", special.Member!.ContainingTypePattern);
        Assert.NotNull(special.Member.ContainingType);
        Assert.Equal("Dispose", special.Member.MemberPattern);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("!")]
    [InlineData("~")]
    [InlineData("++")]
    [InlineData("--")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("%")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("^")]
    [InlineData("<<")]
    [InlineData(">>")]
    [InlineData(">>>")]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData(">=")]
    [InlineData("+=")]
    [InlineData("-=")]
    [InlineData("*=")]
    [InlineData("/=")]
    [InlineData("%=")]
    [InlineData("&=")]
    [InlineData("|=")]
    [InlineData("^=")]
    [InlineData("<<=")]
    [InlineData(">>=")]
    [InlineData(">>>=")]
    public void Parse_OperatorCatalogTreatsStarAsLiteralToken(string token)
    {
        var selector = SymbolPathParser.Parse($"N::T::[operator:{token}]()");
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[0]);

        Assert.Equal("operator", special.Tag);
        Assert.Equal(token, special.OperatorToken);
        Assert.Equal(PatternMode.Literal, special.PatternMode);
    }

    [Theory]
    [InlineData("[constructor]", "constructor")]
    [InlineData("[static-constructor]", "static-constructor")]
    [InlineData("[destructor]", "destructor")]
    public void Parse_ParsesEveryPayloadlessSpecialTag(string text, string expectedTag)
    {
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable(text, PatternMode.Glob).Single());

        Assert.Equal(expectedTag, special.Tag);
        Assert.Null(special.OperatorToken);
        Assert.Null(special.ConversionKind);
        Assert.Null(special.ConversionTarget);
        Assert.Null(special.Member);
        Assert.Equal(ParameterListState.Omitted, special.Arity.ParameterState);
        Assert.Equal(PatternMode.Literal, special.PatternMode);
    }

    [Fact]
    public void Parse_ParsesCheckedOperatorTag()
    {
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable("[checked-operator:+](int,int)", PatternMode.Glob).Single());

        Assert.Equal("checked-operator", special.Tag);
        Assert.Equal("+", special.OperatorToken);
        Assert.Equal(2, special.Arity.Parameters.Count);
    }

    [Theory]
    [InlineData("[conversion:implicit:int]", "conversion", "implicit", "int")]
    [InlineData("[conversion:explicit:string]", "conversion", "explicit", "string")]
    [InlineData("[checked-conversion:explicit:int]", "checked-conversion", "explicit", "int")]
    public void Parse_ParsesEveryConversionTag(
        string text,
        string expectedTag,
        string expectedKind,
        string expectedTarget)
    {
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable(text, PatternMode.Glob).Single());

        Assert.Equal(expectedTag, special.Tag);
        Assert.Equal(expectedKind, special.ConversionKind);
        Assert.Equal(expectedTarget, special.ConversionTarget!.SyntaxText);
        Assert.Null(special.OperatorToken);
        Assert.Null(special.Member);
    }

    [Theory]
    [InlineData("get", "Name")]
    [InlineData("set", "Name")]
    [InlineData("init", "Name")]
    [InlineData("add", "Changed")]
    [InlineData("remove", "Changed")]
    public void Parse_ParsesEveryAccessorTag(string tag, string memberName)
    {
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable($"[{tag}:{memberName}]()", PatternMode.Glob).Single());

        Assert.Equal(tag, special.Tag);
        Assert.Null(special.Member!.ContainingTypePattern);
        Assert.Null(special.Member.ContainingType);
        Assert.Equal(memberName, special.Member.MemberPattern);
        Assert.Equal(PatternMode.Glob, special.Member.PatternMode);
        Assert.Equal(PatternMode.Literal, special.PatternMode);
    }

    [Fact]
    public void Parse_ParsesGenericExplicitInterfaceMethod()
    {
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable(
                "[explicit:Game.Contracts.IMapper.Map]<T>(T)",
                PatternMode.Glob).Single());

        Assert.Equal("explicit", special.Tag);
        Assert.Equal("Game.Contracts.IMapper", special.Member!.ContainingTypePattern);
        Assert.NotNull(special.Member.ContainingType);
        Assert.Equal("Map", special.Member.MemberPattern);
        Assert.Equal(GenericListState.Present, special.Arity.GenericState);
        Assert.Equal(["T"], special.Arity.GenericPlaceholders);
        Assert.Equal(
            new(CanonicalGenericPlaceholderScope.Method, 0),
            special.Arity.Parameters.Single().GenericPlaceholders["T"]);
    }

    [Fact]
    public void Parse_QualifiedInterfaceGlobRetainsPatternWithoutExactTypeSelector()
    {
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable(
                "[explicit:Game.Contracts.IMapper*.Map]()",
                PatternMode.Glob).Single());

        Assert.Equal("Game.Contracts.IMapper*", special.Member!.ContainingTypePattern);
        Assert.Null(special.Member.ContainingType);
        Assert.Equal("Map", special.Member.MemberPattern);
    }

    [Fact]
    public void Parse_QualifiedInterfaceTypeRetainsExactTrimmedPayloadText()
    {
        var special = Assert.IsType<SpecialExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable(
                "[explicit:  Game . Contracts . IPlayer  . Name  ]()",
                PatternMode.Glob).Single());

        Assert.Equal("Game . Contracts . IPlayer", special.Member!.ContainingTypePattern);
        Assert.Equal("Game . Contracts . IPlayer", special.Member.ContainingType!.SyntaxText);
        Assert.Equal("Name", special.Member.MemberPattern);
    }

    [Fact]
    public void Parse_SpecialParameterListOmissionIsIndependent()
    {
        var segments = SymbolPathParser.ParseExecutable(
            "[constructor].[constructor]()",
            PatternMode.Glob).Cast<SpecialExecutableSegmentSelector>().ToArray();

        Assert.Equal(ParameterListState.Omitted, segments[0].Arity.ParameterState);
        Assert.Equal(ParameterListState.Present, segments[1].Arity.ParameterState);
        Assert.Empty(segments[1].Arity.Parameters);
    }

    [Fact]
    public void Parse_ParsesConversionsAndAccessorPayloads()
    {
        var selector = SymbolPathParser.Parse(
            "N::T::[conversion:implicit:global::System.Int32](global::System.String)." +
            "[get:Game.Contracts.IPlayer.Name]().[set:Value](int)");

        var conversion = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[0]);
        Assert.Equal("conversion", conversion.Tag);
        Assert.Equal("implicit", conversion.ConversionKind);
        Assert.Equal("global::System.Int32", conversion.ConversionTarget!.SyntaxText);

        var getter = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[1]);
        Assert.Equal("get", getter.Tag);
        Assert.Equal("Game.Contracts.IPlayer", getter.Member!.ContainingTypePattern);
        Assert.Equal("Name", getter.Member.MemberPattern);

        var setter = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[2]);
        Assert.Equal("set", setter.Tag);
        Assert.Equal("Value", setter.Member!.MemberPattern);
    }

    [Fact]
    public void Parse_ExecutableAndHierarchyExposePatternModes()
    {
        var executable = SymbolPathParser.ParseExecutable("@Name*.<lambda#*>", PatternMode.Glob);
        var hierarchy = SymbolPathParser.ParseHierarchy("global.Repository<T>", PatternMode.Literal);

        var named = Assert.IsType<NamedExecutableSegmentSelector>(executable[0]);
        Assert.Equal("Name*", named.IdentifierPattern);
        Assert.Equal(PatternMode.Glob, named.PatternMode);
        Assert.Null(Assert.IsType<LambdaExecutableSegmentSelector>(executable[1]).Ordinal);

        Assert.Equal(["global", "Repository"], hierarchy.Segments.Select(segment => segment.IdentifierPattern));
        Assert.Equal(1, hierarchy.Segments[1].GenericArity);
        Assert.Equal(PatternMode.Literal, hierarchy.Segments[0].PatternMode);
    }

    [Fact]
    public void Parse_GlobModeRetainsWholeAndEmbeddedIdentifierWildcards()
    {
        var hierarchy = SymbolPathParser.ParseHierarchy("@Name*.Na**me.**.*", PatternMode.Glob);
        var executable = SymbolPathParser.ParseExecutable("@Run*.Ru**n.**.*", PatternMode.Glob);

        Assert.Equal(
            ["Name*", "Na**me", "**", "*"],
            hierarchy.Segments.Select(segment => segment.IdentifierPattern));
        Assert.Equal(
            ["Run*", "Ru**n", "**", "*"],
            executable.Cast<NamedExecutableSegmentSelector>()
                .Select(segment => segment.IdentifierPattern));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Name*")]
    [InlineData("Name?")]
    [InlineData("Name[")]
    public void ParseHierarchy_LiteralModeRejectsPatternPunctuation(string text)
    {
        Assert.Throws<SymbolQueryParseException>(() =>
            SymbolPathParser.ParseHierarchy(text, PatternMode.Literal));
    }

    [Theory]
    [InlineData("Name?")]
    [InlineData("Name+")]
    [InlineData("Na@me*")]
    public void ParseHierarchy_GlobModeRejectsNonStarPatternPunctuation(string text)
    {
        Assert.Throws<SymbolQueryParseException>(() =>
            SymbolPathParser.ParseHierarchy(text, PatternMode.Glob));
    }

    [Fact]
    public void ParseEntryPointsRejectUndefinedPatternMode()
    {
        var mode = (PatternMode)0;

        Assert.Throws<ArgumentOutOfRangeException>(() => SymbolPathParser.ParseHierarchy("N", mode));
        Assert.Throws<ArgumentOutOfRangeException>(() => SymbolPathParser.ParseExecutable("M", mode));
    }

    [Theory]
    [InlineData("Method", GenericListState.Omitted, 0, ParameterListState.Omitted, 0)]
    [InlineData("Method<T>", GenericListState.Present, 1, ParameterListState.Omitted, 0)]
    [InlineData("Method()", GenericListState.Omitted, 0, ParameterListState.Present, 0)]
    [InlineData("Method<T>()", GenericListState.Present, 1, ParameterListState.Present, 0)]
    [InlineData("Method<T>(T)", GenericListState.Present, 1, ParameterListState.Present, 1)]
    public void Parse_NamedSegmentsPreserveGenericAndParameterOmissionStates(
        string text,
        GenericListState genericState,
        int genericCount,
        ParameterListState parameterState,
        int parameterCount)
    {
        var segment = Assert.IsType<NamedExecutableSegmentSelector>(
            SymbolPathParser.ParseExecutable(text, PatternMode.Glob).Single());

        Assert.Equal(genericState, segment.Arity.GenericState);
        Assert.Equal(genericCount, segment.Arity.GenericPlaceholders.Count);
        Assert.Equal(parameterState, segment.Arity.ParameterState);
        Assert.Equal(parameterCount, segment.Arity.Parameters.Count);
    }

    [Theory]
    [InlineData("Outer.Local", ParameterListState.Omitted, 0, ParameterListState.Omitted, 0)]
    [InlineData("Outer(int).Local", ParameterListState.Present, 1, ParameterListState.Omitted, 0)]
    [InlineData("Outer.Local(string)", ParameterListState.Omitted, 0, ParameterListState.Present, 1)]
    [InlineData("Outer(int).Local(string)", ParameterListState.Present, 1, ParameterListState.Present, 1)]
    public void ParseExecutable_TracksIndependentOmissionAtEachChild(
        string text,
        ParameterListState outerState,
        int outerParameterCount,
        ParameterListState localState,
        int localParameterCount)
    {
        var segments = SymbolPathParser.ParseExecutable(text, PatternMode.Glob)
            .Cast<NamedExecutableSegmentSelector>().ToArray();

        Assert.Equal(2, segments.Length);
        Assert.Equal(outerState, segments[0].Arity.ParameterState);
        Assert.Equal(outerParameterCount, segments[0].Arity.Parameters.Count);
        Assert.Equal(localState, segments[1].Arity.ParameterState);
        Assert.Equal(localParameterCount, segments[1].Arity.Parameters.Count);
    }

    [Fact]
    public void ParseHierarchy_UsesExactTypeAritiesAndIntentionalGlob()
    {
        var zero = SymbolPathParser.ParseHierarchy("Repository", PatternMode.Literal);
        var one = SymbolPathParser.ParseHierarchy("Repository<T>", PatternMode.Literal);
        var two = SymbolPathParser.ParseHierarchy("Repository<T,U>", PatternMode.Literal);
        var glob = SymbolPathParser.ParseHierarchy("Repository*", PatternMode.Glob);

        Assert.Equal(0, zero.Segments.Single().GenericArity);
        Assert.Equal(1, one.Segments.Single().GenericArity);
        Assert.Equal(2, two.Segments.Single().GenericArity);
        Assert.Equal("Repository*", glob.Segments.Single().IdentifierPattern);
    }

    [Fact]
    public void Parse_FlattensPlaceholderOrdinalsNormalizesEscapesAndAppliesInnerShadowing()
    {
        var selector = SymbolPathParser.Parse(
            "N::Outer<T>.Inner<@U>::Method<@V>(T,U,V).Local<T>(T,U,V)");
        var innerType = selector.Type.Segments[1];
        var method = Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments[0]);
        var local = Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments[1]);

        Assert.Equal(["U"], innerType.GenericPlaceholders);
        Assert.Equal(["V"], method.Arity.GenericPlaceholders);
        Assert.Equal(new(CanonicalGenericPlaceholderScope.Type, 0), method.Arity.Parameters[0].GenericPlaceholders["T"]);
        Assert.Equal(new(CanonicalGenericPlaceholderScope.Type, 1), method.Arity.Parameters[1].GenericPlaceholders["U"]);
        Assert.Equal(new(CanonicalGenericPlaceholderScope.Method, 0), method.Arity.Parameters[2].GenericPlaceholders["V"]);
        Assert.Equal(["T"], local.Arity.GenericPlaceholders);
        Assert.Equal(new(CanonicalGenericPlaceholderScope.Method, 1), local.Arity.Parameters[0].GenericPlaceholders["T"]);
        Assert.Equal(new(CanonicalGenericPlaceholderScope.Type, 1), local.Arity.Parameters[1].GenericPlaceholders["U"]);
        Assert.Equal(new(CanonicalGenericPlaceholderScope.Method, 0), local.Arity.Parameters[2].GenericPlaceholders["V"]);
    }

    [Fact]
    public void Parse_AddsCurrentPlaceholdersBeforeSpecialPayloadTypes()
    {
        var selector = SymbolPathParser.Parse(
            "N::Outer<T>::[conversion:implicit:T](T)." +
            "[explicit:Game.Contracts.IMapper<U>.Map]<U>(U)");
        var conversion = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[0]);
        var explicitMethod = Assert.IsType<SpecialExecutableSegmentSelector>(selector.ExecutableSegments[1]);

        Assert.Equal(
            new(CanonicalGenericPlaceholderScope.Type, 0),
            conversion.ConversionTarget!.GenericPlaceholders["T"]);
        Assert.Equal(
            new(CanonicalGenericPlaceholderScope.Method, 0),
            explicitMethod.Member!.ContainingType!.GenericPlaceholders["U"]);
        Assert.Equal(
            new(CanonicalGenericPlaceholderScope.Method, 0),
            explicitMethod.Arity.Parameters.Single().GenericPlaceholders["U"]);
    }

    [Fact]
    public void Parse_MapsAllRefKindsAndKeepsFunctionPointerRefModifiersInsideType()
    {
        var selector = SymbolPathParser.Parse(
            "N::T::M(ref int,out string,in System.Guid,ref readonly bool,delegate*<ref int,void>)");
        var method = Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments[0]);

        Assert.Equal(
            [
                (int)RefKind.Ref,
                (int)RefKind.Out,
                (int)RefKind.In,
                (int)RefKind.RefReadOnlyParameter,
                (int)RefKind.None,
            ],
            method.Arity.ParameterRefKinds);
        Assert.Equal("delegate*<ref int,void>", method.Arity.Parameters[4].SyntaxText);
    }

    [Fact]
    public void Parse_AcceptsAliasFrameworkAndGlobalFrameworkTypeSpellings()
    {
        var method = Assert.IsType<NamedExecutableSegmentSelector>(
            SymbolPathParser.Parse(
                "N::T::M(int,System.Int32,global::System.Int32,System.Guid)")
                .ExecutableSegments.Single());

        Assert.Equal(
            ["int", "System.Int32", "global::System.Int32", "System.Guid"],
            method.Arity.Parameters.Select(parameter => parameter.SyntaxText));
    }

    [Fact]
    public void Parse_ParsesSyntheticSegmentsAndAnonymousWildcards()
    {
        var segments = SymbolPathParser.ParseExecutable(
            "<initializer:Field*>.<anonymous-method#2>.<anonymous-method#*>." +
            "<lambda#1>.<lambda#*>.<top-level-statements>",
            PatternMode.Glob);

        var initializer = Assert.IsType<InitializerExecutableSegmentSelector>(segments[0]);
        Assert.Equal("Field*", initializer.MemberPattern);
        Assert.Equal(PatternMode.Glob, initializer.PatternMode);
        Assert.Equal(2, Assert.IsType<AnonymousMethodExecutableSegmentSelector>(segments[1]).Ordinal);
        Assert.Null(Assert.IsType<AnonymousMethodExecutableSegmentSelector>(segments[2]).Ordinal);
        Assert.Equal(1, Assert.IsType<LambdaExecutableSegmentSelector>(segments[3]).Ordinal);
        Assert.Null(Assert.IsType<LambdaExecutableSegmentSelector>(segments[4]).Ordinal);
        Assert.IsType<TopLevelStatementsExecutableSegmentSelector>(segments[5]);
    }

    [Fact]
    public void Parse_NormalizesEscapedIdentifiersWithoutLowercasing()
    {
        var escaped = SymbolPathParser.Parse("N::@class<@event>::@class<@event>(@event)");
        var equivalent = SymbolPathParser.ParseExecutable("Name.@Name", PatternMode.Literal);

        Assert.Equal("class", escaped.Type.Segments.Single().IdentifierPattern);
        Assert.Equal(["event"], escaped.Type.Segments.Single().GenericPlaceholders);
        Assert.Equal("class", Assert.IsType<NamedExecutableSegmentSelector>(escaped.ExecutableSegments[0]).IdentifierPattern);
        Assert.Equal(
            ["event"],
            Assert.IsType<NamedExecutableSegmentSelector>(escaped.ExecutableSegments[0])
                .Arity.GenericPlaceholders);
        Assert.Equal(
            ["Name", "Name"],
            equivalent.OfType<NamedExecutableSegmentSelector>().Select(segment => segment.IdentifierPattern));
    }

    [Fact]
    public void Parse_TrimsCompleteInputStructuralFieldsAndParameterTypes()
    {
        var selector = SymbolPathParser.Parse("  N  ::  T  ::  M( System.Int32 )  ");

        Assert.Equal("N", selector.Namespace!.Segments.Single().IdentifierPattern);
        Assert.Equal("T", selector.Type.Segments.Single().IdentifierPattern);
        Assert.Equal(
            "System.Int32",
            Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments.Single())
                .Arity.Parameters.Single().SyntaxText);
    }

    [Fact]
    public void ParserProducedCollectionsAreReadOnlySnapshots()
    {
        var selector = SymbolPathParser.Parse("N::T<U>::M<V>(ref int)");
        var executableSegments = Assert.IsAssignableFrom<IList<ExecutableSegmentSelector>>(selector.ExecutableSegments);
        var namespaceSegments = Assert.IsAssignableFrom<IList<HierarchySegmentSelector>>(selector.Namespace!.Segments);
        var typeSegments = Assert.IsAssignableFrom<IList<HierarchySegmentSelector>>(selector.Type.Segments);
        var typePlaceholders = Assert.IsAssignableFrom<IList<string>>(
            selector.Type.Segments.Single().GenericPlaceholders);
        var arity = Assert.IsType<NamedExecutableSegmentSelector>(selector.ExecutableSegments[0]).Arity;
        var methodPlaceholders = Assert.IsAssignableFrom<IList<string>>(arity.GenericPlaceholders);
        var parameters = Assert.IsAssignableFrom<IList<CanonicalTypeSelector>>(
            arity.Parameters);
        var refKinds = Assert.IsAssignableFrom<IList<int>>(arity.ParameterRefKinds);

        Assert.Throws<NotSupportedException>(() => executableSegments.Add(
            new TopLevelStatementsExecutableSegmentSelector()));
        Assert.Throws<NotSupportedException>(() => namespaceSegments.Clear());
        Assert.Throws<NotSupportedException>(() => typeSegments.Clear());
        Assert.Throws<NotSupportedException>(() => typePlaceholders.Add("W"));
        Assert.Throws<NotSupportedException>(() => methodPlaceholders[0] = "W");
        Assert.Throws<NotSupportedException>(() => parameters.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => refKinds[0] = (int)RefKind.Out);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("T")]
    [InlineData("N::T::M()::Local()")]
    [InlineData("N::T::M()::<lambda#1>")]
    [InlineData("A::B::C::D()")]
    [InlineData("::T::M()")]
    [InlineData("N::::M()")]
    [InlineData("N::T::")]
    [InlineData("N.::T::M()")]
    [InlineData("N::T.::M()")]
    [InlineData("N::T::.M()")]
    [InlineData("N::T::M..Local()")]
    [InlineData("N::T::Method<System.String>")]
    [InlineData("N::Repository<System.String>::M()")]
    [InlineData("N::T::M(*)")]
    [InlineData("N::T::M(Game.Models.*)")]
    [InlineData("N::T::M(foo)")]
    [InlineData("N::T::M<T,T>()")]
    [InlineData("N::T::M<T,@T>()")]
    [InlineData("N::T::M<*>()")]
    [InlineData("N::T::M<T.U>()")]
    [InlineData("N::T::M<T>(T)::Local()")]
    [InlineData("N::T::[constructor]<T>()")]
    [InlineData("N::T::[operator:+]<T>()")]
    [InlineData("N::T::[conversion:implicit:int]<T>()")]
    [InlineData("N::T::[get:Name]<T>()")]
    [InlineData("N::T::<lambda#0>")]
    [InlineData("N::T::<lambda#-1>")]
    [InlineData("N::T::<lambda#x>")]
    [InlineData("N::T::<lambda#>")]
    [InlineData("N::T::<anonymous-method#0>")]
    [InlineData("N::T::<anonymous-method#-1>")]
    [InlineData("N::T::<anonymous-method#x>")]
    [InlineData("N::T::<anonymous-method#>")]
    [InlineData("N::T::<Lambda#1>")]
    [InlineData("N::T::<Anonymous-method#1>")]
    [InlineData("N::T::<Initializer:Field>")]
    [InlineData("N::T::<Top-level-statements>")]
    [InlineData("N::T::<initializer:Field>()")]
    [InlineData("N::T::<top-level-statements>()")]
    [InlineData("N::T::<lambda#1>()")]
    [InlineData("N::T::<anonymous-method#1>()")]
    [InlineData("N::T::[conversion:implicit]")]
    [InlineData("N::T::[conversion:explicit:]()")]
    [InlineData("N::T::[checked-conversion:implicit:int]()")]
    [InlineData("N::T::[unknown]()")]
    [InlineData("N::T::[Constructor]()")]
    [InlineData("N::T::[get:]()")]
    [InlineData("N::T::[explicit:]()")]
    [InlineData("N::T::[operator:]()")]
    [InlineData("N::T::[operator:???]()")]
    [InlineData("N::T::class()")]
    [InlineData("N::T::M(ref)")]
    [InlineData("N::T::M(ref readonly)")]
    [InlineData("N::T::M(readonly int)")]
    [InlineData("N::T::M(params int)")]
    [InlineData("N::T::M(scoped int)")]
    [InlineData("N::T::M(this int)")]
    [InlineData("N::T::M(int value)")]
    [InlineData("N::T::M(int = default)")]
    [InlineData("N::T::M<T")]
    [InlineData("N::T::M(int]")]
    [InlineData("N::T::M(([)])")]
    [InlineData("N::T::[get:Name")]
    public void Parse_RejectsMalformedUserText(string text)
    {
        Assert.Throws<SymbolQueryParseException>(() => SymbolPathParser.Parse(text));
    }

    [Theory]
    [InlineData("N::T::M(int[]<>)")]
    public void Parse_RejectsInvalidConstructedOrWildcardParameterTypes(string text)
    {
        Assert.Throws<SymbolQueryParseException>(() => SymbolPathParser.Parse(text));
    }

    [Fact]
    public void ParseHelperEntryPointsTranslateUserTextFailures()
    {
        Assert.Throws<SymbolQueryParseException>(() =>
            SymbolPathParser.ParseHierarchy("Repository<System.String>", PatternMode.Literal));
        Assert.Throws<SymbolQueryParseException>(() =>
            SymbolPathParser.ParseExecutable("M(foo)", PatternMode.Glob));
    }
}
