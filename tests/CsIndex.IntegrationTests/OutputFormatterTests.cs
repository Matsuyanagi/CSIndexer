using System.Text;
using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Console output";
}

[Collection(ConsoleOutputCollection.Name)]
public sealed class OutputFormatterTests
{
    [Theory]
    [InlineData(
        "Nop.Core.Caching.DistributedCacheLocker::RunWithHeartbeatAsync(System.String,System.TimeSpan,System.TimeSpan,System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>,System.Threading.CancellationTokenSource)",
        "DistributedCacheLocker::RunWithHeartbeatAsync(String,TimeSpan,TimeSpan,Func<CancellationToken, Task>,CancellationTokenSource)")]
    [InlineData(
        "Example.Handlers.Worker::Execute(System.Collections.Generic.Dictionary<System.String,System.Collections.Generic.List<Example.Models.Widget?[]>>,System.Nullable<System.Int32>[])",
        "Worker::Execute(Dictionary<String,List<Widget?[]>>,Nullable<Int32>[])")]
    [InlineData(
        "Example.Handlers.Worker::Run(System.Threading.Tasks.Task)::<lambda#1>",
        "Worker::Run(Task)::<lambda#1>")]
    [InlineData(
        "会社.モデル.サービス::実行(会社.モデル.入力)",
        "サービス::実行(入力)")]
    public void SymbolNameShortenerRemovesNamespacesAndKeepsTypeSyntax(string name, string expected)
    {
        Assert.Equal(expected, SymbolNameShortener.Shorten(name));
    }

