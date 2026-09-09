using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query.Tests;

public sealed class TypedConditionCompilerTests
{
    [Fact]
    public void Compile_ProducesACompiledConditionWithTheRequestedShape()
    {
        var request = new SymbolSelectionRequest(
            Selector: null,
            Conditions: [new(ConditionCategory.Namespace, ConditionSyntax.Literal, "Game")],
            Case: new(),
            FunctionFilter: new(null, AsyncStatusFilter.All),
            KindSpecified: false,
            AsyncStatusSpecified: false);

        var compiled = TypedConditionCompiler.Compile(request);

        Assert.False(compiled.HasDeclarationConditions);
        Assert.False(compiled.RequiresSourceText);
    }

    [Fact]
    public void MatchesLogical_RequiresTheSymbolPathInvariant()
    {
        var request = new SymbolSelectionRequest(
            null,
            [],
            new(),
            new(null, AsyncStatusFilter.All),
            false,
            false);
        var compiled = TypedConditionCompiler.Compile(request);
        var symbol = TestSymbol(path: null);

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = compiled.MatchesLogical(symbol, TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public void PositionalSelectorAndSyntaxMetadataAreNotEvaluatedByTheCompiler()
    {
        var compiled = TypedConditionCompiler.Compile(new SymbolSelectionRequest(
            Selector: "Never.Matches",
            Conditions: [],
            Case: new(),
            FunctionFilter: new(null, AsyncStatusFilter.All),
            KindSpecified: true,
            AsyncStatusSpecified: true));

        Assert.True(compiled.MatchesLogical(
            Symbol(Path("Game", "Host", "Run()")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PublicMatchingMethodsValidateNullArguments()
    {
        var compiled = Compile();
        var symbol = Symbol(Path("Game", "Host", "Run()"));
        var declaration = Declaration("src/Run.cs", null);

        Assert.Throws<ArgumentNullException>(() =>
            compiled.MatchesLogical(null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() =>
            compiled.MatchesDeclaration(null!, declaration, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() =>
            compiled.MatchesDeclaration(symbol, null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Compile_MixesGlobLiteralAndRegexAndConjoinsCategories()
    {
        var compiled = Compile([
            new(ConditionCategory.Namespace, ConditionSyntax.Glob, "Game.*"),
            new(ConditionCategory.Type, ConditionSyntax.Literal, "Outer.Inner"),
            new(ConditionCategory.Method, ConditionSyntax.Regex, "Run\\(int\\)"),
            new(ConditionCategory.File, ConditionSyntax.Literal, "src/Player.cs")
        ]);

        var path = new SymbolPathData(
            "Game.Core",
            "Outer.Inner",
            "Outer.Inner",
            "Run(int)",
            "Run(System::Int32)",
            "Run(int)",
            "Run(System.Int32)",
            CallablePathSegmentKind.Named);
        Assert.True(compiled.MatchesDeclaration(
            Symbol(path),
            Declaration("src/Player.cs", "body"),
            TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesLogical(
            Symbol(path with { TypeDisplayPath = "Outer.Other", TypeIdentityPath = "Outer.Other" }),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SameCategoryAlternativesUseOrComposition()
    {
        var compiled = Compile([
            new(ConditionCategory.Namespace, ConditionSyntax.Literal, "Wrong"),
            new(ConditionCategory.Namespace, ConditionSyntax.Literal, "Game")
        ]);

        Assert.True(compiled.MatchesLogical(
            Symbol(Path("Game", "Host", "Run()")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IncludesConjoinAndExcludesRejectWhenAnyMatches()
    {
        var compiled = Compile([
            new(ConditionCategory.Include, ConditionSyntax.Literal, "required"),
            new(ConditionCategory.Include, ConditionSyntax.Glob, "value*"),
            new(ConditionCategory.Exclude, ConditionSyntax.Literal, "blocked"),
            new(ConditionCategory.Exclude, ConditionSyntax.Regex, "forbidden\\s+api")
        ]);
        var symbol = Symbol(Path("Game", "Host", "Run()"));

        Assert.True(compiled.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", "required value=1"),
            TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", "required value=1 blocked"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FunctionFilterAppliesKindAndAsync()
    {
        var compiled = Compile(
            [],
            filter: new FunctionTargetFilter(IndexedSymbolKind.Method, AsyncStatusFilter.Async));
        var asyncMethod = Symbol(Path("Game", "Host", "Run()"), asyncRole: AsyncRole.ContainsAwait);
        var syncMethod = Symbol(Path("Game", "Host", "Run()"));
        var lambda = Symbol(
            Path("Game", "Host", "<lambda#1>"),
            IndexedSymbolKind.Lambda,
            AsyncRole.ContainsAwait);

        Assert.True(compiled.MatchesLogical(asyncMethod, TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesLogical(syncMethod, TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesLogical(lambda, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FiveCaseOptionsRouteIndependentlyWithStrictDefault()
    {
        var strict = Compile([
            new(ConditionCategory.Namespace, ConditionSyntax.Literal, "game"),
            new(ConditionCategory.Type, ConditionSyntax.Literal, "host"),
            new(ConditionCategory.Method, ConditionSyntax.Literal, "run()"),
            new(ConditionCategory.File, ConditionSyntax.Literal, "SRC/RUN.CS"),
            new(ConditionCategory.Include, ConditionSyntax.Literal, "need")
        ]);
        var ignore = Compile(
            [
                new(ConditionCategory.Namespace, ConditionSyntax.Literal, "game"),
                new(ConditionCategory.Type, ConditionSyntax.Literal, "host"),
                new(ConditionCategory.Method, ConditionSyntax.Literal, "run()"),
                new(ConditionCategory.File, ConditionSyntax.Literal, "SRC/RUN.CS"),
                new(ConditionCategory.Include, ConditionSyntax.Literal, "need"),
            ],
            new(CaseMode.Ignore, CaseMode.Ignore, CaseMode.Ignore, CaseMode.Ignore, CaseMode.Ignore),
            new(null, AsyncStatusFilter.All));
        var symbol = Symbol(Path("Game", "Host", "Run()"));

        Assert.False(strict.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", "Need"),
            TestContext.Current.CancellationToken));
        Assert.True(ignore.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", "Need"),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ConditionCategory.Namespace, "game")]
    [InlineData(ConditionCategory.Type, "host")]
    [InlineData(ConditionCategory.Method, "run()")]
    [InlineData(ConditionCategory.File, "src/run.cs")]
    [InlineData(ConditionCategory.Include, "need")]
    public void EachCaseOptionDefaultsStrictAndCanBeIgnoredWithoutChangingTheOthers(
        ConditionCategory category,
        string value)
    {
        var condition = new TypedCondition(category, ConditionSyntax.Literal, value);
        var strict = Compile([condition]);
        var ignored = Compile(
            [condition],
            new SymbolCaseOptions(
                Namespace: category == ConditionCategory.Namespace ? CaseMode.Ignore : CaseMode.Strict,
                Type: category == ConditionCategory.Type ? CaseMode.Ignore : CaseMode.Strict,
                Method: category == ConditionCategory.Method ? CaseMode.Ignore : CaseMode.Strict,
                File: category == ConditionCategory.File ? CaseMode.Ignore : CaseMode.Strict,
                Source: category == ConditionCategory.Include ? CaseMode.Ignore : CaseMode.Strict));
        var symbol = Symbol(Path("Game", "Host", "Run()"));
        var declaration = Declaration("SRC/RUN.CS", "NEED");

        bool Matches(CompiledTypedConditions compiled) =>
            category is ConditionCategory.Namespace or ConditionCategory.Type or ConditionCategory.Method
                ? compiled.MatchesLogical(symbol, TestContext.Current.CancellationToken)
                : compiled.MatchesDeclaration(symbol, declaration, TestContext.Current.CancellationToken);

        Assert.False(Matches(strict));
        Assert.True(Matches(ignored));
    }

    [Fact]
    public void ExplicitInterfaceOwnerUsesNamespaceTypeAndMemberCaseModes()
    {
        var compiled = Compile(
            [new(ConditionCategory.Method, ConditionSyntax.Glob, "[explicit:game.contracts.imap*.read*]()")],
            new(CaseMode.Ignore, CaseMode.Ignore, CaseMode.Ignore, CaseMode.Strict, CaseMode.Strict),
            new(null, AsyncStatusFilter.All));
        var path = new SymbolPathData(
            "Game",
            "Host",
            "Host",
            "[explicit:Game.Contracts.IMapper.Read]()",
            "[explicit:Game.Contracts::IMapper.Read]()",
            "[explicit:Game.Contracts.IMapper.Read]()",
            "[explicit:Game.Contracts::IMapper.Read]()",
            CallablePathSegmentKind.Special);

        Assert.True(compiled.MatchesLogical(Symbol(path), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExplicitInterfaceOwnerEmbeddedIdentifierGlobIsArityAgnostic()
    {
        var identifierGlob = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.IMap*.Read]()")]);
        var exactIdentifier = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.IMap.Read]()")]);
        var nongeneric = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap.Read]()",
            "[explicit:Game::IMap.Read]()"));
        var generic = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap<int>.Read]()",
            "[explicit:Game::IMap<System::Int32>.Read]()"));
        var unrelated = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.ISet.Read]()",
            "[explicit:Game::ISet.Read]()"));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(identifierGlob.MatchesLogical(nongeneric, cancellationToken));
        Assert.True(identifierGlob.MatchesLogical(generic, cancellationToken));
        Assert.False(identifierGlob.MatchesLogical(unrelated, cancellationToken));
        Assert.True(exactIdentifier.MatchesLogical(nongeneric, cancellationToken));
        Assert.False(exactIdentifier.MatchesLogical(generic, cancellationToken));
    }

    [Fact]
    public void ExplicitInterfaceOwnerGlobRequiresAlignedOuterCanonicalPair()
    {
        var compiled = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.I*.Read]()")]);
        var valid = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap.Read]()",
            "[explicit:Game::IMap.Read]()"));
        var missingBoundary = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap.Read]()",
            "[explicit:Game.IMap.Read]()"));
        var mismatchedComponents = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap.Read]()",
            "[explicit:Game.Contracts::IMap.Read]()"));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(compiled.MatchesLogical(valid, cancellationToken));
        Assert.Throws<InvalidOperationException>(() =>
            compiled.MatchesLogical(missingBoundary, cancellationToken));
        Assert.Throws<InvalidOperationException>(() =>
            compiled.MatchesLogical(mismatchedComponents, cancellationToken));
    }

    [Theory]
    [InlineData("[explicit:game.Contracts.IMapper.Read]()", ConditionCategory.Namespace)]
    [InlineData("[explicit:Game.Contracts.imapper.Read]()", ConditionCategory.Type)]
    [InlineData("[explicit:Game.Contracts.IMapper.read]()", ConditionCategory.Method)]
    public void ExplicitInterfaceOwnerAndMemberRouteEachCaseModeIndependently(
        string pattern,
        ConditionCategory caseCategory)
    {
        var condition = new TypedCondition(ConditionCategory.Method, ConditionSyntax.Glob, pattern);
        var strict = Compile([condition]);
        var ignored = Compile(
            [condition],
            new SymbolCaseOptions(
                Namespace: caseCategory == ConditionCategory.Namespace ? CaseMode.Ignore : CaseMode.Strict,
                Type: caseCategory == ConditionCategory.Type ? CaseMode.Ignore : CaseMode.Strict,
                Method: caseCategory == ConditionCategory.Method ? CaseMode.Ignore : CaseMode.Strict));
        var symbol = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.Contracts.IMapper.Read]()",
            "[explicit:Game.Contracts::IMapper.Read]()"));

        Assert.False(strict.MatchesLogical(symbol, TestContext.Current.CancellationToken));
        Assert.True(ignored.MatchesLogical(symbol, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExplicitInterfaceOwnerGlobMatchesExactGenericArgumentsSemantically()
    {
        var compiled = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.*.IMap<System.Int32>.Read]()")]);
        var matching = Path(
            "Game",
            "Host",
            "[explicit:Game.Contracts.IMap<int>.Read]()",
            "[explicit:Game.Contracts::IMap<System::Int32>.Read]()");
        var wrongArgument = Path(
            "Game",
            "Host",
            "[explicit:Game.Contracts.IMap<string>.Read]()",
            "[explicit:Game.Contracts::IMap<System::String>.Read]()");

        Assert.True(compiled.MatchesLogical(
            Symbol(matching),
            TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesLogical(
            Symbol(wrongArgument),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExplicitInterfaceOwnerGenericWildcardMatchesAnyCanonicalType()
    {
        var star = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.IMap<*>.Read]()")]);
        var doubleStar = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.IMap<**>.Read]()")]);
        var exactInt = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.IMap<System.Int32>.Read]()")]);
        var qualifiedWildcard = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.IMap<System.*>.Read]()")]);
        var alias = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap<int>.Read]()",
            "[explicit:Game::IMap<System::Int32>.Read]()"));
        var placeholder = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap<T>.Read]()",
            "[explicit:Game::IMap<!0>.Read]()"));
        var wrongExact = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap<string>.Read]()",
            "[explicit:Game::IMap<System::String>.Read]()"));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(star.MatchesLogical(alias, cancellationToken));
        Assert.True(star.MatchesLogical(placeholder, cancellationToken));
        Assert.True(doubleStar.MatchesLogical(alias, cancellationToken));
        Assert.True(doubleStar.MatchesLogical(placeholder, cancellationToken));
        Assert.False(exactInt.MatchesLogical(wrongExact, cancellationToken));
        Assert.False(qualifiedWildcard.MatchesLogical(alias, cancellationToken));
    }

    [Fact]
    public void ExplicitInterfaceOwnerWildcardPreservesLiteralClassificationNamespace()
    {
        var compiled = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:valuetype.IMap<*>.Read]()")]);
        var literalNamespace = Symbol(Path(
            "Game",
            "Host",
            "[explicit:valuetype.IMap<int>.Read]()",
            "[explicit:valuetype::IMap<System::Int32>.Read]()"));
        var classifiedOwner = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "[explicit:Game.IMap<*>.Read]()")]);
        var realClassificationPrefix = Symbol(Path(
            "Game",
            "Host",
            "[explicit:Game.IMap<int>.Read]()",
            "[explicit:reftype:Game::IMap<System::Int32>.Read]()"));

        Assert.True(compiled.MatchesLogical(
            literalNamespace,
            TestContext.Current.CancellationToken));
        Assert.True(classifiedOwner.MatchesLogical(
            realClassificationPrefix,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StructuredParametersAndConversionsUseSemanticAliases()
    {
        var method = Compile([new(ConditionCategory.Method, ConditionSyntax.Literal, "Run(System.Int32)")]);
        var conversion = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Literal,
            "[conversion:implicit:System.Int32](System.String)")]);

        Assert.True(method.MatchesLogical(
            Symbol(Path("Game", "Host", "Run(int)", "Run(System::Int32)")),
            TestContext.Current.CancellationToken));
        Assert.True(conversion.MatchesLogical(
            Symbol(Path(
                "Game",
                "Host",
                "[conversion:implicit:int](string)",
                "[conversion:implicit:System::Int32](System::String)")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MalformedStoredCanonicalParameterTypeIsAnInvariantFailure()
    {
        var compiled = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Literal,
            "Run(System.Int32)")]);

        Assert.Throws<InvalidOperationException>(() => compiled.MatchesLogical(
            Symbol(Path("Game", "Host", "Run(int)", "Run(not-a-canonical-type)")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MismatchedStoredTypeDisplayAndIdentityNamesAreAnInvariantFailure()
    {
        var compiled = Compile([new(ConditionCategory.Type, ConditionSyntax.Literal, "Host")]);
        var path = Path("Game", "Host", "Run()") with { TypeIdentityPath = "Other" };

        Assert.Throws<InvalidOperationException>(() => compiled.MatchesLogical(
            Symbol(path),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MalformedStoredNamespacePathIsAnInvariantFailure()
    {
        var compiled = Compile([new(ConditionCategory.Namespace, ConditionSyntax.Glob, "**")]);

        Assert.Throws<InvalidOperationException>(() => compiled.MatchesLogical(
            Symbol(Path("Game<", "Host", "Run()")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NamespaceHierarchyNormalizesEscapedCandidateIdentifiers()
    {
        var literalKeyword = Compile([new(
            ConditionCategory.Namespace,
            ConditionSyntax.Literal,
            "@namespace")]);
        var globKeyword = Compile([new(
            ConditionCategory.Namespace,
            ConditionSyntax.Glob,
            "@name*")]);
        var namedGlobal = Compile([new(
            ConditionCategory.Namespace,
            ConditionSyntax.Literal,
            "@global")]);
        var recursiveGlobal = Compile([new(
            ConditionCategory.Namespace,
            ConditionSyntax.Glob,
            "**")]);
        var escapedKeyword = Symbol(Path("@namespace", "Host", "Run()"));
        var escapedNamedGlobal = Symbol(Path("@global", "Host", "Run()"));
        var zeroComponentGlobal = Symbol(Path(string.Empty, "Host", "Run()"));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(literalKeyword.MatchesLogical(escapedKeyword, cancellationToken));
        Assert.True(globKeyword.MatchesLogical(escapedKeyword, cancellationToken));
        Assert.True(namedGlobal.MatchesLogical(escapedNamedGlobal, cancellationToken));
        Assert.False(namedGlobal.MatchesLogical(zeroComponentGlobal, cancellationToken));
        Assert.True(recursiveGlobal.MatchesLogical(zeroComponentGlobal, cancellationToken));
    }

    [Fact]
    public void StructuredSpecialSegmentsKeepOperatorTokensAndOrdinalWildcardsTyped()
    {
        var operatorCondition = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Literal,
            "[operator:*](int,int)")]);
        var lambdaCondition = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "Run().<lambda#*>")]);
        var initializerCondition = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Glob,
            "<initializer:Fact*>")]);

        Assert.True(operatorCondition.MatchesLogical(
            Symbol(Path(
                "Game",
                "Host",
                "[operator:*](int,int)",
                "[operator:*](System::Int32,System::Int32)")),
            TestContext.Current.CancellationToken));
        Assert.True(lambdaCondition.MatchesLogical(
            Symbol(Path("Game", "Host", "Run().<lambda#1>")),
            TestContext.Current.CancellationToken));
        Assert.True(initializerCondition.MatchesLogical(
            Symbol(Path("Game", "Host", "<initializer:Factory>")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TypeGlob_RecursiveDoubleStarRequiresNoGenericArityContent()
    {
        var compiled = Compile([new(ConditionCategory.Type, ConditionSyntax.Glob, "**<T>.Leaf")]);

        Assert.False(compiled.MatchesLogical(
            Symbol(Path("Game", "Leaf", "Run()")),
            TestContext.Current.CancellationToken));
        Assert.True(compiled.MatchesLogical(
            Symbol(Path("Game", "Box<T>.Leaf", "Run()") with
            {
                TypeIdentityPath = "Box`1.Leaf",
            }),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TypeHierarchyGenericArityFollowsLiteralAndGlobIdentifierRules()
    {
        var literalName = Compile([new(
            ConditionCategory.Type,
            ConditionSyntax.Literal,
            "Repository")]);
        var globName = Compile([new(
            ConditionCategory.Type,
            ConditionSyntax.Glob,
            "Repository")]);
        var literalGeneric = Compile([new(
            ConditionCategory.Type,
            ConditionSyntax.Literal,
            "Repository<T>")]);
        var globGeneric = Compile([new(
            ConditionCategory.Type,
            ConditionSyntax.Glob,
            "Repository<T>")]);
        var globIdentifier = Compile([new(
            ConditionCategory.Type,
            ConditionSyntax.Glob,
            "Repository*")]);
        var nongeneric = Symbol(Path("Game", "Repository", "Run()"));
        var generic = Symbol(Path("Game", "Repository<T>", "Run()") with
        {
            TypeIdentityPath = "Repository`1",
        });
        var arityTwo = Symbol(Path("Game", "Repository<T, U>", "Run()") with
        {
            TypeIdentityPath = "Repository`2",
        });
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(literalName.MatchesLogical(nongeneric, cancellationToken));
        Assert.False(literalName.MatchesLogical(generic, cancellationToken));
        Assert.True(globName.MatchesLogical(nongeneric, cancellationToken));
        Assert.False(globName.MatchesLogical(generic, cancellationToken));
        Assert.True(literalGeneric.MatchesLogical(generic, cancellationToken));
        Assert.False(literalGeneric.MatchesLogical(arityTwo, cancellationToken));
        Assert.True(globGeneric.MatchesLogical(generic, cancellationToken));
        Assert.False(globGeneric.MatchesLogical(arityTwo, cancellationToken));
        Assert.True(globIdentifier.MatchesLogical(nongeneric, cancellationToken));
        Assert.True(globIdentifier.MatchesLogical(generic, cancellationToken));
    }

    [Theory]
    [InlineData("Run", "Run<T>(int)", true)]
    [InlineData("Run<T>", "Run<T>(int)", true)]
    [InlineData("Run()", "Run()", true)]
    [InlineData("Run<T>()", "Run<T>()", true)]
    [InlineData("Run(int)", "Run(int)", true)]
    [InlineData("Run()", "Run(string)", false)]
    [InlineData("Run()", "Run<T>()", false)]
    [InlineData("Run(int)", "Run<T>(int)", false)]
    public void StructuredMethodOmissionStatesFollowTheCanonicalTable(
        string selector,
        string display,
        bool expected)
    {
        var compiled = Compile([new(ConditionCategory.Method, ConditionSyntax.Literal, selector)]);
        var identity = display
            .Replace("<T>", "`1", StringComparison.Ordinal)
            .Replace("int", "System::Int32", StringComparison.Ordinal)
            .Replace("string", "System::String", StringComparison.Ordinal);

        Assert.Equal(
            expected,
            compiled.MatchesLogical(
                Symbol(Path("Game", "Host", display, identity)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MethodRegexIsRawWholeExecutableTextWithoutOmissionExpansion()
    {
        var compiled = Compile([new(
            ConditionCategory.Method,
            ConditionSyntax.Regex,
            "Run\\(\\)")]);

        Assert.True(compiled.MatchesLogical(
            Symbol(Path("Game", "Host", "Run()")),
            TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesLogical(
            Symbol(Path("Game", "Host", "Run(int)", "Run(System::Int32)")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FileOnlyEvaluationDoesNotRequireSourceAndNullSourceFailsWhenRequired()
    {
        var fileOnly = Compile([new(ConditionCategory.File, ConditionSyntax.Literal, "src/Run.cs")]);
        var sourceRequired = Compile([new(ConditionCategory.Include, ConditionSyntax.Literal, "need")]);
        var symbol = Symbol(Path("Game", "Host", "Run()"));

        Assert.True(fileOnly.HasDeclarationConditions);
        Assert.False(fileOnly.RequiresSourceText);
        Assert.True(fileOnly.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", null),
            TestContext.Current.CancellationToken));
        Assert.True(sourceRequired.RequiresSourceText);
        Assert.False(sourceRequired.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FileLiteralNormalizesOnlyTheUserConditionPath()
    {
        var compiled = Compile([new(ConditionCategory.File, ConditionSyntax.Literal, "src\\Run.cs")]);
        var symbol = Symbol(Path("Game", "Host", "Run()"));

        Assert.True(compiled.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", null),
            TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesDeclaration(
            symbol,
            Declaration("src\\Run.cs", null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FileAndSourceEvidenceMustBelongToTheSameDeclaration()
    {
        var compiled = Compile([
            new(ConditionCategory.File, ConditionSyntax.Literal, "src/Run.cs"),
            new(ConditionCategory.Include, ConditionSyntax.Literal, "required")
        ]);
        var symbol = Symbol(Path("Game", "Host", "Run()"));

        Assert.False(compiled.MatchesDeclaration(
            symbol,
            Declaration("src/Run.cs", "other"),
            TestContext.Current.CancellationToken));
        Assert.False(compiled.MatchesDeclaration(
            symbol,
            Declaration("src/Other.cs", "required"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DeclarationConditionFlagsHaveTheRequiredTruthTable()
    {
        var logical = Compile();
        var file = Compile([new(ConditionCategory.File, ConditionSyntax.Literal, "Run.cs")]);
        var include = Compile([new(ConditionCategory.Include, ConditionSyntax.Literal, "x")]);
        var exclude = Compile([new(ConditionCategory.Exclude, ConditionSyntax.Literal, "x")]);

        Assert.False(logical.HasDeclarationConditions);
        Assert.False(logical.RequiresSourceText);
        Assert.True(file.HasDeclarationConditions);
        Assert.False(file.RequiresSourceText);
        Assert.True(include.HasDeclarationConditions);
        Assert.True(include.RequiresSourceText);
        Assert.True(exclude.HasDeclarationConditions);
        Assert.True(exclude.RequiresSourceText);
    }

    [Fact]
    public void InvalidRegexFailsAtCompileTime()
    {
        Assert.Throws<SymbolQueryParseException>(() =>
        {
            _ = TypedConditionCompiler.Compile(Request([
                new(ConditionCategory.Method, ConditionSyntax.Regex, "[")
            ]));
        });
    }

    [Fact]
    public void RegexTimeoutBecomesAQueryParseException()
    {
        var compiled = TypedConditionCompiler.Compile(
            Request([new(ConditionCategory.Include, ConditionSyntax.Regex, "(a+)+$")]),
            TimeSpan.FromMilliseconds(1));

        Assert.Throws<SymbolQueryParseException>(() =>
        {
            _ = compiled.MatchesDeclaration(
                Symbol(Path("Game", "Host", "Run()")),
                Declaration("Run.cs", new string('a', 50_000) + "!"),
                TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public void CancellationIsObservedBeforeConditionProbes()
    {
        using var cancellation = new CancellationTokenSource();
        var compiled = Compile([new(ConditionCategory.Namespace, ConditionSyntax.Literal, "Game")]);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = compiled.MatchesLogical(Symbol(Path("Game", "Host", "Run()")), cancellation.Token);
        });
    }

    [Fact]
    public void CancellationIsObservedImmediatelyAfterAnIndividualConditionProbe()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new SourceTextFilter.CompiledSourcePredicate((_, _) =>
        {
            cancellation.Cancel();
            return true;
        });
        var compiled = new CompiledTypedConditions(
            namespaces: [],
            types: [],
            methods: [],
            files: [],
            includes: [probe],
            excludes: [],
            functionFilter: new FunctionTargetFilter(null, AsyncStatusFilter.All));

        Assert.Throws<OperationCanceledException>(() => compiled.MatchesDeclaration(
            Symbol(Path("Game", "Host", "Run()")),
            Declaration("src/Run.cs", "source"),
            cancellation.Token));
    }

    private static CompiledTypedConditions Compile() =>
        Compile([], new SymbolCaseOptions(), new FunctionTargetFilter(null, AsyncStatusFilter.All));

    private static CompiledTypedConditions Compile(
        IReadOnlyList<TypedCondition> conditions,
        SymbolCaseOptions? @case = null,
        FunctionTargetFilter? filter = null) =>
        TypedConditionCompiler.Compile(new SymbolSelectionRequest(
            null,
            conditions,
            @case ?? new SymbolCaseOptions(),
            filter ?? new FunctionTargetFilter(null, AsyncStatusFilter.All),
            false,
            false));

    private static SymbolSelectionRequest Request(
        IReadOnlyList<TypedCondition> conditions,
        SymbolCaseOptions? @case = null,
        FunctionTargetFilter? filter = null) =>
        new(
            null,
            conditions,
            @case ?? new SymbolCaseOptions(),
            filter ?? new FunctionTargetFilter(null, AsyncStatusFilter.All),
            false,
            false);

    private static StoredSymbol Symbol(
        SymbolPathData path,
        IndexedSymbolKind kind = IndexedSymbolKind.Method,
        AsyncRole asyncRole = AsyncRole.None) =>
        new StoredSymbol(
            Id: 1,
            StableKey: "symbol",
            Kind: kind,
            Name: "Run",
            NamespaceName: path.NamespacePath,
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
            AsyncRole: asyncRole,
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

    private static SymbolPathData Path(
        string @namespace,
        string type,
        string displayExecutable,
        string? identityExecutable = null) =>
        new(
            @namespace,
            type,
            type,
            displayExecutable,
            identityExecutable ?? displayExecutable,
            displayExecutable,
            identityExecutable ?? displayExecutable,
            CallablePathSegmentKind.Named);

    private static StoredDeclaration Declaration(string path, string? source) =>
        new(1, "declaration", 1, 1, path, DeclarationRole.Ordinary, 0, 0, 0, source?.Length ?? 1, source, false);

    private static StoredSymbol TestSymbol(SymbolPathData? path)
    {
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
