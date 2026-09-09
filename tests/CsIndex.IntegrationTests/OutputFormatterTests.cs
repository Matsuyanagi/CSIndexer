using System.Text;
using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Console output";
}

[Collection(ConsoleOutputCollection.Name)]
public sealed class OutputFormatterTests : IDisposable
{
    private static readonly SymbolPathFormatOptions FullSymbolPathOptions =
        new(SymbolPathStyle.CSharp, ShortNames: false);

    private static readonly SymbolPathFormatOptions ShortSymbolPathOptions =
        new(SymbolPathStyle.CSharp, ShortNames: true);

    private readonly TemporaryDirectory _sourceDirectory;
    private readonly IndexPathResolver _pathResolver;

    public OutputFormatterTests()
    {
        _sourceDirectory = new TemporaryDirectory();
        _pathResolver = IndexPathResolver.CreateForIndex(
            Path.Combine(_sourceDirectory.Path, ".csindex", "index.sqlite"),
            _sourceDirectory.Path);
        foreach (var path in new[]
        {
            "source file.cs",
            "source.cs",
            "PartialDefinition.cs",
            "PartialImplementation.cs",
            "missing.cs",
        })
        {
            File.WriteAllText(Path.Combine(_sourceDirectory.Path, path), "test source");
        }
    }

    public void Dispose() => _sourceDirectory.Dispose();

    [Theory]
    [InlineData(
        "Nop.Core.Caching.DistributedCacheLocker::RunWithHeartbeatAsync(System.String,System.TimeSpan,System.TimeSpan,System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>,System.Threading.CancellationTokenSource)",
        "DistributedCacheLocker::RunWithHeartbeatAsync(System.String,System.TimeSpan,System.TimeSpan,System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>,System.Threading.CancellationTokenSource)")]
    [InlineData(
        "Example.Handlers.Worker::Execute(System.Collections.Generic.Dictionary<System.String,System.Collections.Generic.List<Example.Models.Widget?[]>>,System.Nullable<System.Int32>[])",
        "Worker::Execute(System.Collections.Generic.Dictionary<System.String,System.Collections.Generic.List<Example.Models.Widget?[]>>,System.Nullable<System.Int32>[])")]
    [InlineData(
        "Example.Handlers.Worker::Run(System.Threading.Tasks.Task).<lambda#1>",
        "Worker::Run(System.Threading.Tasks.Task).<lambda#1>")]
    [InlineData(
        "会社.モデル.サービス::実行(会社.モデル.入力)",
        "サービス::実行(会社.モデル.入力)")]
    public void SymbolPathFormatterShortNamesRemoveOnlyOwnerNamespace(string name, string expected)
    {
        var separator = name.IndexOf("::", StringComparison.Ordinal);
        var owner = separator < 0 ? name : name[..separator];
        var executable = separator < 0 ? string.Empty : name[(separator + 2)..];
        var path = new SymbolPathData(
            NamespacePath: separator < 0 ? string.Empty : owner[..owner.LastIndexOf('.')],
            TypeDisplayPath: separator < 0 ? owner : owner[(owner.LastIndexOf('.') + 1)..],
            TypeIdentityPath: separator < 0 ? owner : owner[(owner.LastIndexOf('.') + 1)..],
            ExecutableDisplayPath: executable,
            ExecutableIdentityPath: executable,
            SegmentDisplay: executable,
            SegmentIdentity: executable,
            SegmentKind: CallablePathSegmentKind.Named);
        var actual = new CsIndex.Core.Symbols.SymbolPathFormatter().Format(
            path,
            new CsIndex.Core.Symbols.SymbolPathFormatOptions(
                CsIndex.Core.Symbols.SymbolPathStyle.CSharp,
                ShortNames: true));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteSymbolsSingleLineUsesFixedTwoFieldRecordsAndDiagnosticsWriter()
    {
        var sourceBacked = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 1,
            displayName: "Example.SourceBacked()",
            documentPath: "source file.cs",
            sourceStart: 0);
        var metadataOnly = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 2,
            displayName: "Example.MetadataOnly()");
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();

        CreateOutputFormatter("table", FullSymbolPathOptions, SourceLayout.SingleLine, payload, diagnostics)
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
        Assert.Equal("Example.SourceBacked()\tsource file.cs:1:1", lines[0]);
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