    [Fact]
    public void WriteSymbolsSingleLineUsesFixedTwoFieldRecordsAndDiagnosticsWriter()
    {
        var sourceBacked = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 1,
            displayName: "Example.SourceBacked()",
            documentPath: "source\tfile.cs",
            sourceStart: 0);
        var metadataOnly = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 2,
            displayName: "Example.MetadataOnly()");
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();

        new OutputFormatter("table", false, SourceLayout.SingleLine, payload, diagnostics)
            .WriteSymbols(
                new QueryContext(CreateProfile(), [sourceBacked, metadataOnly]),
                TestContext.Current.CancellationToken);

        var lines = GetPhysicalLines(payload.ToString());
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Matches(@"^[^\t]+\t[^\t]*$", line);
            Assert.Equal(1, line.Count(character => character == '\t'));
        });
        Assert.Equal("Example.SourceBacked()\tsource file.cs:0:0", lines[0]);
        Assert.Equal("Example.MetadataOnly()\t", lines[1]);
        Assert.Equal($"Query matched 2 symbol(s):{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    public void WriteSymbolsWithSourceSingleLineUsesFixedThreeFieldRecordsAndSanitizesDisplayText()
    {
        const string storedSource = "var raw=\"\"\"\r\nfirst\tline\rsecond\nthird\u0085fourth\u2028fifth\u2029sixth\r\n\"\"\";";
        var sourceBacked = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 1,
            displayName: "Example.Source\tBacked()",
            normalizedSource: storedSource,
            documentPath: "source.cs",
            sourceStart: 0);
        var metadataOnly = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 2,
            displayName: "Example.MetadataOnly()");
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();

        new OutputFormatter("table", false, SourceLayout.SingleLine, payload, diagnostics)
            .WriteSymbols(
                new QueryContext(CreateProfile(), [sourceBacked, metadataOnly], ShowSource: true),
                TestContext.Current.CancellationToken);

        var lines = GetPhysicalLines(payload.ToString());
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Matches(@"^[^\t]+\t[^\t]*\t[^\t]*$", line);
            Assert.Equal(2, line.Count(character => character == '\t'));
        });
        Assert.Equal(
            "Example.Source Backed()\tsource.cs:0:0\tvar raw=\"\"\" first line second third fourth fifth sixth \"\"\";",
            lines[0]);
        Assert.Equal("Example.MetadataOnly()\t\t", lines[1]);
        Assert.Equal($"Query matched 2 symbol(s):{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    public void WriteSymbolListSingleLineWritesSignatureOnlyAndRoutesSummaryToDiagnostics()
    {
        var sourceBacked = CreateSymbol(
            AsyncRole.DeclaredAsync,
            asyncInvolvementDepth: 0,
            id: 1,
            displayName: "Example.SourceBacked()",
            documentPath: "source.cs",
            sourceStart: 0);
        var metadataOnly = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 2,
            displayName: "Example.MetadataOnly()");
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();

        new OutputFormatter("table", false, SourceLayout.SingleLine, payload, diagnostics)
            .WriteSymbolList(
                new QueryContext(CreateProfile(), [sourceBacked, metadataOnly]),
                TestContext.Current.CancellationToken);

        var lines = GetPhysicalLines(payload.ToString());
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Matches(@"^[^\t]+$", line);
            Assert.DoesNotContain('\t', line);
        });
        Assert.Contains("async Example.SourceBacked() [async: DeclaredAsync; depth: 0]", lines);
        Assert.Contains("Example.MetadataOnly()", lines);
        Assert.DoesNotContain("source.cs", payload.ToString(), StringComparison.Ordinal);
        Assert.Equal($"2 symbol(s):{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    public void WriteSymbolsSingleLineWithNoResultsLeavesPayloadEmpty()
    {
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();

        new OutputFormatter("table", false, SourceLayout.SingleLine, payload, diagnostics)
            .WriteSymbols(
                new QueryContext(CreateProfile(), []),
                TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, payload.ToString());
        Assert.Equal($"Query matched 0 symbol(s):{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    public void GetPhysicalLinesPreservesInternalBlankLinesAndTrimsOnlyOneTrailingTerminator()
    {
        Assert.Equal(["first", "", "second"], GetPhysicalLines("first\r\n\r\nsecond\r\n"));
        Assert.Equal(["first", "", "second"], GetPhysicalLines("first\n\nsecond\n"));
        Assert.Equal(["first", ""], GetPhysicalLines("first\n\n"));
        Assert.Equal([""], GetPhysicalLines("\n"));
        Assert.Empty(GetPhysicalLines(string.Empty));
    }

    [Fact]
    public void WriteSymbolsMultiLineRetainsHeadingAndOneSanitizedSignatureAndSourceLinePerResult()
    {
        const string storedSource = "var raw=\"\"\"\r\nfirst\tline\u2028second\r\n\"\"\";";
        var sourceBacked = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 1,
            displayName: "Example.Source\tBacked()",
            normalizedSource: storedSource,
            documentPath: "source.cs",
            sourceStart: 0);
        var metadataOnly = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 2,
            displayName: "Example.Metadata\nOnly()");
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();

        new OutputFormatter("table", false, SourceLayout.MultiLine, payload, diagnostics)
            .WriteSymbols(
                new QueryContext(CreateProfile(), [sourceBacked, metadataOnly], ShowSource: true),
                TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "Query matched 2 symbol(s):",
                "  Example.Source Backed()  source.cs:0:0",
                "    source: var raw=\"\"\" first line second \"\"\";",
                "  Example.Metadata Only()",
                "    source: ",
            ],
            GetPhysicalLines(payload.ToString()));
        Assert.Equal(string.Empty, diagnostics.ToString());
    }

    [Fact]
    public void TableTextSanitizerReplacesCrLfOnceAndEveryRecordBreakingCharacterWithAsciiSpace()
    {
        Assert.Equal(
            "a b c d e f g h",
            TableTextSanitizer.Sanitize("a\r\nb\tc\rd\ne\u0085f\u2028g\u2029h"));
        Assert.Equal(string.Empty, TableTextSanitizer.Sanitize(null));
    }

    [Fact]
    public void TableTextSanitizerPreservesLiteralBackslashEscapesWhileReplacingRealControls()
    {
        Assert.Equal(
            @"\t|\n|\r|\u0085|\u2028|\u2029| | | | | |",
            TableTextSanitizer.Sanitize(
                @"\t|\n|\r|\u0085|\u2028|\u2029" + "|\t|\r\n|\u0085|\u2028|\u2029|"));
    }

    [Fact]
    public void WriteSymbolsJsonUsesPayloadWriterAndPreservesUnsanitizedNormalizedSource()
    {
        const string storedSource = "var raw=\"\"\"\r\nfirst\tline\rsecond\nthird\u0085fourth\u2028fifth\u2029sixth\r\n\"\"\";";
        var symbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            normalizedSource: storedSource,
            documentPath: "source.cs",
            sourceStart: 0);
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();

        new OutputFormatter("json", false, SourceLayout.SingleLine, payload, diagnostics)
            .WriteSymbols(
                new QueryContext(CreateProfile(), [symbol], ShowSource: true),
                TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(payload.ToString());
        var outputSymbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal(storedSource, outputSymbol.GetProperty("normalizedSource").GetString());
        Assert.Equal(string.Empty, diagnostics.ToString());
    }

    [Fact]
    public void InjectedPayloadWriterReceivesEveryNonGraphFormatterFamily()
    {
        var symbol = CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null);
        var context = new QueryContext(CreateProfile(), [symbol]);
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();
        var formatter = new OutputFormatter("table", false, SourceLayout.SingleLine, payload, diagnostics);

        formatter.WriteDefinitions(
            new DefinitionResult(context, [symbol]),
            TestContext.Current.CancellationToken);
        formatter.WriteCalls(
            new CallResult(context, [CreateCall(AsyncUsageKind.None)], [], []),
            "call(s)",
            TestContext.Current.CancellationToken);
        formatter.WriteRelations(new RelationResult(
            context,
            [new StoredRelation(1, "Example.Source()", 2, "Example.Target()", SymbolRelationKind.Overrides)]),
            TestContext.Current.CancellationToken);
        formatter.WriteConditions(new ConditionsResult(
            CreateProfile(),
            [new ConditionalSummary("FEATURE", 1, 2, IsDefined: true)]),
            TestContext.Current.CancellationToken);

        var output = payload.ToString();
        Assert.Contains("1 definition(s):", output);
        Assert.Contains("1 matched symbol(s); 1 call(s):", output);
        Assert.Contains("1 override(s):", output);
        Assert.Contains("Conditional symbols found:", output);
        Assert.Equal(string.Empty, diagnostics.ToString());
    }

    [Fact]
    public void WriteDefinitionsJsonUsesInjectedPayloadWriter()
    {
        var symbol = CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null, id: 11);
        var context = new QueryContext(CreateProfile(), [symbol]);

        using var document = CaptureInjectedJson(formatter => formatter.WriteDefinitions(
            new DefinitionResult(context, [symbol]),
            TestContext.Current.CancellationToken));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        Assert.Equal(
            symbol.Id,
            Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(
            symbol.Id,
            Assert.Single(document.RootElement.GetProperty("definitions").EnumerateArray()).GetProperty("id").GetInt64());
    }

    [Fact]
    public void WriteCallsJsonUsesInjectedPayloadWriter()
    {
        var context = new QueryContext(CreateProfile(), [CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null)]);

        using var document = CaptureInjectedJson(formatter => formatter.WriteCalls(
            new CallResult(context, [CreateCall(AsyncUsageKind.Awaited)], [], []),
            "call(s)",
            TestContext.Current.CancellationToken));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        var call = Assert.Single(document.RootElement.GetProperty("calls").EnumerateArray());
        Assert.Equal(1, call.GetProperty("id").GetInt64());
        Assert.Equal("Awaited", call.GetProperty("asyncUsageKind").GetString());
        Assert.Empty(document.RootElement.GetProperty("callers").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("possibleRuntimeTargets").EnumerateArray());
    }

    [Fact]
    public void WriteRelationsJsonUsesInjectedPayloadWriter()
    {
        var context = new QueryContext(CreateProfile(), [CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null)]);
        var relation = new StoredRelation(
            1,
            "Example.Source()",
            2,
            "Example.Target()",
            SymbolRelationKind.Overrides);

        using var document = CaptureInjectedJson(formatter => formatter.WriteRelations(
            new RelationResult(context, [relation]),
            TestContext.Current.CancellationToken));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        var outputRelation = Assert.Single(document.RootElement.GetProperty("relations").EnumerateArray());
        Assert.Equal(relation.SourceDisplayName, outputRelation.GetProperty("source").GetString());
        Assert.Equal(relation.TargetDisplayName, outputRelation.GetProperty("target").GetString());
        Assert.Equal("Overrides", outputRelation.GetProperty("kind").GetString());
    }

    [Fact]
    public void WriteConditionsJsonUsesInjectedPayloadWriter()
    {
        var summary = new ConditionalSummary("FEATURE", 1, 2, IsDefined: true);

        using var document = CaptureInjectedJson(formatter => formatter.WriteConditions(
            new ConditionsResult(CreateProfile(), [summary]),
            TestContext.Current.CancellationToken));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        Assert.Empty(document.RootElement.GetProperty("activeSymbols").EnumerateArray());
        var outputSummary = Assert.Single(document.RootElement.GetProperty("conditionalSymbols").EnumerateArray());
        Assert.Equal(summary.SymbolName, outputSummary.GetProperty("symbolName").GetString());
        Assert.Equal(summary.FileCount, outputSummary.GetProperty("fileCount").GetInt32());
        Assert.Equal(summary.OccurrenceCount, outputSummary.GetProperty("occurrenceCount").GetInt32());
        Assert.True(outputSummary.GetProperty("isDefined").GetBoolean());
    }

    [Fact]
    public void WriteSymbolsJsonShortensDisplayNameButPreservesCanonicalFields()
    {
        const string displayName = "Nop.Core.Caching.DistributedCacheLocker::RunWithHeartbeatAsync(System.String)";
        var symbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            displayName: displayName,
            parameters: [new StoredParameter(0, "name", "System.String", 0, false)],
            namespaceName: "Nop.Core.Caching");
        var context = new QueryContext(CreateProfile(), [symbol]);

        using var document = CaptureJson(() => new OutputFormatter("json", shortNames: true).WriteSymbols(context));

        var outputSymbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal(
            "DistributedCacheLocker::RunWithHeartbeatAsync(String)",
            outputSymbol.GetProperty("displayName").GetString());
        Assert.Equal("symbol-1", outputSymbol.GetProperty("stableKey").GetString());
        Assert.Equal("Nop.Core.Caching", outputSymbol.GetProperty("namespaceName").GetString());
        Assert.Equal(displayName, outputSymbol.GetProperty("fullyQualifiedName").GetString());
        Assert.Equal("System.String", Assert.Single(outputSymbol.GetProperty("parameters").EnumerateArray()).GetString());
    }

    [Fact]
    public void WriteSymbolsTableUsesDeclarationOrderingAndShortensReturnAndParameterTypes()
    {
        var symbol = CreateSymbol(
            AsyncRole.DeclaredAsync,
            asyncInvolvementDepth: 0,
            displayName: "Tokyo.Gamer::Play(System.String,System.Threading.CancellationToken)",
            parameters:
            [
                new StoredParameter(0, "name", "System.String", 0, false),
                new StoredParameter(1, "token", "System.Threading.CancellationToken", 0, false),
            ],
            namespaceName: "Tokyo",
            isStatic: true,
            returnTypeKey: "System.Threading.Tasks.Task<System.Int32>",
            accessibility: (int)IndexedAccessibility.Public);
        var context = new QueryContext(CreateProfile(), [symbol]);

        var output = CaptureText(() => new OutputFormatter("table", shortNames: true).WriteSymbols(context));

        Assert.Contains("public static async Task<Int32> Gamer::Play(String,CancellationToken)", output);
    }

    [Fact]
    public void WriteSymbolsJsonKeepsCanonicalFieldsAndOnlyShowsSourceWhenRequested()
    {
        var symbol = CreateSymbol(
            AsyncRole.DeclaredAsync,
            asyncInvolvementDepth: 0,
            displayName: "Tokyo.Gamer::Play(System.String)",
            parameters: [new StoredParameter(0, "name", "System.String", 0, false)],
            namespaceName: "Tokyo",
            isStatic: true,
            returnTypeKey: "System.Threading.Tasks.Task<System.Int32>",
            normalizedSource: "public static async Task<int>Play(string name){return 1;}",
            accessibility: (int)IndexedAccessibility.Public);

        using var hiddenSource = CaptureJson(() => new OutputFormatter("json", shortNames: true)
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol])));
        var hiddenSymbol = Assert.Single(hiddenSource.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal("Gamer::Play(String)", hiddenSymbol.GetProperty("displayName").GetString());
        Assert.Equal("public static async Task<Int32> Gamer::Play(String)", hiddenSymbol.GetProperty("signature").GetString());
        Assert.Equal("System.Threading.Tasks.Task<System.Int32>", hiddenSymbol.GetProperty("returnType").GetString());
        Assert.Equal("System.String", Assert.Single(hiddenSymbol.GetProperty("parameters").EnumerateArray()).GetString());
        Assert.Equal("public", hiddenSymbol.GetProperty("accessibility").GetString());
        Assert.True(hiddenSymbol.GetProperty("isStatic").GetBoolean());
        Assert.True(hiddenSymbol.GetProperty("isAsync").GetBoolean());
        Assert.False(hiddenSymbol.TryGetProperty("normalizedSource", out _));

        using var shownSource = CaptureJson(() => new OutputFormatter("json", shortNames: true)
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol], ShowSource: true)));
        var shownSymbol = Assert.Single(shownSource.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal("public static async Task<int>Play(string name){return 1;}",
            shownSymbol.GetProperty("normalizedSource").GetString());
    }

    [Fact]
    public void WriteSymbolsFormatsConstructorAndLambdaApplicableFieldsAndGatesSource()
    {
        var constructor = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 101,
            displayName: "Tokyo.Gamer::.ctor(System.String)",
            parameters: [new StoredParameter(0, "name", "System.String", 0, false)],
            namespaceName: "Tokyo",
            name: ".ctor",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.Constructor,
            normalizedSource: "public Gamer(string name){}",
            accessibility: (int)IndexedAccessibility.Public);
        var lambda = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 102,
            displayName: "Tokyo.Gamer::Run()::<lambda#1>",
            namespaceName: "Tokyo",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#1>",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.AnonymousFunction,
            returnTypeKey: "System.Int32",
            normalizedSource: "()=>42",
            accessibility: (int)IndexedAccessibility.NotApplicable);
        var hiddenContext = new QueryContext(CreateProfile(), [constructor, lambda]);

        var table = CaptureText(() => new OutputFormatter("table").WriteSymbols(hiddenContext));
        Assert.Equal(
            "public Tokyo.Gamer::.ctor(System.String)\t" + Environment.NewLine +
            "System.Int32 Tokyo.Gamer::Run()::<lambda#1>\t" + Environment.NewLine,
            table);
        Assert.DoesNotContain("source:", table);

        var shownTable = CaptureText(() => new OutputFormatter(
            "table",
            false,
            SourceLayout.MultiLine,
            Console.Out,
            Console.Error).WriteSymbols(new QueryContext(CreateProfile(), [constructor, lambda], ShowSource: true)));
        Assert.Contains("source: public Gamer(string name){}", shownTable);
        Assert.Contains("source: ()=>42", shownTable);

        using var hiddenJson = CaptureJson(() => new OutputFormatter("json").WriteSymbols(hiddenContext));
        var hiddenConstructor = Assert.Single(hiddenJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("id").GetInt64() == constructor.Id);
        var hiddenLambda = Assert.Single(hiddenJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("id").GetInt64() == lambda.Id);
        Assert.Equal("public Tokyo.Gamer::.ctor(System.String)", hiddenConstructor.GetProperty("signature").GetString());
        Assert.Equal(JsonValueKind.Null, hiddenConstructor.GetProperty("returnType").ValueKind);
        Assert.Equal("public", hiddenConstructor.GetProperty("accessibility").GetString());
        Assert.False(hiddenConstructor.TryGetProperty("normalizedSource", out _));
        Assert.Equal("System.Int32 Tokyo.Gamer::Run()::<lambda#1>", hiddenLambda.GetProperty("signature").GetString());
        Assert.Equal("System.Int32", hiddenLambda.GetProperty("returnType").GetString());
        Assert.Equal(JsonValueKind.Null, hiddenLambda.GetProperty("accessibility").ValueKind);
        Assert.False(hiddenLambda.TryGetProperty("normalizedSource", out _));

        using var shownJson = CaptureJson(() => new OutputFormatter("json").WriteSymbols(
            new QueryContext(CreateProfile(), [constructor, lambda], ShowSource: true)));
        Assert.Equal(
            "public Gamer(string name){}",
            Assert.Single(shownJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
                symbol.GetProperty("id").GetInt64() == constructor.Id)
            .GetProperty("normalizedSource").GetString());
        Assert.Equal(
            "()=>42",
            Assert.Single(shownJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
                symbol.GetProperty("id").GetInt64() == lambda.Id)
            .GetProperty("normalizedSource").GetString());
    }

    [Fact]
    public void WriteSymbolsFormatsLocalAccessorOperatorAndConversionApplicableFields()
    {
        var local = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 201,
            displayName: "Test.A::Host()::Local()",
            name: "Local",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.LocalFunction,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.NotApplicable);
        var getter = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 202,
            displayName: "Test.A::get_Value()",
            name: "get_Value",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.PropertyGet,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.Public);
        var addition = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 203,
            displayName: "Test.A::op_Addition(Test.A,Test.A)",
            name: "op_Addition",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.UserDefinedOperator,
            isStatic: true,
            returnTypeKey: "Test.A",
            accessibility: (int)IndexedAccessibility.Public);
        var conversion = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 204,
            displayName: "Test.A::op_Implicit(Test.A)",
            name: "op_Implicit",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.Conversion,
            isStatic: true,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.Public);
        var context = new QueryContext(CreateProfile(), [local, getter, addition, conversion]);

        var table = CaptureText(() => new OutputFormatter("table").WriteSymbols(context));
        Assert.Equal(
            "System.Int32 Test.A::Host()::Local()\t" + Environment.NewLine +
            "public System.Int32 Test.A::get_Value()\t" + Environment.NewLine +
            "public static Test.A Test.A::op_Addition(Test.A,Test.A)\t" + Environment.NewLine +
            "public static System.Int32 Test.A::op_Implicit(Test.A)\t" + Environment.NewLine,
            table);

        using var json = CaptureJson(() => new OutputFormatter("json").WriteSymbols(context));
        var symbols = json.RootElement.GetProperty("matched").EnumerateArray()
            .ToDictionary(symbol => symbol.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, symbols[local.Id].GetProperty("accessibility").ValueKind);
        Assert.False(symbols[local.Id].GetProperty("isStatic").GetBoolean());
        Assert.Equal("System.Int32", symbols[local.Id].GetProperty("returnType").GetString());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.LocalFunction, symbols[local.Id].GetProperty("methodKind").GetInt32());
        Assert.Equal("public", symbols[getter.Id].GetProperty("accessibility").GetString());
        Assert.False(symbols[getter.Id].GetProperty("isStatic").GetBoolean());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.PropertyGet, symbols[getter.Id].GetProperty("methodKind").GetInt32());
        Assert.All(new[] { addition, conversion }, symbol =>
        {
            Assert.Equal("public", symbols[symbol.Id].GetProperty("accessibility").GetString());
            Assert.True(symbols[symbol.Id].GetProperty("isStatic").GetBoolean());
        });
        Assert.Equal("Test.A", symbols[addition.Id].GetProperty("returnType").GetString());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.UserDefinedOperator, symbols[addition.Id].GetProperty("methodKind").GetInt32());
        Assert.Equal("System.Int32", symbols[conversion.Id].GetProperty("returnType").GetString());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.Conversion, symbols[conversion.Id].GetProperty("methodKind").GetInt32());
    }

    [Fact]
    public void OutputDestinationCommitReplacesExistingFileWithUtf8WithoutBom()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(outputPath, databasePath))
        {
            destination.Writer.WriteLine("結果 payload");
            destination.Commit(TestContext.Current.CancellationToken);
        }

        var bytes = File.ReadAllBytes(outputPath);
        Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes($"結果 payload{Environment.NewLine}"), bytes);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void OutputDestinationDisposeBeforeCommitPreservesExistingFileAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(outputPath, databasePath))
        {
            destination.Writer.Write("partial payload");
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationCreateDoesNotOpenTheDestinationUntilWriterIsRequested()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (OutputDestination.Create(outputPath, databasePath))
        {
            Assert.Equal("output sentinel", File.ReadAllText(outputPath));
            Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void OutputDestinationResolvesRelativePathsAgainstCurrentDirectory()
    {
        using var directory = new TemporaryDirectory();
        var originalCurrentDirectory = Environment.CurrentDirectory;
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        File.WriteAllText(databasePath, "database sentinel");
        try
        {
            Environment.CurrentDirectory = directory.Path;
            using var destination = OutputDestination.Create("relative.txt", databasePath);
            destination.Writer.Write("relative payload");
            destination.Commit(TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
        }

        Assert.Equal("relative payload", File.ReadAllText(Path.Combine(directory.Path, "relative.txt")));
    }

    [Fact]
    public void OutputDestinationRejectsNormalizedDatabasePathWithoutChangingDatabase()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var equivalentFileName = OperatingSystem.IsWindows() ? "INDEX.SQLITE" : "index.sqlite";
        var equivalentOutputPath = Path.Combine(directory.Path, "unused", "..", equivalentFileName);
        File.WriteAllText(databasePath, "database sentinel");

        Assert.Throws<CliUsageException>(() => OutputDestination.Create(equivalentOutputPath, databasePath));

        Assert.Equal("database sentinel", File.ReadAllText(databasePath));
        Assert.Equal([databasePath], Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(@"\\?\UNC\server\share\folder\index.sqlite")]
    [InlineData(@"\\.\UNC\server\share\folder\index.sqlite")]
    public void OutputDestinationRejectsEquivalentWindowsExtendedUncComparisonPathWithoutAccessingTheShare(
        string equivalentOutputPath)
    {
        const string databasePath = @"\\server\share\folder\index.sqlite";

        var exception = Assert.Throws<CliUsageException>(() =>
            OutputDestination.Create(equivalentOutputPath, databasePath));

        Assert.Contains("must not match the active database path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputDestinationRejectsMissingParentAsOutputErrorWithoutCreatingDirectories()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var missingParent = Path.Combine(directory.Path, "missing", "result.txt");
        File.WriteAllText(databasePath, "database sentinel");

        var exception = Assert.Throws<OutputException>(() => OutputDestination.Create(missingParent, databasePath));

        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingParent)));
        Assert.Equal([databasePath], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void OutputDestinationWithoutFileNeverClosesConsoleOut()
    {
        using var consoleOutput = new StringWriter();
        var originalOutput = Console.Out;
        try
        {
            Console.SetOut(consoleOutput);
            using (var destination = OutputDestination.Create(outputPath: null, databasePath: "index.sqlite"))
            {
                destination.Writer.WriteLine("payload");
                destination.Commit(TestContext.Current.CancellationToken);
            }

            Console.Out.WriteLine("after dispose");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        Assert.Equal($"payload{Environment.NewLine}after dispose{Environment.NewLine}", consoleOutput.ToString());
    }

    [Fact]
    public void FormatterCancellationAfterPartialWritePreservesDestinationAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        var child = CreateSymbol(AsyncRole.None, null, id: 102, displayName: "Example.Child()");
        using var cancellation = new CancellationTokenSource();

        using (var destination = OutputDestination.Create(outputPath, databasePath))
        {
            var nodes = new CancelAfterFirstReadList<StoredSymbol>([root, child], cancellation);
            var result = new AsyncPathResult(CreateProfile(), root, nodes, Found: true, Truncated: false);
            var formatter = new GraphOutputFormatter(shortNames: false, destination.Writer);

            Assert.Throws<OperationCanceledException>(() => formatter.WriteAsyncPath(
                result,
                "tree",
                cancellation.Token));
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationCommitCancellationBeforeFlushPreservesDestinationAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        using var cancellation = new CancellationTokenSource();
        TrackingFlushTextWriter? trackingWriter = null;

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => trackingWriter = new TrackingFlushTextWriter(stream)))
        {
            destination.Writer.Write("complete payload");
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => destination.Commit(cancellation.Token));
            Assert.Equal(0, Assert.IsType<TrackingFlushTextWriter>(trackingWriter).FlushCount);
            Assert.Throws<InvalidOperationException>(() => _ = destination.Writer);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationCommitCancellationDuringFlushPreservesDestinationAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        using var cancellation = new CancellationTokenSource();

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new FlushCancellingTextWriter(stream, cancellation)))
        {
            destination.Writer.Write("complete payload");

            Assert.Throws<OperationCanceledException>(() => destination.Commit(cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationSuccessfulCommitIsIdempotentAndWriterCannotReopenIt()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        using var cancellation = new CancellationTokenSource();

        using (var destination = OutputDestination.Create(outputPath, databasePath))
        {
            destination.Writer.Write("committed payload");
            destination.Commit(TestContext.Current.CancellationToken);
            cancellation.Cancel();

            destination.Commit(cancellation.Token);
            Assert.Throws<InvalidOperationException>(() => _ = destination.Writer);
        }

        Assert.Equal("committed payload", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationFailedCommitCannotBeRetriedOrReopened()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(outputPath, databasePath))
        {
            destination.Writer.Write("replacement payload");
            using (File.Open(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<OutputException>(destination.Commit);
            }

            Assert.Throws<InvalidOperationException>(destination.Commit);
            Assert.Throws<InvalidOperationException>(() => _ = destination.Writer);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationWriterCannotBeReusedAfterWriteFailure()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new WriteThenThrowTextWriter(stream)))
        {
            Assert.Throws<OutputException>(() => destination.Writer.WriteLine("partial payload"));
            Assert.Throws<InvalidOperationException>(() => _ = destination.Writer);
            Assert.Throws<InvalidOperationException>(destination.Commit);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationCancellationRemainsPrimaryWhenAbortDisposeAlsoFails()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        using var cancellation = new CancellationTokenSource();

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new DisposeThrowingTextWriter(stream)))
        {
            var exception = Assert.Throws<OperationCanceledException>(() => destination.WritePayload(writer =>
            {
                writer.Write("final payload");
                cancellation.Cancel();
            }, cancellation.Token));

            AssertCleanupFailure(exception, "Forced dispose failure.");
            Assert.Throws<InvalidOperationException>(() => _ = destination.Writer);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationFormatterFailureRemainsPrimaryWhenAbortDisposeAlsoFails()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        var formatterFailure = new InvalidOperationException("Forced formatter failure.");

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new DisposeThrowingTextWriter(stream)))
        {
            var exception = Assert.Throws<InvalidOperationException>(() => destination.WritePayload(writer =>
            {
                writer.Write("partial payload");
                throw formatterFailure;
            }, TestContext.Current.CancellationToken));

            Assert.Same(formatterFailure, exception);
            AssertCleanupFailure(exception, "Forced dispose failure.");
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationWriteFailureRemainsPrimaryWhenAbortDisposeAlsoFails()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new WriteAndDisposeThrowingTextWriter(stream)))
        {
            var exception = Assert.Throws<OutputException>(() => destination.WritePayload(
                writer => writer.WriteLine("partial payload"),
                TestContext.Current.CancellationToken));

            Assert.IsType<IOException>(exception.InnerException);
            Assert.Contains("Forced write failure.", exception.Message, StringComparison.Ordinal);
            AssertCleanupFailure(exception, "Forced dispose failure.");
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationCommitFailureRemainsPrimaryWhenAbortDisposeAlsoFails()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new FlushAndDisposeThrowingTextWriter(stream)))
        {
            destination.Writer.Write("partial payload");
            var exception = Assert.Throws<OutputException>(() =>
                destination.Commit(TestContext.Current.CancellationToken));

            Assert.IsType<IOException>(exception.InnerException);
            Assert.Contains("Forced flush failure.", exception.Message, StringComparison.Ordinal);
            AssertCleanupFailure(exception, "Forced dispose failure.");
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationRetriesTemporaryNameCollisionWithoutDeletingTheExistingCandidate()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        var collisionPath = Path.Combine(directory.Path, ".result.txt.collision.tmp");
        var ownedTemporaryPath = Path.Combine(directory.Path, ".result.txt.owned.tmp");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        File.WriteAllText(collisionPath, "collision sentinel");
        var candidates = new Queue<string>([collisionPath, ownedTemporaryPath]);

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            CreateUtf8Writer,
            _ => candidates.Dequeue()))
        {
            destination.Writer.Write("replacement payload");
            destination.Commit(TestContext.Current.CancellationToken);
        }

        Assert.Equal("replacement payload", File.ReadAllText(outputPath));
        Assert.Equal("collision sentinel", File.ReadAllText(collisionPath));
        Assert.False(File.Exists(ownedTemporaryPath));
        Assert.Equal(
            [collisionPath, databasePath, outputPath],
            Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationBoundsTemporaryNameCollisionRetriesWithoutTakingOwnership()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        var collisionPath = Path.Combine(directory.Path, ".result.txt.collision.tmp");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        File.WriteAllText(collisionPath, "collision sentinel");
        var attempts = 0;

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            CreateUtf8Writer,
            _ =>
            {
                attempts++;
                return collisionPath;
            }))
        {
            Assert.Throws<OutputException>(() => _ = destination.Writer);
            Assert.Throws<InvalidOperationException>(() => _ = destination.Writer);
        }

        Assert.Equal(8, attempts);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal("collision sentinel", File.ReadAllText(collisionPath));
        Assert.Equal(
            [collisionPath, databasePath, outputPath],
            Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    public void OutputDestinationWriterFactoryFailureIsAnOutputErrorAndCleansOnlyOwnedTemporaryFile(
        string failureKind)
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        var ownedTemporaryPath = Path.Combine(directory.Path, ".result.txt.owned.tmp");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            _ => failureKind == "null"
                ? null!
                : throw new InvalidOperationException("Forced writer factory failure."),
            _ => ownedTemporaryPath))
        {
            var exception = Assert.Throws<OutputException>(() => _ = destination.Writer);

            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Contains(
                failureKind == "null" ? "returned null" : "Forced writer factory failure.",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => _ = destination.Writer);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.False(File.Exists(ownedTemporaryPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("writer", "writer")]
    [InlineData("writer", "commit")]
    [InlineData("writer", "dispose")]
    [InlineData("path", "writer")]
    [InlineData("path", "commit")]
    [InlineData("path", "dispose")]
    public void OutputDestinationRejectsReentrantOpenCallbacksWithoutLosingTemporaryFileOwnership(
        string callbackKind,
        string operation)
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");
        OutputDestination? destination = null;
        var callbackEntered = false;
        var candidateIndex = 0;

        void ReenterDestination()
        {
            switch (operation)
            {
                case "writer":
                    _ = destination!.Writer;
                    break;
                case "commit":
                    destination!.Commit(TestContext.Current.CancellationToken);
                    break;
                case "dispose":
                    destination!.Dispose();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        TextWriter CreateWriter(Stream stream)
        {
            try
            {
                if (callbackKind == "writer" && !callbackEntered)
                {
                    callbackEntered = true;
                    ReenterDestination();
                }

                return new StringWriter();
            }
            finally
            {
                stream.Dispose();
            }
        }

        string CreateTemporaryPath(string _)
        {
            var candidatePath = Path.Combine(directory.Path, $".result.{candidateIndex++}.tmp");
            if (callbackKind == "path" && !callbackEntered)
            {
                callbackEntered = true;
                ReenterDestination();
            }

            return candidatePath;
        }

        destination = OutputDestination.Create(
            outputPath,
            databasePath,
            CreateWriter,
            CreateTemporaryPath);
        Exception? openingFailure;
        Exception? writerReuseFailure = null;
        Exception? commitReuseFailure = null;
        using (destination)
        {
            openingFailure = Record.Exception(() => _ = destination.Writer);
            if (openingFailure is not null)
            {
                writerReuseFailure = Record.Exception(() => _ = destination.Writer);
                commitReuseFailure = Record.Exception(() =>
                    destination.Commit(TestContext.Current.CancellationToken));
            }
        }

        Assert.True(callbackEntered);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
        var outputException = Assert.IsType<OutputException>(openingFailure);
        var lifecycleException = Assert.IsType<InvalidOperationException>(outputException.InnerException);
        Assert.Contains("being opened", lifecycleException.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(writerReuseFailure);
        Assert.IsType<InvalidOperationException>(commitReuseFailure);
    }

    [Fact]
    public void OutputDestinationWriteFailurePreservesDestinationAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new WriteThenThrowTextWriter(stream)))
        {
            Assert.Throws<OutputException>(() => destination.Writer.WriteLine("partial payload"));
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationFlushFailurePreservesDestinationAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (var destination = OutputDestination.Create(
            outputPath,
            databasePath,
            stream => new FlushThrowingTextWriter(stream)))
        {
            destination.Writer.Write("partial payload");
            Assert.Throws<OutputException>(destination.Commit);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OutputDestinationReplaceFailurePreservesDestinationAndRemovesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "index.sqlite");
        var outputPath = Path.Combine(directory.Path, "result.txt");
        File.WriteAllText(databasePath, "database sentinel");
        File.WriteAllText(outputPath, "output sentinel");

        using (File.Open(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = OutputDestination.Create(outputPath, databasePath))
        {
            destination.Writer.Write("replacement payload");
            Assert.Throws<OutputException>(destination.Commit);
        }

        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([databasePath, outputPath], Directory.GetFiles(directory.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GraphOutputFormatterWritesPayloadOnlyToInjectedWriter()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        var result = new AsyncPathResult(CreateProfile(), root, [root], Found: true, Truncated: false);
        using var payload = new StringWriter();
        var formatter = new GraphOutputFormatter(shortNames: false, payload);

        var consoleOutput = CaptureText(() => formatter.WriteAsyncPath(result, "tree"));

        Assert.Equal(string.Empty, consoleOutput);
        Assert.Equal($"Example.Root(){Environment.NewLine}", payload.ToString());
    }

    [Fact]
    public void GraphOutputFormatterWritesAsyncTreeLineAndJsonWithNoPathAndTruncationStates()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        var middle = CreateSymbol(AsyncRole.None, 1, id: 102, displayName: "Example.Middle()");
        var origin = CreateSymbol(AsyncRole.DeclaredAsync, 0, id: 103, displayName: "Example.EndAsync()");
        var result = new AsyncPathResult(CreateProfile(), root, [root, middle, origin], Found: true, Truncated: true);

        var tree = CaptureGraphText(formatter => formatter.WriteAsyncPath(result, "tree"));
        var line = CaptureGraphText(formatter => formatter.WriteAsyncPath(result, "line"));
        using var json = CaptureGraphJson(formatter => formatter.WriteAsyncPath(result, "json"));

        Assert.Equal(
            "Example.Root()" + Environment.NewLine +
            "└─ Example.Middle()" + Environment.NewLine +
            "   └─ async Example.EndAsync()" + Environment.NewLine +
            "      └─ <truncated>" + Environment.NewLine,
            tree);
        Assert.Equal(
            "Example.Root() -> Example.Middle() -> async Example.EndAsync() -> <truncated>" + Environment.NewLine,
            line);
        Assert.True(json.RootElement.GetProperty("found").GetBoolean());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(101, json.RootElement.GetProperty("root").GetProperty("id").GetInt64());
        Assert.Equal([101L, 102L, 103L], json.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("id").GetInt64()));

        var noPath = new AsyncPathResult(CreateProfile(), root, [], Found: false, Truncated: false);
        Assert.Equal(
            "No reachable asynchronous function: Example.Root()" + Environment.NewLine,
            CaptureGraphText(formatter => formatter.WriteAsyncPath(noPath, "tree")));
        Assert.Equal(
            "No reachable asynchronous function: Example.Root()" + Environment.NewLine,
            CaptureGraphText(formatter => formatter.WriteAsyncPath(noPath, "line")));
        using var noPathJson = CaptureGraphJson(formatter => formatter.WriteAsyncPath(noPath, "json"));
        Assert.False(noPathJson.RootElement.GetProperty("found").GetBoolean());
        Assert.Empty(noPathJson.RootElement.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public void GraphOutputFormatterWritesCallerTreeMermaidAndJsonWithEscapedUniqueEdges()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Target()");
        var caller = CreateSymbol(
            AsyncRole.None,
            null,
            id: 202,
            displayName: "Example.Caller(\"quoted\")\n[bracket]");
        var result = new CallerTreeResult(
            CreateProfile(),
            root,
            [new CallerTreeNode(root, 0), new CallerTreeNode(caller, 1)],
            [new CallerTreeEdge(caller.Id, root.Id), new CallerTreeEdge(caller.Id, root.Id)],
            Truncated: true);

        var tree = CaptureGraphText(formatter => formatter.WriteCallerTree(result, "tree"));
        var mermaid = CaptureGraphText(formatter => formatter.WriteCallerTree(result, "mermaid"));
        using var json = CaptureGraphJson(formatter => formatter.WriteCallerTree(result, "json"));

        Assert.Equal(
            "Example.Target()" + Environment.NewLine +
            "└─ Example.Caller(\"quoted\") [bracket]" + Environment.NewLine +
            "└─ <truncated>" + Environment.NewLine,
            tree);
        Assert.StartsWith("flowchart TD" + Environment.NewLine, mermaid, StringComparison.Ordinal);
        Assert.Contains("n101[\"Example.Target()\"]", mermaid);
        Assert.Contains("n202[\"Example.Caller(&quot;quoted&quot;)<br/>&#91;bracket&#93;\"]", mermaid);
        Assert.Equal(1, mermaid.Split("n202 --> n101", StringSplitOptions.None).Length - 1);
        Assert.Contains("%% truncated", mermaid);
        var callerNode = Assert.Single(json.RootElement.GetProperty("nodes").EnumerateArray(), node =>
            node.GetProperty("symbol").GetProperty("id").GetInt64() == caller.Id);
        Assert.Equal(1, callerNode.GetProperty("depth").GetInt32());
        var edge = Assert.Single(json.RootElement.GetProperty("edges").EnumerateArray());
        Assert.Equal(caller.Id, edge.GetProperty("callerSymbolId").GetInt64());
        Assert.Equal(root.Id, edge.GetProperty("calleeSymbolId").GetInt64());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void GraphOutputFormatterUsesCallerEdgesForBranchesAndAdditionalEdges()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        var a = CreateSymbol(AsyncRole.None, null, id: 102, displayName: "Example.A()");
        var z = CreateSymbol(AsyncRole.None, null, id: 103, displayName: "Example.Z()");
        var a2 = CreateSymbol(AsyncRole.None, null, id: 104, displayName: "Example.A2()");
        var z2 = CreateSymbol(AsyncRole.None, null, id: 105, displayName: "Example.Z2()");
        var shared = CreateSymbol(AsyncRole.None, null, id: 106, displayName: "Example.Shared()");
        var result = new CallerTreeResult(
            CreateProfile(),
            root,
            [
                new CallerTreeNode(root, 0),
                new CallerTreeNode(a, 1),
                new CallerTreeNode(z, 1),
                new CallerTreeNode(a2, 2),
                new CallerTreeNode(z2, 2),
                new CallerTreeNode(shared, 2),
            ],
            [
                new CallerTreeEdge(root.Id, a.Id),
                new CallerTreeEdge(a.Id, root.Id),
                new CallerTreeEdge(z.Id, root.Id),
                new CallerTreeEdge(a2.Id, z.Id),
                new CallerTreeEdge(z2.Id, a.Id),
                new CallerTreeEdge(shared.Id, z.Id),
                new CallerTreeEdge(shared.Id, a.Id),
            ],
            Truncated: true);

        var tree = CaptureGraphText(formatter => formatter.WriteCallerTree(result, "tree"));
        var mermaid = CaptureGraphText(formatter => formatter.WriteCallerTree(result, "mermaid"));
        using var json = CaptureGraphJson(formatter => formatter.WriteCallerTree(result, "json"));

        Assert.Equal(
            "Example.Root()" + Environment.NewLine +
            "└─ Example.A()" + Environment.NewLine +
            "   └─ Example.Shared()" + Environment.NewLine +
            "   └─ Example.Z2()" + Environment.NewLine +
            "└─ Example.Z()" + Environment.NewLine +
            "   └─ Example.A2()" + Environment.NewLine +
            "└─ <truncated>" + Environment.NewLine +
            "Additional edges:" + Environment.NewLine +
            "  Example.Root() -> Example.A()" + Environment.NewLine +
            "  Example.Shared() -> Example.Z()" + Environment.NewLine,
            tree);
        Assert.All(
            [
                "n101[\"Example.Root()\"]",
                "n102[\"Example.A()\"]",
                "n103[\"Example.Z()\"]",
                "n104[\"Example.A2()\"]",
                "n105[\"Example.Z2()\"]",
                "n106[\"Example.Shared()\"]",
                "n101 --> n102",
                "n102 --> n101",
                "n103 --> n101",
                "n104 --> n103",
                "n105 --> n102",
                "n106 --> n102",
                "n106 --> n103",
            ],
            edge => Assert.Contains(edge, mermaid));
        Assert.Equal(
            [101L, 102L, 103L, 104L, 106L, 105L],
            json.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(node => node.GetProperty("symbol").GetProperty("id").GetInt64()));
        Assert.Equal(
            ["101->102", "102->101", "103->101", "104->103", "105->102", "106->102", "106->103"],
            json.RootElement.GetProperty("edges").EnumerateArray()
                .Select(edge => $"{edge.GetProperty("callerSymbolId").GetInt64()}->{edge.GetProperty("calleeSymbolId").GetInt64()}"));
    }

    [Fact]
    public void GraphOutputFormatterHonorsCancellationBeforeWriting()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new GraphOutputFormatter(shortNames: false, TextWriter.Null).WriteAsyncPath(
            new AsyncPathResult(CreateProfile(), root, [root], Found: true, Truncated: false),
            "tree",
            cancellation.Token));
    }

    [Fact]
    public void OrderNodes_ObservesCancellationDuringMaterialization()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => GraphOutputFormatter.OrderNodes(
            CancelBeforeYieldingNode(cancellation),
            cancellation.Token));
    }

    [Fact]
    public void OrderNodes_ObservesCancellationDuringOrdering()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;

        Assert.Throws<OperationCanceledException>(() => GraphOutputFormatter.OrderNodes(
            [
                new CallerTreeNode(CreateSymbol(AsyncRole.None, null, id: 3, displayName: "Example.C()"), 1),
                new CallerTreeNode(CreateSymbol(AsyncRole.None, null, id: 2, displayName: "Example.B()"), 1),
                new CallerTreeNode(CreateSymbol(AsyncRole.None, null, id: 1, displayName: "Example.A()"), 1),
            ],
            cancellation.Token,
            () =>
            {
                comparisons++;
                cancellation.Cancel();
            }));

        Assert.True(comparisons > 0);
    }

    [Fact]
    public void OrderEdges_ObservesCancellationDuringOrdering()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;

        Assert.Throws<OperationCanceledException>(() => GraphOutputFormatter.OrderEdges(
            [new CallerTreeEdge(3, 1), new CallerTreeEdge(2, 1), new CallerTreeEdge(1, 1)],
            cancellation.Token,
            () =>
            {
                comparisons++;
                cancellation.Cancel();
            }));

        Assert.True(comparisons > 0);
    }

    [Fact]
    public void OutputFormatterHonorsCancellationBeforeWritingJson()
    {
        var symbol = CreateSymbol(AsyncRole.None, null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CaptureText(() => new OutputFormatter("json")
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol]), cancellation.Token)));
    }

    [Fact]
    public void WriteCallsTableShortensCallerAndCalleeNames()
    {
        var context = new QueryContext(CreateProfile(), []);
        var call = CreateCall(AsyncUsageKind.None) with
        {
            CallerDisplayName = "Example.Features.Caller::Run(System.String)",
            CalleeDisplayName = "Example.Services.Callee::Execute(System.Threading.Tasks.Task)",
            CalleeDefinitionDisplayName = "Example.Services.Callee::Execute(System.Threading.Tasks.Task)",
        };
        var result = new CallResult(context, [call], [], []);

        var output = CaptureText(() => new OutputFormatter("table", shortNames: true).WriteCalls(result, "call(s)"));

        Assert.Contains("Caller::Run(String) -> Callee::Execute(Task)", output);
    }

    [Fact]
    public void WriteSymbolsTableKeepsCanonicalNamesByDefault()
    {
        const string displayName = "Example.Features.Worker::Run(System.Threading.Tasks.Task)";
        var symbol = CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null, displayName: displayName);
        var context = new QueryContext(CreateProfile(), [symbol]);

        var output = CaptureText(() => new OutputFormatter("table").WriteSymbols(context));

        Assert.Equal(
            $"{displayName}\t{Environment.NewLine}",
            output);
    }

    [Fact]
    public void WriteSymbolsJsonIncludesAsyncAnalysis()
    {
        var symbol = CreateSymbol(
            AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable,
            asyncInvolvementDepth: 0);
        var context = new QueryContext(CreateProfile(), [symbol]);

        using var document = CaptureJson(() => new OutputFormatter("json").WriteSymbols(context));

        var outputSymbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal("DeclaredAsync, ReturnsAwaitable", outputSymbol.GetProperty("asyncRole").GetString());
        Assert.True(outputSymbol.GetProperty("isAsyncInvolved").GetBoolean());
        Assert.Equal(0, outputSymbol.GetProperty("asyncInvolvementDepth").GetInt32());
    }

    [Fact]
    public void WriteSymbolListJsonUsesSymbolsAndShortDisplayNames()
    {
        var symbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            displayName: "Example.Features.Worker::Run(System.Threading.Tasks.Task)");
        var context = new QueryContext(CreateProfile(), [symbol]);

        using var document = CaptureJson(() => new OutputFormatter("json", shortNames: true).WriteSymbolList(context));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        var outputSymbol = Assert.Single(document.RootElement.GetProperty("symbols").EnumerateArray());
        Assert.Equal("Worker::Run(Task)", outputSymbol.GetProperty("displayName").GetString());
        Assert.False(document.RootElement.TryGetProperty("matched", out _));
    }

    [Fact]
    public void WriteCallsJsonIncludesAsyncUsageKind()
    {
        var context = new QueryContext(CreateProfile(), []);
        var result = new CallResult(context, [CreateCall(AsyncUsageKind.Awaited)], [], []);

        using var document = CaptureJson(() => new OutputFormatter("json").WriteCalls(result, "call(s)"));

        var call = Assert.Single(document.RootElement.GetProperty("calls").EnumerateArray());
        Assert.Equal("Awaited", call.GetProperty("asyncUsageKind").GetString());
    }

    [Fact]
    public void WriteSymbolsTableShowsAsyncRoleAndDepthOnlyWhenAsyncAnalysisExists()
    {
        var asyncSymbol = CreateSymbol(
            AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable,
            asyncInvolvementDepth: 0,
            displayName: "Example.AsyncMethod()");
        var synchronousSymbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 2,
            displayName: "Example.SyncMethod()");
        var context = new QueryContext(CreateProfile(), [asyncSymbol, synchronousSymbol]);

        var output = CaptureText(() => new OutputFormatter("table").WriteSymbols(context));

        Assert.Contains(
            "Example.AsyncMethod() [async: DeclaredAsync, ReturnsAwaitable; depth: 0]",
            output);
        Assert.Contains("Example.SyncMethod()", output);
        Assert.DoesNotContain("Example.SyncMethod() [async:", output);
    }

    [Fact]
    public void WriteCallsTableShowsAsyncUsageKind()
    {
        var context = new QueryContext(CreateProfile(), []);
        var result = new CallResult(context, [CreateCall(AsyncUsageKind.Awaited)], [], []);

        var output = CaptureText(() => new OutputFormatter("table").WriteCalls(result, "call(s)"));

        Assert.Contains("[Awaited]", output);
    }

    private static JsonDocument CaptureJson(Action write) => JsonDocument.Parse(CaptureText(write));

    private static JsonDocument CaptureGraphJson(Action<GraphOutputFormatter> write) =>
        JsonDocument.Parse(CaptureGraphText(write));

    private static string CaptureGraphText(Action<GraphOutputFormatter> write)
    {
        using var payload = new StringWriter();
        var consoleOutput = CaptureText(() => write(new GraphOutputFormatter(shortNames: false, payload)));

        Assert.Equal(string.Empty, consoleOutput);
        return payload.ToString();
    }

    private static JsonDocument CaptureInjectedJson(Action<OutputFormatter> write)
    {
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();
        var formatter = new OutputFormatter("json", false, SourceLayout.SingleLine, payload, diagnostics);

        var consoleOutput = CaptureText(() => write(formatter));

        Assert.Equal(string.Empty, consoleOutput);
        Assert.Equal(string.Empty, diagnostics.ToString());
        return JsonDocument.Parse(payload.ToString());
    }

    private static string[] GetPhysicalLines(string output)
    {
        if (output.Length == 0)
        {
            return [];
        }

        var withoutTrailingTerminator = output.EndsWith("\r\n", StringComparison.Ordinal)
            ? output[..^2]
            : output.EndsWith('\n')
                ? output[..^1]
                : output;
        return withoutTrailingTerminator
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.None);
    }

    private static IEnumerable<CallerTreeNode> CancelBeforeYieldingNode(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        yield return new CallerTreeNode(CreateSymbol(AsyncRole.None, null), 0);
    }

    private static string CaptureText(Action write)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            write();
            return output.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static void AssertCleanupFailure(Exception primaryFailure, string expectedMessage)
    {
        var cleanupFailure = Assert.IsAssignableFrom<Exception>(
            primaryFailure.Data["OutputDestination.CleanupFailure"]);
        Assert.Contains(expectedMessage, cleanupFailure.ToString(), StringComparison.Ordinal);
    }

    private static TextWriter CreateUtf8Writer(Stream stream) => new StreamWriter(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 1024,
        leaveOpen: true);

    private sealed class CancelAfterFirstReadList<T>(IReadOnlyList<T> values, CancellationTokenSource cancellation)
        : IReadOnlyList<T>
    {
        public int Count => values.Count;

        public T this[int index]
        {
            get
            {
                var value = values[index];
                if (index == 0)
                {
                    cancellation.Cancel();
                }

                return value;
            }
        }

        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class WriteThenThrowTextWriter(Stream stream) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public override Encoding Encoding => _writer.Encoding;

        public override void WriteLine(string? value)
        {
            _writer.Write(value);
            _writer.Flush();
            throw new IOException("Forced write failure.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FlushThrowingTextWriter(Stream stream) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public override Encoding Encoding => _writer.Encoding;

        public override void Write(string? value) => _writer.Write(value);

        public override void Flush() => throw new IOException("Forced flush failure.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FlushCancellingTextWriter(
        Stream stream,
        CancellationTokenSource cancellation) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public override Encoding Encoding => _writer.Encoding;

        public override void Write(string? value) => _writer.Write(value);

        public override void Flush()
        {
            _writer.Flush();
            cancellation.Cancel();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingFlushTextWriter(Stream stream) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public int FlushCount { get; private set; }

        public override Encoding Encoding => _writer.Encoding;

        public override void Write(string? value) => _writer.Write(value);

        public override void Flush()
        {
            FlushCount++;
            _writer.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private class DisposeThrowingTextWriter(Stream stream) : TextWriter
    {
        protected readonly StreamWriter Writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public override Encoding Encoding => Writer.Encoding;

        public override void Write(string? value) => Writer.Write(value);

        public override void WriteLine(string? value) => Writer.WriteLine(value);

        public override void Flush() => Writer.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Writer.Dispose();
                throw new IOException("Forced dispose failure.");
            }

            base.Dispose(disposing);
        }
    }

    private sealed class WriteAndDisposeThrowingTextWriter(Stream stream) : DisposeThrowingTextWriter(stream)
    {
        public override void WriteLine(string? value)
        {
            Writer.WriteLine(value);
            Writer.Flush();
            throw new IOException("Forced write failure.");
        }
    }

    private sealed class FlushAndDisposeThrowingTextWriter(Stream stream) : DisposeThrowingTextWriter(stream)
    {
        public override void Flush() => throw new IOException("Forced flush failure.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "csindex-output-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static StoredProfile CreateProfile() => new(
        Id: 1,
        Name: "default",
        InputMode: InputMode.Directory,
        Configuration: null,
        TargetFramework: null,
        RuntimeIdentifier: null,
        PreprocessorSymbols: [],
        InputRoot: ".");

    private static StoredSymbol CreateSymbol(
        AsyncRole asyncRole,
        int? asyncInvolvementDepth,
        long id = 1,
        string displayName = "Example.Method()",
        IReadOnlyList<StoredParameter>? parameters = null,
        string namespaceName = "Example",
        IndexedSymbolKind kind = IndexedSymbolKind.Method,
        string name = "Method",
        int? methodKind = null,
        bool isStatic = false,
        string? returnTypeKey = null,
        string? normalizedSource = null,
        int? accessibility = null,
        string? documentPath = null,
        int? sourceStart = null) => new(
        Id: id,
        StableKey: $"symbol-{id}",
        Kind: kind,
        Name: name,
        NamespaceName: namespaceName,
        TypeSimpleName: "Example",
        TypeMetadataName: "Example",
        FullyQualifiedName: displayName,
        DisplayName: displayName,
        ContainingSymbolId: null,
        Arity: 0,
        ParameterCount: 0,
        MethodKind: methodKind,
        IsStatic: isStatic,
        IsAbstract: false,
        IsVirtual: false,
        IsOverride: false,
        AsyncRole: asyncRole,
        AsyncInvolvementDepth: asyncInvolvementDepth,
        AsyncNextSymbolId: null,
        ReturnTypeKey: returnTypeKey,
        NormalizedSource: normalizedSource,
        NormalizedSourceHash: null,
        DocumentPath: documentPath,
        SourceStart: sourceStart,
        SourceLength: normalizedSource?.Length,
        IsGenerated: false,
        AssemblyName: null,
        Parameters: parameters ?? [],
        TypeKind: null,
        Accessibility: accessibility);

    private static StoredCall CreateCall(AsyncUsageKind asyncUsageKind) => new(
        Id: 1,
        CallerSymbolId: 1,
        CallerDisplayName: "Example.Caller()",
        CallerContainingSymbolId: null,
        CalleeSymbolId: 2,
        CalleeDisplayName: "Example.Callee()",
        CalleeDefinitionId: 2,
        CalleeDefinitionDisplayName: "Example.Callee()",
        ReferenceKind: ReferenceKind.Invocation,
        DispatchKind: DispatchKind.Static,
        ResolutionStatus: ResolutionStatus.Resolved,
        ResolutionReason: ResolutionReason.None,
        AsyncUsageKind: asyncUsageKind,
        DocumentId: 1,
        DocumentPath: "missing.cs",
        SourceStart: 0,
        SourceLength: 1,
        IsGenerated: false,
        UnresolvedName: null,
        ReceiverTypeKey: null);
}
