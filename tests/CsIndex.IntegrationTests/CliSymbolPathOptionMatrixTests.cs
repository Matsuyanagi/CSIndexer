using CsIndex.Cli;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class CliSymbolPathOptionMatrixTests : IDisposable
{
    private static readonly string[] NamespaceConditions =
    [
        "namespace", "namespace-literal", "namespace-regex", "namespace-case",
    ];

    private static readonly string[] TypeConditions =
    [
        "type", "type-literal", "type-regex", "type-case",
    ];

    private static readonly string[] MethodConditions =
    [
        "method", "method-literal", "method-regex", "method-case",
    ];

    private static readonly string[] FileConditions =
    [
        "file", "file-literal", "file-regex", "file-case",
    ];

    private static readonly string[] SourceConditions =
    [
        "include", "include-literal", "include-regex",
        "exclude", "exclude-literal", "exclude-regex", "source-case",
    ];

    private static readonly string[] RootConditions =
    [.. NamespaceConditions, .. TypeConditions, .. MethodConditions, .. FileConditions];

    private static readonly string[] AllConditions =
    [.. RootConditions, .. SourceConditions];

    private static readonly string[] QueryOptionFamilies =
    [
        .. AllConditions,
        "kind", "async-status",
        "symbol-path-style", "short-names", "base-dir", "path-style",
    ];

    private static readonly CommandScope[] CommandScopes =
    [
        new(
            "global",
            [],
            ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose"]),
        new(
            "symbol find",
            ["symbol", "find"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "show-source", "source-layout",
            ]),
        new(
            "symbol list",
            ["symbol", "list"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "async-involved",
            ]),
        new(
            "source search",
            ["source", "search"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "source-layout",
            ]),
        new(
            "source show",
            ["source", "show"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "source-layout",
            ]),
        new(
            "definition query",
            ["definition"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "at", "require-single", "include-overrides",
            ]),
        new(
            "references",
            ["references"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "exclude-generated", "only-generated",
            ]),
        new(
            "callers",
            ["callers"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "exclude-generated", "only-generated",
                "dispatch", "caller-scope", "show-source",
            ]),
        new(
            "callees",
            ["callees"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "require-single", "include-overrides", "exclude-generated", "only-generated",
                "exclude-lambda-calls",
            ]),
        new(
            "overrides",
            ["overrides"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. AllConditions, "kind", "async-status", "symbol-path-style", "short-names", "base-dir", "path-style",
                "require-single",
            ]),
        new(
            "async tree",
            ["async", "tree"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "max-nodes",
            ]),
        new(
            "callers tree",
            ["callers", "tree"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
                .. QueryOptionFamilies, "depth", "max-nodes", "show-source",
            ]),
        new(
            "definition --at",
            ["definition", "--at", "Source.cs:1:1"],
            [
                "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose", "at",
                "symbol-path-style", "short-names", "base-dir", "path-style",
            ]),
        new(
            "conditions",
            ["conditions"],
            ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose", "base-dir", "path-style"]),
        new(
            "index",
            ["index"],
            [
                "db", "help", "mode", "solution", "configuration", "framework", "target-framework", "runtime",
                "profile-name", "define", "undefine", "define-file", "reference", "unity-editor", "exclude",
                "generated-source", "rebuild", "verbose", "diagnostics", "help-verbose",
            ]),
    ];

    private static readonly string[] ScopeProbeOptions = CommandScopes
        .SelectMany(command => command.Allowed)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(option => option, StringComparer.Ordinal)
        .ToArray();

    private static readonly IReadOnlySet<string> FlagOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "help", "help-verbose", "verbose", "rebuild", "diagnostics", "exclude-generated", "only-generated",
        "require-single", "async-involved", "short-names", "exclude-lambda-calls", "include-overrides", "show-source",
    };

    private readonly SemanticIndexFixture _fixture = new();

    [Fact]
    public async Task EveryDocumentedOptionHasTheExactCommandScope()
    {
        foreach (var command in CommandScopes)
        {
            foreach (var option in ScopeProbeOptions)
            {
                var result = await RunAsync([.. command.Prefix, "--help", .. OptionTokens(command, option)]);
                var expected = command.Allowed.Contains(option, StringComparer.Ordinal);

                Assert.True(
                    expected
                        ? result.ExitCode == ExitCodes.Success
                        : result.ExitCode == ExitCodes.InvalidArguments,
                    $"{command.Name} {(expected ? "should allow" : "should reject")} --{option}. " +
                    $"Exit={result.ExitCode}; stderr={result.StandardError}");
            }
        }
    }

    [Fact]
    public async Task NormalHelpPublishesTheExactAcceptedOptionMatrix()
    {
        foreach (var command in CommandScopes)
        {
            var result = await RunAsync([.. command.Prefix, "--help"]);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Equal(
                command.Allowed.Distinct(StringComparer.Ordinal).OrderBy(option => option, StringComparer.Ordinal),
                ReadAcceptedOptions(result.StandardOutput));
        }
    }

    [Fact]
    public async Task NormalHelpDescriptionsAgreeWithSelectionMinimumAndFinalCommandScope()
    {
        var symbolFind = await RunAsync("symbol", "find", "--help");
        var definitionAt = await RunAsync("definition", "--at", "Source.cs:1:1", "--help");

        Assert.Equal(ExitCodes.Success, symbolFind.ExitCode);
        Assert.Contains(
            "Provide <pattern> or at least one typed condition, --kind, or --async-status.",
            symbolFind.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, definitionAt.ExitCode);
        Assert.Contains("--at <path:line:column>", definitionAt.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--kind ", definitionAt.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--async-status ", definitionAt.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--require-single ", definitionAt.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--include-overrides ", definitionAt.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryValueOptionAcceptsTheEqualsSpellingInTerminalHelp()
    {
        foreach (var command in CommandScopes)
        {
            foreach (var option in ScopeProbeOptions.Where(IsValueOption))
            {
                var expected = command.Allowed.Contains(option, StringComparer.Ordinal);
                var result = await RunAsync([.. command.Prefix, "--help", $"--{option}={ValueFor(option)}"]);

                Assert.True(
                    expected
                        ? result.ExitCode == ExitCodes.Success
                        : result.ExitCode == ExitCodes.InvalidArguments,
                    $"{command.Name} {(expected ? "should allow" : "should reject")} --{option}=... . " +
                    $"Exit={result.ExitCode}; stderr={result.StandardError}");
            }
        }
    }

    [Fact]
    public async Task RemovedGlobalMatcherOptionsAreRejectedByEveryCommand()
    {
        foreach (var command in CommandScopes)
        {
            foreach (var option in new[] { "regex", "ignore-case" })
            {
                foreach (var token in new[] { $"--{option}=value", $"--{option}", })
                {
                    var arguments = token.EndsWith("=value", StringComparison.Ordinal)
                        ? new[] { token }
                        : new[] { token, "value" };
                    var result = await RunAsync([.. command.Prefix, "--help", .. arguments]);

                    Assert.True(
                        result.ExitCode == ExitCodes.InvalidArguments,
                        $"{command.Name} should reject removed --{option}: {result.StandardError}");
                    Assert.Contains(option, result.StandardError, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task AllProfilesIsRejectedByEveryCommandScope()
    {
        foreach (var command in CommandScopes)
        {
            var result = await RunAsync([.. command.Prefix, "--help", "--all-profiles"]);

            Assert.True(
                result.ExitCode == ExitCodes.InvalidArguments,
                $"{command.Name} should reject parser-recognized but inapplicable --all-profiles");
            Assert.Contains("--all-profiles", result.StandardError, StringComparison.Ordinal);
            Assert.Empty(result.StandardOutput);
        }
    }

    [Fact]
    public void InterleavedTypedConditionsRetainTheirExactCommandLineOrder()
    {
        var parsed = CliArguments.Parse(
        [
            "--namespace", "glob-first",
            "--namespace-literal", "literal-middle",
            "--namespace-regex", "regex-late",
            "--namespace", "glob-last",
        ]);

        Assert.Equal(
            [
                new CliOptionOccurrence("namespace", "glob-first"),
                new CliOptionOccurrence("namespace-literal", "literal-middle"),
                new CliOptionOccurrence("namespace-regex", "regex-late"),
                new CliOptionOccurrence("namespace", "glob-last"),
            ],
            parsed.GetOccurrences("namespace", "namespace-literal", "namespace-regex"));
        Assert.Empty(parsed.GetOccurrences());
    }

    [Fact]
    public async Task EveryFlagRejectsAnEqualsValueInTerminalHelp()
    {
        foreach (var command in CommandScopes)
        {
            foreach (var option in ScopeProbeOptions.Where(option => !IsValueOption(option)))
            {
                var result = await RunAsync([.. command.Prefix, "--help", $"--{option}=true"]);

                Assert.True(
                    result.ExitCode == ExitCodes.InvalidArguments,
                    $"{command.Name} should reject a value for --{option}: {result.StandardError}");
            }
        }
    }

    [Theory]
    [InlineData("base-dir", "root")]
    [InlineData("path-style", "relative")]
    [InlineData("symbol-path-style", "csharp")]
    public async Task IndexRejectsQueryPathAndPresentationOptions(string option, string value)
    {
        var result = await RunAsync("index", "--help", $"--{option}", value);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains($"--{option}", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(result.StandardOutput);
    }

    [Theory]
    [InlineData("namespace-case", "broken")]
    [InlineData("type-case", "broken")]
    [InlineData("method-case", "broken")]
    [InlineData("file-case", "broken")]
    [InlineData("source-case", "broken")]
    [InlineData("path-style", "portable")]
    [InlineData("symbol-path-style", "display")]
    public async Task InvalidCaseAndStyleValuesAreUsageErrors(string option, string value)
    {
        var result = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", $"--{option}", value);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains(value, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown option(s)", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("namespace-case")]
    [InlineData("type-case")]
    [InlineData("method-case")]
    [InlineData("file-case")]
    [InlineData("source-case")]
    public async Task DuplicateCaseOptionsAreUsageErrors(string option)
    {
        var result = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", $"--{option}", "strict", $"--{option}", "ignore");

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("only once", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("symbol-path-style", "csharp", "explicit")]
    [InlineData("path-style", "absolute", "relative")]
    [InlineData("base-dir", ".", "src")]
    public async Task DuplicateStyleAndPathOptionsAreUsageErrors(string option, string first, string second)
    {
        var result = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", $"--{option}", first, $"--{option}", second);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("only once", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind", "type")]
    [InlineData("async-status", "involved")]
    public async Task InvalidFunctionFilterValuesListTheSupportedValues(string option, string value)
    {
        var result = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()", $"--{option}", value);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains("Unknown", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(value, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBaseDirectoryAndValueTokensAreRejectedByTheTokenizer()
    {
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["--base-dir", ""]));
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["--base-dir="]));
        Assert.Throws<CliUsageException>(() => CliArguments.Parse(["--symbol-path-style", ""]));
    }

    [Fact]
    public async Task PresentationPathAndHelpOptionsAloneDoNotSatisfySelectionMinimums()
    {
        await _fixture.BuildTask;

        string[][] options =
        [
            ["--symbol-path-style", "csharp"],
            ["--short-names"],
            ["--base-dir", _fixture.RootPath],
            ["--path-style", "relative"],
        ];

        foreach (var option in options)
        {
            var symbolFind = await RunAsync(["symbol", "find", .. option, "--db", _fixture.DatabasePath]);
            var sourceSearch = await RunAsync(["source", "search", .. option, "--db", _fixture.DatabasePath]);

            Assert.Equal(ExitCodes.InvalidArguments, symbolFind.ExitCode);
            Assert.Equal(ExitCodes.InvalidArguments, sourceSearch.ExitCode);
        }

        var symbolHelp = await RunAsync(["symbol", "find", "--help", "--db", _fixture.DatabasePath]);
        var sourceSearchHelp = await RunAsync(["source", "search", "--help", "--db", _fixture.DatabasePath]);
        Assert.Equal(ExitCodes.Success, symbolHelp.ExitCode);
        Assert.Equal(ExitCodes.Success, sourceSearchHelp.ExitCode);
        Assert.Contains("Usage: csindex symbol find", symbolHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Usage: csindex source search", sourceSearchHelp.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedConditionOccurrenceOrderIsObservableThroughRegexDiagnostics()
    {
        await _fixture.BuildTask;

        var firstRegex = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()",
            "--method-regex", "[",
            "--method-literal", "Play()",
            "--method", "Play",
            "--method-regex", "(",
            "--db", _fixture.DatabasePath);
        var secondRegex = await RunAsync(
            "symbol", "find", "Alpha.AClass::Play()",
            "--method-regex", "(",
            "--method", "Play",
            "--method-literal", "Play()",
            "--method-regex", "[",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, firstRegex.ExitCode);
        Assert.Equal(ExitCodes.InvalidArguments, secondRegex.ExitCode);
        Assert.Contains("Invalid regular expression condition", firstRegex.StandardError, StringComparison.Ordinal);
        Assert.Contains("Invalid regular expression condition", secondRegex.StandardError, StringComparison.Ordinal);
        Assert.NotEqual(firstRegex.StandardError, secondRegex.StandardError);
    }

    [Fact]
    public async Task EveryCaseOptionControlsOnlyItsOwnConditionCategory()
    {
        await _fixture.BuildTask;
        var cases = new[]
        {
            new CaseProbe("namespace", "alpha"),
            new CaseProbe("type", "aclass"),
            new CaseProbe("method", "play()"),
            new CaseProbe("file", "main.CS"),
            new CaseProbe("source", "PUBLIC VOID PLAY"),
        };

        foreach (var probe in cases)
        {
            var strictArguments = BuildCaseProbeArguments(probe, "strict");
            var ignoreArguments = BuildCaseProbeArguments(probe, "ignore");
            var strict = await RunAsync([.. strictArguments, "--db", _fixture.DatabasePath]);
            var ignore = await RunAsync([.. ignoreArguments, "--db", _fixture.DatabasePath]);

            Assert.Equal(ExitCodes.Success, strict.ExitCode);
            Assert.Equal(ExitCodes.Success, ignore.ExitCode);
            using var strictDocument = System.Text.Json.JsonDocument.Parse(strict.StandardOutput);
            using var ignoreDocument = System.Text.Json.JsonDocument.Parse(ignore.StandardOutput);
            Assert.Empty(strictDocument.RootElement.GetProperty("matched").EnumerateArray());
            Assert.Contains(
                ignoreDocument.RootElement.GetProperty("matched").EnumerateArray(),
                symbol => symbol.GetProperty("displayName").GetString() == "Alpha.AClass::Play()");
        }
    }

    [Fact]
    public async Task OverridesAcceptSourceConditionsAsRootRefinements()
    {
        await _fixture.BuildTask;

        var strict = await RunAsync(
            "overrides", "Alpha.Pianist::Play()", "--include-literal", "PUBLIC VIRTUAL VOID PLAY",
            "--source-case", "strict", "--db", _fixture.DatabasePath);
        var ignore = await RunAsync(
            "overrides", "Alpha.Pianist::Play()", "--include-literal", "PUBLIC VIRTUAL VOID PLAY",
            "--source-case", "ignore", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, strict.ExitCode);
        Assert.Contains("No source-backed", strict.StandardError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, ignore.ExitCode);
        Assert.Contains("Alpha.ProPianist::Play()", ignore.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Game::Player::Run()::<lambda#1>")]
    [InlineData("Game.Player.Run()")]
    [InlineData("Game::Player::Run()::Local()")]
    [InlineData("Game::Player::Run(Guid)")]
    [InlineData("Game::Player::Method<System.String>")]
    public async Task RemovedOrMalformedSelectorGrammarIsRejected(string selector)
    {
        await _fixture.BuildTask;

        var result = await RunAsync("symbol", "find", selector, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.StartsWith("Query error: ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitAllSelectionValuesSatisfyTheSymbolFindMinimum()
    {
        await _fixture.BuildTask;

        var kindAll = await RunAsync(
            "symbol", "find", "--kind", "all", "--db", _fixture.DatabasePath);
        var asyncStatusAll = await RunAsync(
            "symbol", "find", "--async-status=all", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, kindAll.ExitCode);
        Assert.Equal(ExitCodes.Success, asyncStatusAll.ExitCode);
    }

    [Fact]
    public async Task ZeroMatchSelectionIsSuccessfulButMandatoryRootsRemainQueryErrors()
    {
        await _fixture.BuildTask;

        var zeroFind = await RunAsync(
            "symbol", "find", "NoSuch.Namespace::NoSuch.Type::Run()", "--db", _fixture.DatabasePath);
        var zeroList = await RunAsync(
            "symbol", "list", "--namespace", "NoSuch.Namespace", "--db", _fixture.DatabasePath);
        var zeroSearch = await RunAsync(
            "source", "search", "--include", "__no_such_source_text__", "--db", _fixture.DatabasePath);
        var missingShow = await RunAsync(
            "source", "show", "NoSuch.Namespace::NoSuch.Type::Run()", "--db", _fixture.DatabasePath);
        var missingGraph = await RunAsync(
            "async", "tree", "NoSuch.Namespace::NoSuch.Type::Run()", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, zeroFind.ExitCode);
        Assert.Equal(ExitCodes.Success, zeroList.ExitCode);
        Assert.Equal(ExitCodes.Success, zeroSearch.ExitCode);
        Assert.Equal(ExitCodes.InvalidArguments, missingShow.ExitCode);
        Assert.Equal(ExitCodes.InvalidArguments, missingGraph.ExitCode);
        Assert.Contains("No source-backed", missingShow.StandardError, StringComparison.Ordinal);
        Assert.Contains("No source-backed", missingGraph.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedConditionsRefineAnIncludeOverridesMethodQuery()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "symbol", "find", "Alpha.Pianist::Play()", "--method-literal", "Play()",
            "--include-overrides", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Alpha.ProPianist::Play()", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireSingleFailureDoesNotConstructOutputDestination()
    {
        await _fixture.BuildTask;
        var factoryCalled = false;

        var result = await RunWithOutputDestinationFactoryAsync(
            [
                "symbol", "find", "Alpha.AsyncOverride*::Run()", "--require-single", "--db", _fixture.DatabasePath,
            ],
            (_, _) =>
            {
                factoryCalled = true;
                throw new InvalidOperationException("output destination must remain unopened");
            });

        Assert.Equal(ExitCodes.RequireSingleFailure, result.ExitCode);
        Assert.False(factoryCalled);
        Assert.Contains("--require-single expected one symbol", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncludeOverridesRequiresOneWildcardFreeMethodSelectorAndNoOutputOnRejection()
    {
        await _fixture.BuildTask;
        string[][] supportedCommands =
        [
            ["symbol", "find", "Alpha.Pianist::Play()"],
            ["definition", "Alpha.Pianist::Play()"],
            ["references", "Alpha.Pianist::Play()"],
            ["callers", "Alpha.Pianist::Play()"],
            ["callees", "Alpha.Pianist::Play()"],
        ];

        foreach (var command in supportedCommands)
        {
            var expanded = await RunAsync(
                [.. command, "--include-overrides", "--require-single", "--db", _fixture.DatabasePath]);
            Assert.Equal(ExitCodes.Success, expanded.ExitCode);
            Assert.Contains("Alpha.ProPianist::Play()", expanded.StandardOutput, StringComparison.Ordinal);

            var refined = await RunAsync(
                [.. command, "--method-literal", "Play()", "--include-overrides", "--db", _fixture.DatabasePath]);
            Assert.Equal(ExitCodes.Success, refined.ExitCode);
            Assert.Contains("Alpha.ProPianist::Play()", refined.StandardOutput, StringComparison.Ordinal);
        }

        string[][] conditionOnly =
        [
            ["symbol", "find", "--method", "Play"],
            ["definition", "--method", "Play"],
            ["references", "--method", "Play"],
            ["callers", "--method", "Play"],
            ["callees", "--method", "Play"],
        ];
        foreach (var command in conditionOnly)
        {
            await AssertIncludeOverridesRejectedWithoutOutputAsync(
                [.. command, "--include-overrides", "--db", _fixture.DatabasePath]);
        }

        foreach (var commandName in new[] { "symbol", "definition", "references", "callers", "callees" })
        {
            string[] wildcard = commandName == "symbol"
                ? ["symbol", "find", "Alpha.Pianist*::Play()"]
                : [commandName, "Alpha.Pianist*::Play()"];
            await AssertIncludeOverridesRejectedWithoutOutputAsync(
                [.. wildcard, "--include-overrides", "--db", _fixture.DatabasePath]);
        }

        foreach (var commandName in new[] { "symbol", "definition", "references", "callers", "callees" })
        {
            string[] lambda = commandName == "symbol"
                ? ["symbol", "find", "Alpha.LambdaPlayer::Execute().<lambda#1>"]
                : [commandName, "Alpha.LambdaPlayer::Execute().<lambda#1>"];
            await AssertIncludeOverridesRejectedWithoutOutputAsync(
                [.. lambda, "--kind", "lambda", "--include-overrides", "--db", _fixture.DatabasePath]);
        }
    }

    [Fact]
    public async Task IncludeOverridesRejectsSelectedLambdaInitializerAndTopLevelRootsWithoutOutput()
    {
        using var resolutionFixture = new SymbolResolutionFixture();
        await resolutionFixture.BuildTask;
        string[][] commands =
        [
            ["symbol", "find"],
            ["definition"],
            ["references"],
            ["callers"],
            ["callees"],
        ];
        string[] selectors =
        [
            "Namespace1.Namespace2::Class1.Class2::Method1().<lambda#1>",
            "Catalog::SpecialHost::<initializer:Factory>",
            "global::Program::<top-level-statements>",
        ];

        foreach (var command in commands)
        {
            foreach (var selector in selectors)
            {
                await AssertIncludeOverridesRejectedWithoutOutputAsync(
                    [.. command, selector, "--include-overrides", "--db", resolutionFixture.DatabasePath]);
            }
        }
    }

    [Fact]
    public async Task OverridesCommandRejectsIncludeOverridesWithoutOpeningOutput()
    {
        await _fixture.BuildTask;

        await AssertIncludeOverridesRejectedWithoutOutputAsync(
            ["overrides", "Alpha.Pianist::Play()", "--include-overrides", "--db", _fixture.DatabasePath]);
    }

    [Fact]
    public async Task GeneratedRootFilterRunsBeforeRequireSingleAndTraversalFilteringRemainsActive()
    {
        await _fixture.BuildTask;

        foreach (var command in new[] { "references", "callers", "callees" })
        {
            var onlyGeneratedOrdinaryRoot = await RunAsync(
                command, "GameNS.Player::Play()", "--only-generated", "--require-single", "--db", _fixture.DatabasePath);
            var excludedGeneratedRoot = await RunAsync(
                command, "GeneratedCode.GeneratedCaller::Execute", "--exclude-generated", "--require-single",
                "--db", _fixture.DatabasePath);

            Assert.Equal(ExitCodes.RequireSingleFailure, onlyGeneratedOrdinaryRoot.ExitCode);
            Assert.Equal(ExitCodes.RequireSingleFailure, excludedGeneratedRoot.ExitCode);
        }

        foreach (var command in new[] { "references", "callers" })
        {
            var unfiltered = await RunAsync(
                command, "GameNS.Player::Play()", "--output-format", "json", "--db", _fixture.DatabasePath);
            var traversalFiltered = await RunAsync(
                command, "GameNS.Player::Play()", "--exclude-generated", "--output-format", "json",
                "--db", _fixture.DatabasePath);

            Assert.Equal(ExitCodes.Success, unfiltered.ExitCode);
            Assert.Equal(ExitCodes.Success, traversalFiltered.ExitCode);
            using var unfilteredDocument = System.Text.Json.JsonDocument.Parse(unfiltered.StandardOutput);
            Assert.NotEmpty(unfilteredDocument.RootElement.GetProperty("calls").EnumerateArray());
            using var filteredDocument = System.Text.Json.JsonDocument.Parse(traversalFiltered.StandardOutput);
            Assert.Empty(filteredDocument.RootElement.GetProperty("calls").EnumerateArray());
        }

        // GeneratedCaller is the only generated fixture root and its call is in the
        // generated document, so the callee direction has no ordinary-root/generated-
        // descendant pair with which to observe post-selection exclusion.
        var generatedCallee = await RunAsync(
            "callees", "GeneratedCode.GeneratedCaller::Execute", "--only-generated", "--output-format", "json",
            "--db", _fixture.DatabasePath);
        Assert.Equal(ExitCodes.Success, generatedCallee.ExitCode);
        using var generatedCalleeDocument = System.Text.Json.JsonDocument.Parse(generatedCallee.StandardOutput);
        Assert.NotEmpty(generatedCalleeDocument.RootElement.GetProperty("calls").EnumerateArray());
    }

    [Fact]
    public async Task CalleesApplyGeneratedFilterToTraversalCallDocumentsAfterRootSelection()
    {
        await _fixture.BuildTask;
        await _fixture.AddResolvedCallAsync(
            "Alpha.AClass::Play()",
            "GeneratedCode.GeneratedCaller::Execute(GameNS.Player)",
            cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddResolvedCallAsync(
            "GeneratedCode.GeneratedCaller::Execute(GameNS.Player)",
            "Alpha.AClass::Play()",
            cancellationToken: TestContext.Current.CancellationToken);

        var ordinaryUnfiltered = await RunAsync(
            "callees", "Alpha.AClass::Play()", "--output-format", "json", "--db", _fixture.DatabasePath);
        var ordinaryExcluded = await RunAsync(
            "callees", "Alpha.AClass::Play()", "--exclude-generated", "--output-format", "json",
            "--db", _fixture.DatabasePath);
        var generatedUnfiltered = await RunAsync(
            "callees", "GeneratedCode.GeneratedCaller::Execute", "--output-format", "json",
            "--db", _fixture.DatabasePath);
        var generatedOnly = await RunAsync(
            "callees", "GeneratedCode.GeneratedCaller::Execute", "--only-generated", "--output-format", "json",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, ordinaryUnfiltered.ExitCode);
        Assert.Equal(ExitCodes.Success, ordinaryExcluded.ExitCode);
        Assert.Equal(ExitCodes.Success, generatedUnfiltered.ExitCode);
        Assert.Equal(ExitCodes.Success, generatedOnly.ExitCode);
        using var ordinaryUnfilteredDocument = System.Text.Json.JsonDocument.Parse(ordinaryUnfiltered.StandardOutput);
        using var ordinaryExcludedDocument = System.Text.Json.JsonDocument.Parse(ordinaryExcluded.StandardOutput);
        using var generatedUnfilteredDocument = System.Text.Json.JsonDocument.Parse(generatedUnfiltered.StandardOutput);
        using var generatedOnlyDocument = System.Text.Json.JsonDocument.Parse(generatedOnly.StandardOutput);
        Assert.NotEmpty(ordinaryUnfilteredDocument.RootElement.GetProperty("calls").EnumerateArray());
        Assert.Empty(ordinaryExcludedDocument.RootElement.GetProperty("calls").EnumerateArray());
        Assert.True(
            generatedUnfilteredDocument.RootElement.GetProperty("calls").GetArrayLength() >
            generatedOnlyDocument.RootElement.GetProperty("calls").GetArrayLength());
        Assert.NotEmpty(generatedOnlyDocument.RootElement.GetProperty("calls").EnumerateArray());
    }

    [Fact]
    public async Task MandatoryRootsRejectMissingMatchesBeforeOutputConstruction()
    {
        await _fixture.BuildTask;
        string[][] missingCommands =
        [
            ["source", "show", "NoSuch.Namespace::NoSuch.Type::Run()"],
            ["async", "tree", "NoSuch.Namespace::NoSuch.Type::Run()"],
            ["callers", "tree", "NoSuch.Namespace::NoSuch.Type::Run()"],
        ];

        foreach (var command in missingCommands)
        {
            var factoryCalled = false;
            var result = await RunWithOutputDestinationFactoryAsync(
                [.. command, "--db", _fixture.DatabasePath],
                (_, _) =>
                {
                    factoryCalled = true;
                    throw new InvalidOperationException("mandatory root failure must remain unopened");
                });

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.False(factoryCalled, string.Join(' ', command));
            Assert.Contains("No source-backed", result.StandardError, StringComparison.Ordinal);
        }

        string[][] ambiguousCommands =
        [
            ["source", "show", "Alpha.AClass::Play"],
            ["async", "tree", "Alpha.AsyncGraph::**"],
            ["callers", "tree", "Alpha.CallerGraph::**"],
        ];

        foreach (var command in ambiguousCommands)
        {
            var factoryCalled = false;
            var result = await RunWithOutputDestinationFactoryAsync(
                [.. command, "--db", _fixture.DatabasePath],
                (_, _) =>
                {
                    factoryCalled = true;
                    throw new InvalidOperationException("mandatory cardinality failure must remain unopened");
                });

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.False(factoryCalled, string.Join(' ', command));
            Assert.StartsWith("Query error: ", result.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SelectorRequiredCommandsClassifyZeroMatchesAsQueryErrors()
    {
        await _fixture.BuildTask;
        string[][] commands =
        [
            ["definition"],
            ["references"],
            ["callers"],
            ["callees"],
            ["overrides"],
        ];

        foreach (var command in commands)
        {
            var missingSelector = await RunAsync([.. command, "--db", _fixture.DatabasePath]);
            var zeroMatch = await RunWithOutputDestinationFactoryAsync(
                [.. command, "NoSuch.Namespace::NoSuch.Type::Run()", "--db", _fixture.DatabasePath],
                (_, _) => throw new InvalidOperationException("zero-match query must not open output"));
            var multipleRoots = await RunWithOutputDestinationFactoryAsync(
                [.. command, "Alpha.AClass::Play", "--require-single", "--db", _fixture.DatabasePath],
                (_, _) => throw new InvalidOperationException("require-single query must not open output"));

            Assert.Equal(ExitCodes.InvalidArguments, missingSelector.ExitCode);
            Assert.Equal(ExitCodes.InvalidArguments, zeroMatch.ExitCode);
            Assert.Equal(ExitCodes.RequireSingleFailure, multipleRoots.ExitCode);
            Assert.NotEmpty(missingSelector.StandardError);
            Assert.StartsWith("Query error: ", zeroMatch.StandardError, StringComparison.Ordinal);
            Assert.Contains("--require-single expected one symbol", multipleRoots.StandardError, StringComparison.Ordinal);
        }
    }

    private async Task AssertIncludeOverridesRejectedWithoutOutputAsync(string[] args)
    {
        var factoryCalled = false;
        var result = await RunWithOutputDestinationFactoryAsync(
            args,
            (_, _) =>
            {
                factoryCalled = true;
                throw new InvalidOperationException("include-overrides rejection must remain unopened");
            });

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.False(factoryCalled, string.Join(' ', args));
        Assert.Contains("include-overrides", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _fixture.Dispose();

    private static bool IsValueOption(string option) =>
        !FlagOptions.Contains(option);

    private static string ValueFor(string option) => option switch
    {
        "at" => "Source.cs:1:1",
        "db" => "missing.sqlite",
        "profile" or "profile-name" => "default",
        "output-format" => "json",
        "output-file" => "result.txt",
        "mode" => "directory",
        "solution" or "configuration" or "framework" or "target-framework" or "runtime" => "value",
        "define" or "undefine" => "SYMBOL",
        "define-file" or "reference" or "unity-editor" => "value",
        "generated-source" => "physical",
        "exclude" => "obj/**",
        "source-layout" => "single-line",
        "dispatch" => "static",
        "caller-scope" => "direct",
        "depth" or "max-nodes" => "1",
        "namespace-case" or "type-case" or "method-case" or "file-case" or "source-case" => "strict",
        "kind" => "all",
        "async-status" => "all",
        "symbol-path-style" => "csharp",
        "path-style" => "absolute",
        _ => option is "include" or "include-literal" or "include-regex" or
            "exclude" or "exclude-literal" or "exclude-regex"
            ? "Play"
            : option is "file" or "file-literal" or "file-regex"
                ? "Main.cs"
                : option is "namespace" or "namespace-literal" or "namespace-regex"
                    ? "Alpha"
                    : option is "type" or "type-literal" or "type-regex"
                        ? "AClass"
                        : option is "method" or "method-literal" or "method-regex"
                            ? "Play"
                            : ".",
    };

    private static string[] OptionTokens(string option) =>
        IsValueOption(option)
            ? [$"--{option}", ValueFor(option)]
            : [$"--{option}"];

    private static string[] OptionTokens(CommandScope command, string option) =>
        option == "at" && command.Prefix.Contains("--at", StringComparer.Ordinal)
            ? []
            : OptionTokens(option);

    private static string[] BuildCaseProbeArguments(CaseProbe probe, string mode)
    {
        var conditions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["namespace"] = "Alpha",
            ["type"] = "AClass",
            ["method"] = "Play()",
            ["file"] = "Main.cs",
            ["source"] = "public void Play",
        };
        conditions[probe.Category] = probe.Value;
        return
        [
            "symbol", "find",
            "--namespace-literal", conditions["namespace"],
            "--type-literal", conditions["type"],
            "--method-literal", conditions["method"],
            "--file-literal", conditions["file"],
            "--include-literal", conditions["source"],
            "--namespace-case", probe.Category == "namespace" ? mode : "strict",
            "--type-case", probe.Category == "type" ? mode : "strict",
            "--method-case", probe.Category == "method" ? mode : "strict",
            "--file-case", probe.Category == "file" ? mode : "strict",
            "--source-case", probe.Category == "source" ? mode : "strict",
            "--output-format", "json",
        ];
    }

    private static IReadOnlyList<string> ReadAcceptedOptions(string help)
    {
        var lines = help.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var heading = Array.IndexOf(lines, "Accepted options:");
        Assert.True(heading >= 0, help);
        return lines
            .Skip(heading + 1)
            .TakeWhile(line => line.StartsWith("  --", StringComparison.Ordinal))
            .Select(line => line[4..])
            .OrderBy(option => option, StringComparer.Ordinal)
            .ToArray();
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

    private static async Task<CommandResult> RunWithOutputDestinationFactoryAsync(
        string[] args,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.RunAsync(args, CancellationToken.None, outputDestinationFactory);
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed record CommandScope(string Name, string[] Prefix, string[] Allowed);

    private sealed record CaseProbe(string Category, string Value);

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