        CreateOutputFormatter("table", FullSymbolPathOptions, SourceLayout.SingleLine, payload, diagnostics)
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
            "Example.Source Backed()\tsource.cs:1:1\tvar raw=\"\"\" first line second third fourth fifth sixth \"\"\";",
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

        CreateOutputFormatter("table", FullSymbolPathOptions, SourceLayout.SingleLine, payload, diagnostics)
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

        CreateOutputFormatter("table", FullSymbolPathOptions, SourceLayout.SingleLine, payload, diagnostics)
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
        const string storedSource =
            "var raw=\"\"\"\r\nfirst\tsecond\rthird\nfourth\u0085fifth\u2028sixth\u2029seventh\r\n\"\"\";";
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

        CreateOutputFormatter("table", FullSymbolPathOptions, SourceLayout.MultiLine, payload, diagnostics)
            .WriteSymbols(
                new QueryContext(CreateProfile(), [sourceBacked, metadataOnly], ShowSource: true),
                TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "Query matched 2 symbol(s):",
                "  Example.Source Backed()  source.cs:1:1",
                "    source: var raw=\"\"\" first second third fourth fifth sixth seventh \"\"\";",
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

        CreateOutputFormatter("json", FullSymbolPathOptions, SourceLayout.SingleLine, payload, diagnostics)
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
        var formatter = CreateOutputFormatter("table", FullSymbolPathOptions, SourceLayout.SingleLine, payload, diagnostics);

        formatter.WriteDefinitions(
            CreateDefinitionResult(context, [symbol]),
            TestContext.Current.CancellationToken);
        formatter.WriteCalls(
            new CallResult(
                CreateSelection(context),
                [CreateCall(AsyncUsageKind.None)],
                [],
                [],
                CreateEndpointSymbols()),
            "call(s)",
            TestContext.Current.CancellationToken);
        formatter.WriteRelations(new RelationResult(
            CreateSelection(context),
            [new StoredRelation(1, 2, SymbolRelationKind.Overrides)],
            CreateEndpointSymbols()),
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
            CreateDefinitionResult(context, [symbol]),
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
    public void DefinitionRowsRequireDedicatedRoleFieldsInJsonAndTable()
    {
        var symbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 11,
            displayName: "Partials.PartialHost::PartialWork()");
        var context = new QueryContext(CreateProfile(), [symbol]);
        var definition = CreateDeclaration(symbol) with
        {
            Id = 21,
            DeclarationKey = "partial-definition",
            DocumentPath = "PartialDefinition.cs",
            Role = DeclarationRole.PartialDefinition,
            SourceStart = 10,
        };
        var implementation = CreateDeclaration(symbol) with
        {
            Id = 22,
            DeclarationKey = "partial-implementation",
            DocumentPath = "PartialImplementation.cs",
            Role = DeclarationRole.PartialImplementation,
            SourceStart = 20,
        };
        var result = new DefinitionResult(
            CreateSelection(context),
            [new DeclarationResultRow(symbol, definition), new DeclarationResultRow(symbol, implementation)]);

        var table = CaptureText(() => CreateOutputFormatter("table", FullSymbolPathOptions).WriteDefinitions(result));
        using var json = CaptureJson(() => CreateOutputFormatter("json", FullSymbolPathOptions).WriteDefinitions(result));

        Assert.Contains("\tpartial-definition\t", table, StringComparison.Ordinal);
        Assert.Contains("\tpartial-implementation\t", table, StringComparison.Ordinal);
        Assert.Equal(
            ["partial-definition", "partial-implementation"],
            json.RootElement.GetProperty("definitions").EnumerateArray()
                .Select(row => row.GetProperty("declarationRole").GetString()));
        Assert.All(
            json.RootElement.GetProperty("matched").EnumerateArray(),
            matched => Assert.False(matched.TryGetProperty("declarationRole", out _)));
    }

    [Fact]
    public void WriteCallsJsonUsesInjectedPayloadWriter()
    {
        var context = new QueryContext(CreateProfile(), [CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null)]);

        using var document = CaptureInjectedJson(formatter => formatter.WriteCalls(
            new CallResult(
                CreateSelection(context),
                [CreateCall(AsyncUsageKind.Awaited)],
                [],
                [],
                CreateEndpointSymbols()),
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
            2,
            SymbolRelationKind.Overrides);

        using var document = CaptureInjectedJson(formatter => formatter.WriteRelations(
            new RelationResult(CreateSelection(context), [relation], CreateEndpointSymbols()),
            TestContext.Current.CancellationToken));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        var outputRelation = Assert.Single(document.RootElement.GetProperty("relations").EnumerateArray());
        Assert.Equal(
            FormatPath(CreateEndpointSymbols()[relation.SourceSymbolId]),
            outputRelation.GetProperty("source").GetString());
        Assert.Equal(
            FormatPath(CreateEndpointSymbols()[relation.TargetSymbolId]),
            outputRelation.GetProperty("target").GetString());
        Assert.Equal("Overrides", outputRelation.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("table", "caller", 1)]
    [InlineData("json", "caller", 1)]
    [InlineData("table", "callee", 2)]
    [InlineData("json", "callee", 2)]
    [InlineData("table", "definition", 3)]
    [InlineData("json", "definition", 3)]
    public void WriteCallsRejectsEveryMissingNonNullHydratedEndpoint(
        string format,
        string missingRole,
        long missingId)
    {
        var call = CreateCall(AsyncUsageKind.None) with { CalleeDefinitionId = 3 };
        var endpoints = CreateEndpointSymbols().ToDictionary();
        endpoints[3] = CreateSymbol(
            AsyncRole.None,
            null,
            id: 3,
            displayName: "Example.Definition()");
        Assert.True(endpoints.Remove(missingId), missingRole);
        var result = new CallResult(
            CreateSelection(new QueryContext(CreateProfile(), [])),
            [call],
            [],
            [],
            endpoints);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CaptureText(() => CreateOutputFormatter(format, FullSymbolPathOptions).WriteCalls(result, "call(s)")));

        Assert.Equal(
            $"call endpoint symbol ID {missingId} is missing from the hydration batch.",
            exception.Message);
    }

    [Theory]
    [InlineData("table", 1)]
    [InlineData("json", 1)]
    [InlineData("table", 2)]
    [InlineData("json", 2)]
    public void WriteRelationsRejectsEveryMissingHydratedEndpoint(string format, long missingId)
    {
        var endpoints = CreateEndpointSymbols().ToDictionary();
        Assert.True(endpoints.Remove(missingId));
        var result = new RelationResult(
            CreateSelection(new QueryContext(CreateProfile(), [])),
            [new StoredRelation(1, 2, SymbolRelationKind.Overrides)],
            endpoints);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CaptureText(() => CreateOutputFormatter(format, FullSymbolPathOptions).WriteRelations(result)));

        Assert.Equal(
            $"relation endpoint symbol ID {missingId} is missing from the hydration batch.",
            exception.Message);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("json")]
    public void WriteCallsUsesUnresolvedNameOnlyWhenBothEndpointIdsAreNull(string format)
    {
        const string unresolvedName = "DynamicTarget<System.Guid>";
        var call = CreateCall(AsyncUsageKind.None) with
        {
            CalleeSymbolId = null,
            CalleeDefinitionId = null,
            UnresolvedName = unresolvedName,
            ResolutionStatus = ResolutionStatus.Unresolved,
        };
        var endpoints = CreateEndpointSymbols()
            .Where(pair => pair.Key == call.CallerSymbolId)
            .ToDictionary();
        var result = new CallResult(
            CreateSelection(new QueryContext(CreateProfile(), [])),
            [call],
            [],
            [],
            endpoints);

        var output = CaptureText(() => CreateOutputFormatter(format, FullSymbolPathOptions).WriteCalls(result, "call(s)"));

        if (format == "json")
        {
            using var document = JsonDocument.Parse(output);
            Assert.Equal(
                unresolvedName,
                Assert.Single(document.RootElement.GetProperty("calls").EnumerateArray())
                    .GetProperty("callee")
                    .GetString());
        }
        else
        {
            Assert.Contains(unresolvedName, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WriteCallsJsonEmitsUnresolvedNameOnlyForACompletelyDanglingEndpoint()
    {
        const string danglingName = "DynamicTarget<System.Guid>";
        var resolved = CreateCall(AsyncUsageKind.None) with
        {
            Id = 11,
            UnresolvedName = "stale-resolver-token",
        };
        var dangling = CreateCall(AsyncUsageKind.None) with
        {
            Id = 12,
            CalleeSymbolId = null,
            CalleeDefinitionId = null,
            UnresolvedName = danglingName,
            ResolutionStatus = ResolutionStatus.Unresolved,
        };
        var result = new CallResult(
            CreateSelection(new QueryContext(CreateProfile(), [])),
            [resolved, dangling],
            [],
            [],
            CreateEndpointSymbols());

        using var document = CaptureJson(() => CreateOutputFormatter("json", FullSymbolPathOptions).WriteCalls(result, "call(s)"));
        var calls = document.RootElement.GetProperty("calls").EnumerateArray()
            .ToDictionary(call => call.GetProperty("id").GetInt64());

        Assert.Equal(FormatPath(CreateEndpointSymbols()[2]), calls[resolved.Id].GetProperty("callee").GetString());
        Assert.Equal(JsonValueKind.Null, calls[resolved.Id].GetProperty("unresolvedName").ValueKind);
        Assert.Equal(danglingName, calls[dangling.Id].GetProperty("callee").GetString());
        Assert.Equal(danglingName, calls[dangling.Id].GetProperty("unresolvedName").GetString());
        Assert.DoesNotContain("symbol-2", calls[resolved.Id].GetProperty("callee").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("json")]
    public void WriteCallsRejectsMissingEndpointIdsWithoutAnUnresolvedToken(string format)
    {
        var call = CreateCall(AsyncUsageKind.None) with
        {
            CalleeSymbolId = null,
            CalleeDefinitionId = null,
            UnresolvedName = " ",
        };
        var endpoints = CreateEndpointSymbols()
            .Where(pair => pair.Key == call.CallerSymbolId)
            .ToDictionary();
        var result = new CallResult(
            CreateSelection(new QueryContext(CreateProfile(), [])),
            [call],
            [],
            [],
            endpoints);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CaptureText(() => CreateOutputFormatter(format, FullSymbolPathOptions).WriteCalls(result, "call(s)")));

        Assert.Equal(
            $"Call ID {call.Id} has no resolved callee endpoint or unresolved name.",
            exception.Message);
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

        using var document = CaptureJson(() => CreateOutputFormatter("json", ShortSymbolPathOptions).WriteSymbols(context));

        var outputSymbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal(
            "DistributedCacheLocker::RunWithHeartbeatAsync(System.String)",
            outputSymbol.GetProperty("displayName").GetString());
        Assert.Equal("symbol-1", outputSymbol.GetProperty("stableKey").GetString());
        Assert.Equal("Nop.Core.Caching", outputSymbol.GetProperty("namespaceName").GetString());
        Assert.Equal(displayName, outputSymbol.GetProperty("fullyQualifiedName").GetString());
        Assert.Equal("System.String", Assert.Single(outputSymbol.GetProperty("parameters").EnumerateArray()).GetString());
    }

    [Fact]
    public void WriteSymbolsTableKeepsReturnAndParameterTypesWhileShorteningOwnerNamespace()
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

        var output = CaptureText(() => CreateOutputFormatter("table", ShortSymbolPathOptions).WriteSymbols(context));

        Assert.Contains(
            "public static async System.Threading.Tasks.Task<System.Int32> " +
            "Gamer::Play(System.String,System.Threading.CancellationToken)",
            output);
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

        using var hiddenSource = CaptureJson(() => CreateOutputFormatter("json", ShortSymbolPathOptions)
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol])));
        var hiddenSymbol = Assert.Single(hiddenSource.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal("Gamer::Play(System.String)", hiddenSymbol.GetProperty("displayName").GetString());
        Assert.Equal(
            "public static async System.Threading.Tasks.Task<System.Int32> Gamer::Play(System.String)",
            hiddenSymbol.GetProperty("signature").GetString());
        Assert.Equal("System.Threading.Tasks.Task<System.Int32>", hiddenSymbol.GetProperty("returnType").GetString());
        Assert.Equal("System.String", Assert.Single(hiddenSymbol.GetProperty("parameters").EnumerateArray()).GetString());
        Assert.Equal("public", hiddenSymbol.GetProperty("accessibility").GetString());
        Assert.True(hiddenSymbol.GetProperty("isStatic").GetBoolean());
        Assert.True(hiddenSymbol.GetProperty("isAsync").GetBoolean());
        Assert.False(hiddenSymbol.TryGetProperty("normalizedSource", out _));

        using var shownSource = CaptureJson(() => CreateOutputFormatter("json", ShortSymbolPathOptions)
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
            displayName: "Tokyo.Gamer::[constructor](string)",
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
            displayName: "Tokyo.Gamer::Run().<lambda#1>",
            namespaceName: "Tokyo",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#1>",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.AnonymousFunction,
            returnTypeKey: "System.Int32",
            normalizedSource: "()=>42",
            accessibility: (int)IndexedAccessibility.NotApplicable);
        var hiddenContext = new QueryContext(CreateProfile(), [constructor, lambda]);

        var table = CaptureText(() => CreateOutputFormatter("table", FullSymbolPathOptions).WriteSymbols(hiddenContext));
        Assert.Equal(
            "public Tokyo.Gamer::[constructor](string)\t" + Environment.NewLine +
            "System.Int32 Tokyo.Gamer::Run().<lambda#1>\t" + Environment.NewLine,
            table);
        Assert.DoesNotContain("source:", table);

        var shownTable = CaptureText(() => CreateOutputFormatter(
            "table",
            FullSymbolPathOptions,
            SourceLayout.MultiLine,
            Console.Out,
            Console.Error).WriteSymbols(new QueryContext(CreateProfile(), [constructor, lambda], ShowSource: true)));
        Assert.Contains("source: public Gamer(string name){}", shownTable);
        Assert.Contains("source: ()=>42", shownTable);

        using var hiddenJson = CaptureJson(() => CreateOutputFormatter("json", FullSymbolPathOptions).WriteSymbols(hiddenContext));
        var hiddenConstructor = Assert.Single(hiddenJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("id").GetInt64() == constructor.Id);
        var hiddenLambda = Assert.Single(hiddenJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("id").GetInt64() == lambda.Id);
        Assert.Equal(
            "public Tokyo.Gamer::[constructor](string)",
            hiddenConstructor.GetProperty("signature").GetString());
        Assert.Equal(JsonValueKind.Null, hiddenConstructor.GetProperty("returnType").ValueKind);
        Assert.Equal("public", hiddenConstructor.GetProperty("accessibility").GetString());
        Assert.False(hiddenConstructor.TryGetProperty("normalizedSource", out _));
        Assert.Equal("System.Int32 Tokyo.Gamer::Run().<lambda#1>", hiddenLambda.GetProperty("signature").GetString());
        Assert.Equal("System.Int32", hiddenLambda.GetProperty("returnType").GetString());
        Assert.Equal(JsonValueKind.Null, hiddenLambda.GetProperty("accessibility").ValueKind);
        Assert.False(hiddenLambda.TryGetProperty("normalizedSource", out _));

        using var shownJson = CaptureJson(() => CreateOutputFormatter("json", FullSymbolPathOptions).WriteSymbols(
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
            displayName: "Test.A::Host().Local()",
            name: "Local",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.LocalFunction,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.NotApplicable);
        var getter = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 202,
            displayName: "Test.A::[get:Value]()",
            name: "get_Value",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.PropertyGet,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.Public);
        var addition = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 203,
            displayName: "Test.A::[operator:+](Test.A,Test.A)",
            name: "op_Addition",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.UserDefinedOperator,
            isStatic: true,
            returnTypeKey: "Test.A",
            accessibility: (int)IndexedAccessibility.Public);
        var conversion = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 204,
            displayName: "Test.A::[conversion:implicit:int](Test.A)",
            name: "op_Implicit",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.Conversion,
            isStatic: true,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.Public);
        var context = new QueryContext(CreateProfile(), [local, getter, addition, conversion]);

        var table = CaptureText(() => CreateOutputFormatter("table", FullSymbolPathOptions).WriteSymbols(context));
        Assert.Equal(
            "System.Int32 Test.A::Host().Local()\t" + Environment.NewLine +
            "public System.Int32 Test.A::[get:Value]()\t" + Environment.NewLine +
            "public static Test.A Test.A::[operator:+](Test.A,Test.A)\t" + Environment.NewLine +
            "public static System.Int32 Test.A::[conversion:implicit:int](Test.A)\t" + Environment.NewLine,
            table);

        using var json = CaptureJson(() => CreateOutputFormatter("json", FullSymbolPathOptions).WriteSymbols(context));
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
            var result = new AsyncPathResult(CreateSelection(CreateProfile(), root), root, nodes, Found: true, Truncated: false);
            var formatter = CreateGraphOutputFormatter(FullSymbolPathOptions, destination.Writer);

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
        var result = new AsyncPathResult(CreateSelection(CreateProfile(), root), root, [root], Found: true, Truncated: false);
        using var payload = new StringWriter();
        var formatter = CreateGraphOutputFormatter(FullSymbolPathOptions, payload);

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
        var result = new AsyncPathResult(CreateSelection(CreateProfile(), root), root, [root, middle, origin], Found: true, Truncated: true);

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

        var noPath = new AsyncPathResult(CreateSelection(CreateProfile(), root), root, [], Found: false, Truncated: false);
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
            CreateSelection(CreateProfile(), root),
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
            CreateSelection(CreateProfile(), root),
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
            [101L, 102L, 106L, 105L, 103L, 104L],
            json.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(node => node.GetProperty("symbol").GetProperty("id").GetInt64()));
        Assert.Equal(
            ["101->102", "102->101", "106->102", "106->103", "105->102", "103->101", "104->103"],
            json.RootElement.GetProperty("edges").EnumerateArray()
                .Select(edge => $"{edge.GetProperty("callerSymbolId").GetInt64()}->{edge.GetProperty("calleeSymbolId").GetInt64()}"));
    }

    [Fact]
    public void GraphOutputFormatterHonorsCancellationBeforeWriting()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CreateGraphOutputFormatter(FullSymbolPathOptions, TextWriter.Null).WriteAsyncPath(
            new AsyncPathResult(CreateSelection(CreateProfile(), root), root, [root], Found: true, Truncated: false),
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
            new Dictionary<long, int> { [1] = 0, [2] = 1, [3] = 2 },
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

        Assert.Throws<OperationCanceledException>(() => CaptureText(() => CreateOutputFormatter("json", FullSymbolPathOptions)
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol]), cancellation.Token)));
    }

    [Fact]
    public void WriteCallsTableShortensCallerAndCalleeNames()
    {
        var context = new QueryContext(CreateProfile(), []);
        var call = CreateCall(AsyncUsageKind.None);
        var endpointSymbols = new Dictionary<long, StoredSymbol>
        {
            [1] = CreateSymbol(
                AsyncRole.None,
                null,
                id: 1,
                displayName: "Example.Features.Caller::Run(System.String)"),
            [2] = CreateSymbol(
                AsyncRole.None,
                null,
                id: 2,
                displayName: "Example.Services.Callee::Execute(System.Threading.Tasks.Task)"),
        };
        var result = new CallResult(CreateSelection(context), [call], [], [], endpointSymbols);

        var output = CaptureText(() => CreateOutputFormatter("table", ShortSymbolPathOptions).WriteCalls(result, "call(s)"));

        Assert.Contains(
            "Caller::Run(System.String) -> Callee::Execute(System.Threading.Tasks.Task)",
            output);
    }

    [Fact]
    public void WriteSymbolsTableKeepsCanonicalNamesByDefault()
    {
        const string displayName = "Example.Features.Worker::Run(System.Threading.Tasks.Task)";
        var symbol = CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null, displayName: displayName);
        var context = new QueryContext(CreateProfile(), [symbol]);

        var output = CaptureText(() => CreateOutputFormatter("table", FullSymbolPathOptions).WriteSymbols(context));

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

        using var document = CaptureJson(() => CreateOutputFormatter("json", FullSymbolPathOptions).WriteSymbols(context));

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

        using var document = CaptureJson(() => CreateOutputFormatter("json", ShortSymbolPathOptions).WriteSymbolList(context));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        var outputSymbol = Assert.Single(document.RootElement.GetProperty("symbols").EnumerateArray());
        Assert.Equal(
            "Worker::Run(System.Threading.Tasks.Task)",
            outputSymbol.GetProperty("displayName").GetString());
        Assert.False(document.RootElement.TryGetProperty("matched", out _));
    }

    [Fact]
    public void WriteCallsJsonIncludesAsyncUsageKind()
    {
        var context = new QueryContext(CreateProfile(), []);
        var result = new CallResult(
            CreateSelection(context),
            [CreateCall(AsyncUsageKind.Awaited)],
            [],
            [],
            CreateEndpointSymbols());

        using var document = CaptureJson(() => CreateOutputFormatter("json", FullSymbolPathOptions).WriteCalls(result, "call(s)"));

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

        var output = CaptureText(() => CreateOutputFormatter("table", FullSymbolPathOptions).WriteSymbols(context));

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
        var result = new CallResult(
            CreateSelection(context),
            [CreateCall(AsyncUsageKind.Awaited)],
            [],
            [],
            CreateEndpointSymbols());

        var output = CaptureText(() => CreateOutputFormatter("table", FullSymbolPathOptions).WriteCalls(result, "call(s)"));

        Assert.Contains("[Awaited]", output);
    }

    private OutputFormatter CreateOutputFormatter(
        string format,
        SymbolPathFormatOptions symbolPathOptions) =>
        new(format, symbolPathOptions, _pathResolver, PathDisplayStyle.Relative);

    private OutputFormatter CreateOutputFormatter(
        string format,
        SymbolPathFormatOptions symbolPathOptions,
        SourceLayout sourceLayout,
        TextWriter writer,
        TextWriter diagnosticsWriter) =>
        new(
            format,
            symbolPathOptions,
            _pathResolver,
            PathDisplayStyle.Relative,
            sourceLayout,
            writer,
            diagnosticsWriter);

    private GraphOutputFormatter CreateGraphOutputFormatter(
        SymbolPathFormatOptions symbolPathOptions,
        TextWriter writer) =>
        new(symbolPathOptions, _pathResolver, PathDisplayStyle.Relative, writer);

    private static JsonDocument CaptureJson(Action write) => JsonDocument.Parse(CaptureText(write));

    private JsonDocument CaptureGraphJson(Action<GraphOutputFormatter> write) =>
        JsonDocument.Parse(CaptureGraphText(write));

    private string CaptureGraphText(Action<GraphOutputFormatter> write)
    {
        using var payload = new StringWriter();
        var consoleOutput = CaptureText(() => write(CreateGraphOutputFormatter(FullSymbolPathOptions, payload)));

        Assert.Equal(string.Empty, consoleOutput);
        return payload.ToString();
    }

    private JsonDocument CaptureInjectedJson(Action<OutputFormatter> write)
    {
        using var payload = new StringWriter();
        using var diagnostics = new StringWriter();
        var formatter = CreateOutputFormatter(
            "json",
            FullSymbolPathOptions,
            SourceLayout.SingleLine,
            payload,
            diagnostics);

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

    private static RootSelection CreateSelection(QueryContext context) =>
        new(
            context.Profile,
            context.MatchedSymbols.Select(symbol => new ResolvedLogicalRoot(
                symbol,
                symbol.PreferredDeclaration is { } declaration ? [declaration] : [])).ToArray());

    private static RootSelection CreateSelection(StoredProfile profile, StoredSymbol root) =>
        new(
            profile,
            [new ResolvedLogicalRoot(
                root,
                root.PreferredDeclaration is { } declaration ? [declaration] : [])]);

    private static DefinitionResult CreateDefinitionResult(
        QueryContext context,
        IReadOnlyList<StoredSymbol> definitions) =>
        new(
            CreateSelection(context),
            definitions
                .Select(symbol => new DeclarationResultRow(symbol, CreateDeclaration(symbol)))
                .ToArray());

    private static StoredDeclaration CreateDeclaration(StoredSymbol symbol) =>
        symbol.PreferredDeclaration ?? new StoredDeclaration(
            Id: symbol.Id,
            DeclarationKey: $"declaration-{symbol.Id}",
            SymbolId: symbol.Id,
            DocumentId: symbol.Id,
            DocumentPath: symbol.DocumentPath ?? "source.cs",
            Role: DeclarationRole.Ordinary,
            SourceStart: symbol.SourceStart ?? 0,
            SourceLength: symbol.SourceLength ?? symbol.NormalizedSource?.Length ?? 0,
            NormalizedStart: 0,
            NormalizedLength: symbol.NormalizedSource?.Length ?? 0,
            NormalizedSource: symbol.NormalizedSource,
            IsGenerated: symbol.IsGenerated);

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
        int? sourceStart = null)
    {
        var path = CreatePath(displayName, namespaceName);
        var preferredDeclaration = normalizedSource is not null || documentPath is not null
            ? new StoredDeclaration(
                Id: id,
                DeclarationKey: $"declaration-{id}",
                SymbolId: id,
                DocumentId: id,
                DocumentPath: documentPath ?? "source.cs",
                Role: DeclarationRole.Ordinary,
                SourceStart: sourceStart ?? 0,
                SourceLength: normalizedSource?.Length ?? 0,
                NormalizedStart: 0,
                NormalizedLength: normalizedSource?.Length ?? 0,
                NormalizedSource: normalizedSource,
                IsGenerated: false)
            : null;
        return new StoredSymbol(
            Id: id,
            StableKey: $"symbol-{id}",
            Kind: kind,
            Name: name,
            NamespaceName: namespaceName,
            TypeSimpleName: path.TypeDisplayPath,
            TypeMetadataName: path.TypeIdentityPath,
            ContainingSymbolId: null,
            Arity: 0,
            ParameterCount: parameters?.Count ?? 0,
            MethodKind: methodKind,
            IsStatic: isStatic,
            IsAbstract: false,
            IsVirtual: false,
            IsOverride: false,
            AsyncRole: asyncRole,
            AsyncInvolvementDepth: asyncInvolvementDepth,
            AsyncNextSymbolId: null,
            ReturnTypeKey: returnTypeKey,
            DocumentPath: documentPath,
            SourceStart: sourceStart,
            SourceLength: preferredDeclaration?.SourceLength,
            IsGenerated: false,
            AssemblyName: null,
            Parameters: parameters ?? [],
            TypeKind: null,
            Accessibility: accessibility) with
        {
            Path = path,
            PreferredDeclarationId = preferredDeclaration?.Id,
            PreferredDocumentPath = preferredDeclaration?.DocumentPath,
            PreferredSourceStart = preferredDeclaration?.SourceStart,
            PreferredIsGenerated = preferredDeclaration?.IsGenerated,
            PreferredDeclaration = preferredDeclaration,
        };
    }

    private static SymbolPathData CreatePath(string displayName, string namespaceName)
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
                displayName,
                displayName,
                CallablePathSegmentKind.Named);
        }

        var owner = displayName[..separator];
        var executable = displayName[(separator + 2)..];
        var lastDot = owner.LastIndexOf('.');
        var typePath = lastDot < 0 ? owner : owner[(lastDot + 1)..];
        var namespacePath = lastDot < 0 ? namespaceName : owner[..lastDot];
        return new SymbolPathData(
            namespacePath,
            typePath,
            typePath,
            executable,
            executable,
            executable,
            executable,
            CallablePathSegmentKind.Named);
    }

    private static IReadOnlyDictionary<long, StoredSymbol> CreateEndpointSymbols() =>
        new Dictionary<long, StoredSymbol>
        {
            [1] = CreateSymbol(AsyncRole.None, null, id: 1, displayName: "Example.Source()"),
            [2] = CreateSymbol(AsyncRole.None, null, id: 2, displayName: "Example.Target()"),
        };

    private static StoredCall CreateCall(AsyncUsageKind asyncUsageKind) => new(
        Id: 1,
        CallerSymbolId: 1,
        CallerContainingSymbolId: null,
        CalleeSymbolId: 2,
        CalleeDefinitionId: 2,
        ReferenceKind: ReferenceKind.Invocation,
        DispatchKind: DispatchKind.Static,
        ResolutionStatus: ResolutionStatus.Resolved,
        ResolutionReason: ResolutionReason.None,
        AsyncUsageKind: asyncUsageKind,
        DocumentId: 1,
        DocumentPath: "missing.cs",
        SourceStart: 0,
        SourceLength: 1,
        NormalizedStart: 0,
        NormalizedLength: 1,
        NormalizedSource: null,
        IsGenerated: false,
        UnresolvedName: null,
        ReceiverTypeKey: null);

    private static string FormatPath(StoredSymbol symbol) =>
        new SymbolPathFormatter().Format(
            Assert.IsType<SymbolPathData>(symbol.Path),
            new SymbolPathFormatOptions());
}
