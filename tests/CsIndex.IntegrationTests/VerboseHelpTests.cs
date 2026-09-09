using CsIndex.Cli;
using CsIndex.Core.Analysis;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class VerboseHelpTests
{
    private static readonly string[][] RecognizedCommandPaths =
    [
        [],
        ["index"],
        ["symbol", "find"],
        ["symbol", "list"],
        ["source", "show"],
        ["source", "search"],
        ["definition"],
        ["definition", "--at", "Source.cs:1:1"],
        ["references"],
        ["callers"],
        ["callees"],
        ["overrides"],
        ["async", "tree"],
        ["callers", "tree"],
        ["conditions"],
    ];

    private static readonly (string[] Path, string[] ExpectedLines)[] CommandOptionScopeCases =
    [
        (
            [],
            [
                "Global scope accepts --db, --profile, --output-format, --output-file, --help, --help-verbose, and --verbose.",
                "Common query scope: symbol-emitting queries accept --db, --profile, documented --output-format, --output-file, --symbol-path-style, --short-names, --base-dir, --path-style, --help, --help-verbose, and --verbose unless a row explicitly restricts them; conditions excludes symbol presentation/short names, and definition --at accepts presentation/path but no root filters.",
            ]),
        (
            ["index"],
            [
                "Index scope accepts --db, --mode, --solution, --configuration, --framework, --target-framework, --runtime, --profile-name, --define, --undefine, --define-file, --reference, --unity-editor, --exclude, --generated-source, --rebuild, --verbose, --diagnostics, --help, and --help-verbose only.",
                "Index scope has no query presentation or path options.",
            ]),
        (
            ["symbol", "find"],
            [
                "Symbol find scope: optional selector; --namespace, --namespace-literal, --namespace-regex, --type, --type-literal, --type-regex, --method, --method-literal, --method-regex, --file, --file-literal, --file-regex, --include, --include-literal, --include-regex, --exclude, --exclude-literal, --exclude-regex.",
                "Symbol find scope also accepts --namespace-case, --type-case, --method-case, --file-case, --source-case, --kind, --async-status, --require-single, --include-overrides, --show-source, and --source-layout.",
            ]),
        (
            ["symbol", "list"],
            [
                "Symbol list scope: no selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Symbol list scope also accepts --async-involved.",
            ]),
        (
            ["source", "search"],
            [
                "Source search scope: no selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Source search scope also accepts --source-layout.",
            ]),
        (
            ["source", "show"],
            [
                "Source show scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Source show scope also accepts --source-layout.",
            ]),
        (
            ["definition"],
            [
                "Definition scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Definition scope also accepts --require-single and --include-overrides.",
            ]),
        (
            ["references"],
            [
                "References scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "References scope also accepts --exclude-generated, --only-generated, --require-single, and --include-overrides.",
            ]),
        (
            ["callers"],
            [
                "Callers scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Callers scope also accepts --exclude-generated, --only-generated, --require-single, --include-overrides, --dispatch, --caller-scope, and --show-source.",
                "callers --show-source: include the normalized invocation or object-creation expression",
            ]),
        (
            ["callees"],
            [
                "Callees scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Callees scope also accepts --exclude-generated, --only-generated, --require-single, --include-overrides, and --exclude-lambda-calls.",
            ]),
        (
            ["overrides"],
            [
                "Overrides scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Overrides scope also accepts --require-single; it does not accept --include-overrides.",
            ]),
        (
            ["async", "tree"],
            [
                "Async tree scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Async tree scope also accepts --max-nodes and graph output options.",
            ]),
        (
            ["callers", "tree"],
            [
                "Callers tree scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Callers tree scope also accepts --depth, --max-nodes, --show-source, and graph output options.",
                "callers tree --show-source: include normalized source for every physical call site",
            ]),
        (
            ["definition", "--at", "Source.cs:1:1"],
            [
                "Definition --at scope: no selector or root conditions; presentation/path options only.",
            ]),
        (
            ["conditions"],
            [
                "Conditions scope: no selector or root conditions; --base-dir and --path-style only among query path options.",
            ]),
    ];

    private static readonly string[] VerboseExamples =
    [
        "Game.Core.Player.Inventory::Load(int).Validate()",
        "Game.Core::Player.Inventory::Load(int).Validate()",
        "global::Program::<top-level-statements>",
        "Player.Inventory::Load(int).Validate()",
        "Outer.Local",
        "Outer(int).Local",
        "Outer.Local(string)",
        "Method",
        "Method<T>",
        "Method()",
        "Method<T>(T)",
        "Namespace1.Namespace1_2::*::**.Method2.<lambda#1>",
        "**::Class1::Method2.<lambda#1>",
        "Game::Player::[constructor](int,string)",
        "Game::Player::[static-constructor]()",
        "Game::Player::[destructor]()",
        "Math::Number::[operator:+](Math.Number,Math.Number)",
        "Math::Number::[checked-operator:+](Math.Number,Math.Number)",
        "Game::Value::[conversion:implicit:int](Game.Value)",
        "Game::Value::[conversion:explicit:string](Game.Value)",
        "Game::Value::[checked-conversion:explicit:int](Game.Value)",
        "Game::Player::[get:Name]()",
        "Game::Player::[set:Name](string)",
        "Game::Player::[init:Name](string)",
        "Game::Player::[get:Item](int)",
        "Game::Player::[set:Item](int,string)",
        "Game::Player::[add:Changed](System.EventHandler)",
        "Game::Player::[remove:Changed](System.EventHandler)",
        "Game::Player::[get:Game.Contracts.IPlayer.Name]()",
        "Game::Player::[set:Game.Contracts.IPlayer.Name](string)",
        "Game::Player::[add:Game.Contracts.IEvents.Changed](System.EventHandler)",
        "Game::Player::[explicit:System.IDisposable.Dispose]()",
        "Game::Player::[explicit:Game.Contracts.IMapper.Map]<T>(T)",
        "Game::Player::<initializer:Score>",
        "Game::Player::Run().<lambda#1>",
        "Game::Player::Run().<anonymous-method#2>",
        "Game::Player::Run()::<lambda#1>",
        "Game.Player.Run()",
        "Game::Player::Run()::Local()",
        "Game::Player::Run(Guid)",
        "Game::Player::Method<System.String>",
    ];

    private static readonly string[] VerboseExplanations =
    [
        "csharp paths are namespace/type suffix searches and may return extra namespaces",
        "explicit syntax fixes the boundary",
        "global is the exact global namespace",
        "@global is a literal identifier",
        "* matches one hierarchy segment",
        "** matches zero or more",
        "omitted parameters and generic lists broaden independently",
        "() means exactly non-generic zero parameters",
        "concise conditions are glob",
        "literal and regex are explicit",
        "case categories are independent and default to strict",
        "same-category conditions OR",
        "categories AND",
        "includes AND",
        "excludes OR",
        "root filters run before cardinality",
        "never filter traversal descendants",
        "kind all is Method/Lambda/Initializer/TopLevelStatements",
        "direct AsyncRole, not child leakage",
        "partial definition/implementation is one logical root with role-specific declaration rows",
        "no artificial path suffix",
        "DB paths are root-relative",
        "base-dir changes query reconstruction only",
        "path style defaults absolute",
        "relative stored paths",
        "symbol path style is presentation-only",
        "short names remove owner namespace only",
        "old child :: separator",
        "constructed generic notation",
        "Supported operator tokens",
        "+ - ! ~ ++ -- true false * / % & | ^",
        "<< >> >>> == != < > <= >=",
        "+= -= *= /= %= &= |= ^= <<= >>= >>>=",
    ];

    [Fact]
    public async Task EveryRecognizedCommandHasByteIdenticalVerboseHelpSpellings()
    {
        foreach (var command in RecognizedCommandPaths)
        {
            var helpThenVerbose = await RunAsync([.. command, "--help", "--verbose"]);
            var verboseThenHelp = await RunAsync([.. command, "--verbose", "--help"]);
            var helpVerbose = await RunAsync([.. command, "--help-verbose"]);

            Assert.Equal(ExitCodes.Success, helpThenVerbose.ExitCode);
            Assert.Equal(ExitCodes.Success, verboseThenHelp.ExitCode);
            Assert.Equal(ExitCodes.Success, helpVerbose.ExitCode);
            Assert.Equal(helpThenVerbose.StandardOutput, verboseThenHelp.StandardOutput);
            Assert.Equal(helpThenVerbose.StandardOutput, helpVerbose.StandardOutput);
            Assert.Equal(string.Empty, helpThenVerbose.StandardError);
            Assert.Equal(string.Empty, verboseThenHelp.StandardError);
            Assert.Equal(string.Empty, helpVerbose.StandardError);
            foreach (var example in VerboseExamples)
            {
                Assert.Contains(example, helpThenVerbose.StandardOutput, StringComparison.Ordinal);
            }

            foreach (var explanation in VerboseExplanations)
            {
                Assert.Contains(explanation, helpThenVerbose.StandardOutput, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task VerboseGlobalHelpContainsCanonicalExamplesAndExplanations()
    {
        var result = await RunAsync("--help-verbose");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        foreach (var example in VerboseExamples)
        {
            Assert.Contains(example, result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var explanation in VerboseExplanations)
        {
            Assert.Contains(explanation, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var (command, expectedLines) in CommandOptionScopeCases)
        {
            var scoped = await RunAsync([.. command, "--help-verbose"]);

            Assert.Equal(ExitCodes.Success, scoped.ExitCode);
            foreach (var expectedLine in expectedLines)
            {
                Assert.Contains(expectedLine, scoped.StandardOutput, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task CallersHelpDescribesShowSourceExactly()
    {
        var result = await RunAsync("callers", "--help");
        var treeResult = await RunAsync("callers", "tree", "--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(ExitCodes.Success, treeResult.ExitCode);
        var helpLines = result.StandardOutput
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.None);
        Assert.Contains(
            "  --show-source               Include the normalized invocation or object-creation expression",
            helpLines);

        var treeHelpLines = treeResult.StandardOutput
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.None);
        Assert.Contains(
            "  --show-source               Include normalized source for every physical call site",
            treeHelpLines);
    }

    [Fact]
    public async Task CanonicalVerboseExamplesUseDotForExecutableChildrenAndKeepLegacyFormsInvalid()
    {
        var result = await RunAsync("--help-verbose");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var canonicalStart = result.StandardOutput.IndexOf(
            "Canonical symbol path examples",
            StringComparison.Ordinal);
        var invalidStart = result.StandardOutput.IndexOf(
            "Path validation examples (invalid forms and reasons)",
            StringComparison.Ordinal);
        Assert.True(canonicalStart >= 0);
        Assert.True(invalidStart > canonicalStart);

        var canonicalExamples = result.StandardOutput[canonicalStart..invalidStart];
        Assert.Contains("Game::Player::Run().<lambda#1>", canonicalExamples, StringComparison.Ordinal);
        Assert.Contains("Game::Player::Run().Local()", canonicalExamples, StringComparison.Ordinal);
        Assert.DoesNotContain("Game::Player::Run()::<lambda#1>", canonicalExamples, StringComparison.Ordinal);
        Assert.DoesNotContain("Game::Player::Run()::Local()", canonicalExamples, StringComparison.Ordinal);
        Assert.DoesNotContain("Game.Player.Run()", canonicalExamples, StringComparison.Ordinal);
        Assert.DoesNotContain("Game::Player::Run(Guid)", canonicalExamples, StringComparison.Ordinal);
        Assert.DoesNotContain("Game::Player::Method<System.String>", canonicalExamples, StringComparison.Ordinal);

        var invalidExamples = result.StandardOutput[invalidStart..];
        Assert.Contains(
            "Game::Player::Run()::<lambda#1>    invalid: old child :: separator",
            invalidExamples,
            StringComparison.Ordinal);
        Assert.Contains(
            "Game::Player::Run()::Local()    invalid: three top-level fields",
            invalidExamples,
            StringComparison.Ordinal);
        Assert.Contains(
            "Game.Player.Run()    invalid: no top-level :: separator",
            invalidExamples,
            StringComparison.Ordinal);
        Assert.Contains(
            "Game::Player::Run(Guid)    invalid: non-alias type must be fully qualified",
            invalidExamples,
            StringComparison.Ordinal);
        Assert.Contains(
            "Game::Player::Method<System.String>    invalid: constructed generic notation",
            invalidExamples,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalHelpRemainsConciseAndQueryVerboseRequiresHelp()
    {
        var normal = await RunAsync("--help");
        var verbose = await RunAsync("--help", "--verbose");

        Assert.Equal(ExitCodes.Success, normal.ExitCode);
        Assert.Equal(ExitCodes.Success, verbose.ExitCode);
        Assert.DoesNotContain("Game.Core.Player.Inventory::Load(int).Validate()", normal.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Game.Core.Player.Inventory::Load(int).Validate()", verbose.StandardOutput, StringComparison.Ordinal);

        foreach (var category in new[] { "namespace", "type", "method", "file", "source" })
        {
            Assert.Contains($"--{category}-case strict|ignore", normal.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain($"--{category}-case strict|insensitive", normal.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var command in RecognizedCommandPaths.Where(command => command.Length > 0 && command[0] != "index"))
        {
            var result = await RunAsync([.. command, "--verbose"]);

            Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
            Assert.Contains("verbose", result.StandardError, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExactLegacyGlobalHelpAliasesRemainSupportedWithoutSwallowingTrailingTokens()
    {
        var canonical = await RunAsync("--help");

        foreach (var alias in new[] { "-h", "help" })
        {
            var exact = await RunAsync(alias);
            var trailing = await RunAsync(alias, "--not-an-option");

            Assert.Equal(ExitCodes.Success, exact.ExitCode);
            Assert.Equal(canonical.StandardOutput, exact.StandardOutput);
            Assert.Equal(string.Empty, exact.StandardError);
            Assert.Equal(ExitCodes.InvalidArguments, trailing.ExitCode);
            Assert.Equal(string.Empty, trailing.StandardOutput);
        }
    }

    [Fact]
    public async Task UnknownCommandAndOptionStillFailBeforeHelpOutput()
    {
        var unknownCommand = await RunAsync("not-a-command", "--help", "--verbose");
        var unknownOption = await RunAsync("symbol", "find", "--help", "--not-an-option=1");

        Assert.Equal(ExitCodes.InvalidArguments, unknownCommand.ExitCode);
        Assert.Equal(ExitCodes.InvalidArguments, unknownOption.ExitCode);
        Assert.Equal(string.Empty, unknownCommand.StandardOutput);
        Assert.Equal(string.Empty, unknownOption.StandardOutput);
    }

    [Fact]
    public async Task TerminalHelpSkipsDatabaseSourceAndOutputConstruction()
    {
        var missingDatabase = Path.Combine(Path.GetTempPath(), $"csindex-missing-{Guid.NewGuid():N}", "missing.sqlite");
        var missingOutput = Path.Combine(Path.GetTempPath(), $"csindex-missing-output-{Guid.NewGuid():N}", "result.txt");

        foreach (var command in RecognizedCommandPaths.Where(command => command.Length > 0))
        {
            var args = new List<string>(command)
            {
                "--help-verbose",
                "--db", missingDatabase,
            };
            if (command[0] != "index")
            {
                args.AddRange(["--output-file", missingOutput]);
            }

            var result = await RunWithOutputDestinationFactoryAsync(
                args.ToArray(),
                (_, _) => throw new InvalidOperationException("terminal help must not construct output"));

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.NotEmpty(result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
        }

        var global = await RunWithOutputDestinationFactoryAsync(
            ["--help-verbose", "--db", missingDatabase, "--output-file", missingOutput],
            (_, _) => throw new InvalidOperationException("terminal help must not construct output"));

        Assert.Equal(ExitCodes.Success, global.ExitCode);
        Assert.NotEmpty(global.StandardOutput);
        Assert.Equal(string.Empty, global.StandardError);
    }

    [Fact]
    public async Task VerboseHelpSkipsEveryImmutableProgramDependencyBoundary()
    {
        var missingDatabase = Path.Combine(Path.GetTempPath(), $"csindex-missing-{Guid.NewGuid():N}", "missing.sqlite");
        var invocationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var command in RecognizedCommandPaths)
        {
            var dependencies = new ProgramDependencies(
                (_, _) => ThrowDependency<OutputDestination>("output", invocationCounts),
                (_, _) => ThrowDependency<SemanticQueryService>("query", invocationCounts),
                () => ThrowDependency<AnalysisCoordinator>("analysis", invocationCounts),
                _ => ThrowDependency<SqliteIndex>("sqlite", invocationCounts));
            string[] args = [.. command, .. KnownOperationalOptions(command, missingDatabase), "--help-verbose"];

            var result = await RunWithDependenciesAsync(args, dependencies);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.NotEmpty(result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.Empty(invocationCounts);
        }
    }

    [Fact]
    public async Task QueryDependencyReceivesTheParsedBaseDirectory()
    {
        string? capturedBaseDirectory = "unset";
        var dependencies = new ProgramDependencies(
            (_, _) => throw new InvalidOperationException("output boundary should not be needed"),
            (_, baseDirectory) =>
            {
                capturedBaseDirectory = baseDirectory;
                throw new InvalidOperationException("query boundary reached after capture");
            },
            () => throw new InvalidOperationException("analysis boundary should not be needed"),
            _ => throw new InvalidOperationException("sqlite boundary should not be needed"));

        var result = await RunWithDependenciesAsync(
            [
                "symbol", "find", "--namespace-literal", "Alpha", "--base-dir", "portable-root",
                "--db", "missing.sqlite",
            ],
            dependencies);

        Assert.Equal(ExitCodes.AnalysisFailure, result.ExitCode);
        Assert.Contains("query boundary reached after capture", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("portable-root", capturedBaseDirectory);
    }

    [Fact]
    public async Task OperationalSingletonDuplicatesFailBeforeQueryDependenciesButHelpStillBypassesThem()
    {
        string[][] cases =
        [
            ["symbol", "find", "Alpha.AClass::Play()", "--profile", "first", "--profile", "second"],
            ["conditions", "--output-file", "first.txt", "--output-file", "second.txt"],
        ];

        foreach (var args in cases)
        {
            var queryInvoked = false;
            var dependencies = new ProgramDependencies(
                (_, _) => throw new InvalidOperationException("output boundary should not be needed"),
                (_, _) =>
                {
                    queryInvoked = true;
                    throw new InvalidOperationException("query boundary must follow singleton validation");
                },
                () => throw new InvalidOperationException("analysis boundary should not be needed"),
                _ => throw new InvalidOperationException("sqlite boundary should not be needed"));

            var operational = await RunWithDependenciesAsync(args, dependencies);
            var help = await RunWithDependenciesAsync([.. args, "--help"], dependencies);

            Assert.Equal(ExitCodes.InvalidArguments, operational.ExitCode);
            Assert.Contains("only once", operational.StandardError, StringComparison.Ordinal);
            Assert.False(queryInvoked);
            Assert.Equal(ExitCodes.Success, help.ExitCode);
            Assert.NotEmpty(help.StandardOutput);
        }
    }

    [Theory]
    [MemberData(nameof(PreDependencyValidationCases))]
    public async Task FinalCommandScopeAndValuesAreValidatedBeforeQueryDependencies(
        string[] args,
        string expectedError)
    {
        var queryInvoked = false;
        var dependencies = new ProgramDependencies(
            (_, _) => throw new InvalidOperationException("output boundary should not be needed"),
            (_, _) =>
            {
                queryInvoked = true;
                throw new InvalidOperationException("query boundary must follow command validation");
            },
            () => throw new InvalidOperationException("analysis boundary should not be needed"),
            _ => throw new InvalidOperationException("sqlite boundary should not be needed"));

        var result = await RunWithDependenciesAsync(args, dependencies);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
        Assert.False(queryInvoked);
    }

    public static TheoryData<string[], string> PreDependencyValidationCases { get; } = new()
    {
        {
            ["definition", "--method-literal", "Play"],
            "This command requires exactly one symbol query."
        },
        {
            ["overrides", "Alpha.Pianist::Play()", "--source-case", "broken"],
            "Unknown source-case: broken. Use strict or ignore."
        },
        {
            [
                "definition", "--at", "Source.cs:1:1",
                "--namespace-case", "strict", "--namespace-case", "ignore",
            ],
            "Unknown option(s): --namespace-case"
        },
    };

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

    private static async Task<CommandResult> RunWithDependenciesAsync(
        string[] args,
        ProgramDependencies dependencies)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.RunAsync(args, CancellationToken.None, dependencies);
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private static string[] KnownOperationalOptions(string[] command, string missingDatabase) =>
        command switch
        {
            [] => ["--db", missingDatabase, "--output-format", "json"],
            ["index"] => ["--db", missingDatabase, "--mode", "directory", "--solution", "missing.sln"],
            ["symbol", "find"] => ["--db", missingDatabase, "--namespace-literal", "Alpha", "--base-dir", "portable-root"],
            ["symbol", "list"] => ["--db", missingDatabase, "--namespace-literal", "Alpha", "--short-names"],
            ["source", "show"] => ["--db", missingDatabase, "--base-dir", "portable-root"],
            ["source", "search"] => ["--db", missingDatabase, "--include-literal", "Play", "--path-style", "relative"],
            ["definition"] => ["--db", missingDatabase, "--namespace-literal", "Alpha"],
            ["definition", "--at", "Source.cs:1:1"] => ["--db", missingDatabase, "--base-dir", "portable-root"],
            ["references"] => ["--db", missingDatabase, "--namespace-literal", "Alpha"],
            ["callers"] => ["--db", missingDatabase, "--namespace-literal", "Alpha"],
            ["callees"] => ["--db", missingDatabase, "--namespace-literal", "Alpha"],
            ["overrides"] => ["--db", missingDatabase, "--namespace-literal", "Alpha"],
            ["async", "tree"] => ["--db", missingDatabase, "--namespace-literal", "Alpha", "--max-nodes", "1"],
            ["callers", "tree"] => ["--db", missingDatabase, "--namespace-literal", "Alpha", "--depth", "1"],
            ["conditions"] => ["--db", missingDatabase, "--base-dir", "portable-root"],
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static T ThrowDependency<T>(string dependency, IDictionary<string, int> invocationCounts)
    {
        invocationCounts.TryGetValue(dependency, out var count);
        invocationCounts[dependency] = count + 1;
        throw new InvalidOperationException($"verbose help must not invoke {dependency}");
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
