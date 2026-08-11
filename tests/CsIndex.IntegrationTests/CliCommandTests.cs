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
    public void OutputFileAliasesNormalizeExactlyAndRejectMissingEmptyOrDuplicateValues()
    {
        Assert.Equal("result.json", CliArguments.Parse(["-o", "result.json"]).GetSingle("output-file"));
        Assert.Equal("result.json", CliArguments.Parse(["--output-file=result.json"]).GetSingle("output-file"));
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["-o"]));
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["-o", ""]));
        Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["-o", "a", "--output-file", "b"]).GetSingle("output-file"));

        var compactForms = CliArguments.Parse(["-opath", "-o=result.json"]);
        Assert.Null(compactForms.GetSingle("output-file"));
        Assert.Equal(["-opath", "-o=result.json"], compactForms.Positionals);
    }

    [Fact]
    public async Task FunctionFilterValueMatrixIsAcceptedByEveryApplicableCommand()
    {
        await _fixture.BuildTask;
        var definitionAt = _fixture.GetLocation("a.Play()");
        string[][] syncMethodCommands =
        [
            ["symbol", "find", "Alpha.AsyncPlayer::Sync()"],
            ["symbol", "list"],
            ["source", "show", "Alpha.AsyncPlayer::Sync()"],
            ["source", "search", "--include", "Sync()"],
            ["definition", "Alpha.AsyncPlayer::Sync()"],
            ["definition", "--at", definitionAt],
            ["references", "Alpha.LambdaPlayer::Play()"],
            ["callers", "Alpha.LambdaPlayer::Play()"],
            ["callees", "Alpha.DescendantCallees::Execute()"],
            ["async", "tree", "Alpha.AsyncGraph::Start()"],
            ["callers", "tree", "Alpha.CallerGraph::DirectTarget()"],
            ["overrides", "Alpha.AsyncOverrideBase::Run()"],
        ];
        string[][] lambdaCommands =
        [
            ["symbol", "find", "Alpha.LambdaPlayer::Execute()::<lambda#1>"],
            ["symbol", "list"],
            ["source", "show", "Alpha.LambdaPlayer::Execute()::<lambda#1>"],
            ["source", "search", "--include", "LambdaTarget()"],
            ["definition", "Alpha.LambdaPlayer::Execute()::<lambda#1>"],
            ["definition", "--at", definitionAt],
            ["references", "Alpha.LambdaPlayer::Execute()::<lambda#1>"],
            ["callers", "Alpha.LambdaPlayer::Execute()::<lambda#1>"],
            ["callees", "Alpha.LambdaPlayer::Execute()::<lambda#1>"],
            ["async", "tree", "Alpha.AsyncGraph::ALambdaPathOwner()::<lambda#1>"],
            ["callers", "tree", "Alpha.AsyncGraph::ALambdaPathOwner()::<lambda#1>"],
        ];
        string[][] asyncCommands =
        [
            ["symbol", "find", "Alpha.AsyncPlayer::ExecuteAsync()"],
            ["symbol", "list"],
            ["source", "show", "Alpha.AsyncPlayer::ExecuteAsync()"],
            ["source", "search", "--include", "Task.Yield()"],
            ["definition", "Alpha.AsyncPlayer::ExecuteAsync()"],
            ["definition", "--at", definitionAt],
            ["references", "Alpha.AsyncPlayer::ExecuteAsync()"],
            ["callers", "Alpha.AsyncPlayer::ExecuteAsync()"],
            ["callees", "Alpha.AsyncPlayer::ExecuteAsync()"],
            ["async", "tree", "Alpha.AsyncGraph::SelfAsync()"],
            ["callers", "tree", "Alpha.AsyncGraph::EndAsync()"],
            ["overrides", "Alpha.AsyncOverrideDerived::Run()"],
        ];

        foreach (var command in syncMethodCommands)
        {
            await AssertCommandSucceedsAsync(command, "--kind", "all");
            await AssertCommandSucceedsAsync(command, "--kind", "method");
            await AssertCommandSucceedsAsync(command, "--async-status", "all");
            await AssertCommandSucceedsAsync(command, "--async-status", "sync");
        }

        foreach (var command in lambdaCommands)
        {
            await AssertCommandSucceedsAsync(command, "--kind", "lambda");
        }

        foreach (var command in asyncCommands)
        {
            await AssertCommandSucceedsAsync(command, "--async-status", "async");
        }
    }

    [Fact]
    public async Task ExplicitAllFiltersMatchOmissionAndDirectStatusAppliesBeforeRequireSingle()
    {
        await _fixture.BuildTask;

        var omitted = await RunAsync(
            "symbol", "find", "Alpha.AsyncPlayer", "--output-format", "json", "--db", _fixture.DatabasePath);
        var explicitAll = await RunAsync(
            "symbol", "find", "Alpha.AsyncPlayer", "--kind", "all", "--async-status", "all",
            "--output-format", "json", "--db", _fixture.DatabasePath);
        var unfiltered = await RunAsync(
            "symbol", "find", "Alpha.AsyncOverride*::Run()", "--require-single", "--async-status", "all",
            "--db", _fixture.DatabasePath);
        var syncOnly = await RunAsync(
            "symbol", "find", "Alpha.AsyncOverride*::Run()", "--require-single", "--async-status", "sync",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, omitted.ExitCode);
        Assert.Equal(omitted.StandardOutput, explicitAll.StandardOutput);
        Assert.Equal(ExitCodes.RequireSingleFailure, unfiltered.ExitCode);
        Assert.Equal(ExitCodes.Success, syncOnly.ExitCode);
        Assert.Contains("Alpha.AsyncOverrideBase::Run()", syncOnly.StandardOutput);
        Assert.DoesNotContain("Alpha.AsyncOverrideDerived::Run()", syncOnly.StandardOutput);
    }

    [Theory]
    [InlineData("symbol-find")]
    [InlineData("symbol-list")]
    [InlineData("source-show")]
    [InlineData("source-search")]
    [InlineData("definition-query")]
    [InlineData("definition-at")]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("overrides")]
    public async Task FunctionFiltersConstrainEveryWiredJsonCommand(string commandPath)
    {
        await _fixture.BuildTask;
        var (args, emptyProperties) = commandPath switch
        {
            "symbol-find" =>
                (new[] { "symbol", "find", "Alpha.AsyncPlayer::Sync()", "--async-status", "async" },
                    new[] { "matched" }),
            "symbol-list" =>
                (new[] { "symbol", "list", "--kind", "lambda" }, Array.Empty<string>()),
            "source-show" =>
                (new[] { "source", "show", "Alpha.AsyncPlayer::Sync()", "--async-status", "async" },
                    new[] { "matched" }),
            "source-search" =>
                (new[] { "source", "search", "--include", "public void Sync()", "--async-status", "async" },
                    new[] { "matched" }),
            "definition-query" =>
                (new[] { "definition", "Alpha.AsyncPlayer::Sync()", "--async-status", "async" },
                    new[] { "matched", "definitions" }),
            "definition-at" =>
                (new[] { "definition", "--at", _fixture.GetLocation("a.Play()"), "--async-status", "async" },
                    new[] { "matched", "definitions" }),
            "references" =>
                (new[] { "references", "Alpha.LambdaPlayer::Play()", "--async-status", "async" },
                    new[] { "matched", "calls" }),
            "callers" =>
                (new[] { "callers", "Alpha.LambdaPlayer::Play()", "--async-status", "async" },
                    new[] { "matched", "calls" }),
            "callees" =>
                (new[] { "callees", "Alpha.DescendantCallees::Execute()", "--async-status", "async" },
                    new[] { "matched", "calls" }),
            "overrides" =>
                (new[] { "overrides", "Alpha.AsyncOverrideBase::Run()", "--async-status", "async" },
                    new[] { "matched", "relations" }),
            _ => throw new ArgumentOutOfRangeException(nameof(commandPath)),
        };

        var result = await RunAsync(
            [.. args, "--output-format", "json", "--db", _fixture.DatabasePath]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (commandPath == "symbol-list")
        {
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
            Assert.NotEmpty(symbols);
            Assert.All(symbols, symbol => Assert.Equal("lambda", symbol.GetProperty("kind").GetString()));
            return;
        }

        foreach (var property in emptyProperties)
        {
            Assert.Empty(document.RootElement.GetProperty(property).EnumerateArray());
        }
    }

    [Theory]
    [InlineData("async", "tree", "Alpha.AsyncGraph::Start()")]
    [InlineData("callers", "tree", "Alpha.CallerGraph::DirectTarget()")]
    public async Task FunctionFiltersConstrainEveryWiredGraphRoot(
        string command,
        string subcommand,
        string query)
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            command,
            subcommand,
            query,
            "--async-status",
            "async",
            "--db",
            _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Equal(
            $"Query error: No source-backed executable matches graph query: {query}{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public async Task InvalidFunctionFilterValuesReportEveryAllowedValue()
    {
        var invalidKind = await RunAsync("symbol", "list", "--kind", "type");
        var invalidAsyncStatus = await RunAsync("symbol", "list", "--async-status", "involved");

        Assert.Equal(ExitCodes.InvalidArguments, invalidKind.ExitCode);
        Assert.Contains(
            "Unknown symbol kind: type. Use all, method, or lambda.",
            invalidKind.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, invalidAsyncStatus.ExitCode);
        Assert.Contains(
            "Unknown async status: involved. Use all, async, or sync.",
            invalidAsyncStatus.StandardError);
    }

    [Fact]
    public async Task IncludeOverridesAcceptsAllAndMethodKindsButRejectsLambdaKind()
    {
        await _fixture.BuildTask;
        foreach (var kind in new string?[] { null, "all", "method" })
        {
            var args = new List<string>
            {
                "symbol", "find", "Alpha.Pianist::Play()", "--include-overrides", "--db", _fixture.DatabasePath,
            };
            if (kind is not null)
            {
                args.AddRange(["--kind", kind]);
            }

            var result = await RunAsync(args.ToArray());
            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Contains("Alpha.ProPianist::Play()", result.StandardOutput);
        }

        string[][] includeOverrideCommands =
        [
            ["symbol", "find", "Alpha.Pianist::Play()"],
            ["definition", "Alpha.Pianist::Play()"],
            ["references", "Alpha.Pianist::Play()"],
            ["callers", "Alpha.Pianist::Play()"],
            ["callees", "Alpha.Pianist::Play()"],
        ];
        foreach (var command in includeOverrideCommands)
        {
            var result = await RunAsync(
                [.. command, "--kind", "lambda", "--include-overrides", "--db", _fixture.DatabasePath]);
            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Contains("--kind lambda", result.StandardError);
            Assert.Contains("--include-overrides", result.StandardError);
        }

        var overrides = await RunAsync(
            "overrides", "Alpha.AsyncOverrideBase::Run()", "--kind", "lambda", "--db", _fixture.DatabasePath);
        Assert.Equal(ExitCodes.InvalidArguments, overrides.ExitCode);
        Assert.Contains("--kind lambda is not applicable to overrides.", overrides.StandardError);
    }

    [Theory]
    [InlineData("definition", "definitions")]
    [InlineData("references", "calls")]
    [InlineData("callers", "calls")]
    [InlineData("callees", "calls")]
    public async Task IncludeOverridesAcceptsExplicitAllAndMethodAcrossSupportedQueryCommands(
        string command,
        string expandedCollection)
    {
        await _fixture.BuildTask;
        foreach (var kind in new[] { "all", "method" })
        {
            var result = await RunAsync(
                command,
                "Alpha.Pianist::Play()",
                "--include-overrides",
                "--kind",
                kind,
                "--output-format",
                "json",
                "--db",
                _fixture.DatabasePath);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            Assert.Contains(root.GetProperty("matched").EnumerateArray(), symbol =>
                symbol.GetProperty("displayName").GetString() == "Alpha.ProPianist::Play()");
            if (expandedCollection == "definitions")
            {
                Assert.Contains(root.GetProperty(expandedCollection).EnumerateArray(), symbol =>
                    symbol.GetProperty("displayName").GetString() == "Alpha.ProPianist::Play()");
            }
            else
            {
                Assert.Contains(root.GetProperty(expandedCollection).EnumerateArray(), call =>
                    (command == "callees" ? call.GetProperty("caller") : call.GetProperty("callee"))
                    .GetString()!.Contains("Alpha.ProPianist::Play()", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task IndexAndConditionsRejectFunctionFilters()
    {
        string[][] commands = [["index", "."], ["conditions"]];
        foreach (var command in commands)
        {
            var kind = await RunAsync([.. command, "--kind", "all"]);
            var asyncStatus = await RunAsync([.. command, "--async-status", "all"]);

            Assert.Equal(ExitCodes.InvalidArguments, kind.ExitCode);
            Assert.Contains("Unknown option(s): --kind", kind.StandardError);
            Assert.Equal(ExitCodes.InvalidArguments, asyncStatus.ExitCode);
            Assert.Contains("Unknown option(s): --async-status", asyncStatus.StandardError);
        }
    }

    [Fact]
    public async Task LegacyOutputOptionIsUnknownForEveryFormerOutputCommand()
    {
        string[][] commands =
        [
            ["symbol", "find", "Alpha.AClass::Play()"],
            ["symbol", "list"],
            ["async", "tree", "Alpha.AsyncGraph::Start()"],
            ["callers", "tree", "Alpha.CallerGraph::DirectTarget()"],
            ["source", "show", "Alpha.AClass::Play()"],
            ["source", "search", "--include", "Play"],
            ["definition", "Alpha.AClass::Play()"],
            ["references", "Alpha.AClass::Play()"],
            ["callers", "Alpha.AClass::Play()"],
            ["callees", "Alpha.AClass::Play()"],
            ["overrides", "Alpha.BaseClass::Run()"],
            ["conditions"],
        ];

        foreach (var command in commands)
        {
            var result = await RunAsync([.. command, "--output", "json"]);

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Contains("Unknown option(s): --output", result.StandardError);
        }
    }

    [Fact]
    public async Task SourceSearchRequiresAtLeastOneIncludeOrExcludeCondition()
    {
        var result = await RunAsync("source", "search");

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("source search requires at least one include or exclude condition", result.StandardError);
    }

    [Fact]
    public async Task UnknownSourceLayoutListsEveryAllowedValue()
    {
        var result = await RunAsync(
            "source", "show", "Alpha.AClass::Play()", "--source-layout", "compact");

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains(
            "Argument error: Unknown source layout: compact. Use single-line or multi-line.",
            result.StandardError);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task SourceLayoutRejectsJsonAndSymbolFindWithoutShowSource()
    {
        string[][] jsonCommands =
        [
            ["symbol", "find", "Alpha.AClass::Play()", "--show-source"],
            ["source", "show", "Alpha.AClass::Play()"],
            ["source", "search", "--include", "Play"],
        ];
        foreach (var command in jsonCommands)
        {
            var result = await RunAsync(
                [.. command, "--source-layout", "single-line", "--output-format", "json"]);

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Contains(
                "--source-layout cannot be combined with --output-format json.",
                result.StandardError);
            Assert.Equal(string.Empty, result.StandardOutput);
        }

        var missingShowSource = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", "--source-layout", "multi-line");

        Assert.Equal(ExitCodes.InvalidArguments, missingShowSource.ExitCode);
        Assert.Contains(
            "--source-layout requires --show-source for symbol find.",
            missingShowSource.StandardError);
        Assert.Equal(string.Empty, missingShowSource.StandardOutput);
    }

    [Fact]
    public async Task NonSourceCommandsRejectSourceLayoutAsUnknownOption()
    {
        string[][] commands =
        [
            ["index", "."],
            ["symbol", "list"],
            ["async", "tree", "Alpha.AsyncGraph::Start()"],
            ["callers", "tree", "Alpha.CallerGraph::DirectTarget()"],
            ["definition", "Alpha.AClass::Play()"],
            ["references", "Alpha.AClass::Play()"],
            ["callers", "Alpha.AClass::Play()"],
            ["callees", "Alpha.AClass::Play()"],
            ["overrides", "Alpha.BaseClass::Run()"],
            ["conditions"],
        ];

        foreach (var command in commands)
        {
            var result = await RunAsync([.. command, "--source-layout", "single-line"]);

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Contains("Unknown option(s): --source-layout", result.StandardError);
            Assert.Equal(string.Empty, result.StandardOutput);
        }
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
            "async", "tree", "Alpha.AsyncGraph::Start()", "--output-format", "mermaid", "--db", _fixture.DatabasePath);
        var invalidCallerOutput = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--output-format", "line", "--db", _fixture.DatabasePath);
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
            "Query error: No source-backed executable matches graph query: Alpha.Missing::Run()" + Environment.NewLine,
            missingRoot.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, unsupportedOption.ExitCode);
        Assert.Contains("Unknown option(s): --include", unsupportedOption.StandardError);
    }

    [Fact]
    public async Task SymbolFindSupportsLambdaPatternComponentRegexAndSourceFiltering()
    {
        await _fixture.BuildTask;

        var lambda = await RunAsync(
            "symbol", "find", "::<lambda#1>", "--kind", "lambda", "--output-format", "json", "--db", _fixture.DatabasePath);
        var components = await RunAsync(
            "symbol", "find", "--namespace", "Tokyo", "--type", "Gamer", "--method", "Play", "--output-format", "json",
            "--db", _fixture.DatabasePath);
        var regex = await RunAsync(
            "symbol", "find", "--regex", "^(Tokyo|Fukuoka)\\.Gamer::P[lr]ay$", "--output-format", "json", "--db", _fixture.DatabasePath);
        var sourceFiltered = await RunAsync(
            "symbol", "find", "Tokyo.*::Play", "--include", "PrintVar(", "--exclude", "BlockedMarker(", "--show-source",
            "--output-format", "json", "--db", _fixture.DatabasePath);
        var ignoredCase = await RunAsync(
            "symbol", "find", "TOKYO.GAMER::PLAY", "--ignore-case", "--output-format", "json", "--db", _fixture.DatabasePath);

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

        var shown = await RunAsync(
            "source", "show", "Tokyo.Gamer::Play", "--source-layout", "multi-line", "--db", _fixture.DatabasePath);
        var shownJson = await RunAsync(
            "source", "show", "Tokyo.Gamer::Play", "--output-format", "json", "--db", _fixture.DatabasePath);
        var searched = await RunAsync(
            "source", "search", "--include", "PrintVar(", "--exclude", "BlockedMarker(", "--output-format", "json", "--db",
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
        var table = await RunAsync(
            "source", "show", lambdaQuery, "--source-layout", "multi-line", "--db", _fixture.DatabasePath);
        var json = await RunAsync(
            "source", "show", lambdaQuery, "--output-format", "json", "--db", _fixture.DatabasePath);

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
    public async Task SingleLineSymbolAndSourceCommandsEmitOnlyFixedSchemaRecordsAndDiagnosticSummaries()
    {
        await _fixture.BuildTask;

        var find = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", "--db", _fixture.DatabasePath);
        var findWithSource = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", "--show-source", "--source-layout", "single-line",
            "--db", _fixture.DatabasePath);
        var sourceShow = await RunAsync(
            "source", "show", "Tokyo.Gamer::Play()", "--source-layout", "single-line", "--db", _fixture.DatabasePath);
        var sourceSearch = await RunAsync(
            "source", "search", "--include", "PrintVar(", "--db", _fixture.DatabasePath);
        var symbolList = await RunAsync("symbol", "list", "--db", _fixture.DatabasePath);
        var zeroResults = await RunAsync(
            "source", "search", "--include", "__source_layout_missing__", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, find.ExitCode);
        var findLines = GetPhysicalLines(find.StandardOutput);
        Assert.Single(findLines);
        Assert.All(findLines, line =>
        {
            Assert.Matches(@"^[^\t]+\t[^\t]*$", line);
            Assert.Equal(1, line.Count(character => character == '\t'));
        });
        Assert.Contains("Query matched 1 symbol(s):", find.StandardError);

        Assert.Equal(ExitCodes.Success, findWithSource.ExitCode);
        var findWithSourceLines = GetPhysicalLines(findWithSource.StandardOutput);
        Assert.Single(findWithSourceLines);
        Assert.All(findWithSourceLines, line =>
        {
            Assert.Matches(@"^[^\t]+\t[^\t]*\t[^\t]*$", line);
            Assert.Equal(2, line.Count(character => character == '\t'));
        });
        Assert.Contains("Query matched 1 symbol(s):", findWithSource.StandardError);

        Assert.Equal(ExitCodes.Success, sourceShow.ExitCode);
        var sourceShowLines = GetPhysicalLines(sourceShow.StandardOutput);
        Assert.Single(sourceShowLines);
        Assert.All(sourceShowLines, line =>
        {
            Assert.Matches(@"^[^\t]+\t[^\t]*\t[^\t]*$", line);
            Assert.Equal(2, line.Count(character => character == '\t'));
        });
        Assert.Contains("Query matched 1 symbol(s):", sourceShow.StandardError);

        Assert.Equal(ExitCodes.Success, sourceSearch.ExitCode);
        Assert.NotEmpty(GetPhysicalLines(sourceSearch.StandardOutput));
        Assert.All(GetPhysicalLines(sourceSearch.StandardOutput), line =>
        {
            Assert.Matches(@"^[^\t]+\t[^\t]*\t[^\t]*$", line);
            Assert.Equal(2, line.Count(character => character == '\t'));
        });
        Assert.Contains("Query matched", sourceSearch.StandardError);

        Assert.Equal(ExitCodes.Success, symbolList.ExitCode);
        Assert.NotEmpty(GetPhysicalLines(symbolList.StandardOutput));
        Assert.All(GetPhysicalLines(symbolList.StandardOutput), line => Assert.DoesNotContain('\t', line));
        Assert.Contains("symbol(s):", symbolList.StandardError);

        Assert.Equal(ExitCodes.Success, zeroResults.ExitCode);
        Assert.Equal(string.Empty, zeroResults.StandardOutput);
        Assert.Contains("Query matched 0 symbol(s):", zeroResults.StandardError);
    }

    [Fact]
    public async Task MultiLineSourceLayoutRetainsHeadingAndOnePhysicalSourceLine()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "source", "show", "Tokyo.Gamer::Play()", "--source-layout", "multi-line", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var lines = GetPhysicalLines(result.StandardOutput);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Query matched 1 symbol(s):", lines[0]);
        Assert.StartsWith("  ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("    source: ", lines[2], StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task AsyncTreeRendersSelfUnreachableAndTruncatedResultsInEachOutputMode()
    {
        await _fixture.BuildTask;

        var self = await RunAsync("async", "tree", "Alpha.AsyncGraph::SelfAsync()", "--db", _fixture.DatabasePath);
        var unreachable = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Unreachable()", "--output-format", "line", "--db", _fixture.DatabasePath);
        var truncatedTree = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Start()", "--max-nodes", "1", "--db", _fixture.DatabasePath);
        var truncatedJson = await RunAsync(
            "async", "tree", "Alpha.AsyncGraph::Start()", "--max-nodes", "1", "--output-format", "json", "--db", _fixture.DatabasePath);

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
            "callers", "tree", "Alpha.CallerGraph::RecursiveTarget()", "--depth", "0", "--output-format", "mermaid", "--db",
            _fixture.DatabasePath);
        var lambda = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::LambdaTarget()", "--output-format", "json", "--db", _fixture.DatabasePath);
        var bounded = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::DepthTarget()", "--depth", "2", "--output-format", "json", "--db",
            _fixture.DatabasePath);
        var metadata = await RunAsync(
            "callers", "tree", "Alpha.CallerGraph::MetadataTarget()", "--depth", "0", "--output-format", "json", "--db",
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
        Assert.Contains("--output-format table|json", help);
        Assert.Contains("--kind all|method|lambda", help);
        Assert.Contains("--async-status all|async|sync", help);
        Assert.Contains("--source-layout single-line|multi-line", help);
        Assert.DoesNotContain("--output table|json", help);
    }

    [Theory]
    [MemberData(nameof(FunctionFilterHelpCases))]
    public async Task CommandHelpMatchesFunctionFilterAndOutputFormatMatrix(
        string[] args,
        string expectedOutputFormat)
    {
        var result = await RunAsync(args);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("--kind all|method|lambda", result.StandardOutput);
        Assert.Contains("--async-status all|async|sync", result.StandardOutput);
        Assert.Contains($"--output-format {expectedOutputFormat}", result.StandardOutput);
        Assert.DoesNotContain("--output ", result.StandardOutput);
    }

    public static TheoryData<string[], string> FunctionFilterHelpCases { get; } = new()
    {
        { ["symbol", "find", "--help"], "table|json" },
        { ["symbol", "list", "--help"], "table|json" },
        { ["async", "tree", "--help"], "tree|line|json" },
        { ["callers", "tree", "--help"], "tree|mermaid|json" },
        { ["source", "show", "--help"], "table|json" },
        { ["source", "search", "--help"], "table|json" },
        { ["definition", "--help"], "table|json" },
        { ["references", "--help"], "table|json" },
        { ["callers", "--help"], "table|json" },
        { ["callees", "--help"], "table|json" },
        { ["overrides", "--help"], "table|json" },
    };

    [Fact]
    public async Task IndexAndConditionsHelpExcludeFunctionFilters()
    {
        var index = await RunAsync("index", "--help");
        var conditions = await RunAsync("conditions", "--help");

        Assert.Equal(ExitCodes.Success, index.ExitCode);
        Assert.DoesNotContain("--kind", index.StandardOutput);
        Assert.DoesNotContain("--async-status", index.StandardOutput);
        Assert.DoesNotContain("--output-format", index.StandardOutput);
        Assert.Equal(ExitCodes.Success, conditions.ExitCode);
        Assert.DoesNotContain("--kind", conditions.StandardOutput);
        Assert.DoesNotContain("--async-status", conditions.StandardOutput);
        Assert.Contains("--output-format table|json", conditions.StandardOutput);
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
              --output-format table|json  Output format (default: table)
              --require-single  Fail unless the search matches exactly one symbol
              --short-names  Shorten namespaces in displayed symbol names
              --namespace <pattern>  Namespace component filter
              --type <pattern>  Type component filter
              --method <pattern>  Method component filter
              --kind all|method|lambda  Limit function targets by kind (default: all)
              --async-status all|async|sync  Limit function targets by direct async status (default: all)
              --regex  Interpret name filters as regular expressions
              --include <text>  Require normalized source text (repeatable)
              --exclude <text>  Reject normalized source text (repeatable)
              --ignore-case  Compare name and source filters without case sensitivity
              --show-source  Include normalized source in output
              --source-layout single-line|multi-line  Source table layout (default: single-line)
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
              --kind all|method|lambda  Limit function targets by kind (default: all)
              --async-status all|async|sync  Limit function targets by direct async status (default: all)
              --output-format tree|line|json  Output format (default: tree)
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
              --kind all|method|lambda  Limit function targets by kind (default: all)
              --async-status all|async|sync  Limit function targets by direct async status (default: all)
              --output-format tree|mermaid|json  Output format (default: tree)
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
              --kind all|method|lambda  Limit function targets by kind (default: all)
              --async-status all|async|sync  Limit function targets by direct async status (default: all)
              --output-format table|json  Output format (default: table)
              --source-layout single-line|multi-line  Source table layout (default: single-line)
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
              --kind all|method|lambda  Limit function targets by kind (default: all)
              --async-status all|async|sync  Limit function targets by direct async status (default: all)
              --output-format table|json  Output format (default: table)
              --include <text>  Require normalized source text (repeatable)
              --exclude <text>  Reject normalized source text (repeatable)
              --ignore-case  Compare source filters without case sensitivity
              --source-layout single-line|multi-line  Source table layout (default: single-line)
              --short-names  Shorten namespaces in displayed symbol names
              --help  Show this help text
            """
        },
    };

    [Fact]
    public async Task SymbolListDefaultsToMethodsAndLambdasAsSignatureOnlyRecords()
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "list", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain("symbol(s):", result.StandardOutput);
        Assert.Contains("Alpha.AClass::Play()", result.StandardOutput);
        Assert.Contains("<lambda#1>", result.StandardOutput);
        Assert.DoesNotContain($"{_fixture.MainSourcePath}:", result.StandardOutput);
        Assert.Contains("[async:", result.StandardOutput);
        Assert.All(GetPhysicalLines(result.StandardOutput), line => Assert.DoesNotContain('\t', line));
        Assert.Contains("symbol(s):", result.StandardError);
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

        var result = await RunAsync("symbol", "list", "--kind", "lambda", "--output-format", "json", "--db", _fixture.DatabasePath);

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

        var result = await RunAsync("symbol", "list", "--async-involved", "--output-format", "json", "--db", _fixture.DatabasePath);

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

        var result = await RunAsync("symbol", "list", "--output-format", "json", "--db", _fixture.DatabasePath);

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
            "Alpha.Pianist::Play()", "--include-overrides", "--output-format", "table", "--db", _fixture.DatabasePath,
        ]).ToArray());
        Assert.Equal(ExitCodes.Success, table.ExitCode);
        Assert.Contains("Alpha.ProPianist::Play()", table.StandardOutput);

        var json = await RunAsync(commandPrefix.Concat(
        [
            "Alpha.Pianist::Play()", "--include-overrides", "--output-format", "json", "--db", _fixture.DatabasePath,
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
            "symbol", "find", "Alpha.Pianist::Play()", "--include-overrides", "--show-source", "--output-format", "json",
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
            if (optionEnd < 0)
            {
                return line;
            }

            var syntax = line[2..optionEnd];
            var padding = new string(' ', Math.Max(1, 28 - syntax.Length));
            return $"  {syntax}{padding}{line[(optionEnd + 2)..]}";
        }));

    private static string[] GetPhysicalLines(string output) => output
        .ReplaceLineEndings("\n")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private async Task AssertCommandSucceedsAsync(string[] command, params string[] options)
    {
        var args = command.Concat(options).Concat(["--db", _fixture.DatabasePath]).ToArray();
        var result = await RunAsync(args);

        Assert.True(
            result.ExitCode == ExitCodes.Success,
            $"Command failed: {string.Join(' ', args)}{Environment.NewLine}{result.StandardError}");
    }

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
