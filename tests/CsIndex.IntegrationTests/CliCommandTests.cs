using System.Text.Json;
using CsIndex.Cli;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class CliCommandTests : IDisposable
{
    private readonly SemanticIndexFixture _fixture = new();

    [Fact]
    public void SymbolFindArgumentsSupportRepeatableSourceConditionsAndSearchFlags()
    {
        var parsed = CliArguments.Parse(
        [
            "--regex", "--ignore-case", "--show-source",
            "--include", "first", "--include=second", "--exclude", "third",
            "--namespace", "Tokyo.*", "--type", "*Gamer", "--method", "P*l*y", "--kind", "lambda",
        ]);

        Assert.True(parsed.HasFlag("regex"));
        Assert.True(parsed.HasFlag("ignore-case"));
        Assert.True(parsed.HasFlag("show-source"));
        Assert.Equal(["first", "second"], parsed.GetMany("include"));
        Assert.Equal(["third"], parsed.GetMany("exclude"));
        Assert.Equal("Tokyo.*", parsed.GetSingle("namespace"));
        Assert.Equal("*Gamer", parsed.GetSingle("type"));
        Assert.Equal("P*l*y", parsed.GetSingle("method"));
        Assert.Equal("lambda", parsed.GetSingle("kind"));
        Assert.Empty(parsed.Positionals);
    }

    [Fact]
    public async Task SourceSearchRequiresAtLeastOneIncludeOrExcludeCondition()
    {
        var result = await RunAsync("source", "search");

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("source search requires at least one include or exclude condition", result.StandardError);
    }

    [Fact]
    public async Task GraphCommandsValidateValuesRootsAndUnsupportedOptions()
    {
        await _fixture.BuildTask;

        var invalidDepth = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--depth", "-1", "--db", _fixture.DatabasePath);
        var invalidMaxNodes = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Start()", "--max-nodes", "0", "--db", _fixture.DatabasePath);
        var invalidAsyncOutput = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Start()", "--output", "mermaid", "--db", _fixture.DatabasePath);
        var invalidCallerOutput = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--output", "line", "--db", _fixture.DatabasePath);
        var ambiguousRoot = await RunAsync(
            "async", "tree", "Alpha.AClass::Play", "--db", _fixture.DatabasePath);
        var missingRoot = await RunAsync(
            "callers", "tree", "Alpha.Missing::Run()", "--db", _fixture.DatabasePath);
        var unsupportedOption = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Start()", "--include", "PrintVar(", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, invalidDepth.ExitCode);
        Assert.Contains("Depth cannot be negative", invalidDepth.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, invalidMaxNodes.ExitCode);
        Assert.Contains("Maximum node count must be positive", invalidMaxNodes.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, invalidAsyncOutput.ExitCode);
        Assert.Contains("Unknown async tree output: mermaid", invalidAsyncOutput.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, invalidCallerOutput.ExitCode);
        Assert.Contains("Unknown callers tree output: line", invalidCallerOutput.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, ambiguousRoot.ExitCode);
        Assert.Equal(
            "Query error: Graph query is ambiguous for 'Alpha.AClass::Play'. Candidates: " +
            "Alpha.AClass::Play(), Alpha.AClass::Play(System.String)" + Environment.NewLine,
            ambiguousRoot.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, missingRoot.ExitCode);
        Assert.Equal(
            "Query error: No source-backed method matches graph query: Alpha.Missing::Run()" + Environment.NewLine,
            missingRoot.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, unsupportedOption.ExitCode);
        Assert.Contains("Unknown option(s): --include", unsupportedOption.StandardError);
    }

    [Fact]
    public async Task SymbolFindSupportsLambdaPatternComponentRegexAndSourceFiltering()
    {
        await _fixture.BuildTask;

        var lambda = await RunAsync(
            "symbol", "find", "::<lambda#1>", "--kind", "lambda", "--output", "json", "--db", _fixture.DatabasePath);
        var components = await RunAsync(
            "symbol", "find", "--namespace", "Tokyo", "--type", "Gamer", "--method", "Play", "--output", "json",
            "--db", _fixture.DatabasePath);
        var regex = await RunAsync(
            "symbol", "find", "--regex", "^(Tokyo|Fukuoka)\\.Gamer::P[lr]ay$", "--output", "json", "--db", _fixture.DatabasePath);
        var sourceFiltered = await RunAsync(
            "symbol", "find", "Tokyo.*::Play", "--include", "PrintVar(", "--exclude", "BlockedMarker(", "--show-source",
            "--output", "json", "--db", _fixture.DatabasePath);
        var ignoredCase = await RunAsync(
            "symbol", "find", "TOKYO.GAMER::PLAY", "--ignore-case", "--output", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, lambda.ExitCode);
        using var lambdaDocument = JsonDocument.Parse(lambda.StandardOutput);
        Assert.Contains(lambdaDocument.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("displayName").GetString()!.Contains("::<lambda#1>", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Success, components.ExitCode);
        using var componentsDocument = JsonDocument.Parse(components.StandardOutput);
        Assert.Equal(
            ["Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(System.String)"],
            componentsDocument.RootElement.GetProperty("matched").EnumerateArray()
                .Select(symbol => symbol.GetProperty("displayName").GetString()));

        Assert.Equal(ExitCodes.Success, regex.ExitCode);
        using var regexDocument = JsonDocument.Parse(regex.StandardOutput);
        Assert.Equal(
            ["Fukuoka.Gamer::Pray()", "Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(System.String)"],
            regexDocument.RootElement.GetProperty("matched").EnumerateArray()
                .Select(symbol => symbol.GetProperty("displayName").GetString()));

        Assert.Equal(ExitCodes.Success, sourceFiltered.ExitCode);
        using var sourceDocument = JsonDocument.Parse(sourceFiltered.StandardOutput);
        Assert.NotEmpty(sourceDocument.RootElement.GetProperty("matched").EnumerateArray());
        Assert.All(sourceDocument.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            Assert.True(symbol.TryGetProperty("normalizedSource", out _)));

        Assert.Equal(ExitCodes.Success, ignoredCase.ExitCode);
        using var ignoredCaseDocument = JsonDocument.Parse(ignoredCase.StandardOutput);
        Assert.Equal(2, ignoredCaseDocument.RootElement.GetProperty("matched").GetArrayLength());
    }

    [Fact]
    public async Task SymbolFindAndSourceCommandsRejectInvalidSearchInput()
    {
        await _fixture.BuildTask;

        var invalidRegex = await RunAsync("symbol", "find", "[", "--regex", "--db", _fixture.DatabasePath);
        var noNameCondition = await RunAsync("symbol", "find", "--include", "PrintVar(", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, invalidRegex.ExitCode);
        Assert.Contains("Invalid pattern", invalidRegex.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, noNameCondition.ExitCode);
        Assert.Contains("symbol find requires a pattern", noNameCondition.StandardError);
    }

    [Fact]
    public async Task SourceShowAndSearchRenderNormalizedSourceInTableAndJson()
    {
        await _fixture.BuildTask;

        var shown = await RunAsync("source", "show", "Tokyo.Gamer::Play", "--db", _fixture.DatabasePath);
        var shownJson = await RunAsync(
            "source", "show", "Tokyo.Gamer::Play", "--output", "json", "--db", _fixture.DatabasePath);
        var searched = await RunAsync(
            "source", "search", "--include", "PrintVar(", "--exclude", "BlockedMarker(", "--output", "json", "--db",
            _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, shown.ExitCode);
        Assert.Contains("Tokyo.Gamer::Play()", shown.StandardOutput);
        Assert.Contains("source:", shown.StandardOutput);

        Assert.Equal(ExitCodes.Success, shownJson.ExitCode);
        using var shownDocument = JsonDocument.Parse(shownJson.StandardOutput);
        Assert.All(shownDocument.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            Assert.True(symbol.TryGetProperty("normalizedSource", out _)));

        Assert.Equal(ExitCodes.Success, searched.ExitCode);
        using var document = JsonDocument.Parse(searched.StandardOutput);
        var matches = document.RootElement.GetProperty("matched").EnumerateArray().ToArray();
        Assert.Contains(matches, symbol => symbol.GetProperty("displayName").GetString() == "Tokyo.SourceBodies::Match()");
        Assert.DoesNotContain(matches, symbol => symbol.GetProperty("displayName").GetString() == "Tokyo.SourceBodies::Excluded()");
        Assert.All(matches, symbol => Assert.True(symbol.TryGetProperty("normalizedSource", out _)));
    }

    [Fact]
    public async Task SourceShowRendersNormalizedLambdaSourceInTableAndJson()
    {
        await _fixture.BuildTask;

        const string lambdaQuery = "Tokyo.LambdaSearch::Function()::<lambda#1>";
        var table = await RunAsync("source", "show", lambdaQuery, "--db", _fixture.DatabasePath);
        var json = await RunAsync(
            "source", "show", lambdaQuery, "--output", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, table.ExitCode);
        Assert.Contains(lambdaQuery, table.StandardOutput);
        Assert.Contains("source: ()=>LambdaMarker(\"first\")", table.StandardOutput);

        Assert.Equal(ExitCodes.Success, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var lambda = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal(lambdaQuery, lambda.GetProperty("displayName").GetString());
        Assert.Equal("()=>LambdaMarker(\"first\")", lambda.GetProperty("normalizedSource").GetString());
    }

    [Fact]
    public async Task AsyncTreeRendersSelfUnreachableAndTruncatedResultsInEachOutputMode()
    {
        await _fixture.BuildTask;

        var self = await RunAsync("async", "tree", "Alpha.AsyncGraph::SelfAsync()", "--db", _fixture.DatabasePath);
        var unreachable = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Unreachable()", "--output", "line", "--db", _fixture.DatabasePath);
        var truncatedTree = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Start()", "--max-nodes", "1", "--db", _fixture.DatabasePath);
        var truncatedJson = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Start()", "--max-nodes", "1", "--output", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, self.ExitCode);
        Assert.Equal("async Alpha.AsyncGraph::SelfAsync()" + Environment.NewLine, self.StandardOutput);
        Assert.Equal(ExitCodes.Success, unreachable.ExitCode);
        Assert.Equal(
            "No reachable asynchronous function: Alpha.AsyncGraph::Unreachable()" + Environment.NewLine,
            unreachable.StandardOutput);
        Assert.Equal(ExitCodes.Success, truncatedTree.ExitCode);
        Assert.Contains("<truncated>", truncatedTree.StandardOutput);
        Assert.Equal(ExitCodes.Success, truncatedJson.ExitCode);
        using var document = JsonDocument.Parse(truncatedJson.StandardOutput);
        Assert.True(document.RootElement.GetProperty("found").GetBoolean());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Single(document.RootElement.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public async Task CallerTreeRendersTextMermaidJsonDepthCyclesLambdasAndShortNames()
    {
        await _fixture.BuildTask;
        var root = await _fixture.GetStoredSymbolAsync(
            "Alpha.CallerGraph::RecursiveTarget()",
            cancellationToken: TestContext.Current.CancellationToken);
        var right = await _fixture.GetStoredSymbolAsync(
            "Alpha.CallerGraph::RecursiveRight()",
            cancellationToken: TestContext.Current.CancellationToken);

        var table = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--short-names", "--db", _fixture.DatabasePath);
        var mermaid = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::RecursiveTarget()", "--depth", "0", "--output", "mermaid", "--db",
            _fixture.DatabasePath);
        var lambda = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::LambdaTarget()", "--output", "json", "--db", _fixture.DatabasePath);
        var bounded = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::DepthTarget()", "--depth", "2", "--output", "json", "--db",
            _fixture.DatabasePath);
        var metadata = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::MetadataTarget()", "--depth", "0", "--output", "json", "--db",
            _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, table.ExitCode);
        Assert.Contains("CallerGraph::DirectTarget()", table.StandardOutput);
        Assert.DoesNotContain("Alpha.CallerGraph::DirectTarget()", table.StandardOutput);

        Assert.Equal(ExitCodes.Success, mermaid.ExitCode);
        Assert.StartsWith("flowchart TD" + Environment.NewLine, mermaid.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"n{right.Id} --> n{root.Id}", mermaid.StandardOutput);

        Assert.Equal(ExitCodes.Success, lambda.ExitCode);
        using var document = JsonDocument.Parse(lambda.StandardOutput);
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Contains(nodes, node => node.GetProperty("symbol").GetProperty("displayName").GetString()!
            .Contains("::<lambda#1>", StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, node => node.GetProperty("symbol").GetProperty("displayName").GetString() ==
            "Alpha.CallerGraph::LambdaOwner()");

        Assert.Equal(ExitCodes.Success, bounded.ExitCode);
        using var boundedDocument = JsonDocument.Parse(bounded.StandardOutput);
        var boundedNames = boundedDocument.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("symbol").GetProperty("displayName").GetString())
            .ToArray();
        Assert.Contains("Alpha.CallerGraph::DepthTwo()", boundedNames);
        Assert.DoesNotContain("Alpha.CallerGraph::DepthThree()", boundedNames);

        Assert.Equal(ExitCodes.Success, metadata.ExitCode);
        using var metadataDocument = JsonDocument.Parse(metadata.StandardOutput);
        Assert.All(metadataDocument.RootElement.GetProperty("nodes").EnumerateArray(), node =>
            Assert.False(node.GetProperty("symbol").GetProperty("namespaceName").GetString()!
                .StartsWith("System", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GlobalHelpMatchesTheNewCommandUsageGrammar()
    {
        var result = await RunAsync("--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var help = result.StandardOutput.ReplaceLineEndings("\n");
        var usageStart = help.IndexOf("Usage:\n", StringComparison.Ordinal) + "Usage:\n".Length;
        var usageEnd = help.IndexOf("\n\nCommon query options:", usageStart, StringComparison.Ordinal);

        Assert.Equal(
            """
              csindex index <input> [options]
              csindex symbol find [<pattern>] [options]
              csindex symbol list [options]
              csindex async tree <symbol> [options]
              csindex callers tree <symbol> [options]
              csindex source show <symbol> [options]
              csindex source search (--include <text> | --exclude <text>)... [options]
              csindex definition <query> [options]
              csindex definition --at <path:line:column> [options]
              csindex references <query> [options]
              csindex callers <query> [options]
              csindex callees <query> [options]
              csindex overrides <query> [options]
              csindex conditions [options]
            """.ReplaceLineEndings("\n"),
            help[usageStart..usageEnd]);
    }

    [Theory]
    [MemberData(nameof(NewCommandHelpGrammarCases))]
    public async Task NewCommandHelpMatchesAcceptedGrammar(string[] args, string expectedHelp)
    {
        var result = await RunAsync(args);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(FormatExpectedCommandHelp(expectedHelp) + "\n", result.StandardOutput.ReplaceLineEndings("\n"));
    }

    public static TheoryData<string[], string> NewCommandHelpGrammarCases { get; } = new()
    {
        {
            ["symbol", "find", "--help"],
            """
            Usage: csindex symbol find [<pattern>] [options]

              Provide <pattern> or at least one of --namespace, --type, or --method.

              --db <path>  SQLite index path (default: .csindex/index.sqlite)
              --profile <name>  Analysis profile (default: most recently indexed profile)
              --output table|json  Output format (default: table)
              --require-single  Fail unless the search matches exactly one symbol
              --short-names  Shorten namespaces in displayed symbol names
              --namespace <pattern>  Namespace component filter
              --type <pattern>  Type component filter
              --method <pattern>  Method component filter
              --kind method|lambda  Limit results to executable kind
              --regex  Interpret name filters as regular expressions
              --include <text>  Require normalized source text (repeatable)
              --exclude <text>  Reject normalized source text (repeatable)
              --ignore-case  Compare name and source filters without case sensitivity
              --show-source  Include normalized source in output
              --include-overrides  Include descendant overrides and interface implementations (exact method pattern only)
              --help  Show this help text
            """
        },
        {
            ["async", "tree", "--help"],
            """
            Usage: csindex async tree <symbol> [options]

              --db <path>  SQLite index path (default: .csindex/index.sqlite)
              --profile <name>  Analysis profile (default: most recently indexed profile)
              --output tree|line|json  Output format (default: tree)
              --max-nodes <count>  Maximum path nodes (default: 500)
              --short-names  Shorten namespaces in displayed symbol names
              --help  Show this help text
            """
        },
        {
            ["callers", "tree", "--help"],
            """
            Usage: csindex callers tree <symbol> [options]

              --db <path>  SQLite index path (default: .csindex/index.sqlite)
              --profile <name>  Analysis profile (default: most recently indexed profile)
              --output tree|mermaid|json  Output format (default: tree)
              --depth <count>  Maximum caller depth; 0 is unlimited (default: 3)
              --max-nodes <count>  Maximum graph nodes (default: 500)
              --short-names  Shorten namespaces in displayed symbol names
              --help  Show this help text
            """
        },
        {
            ["source", "show", "--help"],
            """
            Usage: csindex source show <symbol> [options]

              --db <path>  SQLite index path (default: .csindex/index.sqlite)
              --profile <name>  Analysis profile (default: most recently indexed profile)
              --output table|json  Output format (default: table)
              --short-names  Shorten namespaces in displayed symbol names
              --help  Show this help text
            """
        },
        {
            ["source", "search", "--help"],
            """
            Usage: csindex source search (--include <text> | --exclude <text>)... [options]

              --db <path>  SQLite index path (default: .csindex/index.sqlite)
              --profile <name>  Analysis profile (default: most recently indexed profile)
              --output table|json  Output format (default: table)
              --include <text>  Require normalized source text (repeatable)
              --exclude <text>  Reject normalized source text (repeatable)
              --ignore-case  Compare source filters without case sensitivity
              --short-names  Shorten namespaces in displayed symbol names
              --help  Show this help text
            """
        },
    };

    [Fact]
    public async Task SymbolListDefaultsToMethodsAndLambdasWithLocationsAndAsyncAnnotations()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "list", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("symbol(s):", result.StandardOutput);
        Assert.Contains("Alpha.AClass::Play()", result.StandardOutput);
        Assert.Contains("<lambda#1>", result.StandardOutput);
        Assert.Contains($"{_fixture.MainSourcePath}:", result.StandardOutput);
        Assert.Contains("[async:", result.StandardOutput);
    }

    [Fact]
    public async Task SymbolListKindMethodExcludesLambdas()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "list", "--kind", "method", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Alpha.AClass::Play()", result.StandardOutput);
        Assert.DoesNotContain("<lambda#", result.StandardOutput);
    }

    [Fact]
    public async Task SymbolListKindLambdaIncludesOnlyLambdas()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "list", "--kind", "lambda", "--output", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
        Assert.NotEmpty(symbols);
        Assert.All(symbols, symbol => Assert.Equal("lambda", symbol.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task SymbolListAsyncInvolvedExcludesSynchronousSymbols()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "list", "--async-involved", "--output", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
        Assert.Contains(symbols, symbol => symbol.GetProperty("displayName").GetString()!.Contains("ExecuteAsync"));
        Assert.DoesNotContain(symbols, symbol => symbol.GetProperty("displayName").GetString()!.Contains("AsyncPlayer::Sync"));
        Assert.All(symbols, symbol => Assert.True(symbol.GetProperty("isAsyncInvolved").GetBoolean()));
    }

    [Fact]
    public async Task SymbolListJsonUsesProfileAndSymbolsProperties()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "list", "--output", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("profile").GetString()));
        Assert.True(document.RootElement.TryGetProperty("symbols", out var symbols));
        Assert.Equal(JsonValueKind.Array, symbols.ValueKind);
        Assert.False(document.RootElement.TryGetProperty("matched", out _));
    }

    [Fact]
    public async Task CalleesExcludeLambdaCallsLimitsOutputToDirectCalls()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "callees", "Alpha.DescendantCallees::Execute()", "--exclude-lambda-calls", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("1 callee call(s)", result.StandardOutput);
        Assert.Contains("DirectCall", result.StandardOutput);
        Assert.DoesNotContain("OuterLambdaCall", result.StandardOutput);
    }

    [Fact]
    public async Task SymbolFindShortNamesFormatsPresentationName()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", "--short-names", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("AClass::Play()", result.StandardOutput);
        Assert.DoesNotContain("Alpha.AClass::Play()", result.StandardOutput);
    }

    [Fact]
    public async Task SymbolListShortNamesFormatsPresentationNames()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "list", "--short-names", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("AClass::Play()", result.StandardOutput);
        Assert.DoesNotContain("Alpha.AClass::Play()", result.StandardOutput);
    }

    [Theory]
    [InlineData("definition", "Alpha.AClass::Play()", "AClass::Play()")]
    [InlineData("references", "Alpha.AClass::Play()", "AClass::Execute() -> AClass::Play()")]
    [InlineData("callers", "Alpha.AClass::Play()", "AClass::Execute() -> AClass::Play()")]
    [InlineData("callees", "Alpha.DescendantCallees::Execute()", "DescendantCallees::Execute() -> DescendantCallees::DirectCall()")]
    [InlineData("overrides", "Alpha.BaseClass::Run()", "XClass::Run() -> BaseClass::Run()")]
    public async Task QueryCommandsShortNamesFormatHumanFacingNames(string command, string query, string expected)
    {
        await _fixture.BuildTask;

        var result = await RunAsync(command, query, "--short-names", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(expected, result.StandardOutput);
        Assert.DoesNotContain("Alpha.", result.StandardOutput);
    }

    [Fact]
    public async Task CalleesIncludeLambdaCallsByDefault()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("callees", "Alpha.DescendantCallees::Execute()", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("5 callee call(s)", result.StandardOutput);
        Assert.Contains("OuterLambdaCall", result.StandardOutput);
        Assert.Contains("FirstNestedLambdaCall", result.StandardOutput);
        Assert.Contains("SecondNestedLambdaCall", result.StandardOutput);
    }

    [Theory]
    [InlineData("symbol", "find", "matched")]
    [InlineData("definition", null, "definitions")]
    [InlineData("references", null, "calls")]
    [InlineData("callers", null, "calls")]
    [InlineData("callees", null, "calls")]
    public async Task IncludeOverridesExpandsPianistAcrossSupportedCommands(
        string command,
        string? subcommand,
        string expandedCollection)
    {
        await _fixture.BuildTask;
        string[] commandPrefix = subcommand is null ? [command] : [command, subcommand];

        var table = await RunAsync(commandPrefix.Concat(
        [
            "Alpha.Pianist::Play()", "--include-overrides", "--output", "table", "--db", _fixture.DatabasePath,
        ]).ToArray());
        Assert.Equal(ExitCodes.Success, table.ExitCode);
        Assert.Contains("Alpha.ProPianist::Play()", table.StandardOutput);

        var json = await RunAsync(commandPrefix.Concat(
        [
            "Alpha.Pianist::Play()", "--include-overrides", "--output", "json", "--db", _fixture.DatabasePath,
        ]).ToArray());
        Assert.Equal(ExitCodes.Success, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var root = document.RootElement;
        Assert.Contains(root.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("displayName").GetString() == "Alpha.ProPianist::Play()");

        if (expandedCollection == "definitions")
        {
            Assert.Contains(root.GetProperty(expandedCollection).EnumerateArray(), symbol =>
                symbol.GetProperty("displayName").GetString() == "Alpha.ProPianist::Play()");
        }
        else if (expandedCollection == "calls")
        {
            Assert.Contains(root.GetProperty(expandedCollection).EnumerateArray(), call =>
                (command == "callees" ? call.GetProperty("caller") : call.GetProperty("callee"))
                .GetString()!.Contains("Alpha.ProPianist::Play()"));
        }
    }

    [Theory]
    [InlineData("Tokyo.*::Play")]
    [InlineData("::<lambda#1>")]
    public async Task SymbolFindIncludeOverridesRejectsExtendedPositionalPatterns(string pattern)
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "symbol", "find", pattern, "--include-overrides", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("--include-overrides cannot be combined", result.StandardError);
    }

    [Fact]
    public async Task SymbolFindIncludeOverridesComposesWithShowSource()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "symbol", "find", "Alpha.Pianist::Play()", "--include-overrides", "--show-source", "--output", "json",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var overriddenPlay = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("displayName").GetString() == "Alpha.ProPianist::Play()");
        Assert.Equal("public override void Play()=>ProPianistBody();", overriddenPlay.GetProperty("normalizedSource").GetString());
    }

    [Theory]
    [InlineData("symbol", "find", "Alpha.IPlayable")]
    [InlineData("definition", "--at", "Source.cs:1:1")]
    public async Task IncludeOverridesRejectsNonMethodQueries(params string[] args)
    {
        await _fixture.BuildTask;

        var result = await RunAsync(args.Concat(["--include-overrides", "--db", _fixture.DatabasePath]).ToArray());

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("--include-overrides requires a method query", result.StandardError);
    }

    [Fact]
    public async Task IncludeOverridesIsRejectedByUnsupportedCommands()
    {
        await _fixture.BuildTask;
        var commands = new[]
        {
            new[] { "symbol", "list", "--include-overrides", "--db", _fixture.DatabasePath },
            new[] { "overrides", "Alpha.Pianist::Play()", "--include-overrides", "--db", _fixture.DatabasePath },
            new[] { "conditions", "--include-overrides", "--db", _fixture.DatabasePath },
            new[] { "index", _fixture.RootPath, "--include-overrides", "--db", _fixture.DatabasePath },
        };

        foreach (var args in commands)
        {
            var result = await RunAsync(args);
            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Contains("Unknown option(s): --include-overrides", result.StandardError);
        }
    }

    [Fact]
    public async Task IncludeOverridesComposesWithDispatchAndShortNames()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "callers", "Alpha.Pianist::Play()", "--include-overrides", "--dispatch", "all", "--short-names",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("ProPianist::Play()", result.StandardOutput);
        Assert.DoesNotContain("Alpha.ProPianist::Play()", result.StandardOutput);
    }

    [Fact]
    public async Task IncludeOverridesComposesWithExcludeLambdaCalls()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "callees", "Alpha.DescendantCallees::Execute()", "--include-overrides", "--exclude-lambda-calls",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("1 callee call(s)", result.StandardOutput);
        Assert.Contains("DirectCall", result.StandardOutput);
        Assert.DoesNotContain("OuterLambdaCall", result.StandardOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalFunctionCommandsPreferExactTargetOverInheritedSameNameMethod(bool includeOverrides)
    {
        await _fixture.BuildTask;
        var commands = new (string Command, string? Subcommand)[]
        {
            ("symbol", "find"),
            ("definition", null),
            ("references", null),
            ("callers", null),
            ("callees", null),
        };

        foreach (var (command, subcommand) in commands)
        {
            var args = subcommand is null
                ? new List<string> { command }
                : [command, subcommand];
            args.Add("Alpha.LocalPlayer::Local()");
            if (includeOverrides)
            {
                args.Add("--include-overrides");
            }

            args.AddRange(["--db", _fixture.DatabasePath]);
            var result = await RunAsync(args.ToArray());

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Contains("Alpha.LocalPlayer::Local()", result.StandardOutput);
            Assert.DoesNotContain("Alpha.LocalBase::Local()", result.StandardOutput);
            Assert.DoesNotContain("InheritedLocalBody", result.StandardOutput);
        }
    }

    [Theory]
    [InlineData("symbol", "find", "--help")]
    [InlineData("definition", "--help")]
    [InlineData("references", "--help")]
    [InlineData("callers", "--help")]
    [InlineData("callees", "--help")]
    public async Task IncludeOverridesAppearsInGlobalAndSupportedCommandHelp(params string[] args)
    {
        var result = await RunAsync(args);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(
            "--include-overrides         Include descendant overrides and interface implementations",
            result.StandardOutput);
    }

    [Fact]
    public async Task GlobalHelpMarksIncludeOverridesAsMethodQueryOnly()
    {
        var result = await RunAsync("--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(
            "--include-overrides         Include descendant overrides and interface implementations (method queries only)",
            result.StandardOutput);
    }

    [Theory]
    [InlineData("symbol", "list", "--kind", "type")]
    [InlineData("symbol", "list", "unexpected")]
    [InlineData("callers", "Alpha.AClass::Play()", "--exclude-lambda-calls")]
    public async Task UnsupportedListValuesAndOptionsReturnInvalidArguments(params string[] command)
    {
        await _fixture.BuildTask;

        var args = command.Concat(["--db", _fixture.DatabasePath]).ToArray();
        var result = await RunAsync(args);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("Argument error:", result.StandardError);
    }

    public void Dispose() => _fixture.Dispose();

    private static string FormatExpectedCommandHelp(string expectedHelp) =>
        string.Join("\n", expectedHelp.ReplaceLineEndings("\n").Split('\n').Select(line =>
        {
            if (!line.StartsWith("  --", StringComparison.Ordinal))
            {
                return line;
            }

            var optionEnd = line.IndexOf("  ", 2, StringComparison.Ordinal);
            return optionEnd < 0
                ? line
                : $"  {line[2..optionEnd].PadRight(28)}{line[(optionEnd + 2)..]}";
        }));

    private static async Task<CommandResult> RunAsync(params string[] args)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.Main(args);
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
