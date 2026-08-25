using System.Text;
using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;
using Microsoft.Data.Sqlite;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class CliCommandTests : IDisposable
{
    private readonly SemanticIndexFixture _fixture = new();

    [Fact]
    public void SymbolFindArgumentsSupportRepeatableSourceConditionsAndTypedInputs()
    {
        var parsed = CliArguments.Parse(
        [
            "--show-source",
            "--include", "first", "--include=second", "--exclude", "third",
            "--namespace", "Tokyo.*", "--type", "*Gamer", "--method", "P*l*y", "--file", "**/*.cs",
            "--kind", "lambda",
        ]);

        Assert.True(parsed.HasFlag("show-source"));
        Assert.Equal(["first", "second"], parsed.GetMany("include"));
        Assert.Equal(["third"], parsed.GetMany("exclude"));
        Assert.Equal("Tokyo.*", parsed.GetSingle("namespace"));
        Assert.Equal("*Gamer", parsed.GetSingle("type"));
        Assert.Equal("P*l*y", parsed.GetSingle("method"));
        Assert.Equal("**/*.cs", parsed.GetSingle("file"));
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
    public async Task OutputFilePayloadMatchesStdoutAcrossEverySupportedCommandAndFormat()
    {
        await _fixture.BuildTask;
        var outputDirectory = Path.Combine(_fixture.RootPath, "payload-equivalence");
        Directory.CreateDirectory(outputDirectory);
        var cases = new (string Name, string[] Arguments)[]
        {
            ("symbol-find-table", ["symbol", "find", "Alpha.AsyncPlayer::Sync()"]),
            ("symbol-find-json", ["symbol", "find", "Alpha.AsyncPlayer::Sync()", "--output-format", "json"]),
            ("symbol-list-table", ["symbol", "list"]),
            ("symbol-list-json", ["symbol", "list", "--output-format", "json"]),
            ("source-show-table", ["source", "show", "Tokyo.Gamer::Play"]),
            ("source-show-json", ["source", "show", "Tokyo.Gamer::Play", "--output-format", "json"]),
            ("source-search-table", ["source", "search", "--include", "PrintVar("]),
            ("source-search-json", ["source", "search", "--include", "PrintVar(", "--output-format", "json"]),
            ("definition-table", ["definition", "Alpha.AsyncPlayer::Sync()"]),
            ("definition-json", ["definition", "Alpha.AsyncPlayer::Sync()", "--output-format", "json"]),
            ("references-table", ["references", "Alpha.LambdaPlayer::Play()"]),
            ("references-json", ["references", "Alpha.LambdaPlayer::Play()", "--output-format", "json"]),
            ("callers-table", ["callers", "Alpha.LambdaPlayer::Play()"]),
            ("callers-json", ["callers", "Alpha.LambdaPlayer::Play()", "--output-format", "json"]),
            ("callees-table", ["callees", "Alpha.DescendantCallees::Execute()"]),
            ("callees-json", ["callees", "Alpha.DescendantCallees::Execute()", "--output-format", "json"]),
            ("overrides-table", ["overrides", "Alpha.AsyncOverrideBase::Run()"]),
            ("overrides-json", ["overrides", "Alpha.AsyncOverrideBase::Run()", "--output-format", "json"]),
            ("async-tree", ["async", "tree", "Alpha.AsyncGraph::Start()"]),
            ("async-line", ["async", "tree", "Alpha.AsyncGraph::Start()", "--output-format", "line"]),
            ("async-json", ["async", "tree", "Alpha.AsyncGraph::Start()", "--output-format", "json"]),
            ("caller-tree", ["callers", "tree", "Alpha.CallerGraph::DirectTarget()"]),
            ("caller-mermaid", ["callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--output-format", "mermaid"]),
            ("caller-json", ["callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--output-format", "json"]),
            ("conditions-table", ["conditions"]),
            ("conditions-json", ["conditions", "--output-format", "json"]),
        };

        foreach (var (name, arguments) in cases)
        {
            var standard = await RunAsync([.. arguments, "--db", _fixture.DatabasePath]);
            var outputPath = Path.Combine(outputDirectory, $"{name}.payload");
            var redirected = await RunAsync(
                [.. arguments, "--output-file", outputPath, "--db", _fixture.DatabasePath]);

            Assert.True(
                standard.ExitCode == ExitCodes.Success,
                $"Stdout command failed: {name}{Environment.NewLine}{standard.StandardError}");
            Assert.True(
                redirected.ExitCode == ExitCodes.Success,
                $"File command failed: {name}{Environment.NewLine}{redirected.StandardError}");
            Assert.NotEqual(string.Empty, standard.StandardOutput);
            Assert.Equal(string.Empty, redirected.StandardOutput);
            Assert.Equal(standard.StandardError, redirected.StandardError);
            var fileBytes = File.ReadAllBytes(outputPath);
            Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(standard.StandardOutput), fileBytes);
            Assert.False(fileBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        }

        Assert.Equal(cases.Length, Directory.GetFiles(outputDirectory).Length);
    }

    [Fact]
    public async Task OutputFileAliasesProduceIdenticalPayloadAndPreserveDiagnostics()
    {
        await _fixture.BuildTask;
        var outputDirectory = Path.Combine(_fixture.RootPath, "alias-equivalence");
        Directory.CreateDirectory(outputDirectory);
        var shortPath = Path.Combine(outputDirectory, "short.txt");
        var longPath = Path.Combine(outputDirectory, "long.txt");

        var shortResult = await RunAsync(
            "symbol", "find", "Alpha.AsyncPlayer::Sync()", "-o", shortPath, "--db", _fixture.DatabasePath);
        var longResult = await RunAsync(
            "symbol", "find", "Alpha.AsyncPlayer::Sync()", "--output-file", longPath, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, shortResult.ExitCode);
        Assert.Equal(ExitCodes.Success, longResult.ExitCode);
        Assert.Equal(string.Empty, shortResult.StandardOutput);
        Assert.Equal(string.Empty, longResult.StandardOutput);
        Assert.NotEqual(string.Empty, shortResult.StandardError);
        Assert.Equal(shortResult.StandardError, longResult.StandardError);
        Assert.Equal(File.ReadAllBytes(shortPath), File.ReadAllBytes(longPath));
    }

    [Fact]
    public async Task OutputFileFailuresPreserveExistingFilesAndReportTheCorrectErrorCategory()
    {
        await _fixture.BuildTask;
        var outputDirectory = Path.Combine(_fixture.RootPath, "failure-atomicity");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "result.txt");
        File.WriteAllText(outputPath, "output sentinel");

        var queryFailure = await RunAsync(
            "callers", "tree", "Alpha.Missing::Run()", "--output-file", outputPath, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, queryFailure.ExitCode);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Equal([outputPath], Directory.GetFiles(outputDirectory));

        var missingOutputPath = Path.Combine(_fixture.RootPath, "missing-output-parent", "result.txt");
        var missingParent = await RunAsync(
            "conditions", "--output-file", missingOutputPath, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.AnalysisFailure, missingParent.ExitCode);
        Assert.Equal(string.Empty, missingParent.StandardOutput);
        Assert.StartsWith("Output error: ", missingParent.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingOutputPath)));
    }

    [Fact]
    public async Task OutputFileFormatValidationFailurePreservesSentinelWithoutOpeningTemporaryFile()
    {
        await _fixture.BuildTask;
        var directory = CreateOutputFailureDirectory("format-validation");
        var outputPath = Path.Combine(directory, "result.txt");
        File.WriteAllText(outputPath, "output sentinel");

        var result = await RunAsync(
            "conditions", "--output-format", "xml", "--output-file", outputPath, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.StartsWith("Argument error: ", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Empty(FindOutputTemporaryFiles(directory));
    }

    [Fact]
    public async Task OutputFileDatabaseFailurePreservesSentinelWithoutOpeningTemporaryFile()
    {
        await _fixture.BuildTask;
        var directory = CreateOutputFailureDirectory("database-failure");
        var outputPath = Path.Combine(directory, "result.txt");
        var missingDatabasePath = Path.Combine(directory, "missing.sqlite");
        File.WriteAllText(outputPath, "output sentinel");

        var result = await RunAsync(
            "conditions", "--output-file", outputPath, "--db", missingDatabasePath);

        Assert.Equal(ExitCodes.DatabaseFailure, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.StartsWith("Database error: ", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Empty(FindOutputTemporaryFiles(directory));
    }

    [Fact]
    public async Task OutputFileRequireSingleFailurePreservesSentinelWithoutOpeningTemporaryFile()
    {
        await _fixture.BuildTask;
        var directory = CreateOutputFailureDirectory("require-single");
        var outputPath = Path.Combine(directory, "result.txt");
        File.WriteAllText(outputPath, "output sentinel");

        var result = await RunAsync(
            "symbol", "find", "Alpha.AsyncOverride*::Run()", "--require-single",
            "--output-file", outputPath, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.RequireSingleFailure, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("--require-single expected one symbol", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Empty(FindOutputTemporaryFiles(directory));
    }

    [Fact]
    public async Task OutputFileFinalRecordCancellationPreservesSentinelAndReportsCancellation()
    {
        await _fixture.BuildTask;
        var directory = CreateOutputFailureDirectory("final-write-cancellation");
        var outputPath = Path.Combine(directory, "result.txt");
        File.WriteAllText(outputPath, "output sentinel");
        using var cancellation = new CancellationTokenSource();

        var result = await RunWithOutputDestinationFactoryAsync(
            [
                "symbol", "find", "Alpha.AsyncPlayer::Sync()",
                "--output-file", outputPath,
                "--db", _fixture.DatabasePath,
            ],
            cancellation.Token,
            (requestedOutputPath, databasePath) => OutputDestination.Create(
                requestedOutputPath,
                databasePath,
                stream => new CancelAfterFirstLineTextWriter(stream, cancellation)));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ExitCodes.AnalysisFailure, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Operation was cancelled.", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Empty(FindOutputTemporaryFiles(directory));
    }

    [Fact]
    public async Task OutputFileEqualToDatabaseIsRejectedBeforeTheDatabaseCanChange()
    {
        await _fixture.BuildTask;
        var databaseBefore = File.ReadAllBytes(_fixture.DatabasePath);

        var rejected = await RunAsync(
            "conditions", "--output-file", _fixture.DatabasePath, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, rejected.ExitCode);
        Assert.Contains("must not match the active database path", rejected.StandardError, StringComparison.Ordinal);
        Assert.Equal(databaseBefore, File.ReadAllBytes(_fixture.DatabasePath));

        var stillQueryable = await RunAsync("conditions", "--db", _fixture.DatabasePath);
        Assert.Equal(ExitCodes.Success, stillQueryable.ExitCode);
    }

    [Theory]
    [InlineData("trailing-separator")]
    [InlineData("extended-drive")]
    [InlineData("device-drive")]
    public async Task OutputFileDatabaseAliasesAreRejectedBeforeTheDatabaseCanChange(string aliasKind)
    {
        await _fixture.BuildTask;

        var directory = Path.Combine(_fixture.RootPath, $"database-alias-{aliasKind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "index.sqlite");
        File.Copy(_fixture.DatabasePath, databasePath);
        var databaseBefore = File.ReadAllBytes(databasePath);
        var databaseArgument = aliasKind == "trailing-separator"
            ? databasePath + Path.DirectorySeparatorChar
            : databasePath;
        var outputArgument = aliasKind switch
        {
            "extended-drive" => $@"\\?\{databasePath}",
            "device-drive" => $@"\\.\{databasePath}",
            _ => databasePath,
        };

        var rejected = await RunAsync(
            "conditions", "--output-file", outputArgument, "--db", databaseArgument);

        Assert.Equal(ExitCodes.InvalidArguments, rejected.ExitCode);
        Assert.Equal(string.Empty, rejected.StandardOutput);
        Assert.StartsWith("Argument error: ", rejected.StandardError, StringComparison.Ordinal);
        Assert.Contains("must not match the active database path", rejected.StandardError, StringComparison.Ordinal);
        Assert.Equal(databaseBefore, File.ReadAllBytes(databasePath));
        Assert.Empty(FindOutputTemporaryFiles(directory));

        var stillQueryable = await RunAsync("conditions", "--db", databasePath);
        Assert.Equal(ExitCodes.Success, stillQueryable.ExitCode);
    }

    [Fact]
    public async Task CommandHelpNeverOpensOutputFile()
    {
        var outputPath = Path.Combine(_fixture.RootPath, "missing-help-parent", "help.txt");

        var result = await RunAsync("conditions", "--help", "--output-file", outputPath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Usage: csindex conditions", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(Directory.Exists(Path.GetDirectoryName(outputPath)));
    }

    [Fact]
    public async Task OutputFormatNotFileExtensionSelectsThePayloadFormat()
    {
        await _fixture.BuildTask;
        var outputDirectory = Path.Combine(_fixture.RootPath, "extension-independent");
        Directory.CreateDirectory(outputDirectory);
        var jsonInTextFile = Path.Combine(outputDirectory, "symbols.txt");
        var tableInJsonFile = Path.Combine(outputDirectory, "symbols.json");

        var json = await RunAsync(
            "symbol", "list", "--output-format", "json", "--output-file", jsonInTextFile, "--db", _fixture.DatabasePath);
        var table = await RunAsync(
            "symbol", "list", "--output-file", tableInJsonFile, "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, json.ExitCode);
        Assert.Equal(ExitCodes.Success, table.ExitCode);
        using var document = JsonDocument.Parse(File.ReadAllText(jsonInTextFile));
        Assert.True(document.RootElement.TryGetProperty("symbols", out _));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(File.ReadAllText(tableInJsonFile)));
    }

    [Fact]
    public async Task KindAndAsyncAllMatchOmissionAcrossListAndFindPayloadMatrix()
    {
        await _fixture.BuildTask;
        var cases = new (string Name, string[] Arguments, string? JsonCollection)[]
        {
            ("symbol-find-table", ["symbol", "find", "Alpha.AsyncPlayer::**"], null),
            ("symbol-find-json", ["symbol", "find", "Alpha.AsyncPlayer::**", "--output-format", "json"], "matched"),
            ("symbol-list-table", ["symbol", "list"], null),
            ("symbol-list-json", ["symbol", "list", "--output-format", "json"], "symbols"),
        };
        var explicitAllOptions = new[]
        {
            new[] { "--kind", "all" },
            new[] { "--async-status", "all" },
            new[] { "--kind", "all", "--async-status", "all" },
        };

        foreach (var (name, arguments, jsonCollection) in cases)
        {
            var omitted = await RunAsync([.. arguments, "--db", _fixture.DatabasePath]);
            Assert.True(
                omitted.ExitCode == ExitCodes.Success,
                $"Omitted command failed: {name}{Environment.NewLine}{omitted.StandardError}");
            Assert.NotEqual(string.Empty, omitted.StandardOutput);

            long[]? omittedIds = null;
            if (jsonCollection is not null)
            {
                using var omittedDocument = JsonDocument.Parse(omitted.StandardOutput);
                omittedIds = omittedDocument.RootElement
                    .GetProperty(jsonCollection)
                    .EnumerateArray()
                    .Select(symbol => symbol.GetProperty("id").GetInt64())
                    .ToArray();
                Assert.NotEmpty(omittedIds);
            }

            foreach (var options in explicitAllOptions)
            {
                var explicitAll = await RunAsync(
                    [.. arguments, .. options, "--db", _fixture.DatabasePath]);

                Assert.Equal(omitted.ExitCode, explicitAll.ExitCode);
                Assert.Equal(omitted.StandardOutput, explicitAll.StandardOutput);
                Assert.Equal(omitted.StandardError, explicitAll.StandardError);
                if (jsonCollection is not null)
                {
                    using var explicitDocument = JsonDocument.Parse(explicitAll.StandardOutput);
                    var explicitIds = explicitDocument.RootElement
                        .GetProperty(jsonCollection)
                        .EnumerateArray()
                        .Select(symbol => symbol.GetProperty("id").GetInt64())
                        .ToArray();
                    Assert.Equal(omittedIds, explicitIds);
                }
            }
        }
    }

    [Fact]
    public async Task OverridesKindAllMatchesOmissionForMethodResults()
    {
        await _fixture.BuildTask;

        var omitted = await RunAsync(
            "overrides", "Alpha.AsyncOverrideBase::Run()", "--output-format", "json",
            "--db", _fixture.DatabasePath);
        var explicitAll = await RunAsync(
            "overrides", "Alpha.AsyncOverrideBase::Run()", "--kind", "all", "--output-format", "json",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, omitted.ExitCode);
        Assert.Equal(omitted.ExitCode, explicitAll.ExitCode);
        Assert.Equal(omitted.StandardOutput, explicitAll.StandardOutput);
        Assert.Equal(omitted.StandardError, explicitAll.StandardError);
        using var document = JsonDocument.Parse(explicitAll.StandardOutput);
        var root = document.RootElement;
        Assert.All(root.GetProperty("matched").EnumerateArray(), symbol =>
            Assert.Equal("method", symbol.GetProperty("kind").GetString()));
        Assert.NotEmpty(root.GetProperty("relations").EnumerateArray());
    }

    [Fact]
    public async Task AsyncStatusTaxonomyFiltersEveryDirectRoleAndKeepsNestedOwnersSeparate()
    {
        await _fixture.BuildTask;

        var asyncResult = await RunAsync(
            "symbol", "list", "--async-status", "async", "--output-format", "json",
            "--db", _fixture.DatabasePath);
        var syncResult = await RunAsync(
            "symbol", "list", "--async-status", "sync", "--output-format", "json",
            "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, asyncResult.ExitCode);
        Assert.Equal(ExitCodes.Success, syncResult.ExitCode);
        using var asyncDocument = JsonDocument.Parse(asyncResult.StandardOutput);
        using var syncDocument = JsonDocument.Parse(syncResult.StandardOutput);
        var asyncSymbols = asyncDocument.RootElement.GetProperty("symbols").EnumerateArray()
            .Where(symbol => symbol.GetProperty("displayName").GetString()!
                .StartsWith("Alpha.AsyncStatusCases::", StringComparison.Ordinal))
            .ToArray();
        var syncSymbols = syncDocument.RootElement.GetProperty("symbols").EnumerateArray()
            .Where(symbol => symbol.GetProperty("displayName").GetString()!
                .StartsWith("Alpha.AsyncStatusCases::", StringComparison.Ordinal))
            .ToArray();
        var asyncNames = asyncSymbols
            .Select(symbol => symbol.GetProperty("displayName").GetString()!)
            .ToArray();
        var syncNames = syncSymbols
            .Select(symbol => symbol.GetProperty("displayName").GetString()!)
            .ToArray();

        Assert.All(asyncSymbols, symbol => Assert.True(symbol.GetProperty("isAsync").GetBoolean()));
        Assert.All(syncSymbols, symbol => Assert.False(symbol.GetProperty("isAsync").GetBoolean()));
        Assert.Contains("Alpha.AsyncStatusCases::DeclaredTaskAsync()", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::TaskResult()", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::ValueTaskResult()", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::UniTaskResult()", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::FireAndForget()", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::StreamAsync()", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::OuterWithAsyncLambda().<lambda#1>", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::OuterWithAsyncLocal().NestedLocalAsync()", asyncNames);

        Assert.Contains("Alpha.AsyncStatusCases::SyncSuffixAsync()", syncNames);
        Assert.DoesNotContain("Alpha.AsyncStatusCases::SyncSuffixAsync()", asyncNames);
        Assert.Contains("Alpha.AsyncStatusCases::OuterWithAsyncLambda()", syncNames);
        Assert.Contains("Alpha.AsyncStatusCases::OuterWithAsyncLocal()", syncNames);
        Assert.DoesNotContain("Alpha.AsyncStatusCases::OuterWithAsyncLambda()", asyncNames);
        Assert.DoesNotContain("Alpha.AsyncStatusCases::OuterWithAsyncLocal()", asyncNames);
        Assert.DoesNotContain("Alpha.AsyncStatusCases::OuterWithAsyncLambda().<lambda#1>", syncNames);
        Assert.DoesNotContain("Alpha.AsyncStatusCases::OuterWithAsyncLocal().NestedLocalAsync()", syncNames);
    }

    [Fact]
    public async Task StoredSourceHashSearchAndJsonRemainLosslessAfterTableSanitization()
    {
        await _fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        const string displayName = "Alpha.LosslessSource::LiteralControls()";
        const string sourceMarker = "first\tline";

        var stored = await _fixture.GetStoredSymbolAsync(
            displayName,
            _fixture.PrimaryProfileName,
            cancellationToken);
        var normalizedSource = Assert.IsType<string>(stored.NormalizedSource);
        var normalizedSourceHash = Assert.IsType<byte[]>(stored.NormalizedSourceHash);
        Assert.Contains('\t', normalizedSource);
        Assert.Contains('\n', normalizedSource);
        Assert.Equal(HashUtilities.Sha256(normalizedSource), normalizedSourceHash);

        var searched = await _fixture.Query.SearchSourceAsync(
            new SymbolSelectionRequest(
                Selector: null,
                Conditions:
                [
                    new TypedCondition(ConditionCategory.Include, ConditionSyntax.Glob, sourceMarker),
                ],
                Case: new SymbolCaseOptions(),
                FunctionFilter: new FunctionTargetFilter(null, AsyncStatusFilter.All),
                KindSpecified: false,
                AsyncStatusSpecified: false),
            profileName: _fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var searchedSymbol = Assert.Single(searched.MatchedSymbols, symbol => symbol.Id == stored.Id);
        Assert.Equal(normalizedSource, searchedSymbol.NormalizedSource);
        Assert.Equal(normalizedSourceHash, searchedSymbol.NormalizedSourceHash);

        var json = await RunAsync(
            "source", "show", displayName, "--output-format", "json", "--db", _fixture.DatabasePath);
        Assert.Equal(ExitCodes.Success, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var jsonSymbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        var jsonSource = jsonSymbol.GetProperty("normalizedSource").GetString();
        Assert.Equal(normalizedSource, jsonSource);
        Assert.Contains('\t', jsonSource!);
        Assert.Contains('\n', jsonSource!);
    }

    [Fact]
    public async Task AnalysisCacheVersionForcesReindexOfPriorDeclarationSource()
    {
        await _fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var inputDirectory = Path.Combine(_fixture.RootPath, $"cache-version-{Guid.NewGuid():N}");
        Directory.CreateDirectory(inputDirectory);
        var sourcePath = Path.Combine(inputDirectory, "ArrayHost.cs");
        var databasePath = Path.Combine(inputDirectory, "index.sqlite");
        await File.WriteAllTextAsync(
            sourcePath,
            "namespace Acceptance; public sealed class ArrayHost { public string[] Echo(string[] args) => args; }",
            cancellationToken);
        string[] indexArguments =
        [
            "index", inputDirectory,
            "--mode", "directory",
            "--configuration", "Release",
            "--framework", "net10.0",
            "--runtime", "win-x64",
            "--profile-name", "normalizer-test",
            "--define", "TRACE",
            "--define", "DEBUG",
            "--undefine", "LEGACY",
            "--exclude", "generated",
            "--db", databasePath,
        ];
        const string previousPayload =
            """{"ToolVersion":"0.1.0","SchemaVersion":5,"AnalysisCacheVersion":2,"InputMode":"Directory","Configuration":"Release","TargetFramework":"net10.0","RuntimeIdentifier":"win-x64","ProfileName":"normalizer-test","Defines":["DEBUG","TRACE"],"Undefines":["LEGACY"],"References":[],"Excludes":["generated"],"GeneratedSource":"Physical","UnityEditor":null}""";
        var previousRequestHash = HashUtilities.Sha256(previousPayload);

        var initialIndex = await RunAsync(indexArguments);
        Assert.Equal(ExitCodes.Success, initialIndex.ExitCode);
        Assert.Contains("Cache: rebuilt", initialIndex.StandardError, StringComparison.Ordinal);

        byte[] currentRequestHash;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT request_hash FROM index_runs;";
            currentRequestHash = Assert.IsType<byte[]>(await command.ExecuteScalarAsync(cancellationToken));
            Assert.NotEqual(previousRequestHash, currentRequestHash);

            const string legacySource = "public string[  ]Echo(string[  ]args)=>args;";
            command.CommandText = """
                UPDATE symbol_declarations
                SET normalized_source = $legacy_source,
                    normalized_source_hash = $legacy_hash
                WHERE symbol_id = (
                    SELECT id
                    FROM symbols
                    WHERE namespace_name = 'Acceptance'
                      AND type_display_path = 'ArrayHost'
                      AND executable_display_path = 'Echo(string[])');
                """;
            command.Parameters.AddWithValue("$legacy_source", legacySource);
            command.Parameters.Add("$legacy_hash", SqliteType.Blob).Value = HashUtilities.Sha256(legacySource);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));

            command.Parameters.Clear();
            command.CommandText = "UPDATE index_runs SET request_hash = $previous_request_hash;";
            command.Parameters.Add("$previous_request_hash", SqliteType.Blob).Value = previousRequestHash;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        var reindex = await RunAsync(indexArguments);
        Assert.Equal(ExitCodes.Success, reindex.ExitCode);
        Assert.Contains("Cache: rebuilt", reindex.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Cache: reused", reindex.StandardError, StringComparison.Ordinal);

        var repository = new SqliteIndex(databasePath).CreateQueryRepository();
        var profile = await repository.GetProfileAsync("normalizer-test", cancellationToken);
        var symbols = await repository.FindExecutableSymbolsAsync(
            profile.Id,
            sourceOnly: false,
            cancellationToken);
        var echo = Assert.Single(symbols, symbol =>
            _fixture.FormatPath(symbol) == "Acceptance.ArrayHost::Echo(string[])");
        var echoDeclaration = Assert.Single(await repository.GetPreferredDeclarationsAsync(
            profile.Id,
            [echo.Id],
            includeSourceText: true,
            cancellationToken));
        var correctedSource = Assert.IsType<string>(echoDeclaration.NormalizedSource);
        var correctedHash = Assert.IsType<byte[]>(echoDeclaration.NormalizedSourceHash);
        Assert.Contains("string[]Echo(string[]args)", correctedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("[  ]", correctedSource, StringComparison.Ordinal);
        Assert.Equal(HashUtilities.Sha256(correctedSource), correctedHash);

        await using var verificationConnection = new SqliteConnection(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        await using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "SELECT request_hash FROM index_runs;";
        var reindexedRequestHash = Assert.IsType<byte[]>(
            await verificationCommand.ExecuteScalarAsync(cancellationToken));
        Assert.Equal(currentRequestHash, reindexedRequestHash);
    }

    [Fact]
    public async Task IndexWithCustomDatabaseUsesItsPortableAnchorAndDotCacheKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var layoutRoot = Path.Combine(_fixture.RootPath, $"custom-index-{Guid.NewGuid():N}");
        var inputRoot = Path.Combine(layoutRoot, "input");
        var databasePath = Path.Combine(layoutRoot, "database", "custom.sqlite");
        Directory.CreateDirectory(inputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(inputRoot, "Main.cs"),
            "namespace Portable; public sealed class Worker { public void Run() { } }",
            cancellationToken);
        var arguments = new[]
        {
            "index", inputRoot,
            "--mode", "directory",
            "--profile-name", "custom-paths",
            "--db", databasePath,
        };

        var first = await RunAsync(arguments);

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Contains("Cache: rebuilt", first.StandardError, StringComparison.Ordinal);
        var repository = new SqliteIndex(databasePath).CreateQueryRepository();
        var profile = await repository.GetProfileAsync("custom-paths", cancellationToken);
        var expectedPaths = IndexPathResolver.CreateForIndex(databasePath, inputRoot);
        Assert.Equal(".", profile.InputRoot);
        Assert.Equal(expectedPaths.IndexRootAnchor, profile.IndexRootAnchor);

        var second = await RunAsync(arguments);

        Assert.Equal(ExitCodes.Success, second.ExitCode);
        Assert.Contains("Cache: reused", second.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Cache: rebuilt", second.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CacheHitDisposesThePreparedAnalysis()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inputRoot = Path.Combine(_fixture.RootPath, $"cache-disposal-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(inputRoot, "index.sqlite");
        Directory.CreateDirectory(inputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(inputRoot, "Main.cs"),
            "namespace Portable; public sealed class Worker { public void Run() { } }",
            cancellationToken);
        var arguments = new[]
        {
            "index", inputRoot,
            "--mode", "directory",
            "--db", databasePath,
        };
        Assert.Equal(ExitCodes.Success, (await RunAsync(arguments)).ExitCode);

        PreparedAnalysis? captured = null;
        Program.PreparedAnalysisObserver = prepared => captured = prepared;
        CommandResult result;
        try
        {
            result = await RunAsync(arguments);
        }
        finally
        {
            Program.PreparedAnalysisObserver = null;
        }

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Cache: reused", result.StandardError, StringComparison.Ordinal);
        var disposed = Assert.IsType<PreparedAnalysis>(captured);
        Assert.Throws<ObjectDisposedException>(() => _ = disposed.Input);
    }

    [Fact]
    public async Task PreparedObserverFailureOccursBeforeDatabaseOpenAndPreservesBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inputRoot = Path.Combine(_fixture.RootPath, $"observer-failure-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(inputRoot, "legacy.sqlite");
        Directory.CreateDirectory(inputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(inputRoot, "Main.cs"),
            "namespace Portable; public sealed class Worker { }",
            cancellationToken);
        await CreateLegacyDatabaseAsync(databasePath, version: 4, cancellationToken);
        var before = await File.ReadAllBytesAsync(databasePath, cancellationToken);
        Program.PreparedAnalysisObserver = _ => throw new InputResolutionException("prepared observer sentinel");
        CommandResult result;
        try
        {
            result = await RunAsync(
                "index", inputRoot,
                "--mode", "directory",
                "--db", databasePath);
        }
        finally
        {
            Program.PreparedAnalysisObserver = null;
        }

        Assert.Equal(ExitCodes.AnalysisFailure, result.ExitCode);
        Assert.Contains("prepared observer sentinel", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath, cancellationToken));
        await AssertLegacySentinelAsync(databasePath, version: 4, cancellationToken);
    }

    [Fact]
    public async Task CrossDrivePreflightLeavesExistingDatabaseUnopenedAndByteIdentical()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inputRoot = Path.Combine(
            Directory.GetCurrentDirectory(),
            $".task5-cross-drive-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(_fixture.RootPath, $"cross-drive-{Guid.NewGuid():N}.sqlite");
        Assert.False(
            string.Equals(
                Path.GetPathRoot(inputRoot),
                Path.GetPathRoot(databasePath),
                StringComparison.OrdinalIgnoreCase),
            "The preflight fixture requires different input and database drives.");
        Directory.CreateDirectory(inputRoot);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(inputRoot, "Main.cs"),
                "namespace Portable; public sealed class Worker { }",
                cancellationToken);
            await CreateLegacyDatabaseAsync(databasePath, version: 4, cancellationToken);
            var before = await File.ReadAllBytesAsync(databasePath, cancellationToken);

            var result = await RunAsync(
                "index", inputRoot,
                "--mode", "directory",
                "--db", databasePath);

            Assert.Equal(ExitCodes.AnalysisFailure, result.ExitCode);
            Assert.StartsWith("Input error: ", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(databasePath, cancellationToken));
            await AssertLegacySentinelAsync(databasePath, version: 4, cancellationToken);
        }
        finally
        {
            Directory.Delete(inputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RebuildDoesNotBypassSchemaFourRejectionOrModifyTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var inputRoot = Path.Combine(_fixture.RootPath, $"schema-four-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(inputRoot, "legacy.sqlite");
        Directory.CreateDirectory(inputRoot);
        await File.WriteAllTextAsync(
            Path.Combine(inputRoot, "Main.cs"),
            "namespace Legacy; public sealed class Worker { }",
            cancellationToken);
        await CreateLegacyDatabaseAsync(databasePath, version: 4, cancellationToken);
        var before = await File.ReadAllBytesAsync(databasePath, cancellationToken);

        var result = await RunAsync(
            "index", inputRoot,
            "--mode", "directory",
            "--rebuild",
            "--db", databasePath);

        Assert.Equal(ExitCodes.DatabaseFailure, result.ExitCode);
        Assert.Contains("Unsupported database schema version 4", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("database was not modified", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Delete or rename the old database", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("choose a new --db path", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("run csindex index explicitly", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath, cancellationToken));
        await AssertLegacySentinelAsync(databasePath, version: 4, cancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LegacySchemaQueryCommandsRejectWithGuidanceAndPreserveBytes(int version)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePath = Path.Combine(
            _fixture.RootPath,
            $"legacy-query-v{version}-{Guid.NewGuid():N}.sqlite");
        await CreateLegacyDatabaseAsync(databasePath, version, cancellationToken);
        var before = await File.ReadAllBytesAsync(databasePath, cancellationToken);

        var result = await RunAsync("conditions", "--db", databasePath);

        Assert.Equal(ExitCodes.DatabaseFailure, result.ExitCode);
        Assert.Contains($"Unsupported database schema version {version}", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("database was not modified", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Delete or rename the old database", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("choose a new --db path", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("run csindex index explicitly", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath, cancellationToken));
        await AssertLegacySentinelAsync(databasePath, version, cancellationToken);
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
            ["symbol", "find", "Alpha.LambdaPlayer::Execute().<lambda#1>"],
            ["symbol", "list"],
            ["source", "show", "Alpha.LambdaPlayer::Execute().<lambda#1>"],
            ["source", "search", "--include", "LambdaTarget()"],
            ["definition", "Alpha.LambdaPlayer::Execute().<lambda#1>"],
            ["definition", "--at", definitionAt],
            ["references", "Alpha.LambdaPlayer::Execute().<lambda#1>"],
            ["callers", "Alpha.LambdaPlayer::Execute().<lambda#1>"],
            ["callees", "Alpha.LambdaPlayer::Execute().<lambda#1>"],
            ["async", "tree", "Alpha.AsyncGraph::ALambdaPathOwner().<lambda#1>"],
            ["callers", "tree", "Alpha.AsyncGraph::ALambdaPathOwner().<lambda#1>"],
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
            "symbol", "find", "Alpha.AsyncPlayer::**", "--output-format", "json", "--db", _fixture.DatabasePath);
        var explicitAll = await RunAsync(
            "symbol", "find", "Alpha.AsyncPlayer::**", "--kind", "all", "--async-status", "all",
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
            "Alpha.AClass::Play(), Alpha.AClass::Play(string)" + Environment.NewLine,
            ambiguousRoot.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, missingRoot.ExitCode);
        Assert.Equal(
            "Query error: No source-backed executable matches graph query: Alpha.Missing::Run()" + Environment.NewLine,
            missingRoot.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, unsupportedOption.ExitCode);
        Assert.Contains("Unknown option(s): --include", unsupportedOption.StandardError);
    }

    [Fact]
    public async Task SymbolFindSupportsLambdaAndComponentGlobsAndSourceFiltering()
    {
        await _fixture.BuildTask;

        var lambda = await RunAsync(
            "symbol", "find", "**::**.<lambda#1>", "--kind", "lambda", "--output-format", "json", "--db", _fixture.DatabasePath);
        var components = await RunAsync(
            "symbol", "find", "--namespace", "Tokyo", "--type", "Gamer", "--method", "Play", "--output-format", "json",
            "--db", _fixture.DatabasePath);
        var sourceFiltered = await RunAsync(
            "symbol", "find", "Tokyo.*::Play", "--include", "PrintVar(", "--exclude", "BlockedMarker(", "--show-source",
            "--output-format", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, lambda.ExitCode);
        using var lambdaDocument = JsonDocument.Parse(lambda.StandardOutput);
        Assert.Contains(lambdaDocument.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("displayName").GetString()!.Contains(".<lambda#1>", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Success, components.ExitCode);
        using var componentsDocument = JsonDocument.Parse(components.StandardOutput);
        Assert.Equal(
            ["Tokyo.Gamer::Play()", "Tokyo.Gamer::Play(string)"],
            componentsDocument.RootElement.GetProperty("matched").EnumerateArray()
                .Select(symbol => symbol.GetProperty("displayName").GetString()));

        Assert.Equal(ExitCodes.Success, sourceFiltered.ExitCode);
        using var sourceDocument = JsonDocument.Parse(sourceFiltered.StandardOutput);
        Assert.NotEmpty(sourceDocument.RootElement.GetProperty("matched").EnumerateArray());
        Assert.All(sourceDocument.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            Assert.True(symbol.TryGetProperty("normalizedSource", out _)));

    }

    [Fact]
    public async Task SymbolFindAndSourceCommandsRejectRemovedGlobalSwitchesAndMissingName()
    {
        await _fixture.BuildTask;

        var removedRegex = await RunAsync(
            "symbol", "find", "Tokyo.*::Play", "--regex", "--db", _fixture.DatabasePath);
        var removedIgnoreCase = await RunAsync(
            "symbol", "find", "TOKYO.GAMER::PLAY", "--ignore-case", "--db", _fixture.DatabasePath);
        var removedSourceIgnoreCase = await RunAsync(
            "source", "search", "--include", "PrintVar(", "--ignore-case", "--db", _fixture.DatabasePath);
        var noNameCondition = await RunAsync("symbol", "find", "--include", "PrintVar(", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.InvalidArguments, removedRegex.ExitCode);
        Assert.Contains("Unknown option(s): --regex", removedRegex.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, removedIgnoreCase.ExitCode);
        Assert.Contains("Unknown option(s): --ignore-case", removedIgnoreCase.StandardError);
        Assert.Equal(ExitCodes.InvalidArguments, removedSourceIgnoreCase.ExitCode);
        Assert.Contains("Unknown option(s): --ignore-case", removedSourceIgnoreCase.StandardError);
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

        const string lambdaQuery = "Tokyo.LambdaSearch::Function().<lambda#1>";
        const string lambdaDisplay = "Tokyo.LambdaSearch::Function().<lambda#1>";
        var table = await RunAsync(
            "source", "show", lambdaQuery, "--source-layout", "multi-line", "--db", _fixture.DatabasePath);
        var json = await RunAsync(
            "source", "show", lambdaQuery, "--output-format", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, table.ExitCode);
        Assert.Contains(lambdaDisplay, table.StandardOutput);
        Assert.Contains("source: ()=>LambdaMarker(\"first\")", table.StandardOutput);

        Assert.Equal(ExitCodes.Success, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var lambda = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal(lambdaDisplay, lambda.GetProperty("displayName").GetString());
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
    public void GetPhysicalLinesPreservesInternalBlankLinesAndTrimsOnlyOneTrailingTerminator()
    {
        Assert.Equal(["first", "", "second"], GetPhysicalLines("first\r\n\r\nsecond\r\n"));
        Assert.Equal(["first", "", "second"], GetPhysicalLines("first\n\nsecond\n"));
        Assert.Equal(["first", ""], GetPhysicalLines("first\n\n"));
        Assert.Equal([""], GetPhysicalLines("\n"));
        Assert.Empty(GetPhysicalLines(string.Empty));
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
            .Contains(".<lambda#1>", StringComparison.Ordinal));
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
        Assert.Contains("-o <path> | --output-file <path>", help);
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
        Assert.Contains("-o <path> | --output-file <path>", result.StandardOutput);
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
        var indexOutput = await RunAsync(
            "index",
            ".",
            "--output-file",
            Path.Combine(_fixture.RootPath, "index-output.txt"));

        Assert.Equal(ExitCodes.Success, index.ExitCode);
        Assert.DoesNotContain("--kind", index.StandardOutput);
        Assert.DoesNotContain("--async-status", index.StandardOutput);
        Assert.DoesNotContain("--output-format", index.StandardOutput);
        Assert.DoesNotContain("--output-file", index.StandardOutput);
        Assert.Equal(ExitCodes.InvalidArguments, indexOutput.ExitCode);
        Assert.Contains("Unknown option(s): --output-file", indexOutput.StandardError);
        Assert.Equal(ExitCodes.Success, conditions.ExitCode);
        Assert.DoesNotContain("--kind", conditions.StandardOutput);
        Assert.DoesNotContain("--async-status", conditions.StandardOutput);
        Assert.Contains("--output-format table|json", conditions.StandardOutput);
        Assert.Contains("-o <path> | --output-file <path>", conditions.StandardOutput);
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
              -o <path> | --output-file <path>  Write the result payload to a file
              --require-single  Fail unless the search matches exactly one symbol
              --short-names  Shorten namespaces in displayed symbol names
              --namespace <pattern>  Namespace component filter
              --type <pattern>  Type component filter
              --method <pattern>  Method component filter
              --file <pattern>  Stored source path filter
              --kind all|method|lambda  Limit function targets by kind (default: all)
              --async-status all|async|sync  Limit function targets by direct async status (default: all)
              --include <text>  Require normalized source text (repeatable)
              --exclude <text>  Reject normalized source text (repeatable)
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
              -o <path> | --output-file <path>  Write the result payload to a file
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
              -o <path> | --output-file <path>  Write the result payload to a file
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
              -o <path> | --output-file <path>  Write the result payload to a file
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
              -o <path> | --output-file <path>  Write the result payload to a file
              --include <text>  Require normalized source text (repeatable)
              --exclude <text>  Reject normalized source text (repeatable)
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

        var result = await RunAsync(
            "symbol", "list", "--kind", "method", "--output-format", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
        Assert.Contains(symbols, symbol => symbol.GetProperty("displayName").GetString() == "Alpha.AClass::Play()");
        Assert.All(symbols, symbol => Assert.Equal("method", symbol.GetProperty("kind").GetString()));
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
    public async Task CalleesJsonDropsResolvedSourceTokensAndPresentsDanglingToken()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "callees", "Alpha.DistinctCaller::Execute", "--output-format", "json", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var calls = document.RootElement.GetProperty("calls").EnumerateArray().ToArray();
        var dangling = Assert.Single(
            calls,
            call => call.GetProperty("callee").GetString() == "new AClass()");
        Assert.Equal("new AClass()", dangling.GetProperty("unresolvedName").GetString());

        var resolved = calls
            .Where(call => call.GetProperty("callee").GetString() != "new AClass()")
            .ToArray();
        Assert.Equal(2, resolved.Length);
        Assert.All(resolved, call =>
            Assert.Equal(JsonValueKind.Null, call.GetProperty("unresolvedName").ValueKind));
    }

    [Fact]
    public async Task CalleesPresentDanglingImplicitConstructorByCapturedSourceToken()
    {
        await _fixture.BuildTask;

        var result = await RunAsync(
            "callees", "Alpha.DistinctCaller::Execute", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(
            "new AClass()",
            result.StandardOutput,
            StringComparison.Ordinal);
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
    [InlineData("definition", "Alpha.AClass::Play()", "AClass::Play()", "Alpha.AClass::")]
    [InlineData("references", "Alpha.AClass::Play()", "AClass::Execute() -> AClass::Play()", "Alpha.AClass::")]
    [InlineData("callers", "Alpha.AClass::Play()", "AClass::Execute() -> AClass::Play()", "Alpha.AClass::")]
    [InlineData("callees", "Alpha.DescendantCallees::Execute()", "DescendantCallees::Execute() -> DescendantCallees::DirectCall()", "Alpha.DescendantCallees::")]
    [InlineData("overrides", "Alpha.BaseClass::Run()", "XClass::Run() -> BaseClass::Run()", "Alpha.BaseClass::")]
    public async Task QueryCommandsShortNamesFormatHumanFacingNames(
        string command,
        string query,
        string expected,
        string fullyQualifiedOwner)
    {
        await _fixture.BuildTask;

        var result = await RunAsync(command, query, "--short-names", "--db", _fixture.DatabasePath);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(expected, result.StandardOutput);
        Assert.DoesNotContain(fullyQualifiedOwner, result.StandardOutput);
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
    [InlineData("**::**.<lambda#1>")]
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
        var cancellationToken = TestContext.Current.CancellationToken;
        var nameOnly = await _fixture.Query.FindSymbolsAsync(
            "Alpha.Pianist::Play()",
            filter: default,
            profileName: _fixture.PrimaryProfileName,
            sourceOnly: false,
            includeOverrides: true,
            includeSourceText: false,
            cancellationToken);
        Assert.All(nameOnly.MatchedSymbols, symbol => Assert.Null(symbol.PreferredDeclaration));

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
    [InlineData(
        "Invalid symbol path: expected exactly one or two top-level '::' separators",
        "symbol", "find", "Alpha.IPlayable")]
    [InlineData(
        "--include-overrides requires a method query",
        "definition", "--at", "Source.cs:1:1")]
    public async Task IncludeOverridesRejectsNonMethodQueries(
        string expectedMessage,
        params string[] args)
    {
        await _fixture.BuildTask;

        var result = await RunAsync(args.Concat(["--include-overrides", "--db", _fixture.DatabasePath]).ToArray());

        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Contains(expectedMessage, result.StandardError);
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
            args.Add("Alpha.LocalPlayer::Execute().Local()");
            if (includeOverrides)
            {
                args.Add("--include-overrides");
            }

            args.AddRange(["--db", _fixture.DatabasePath]);
            var result = await RunAsync(args.ToArray());

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Contains("Alpha.LocalPlayer::Execute().Local()", result.StandardOutput);
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
            if (!line.StartsWith("  --", StringComparison.Ordinal) &&
                !line.StartsWith("  -o ", StringComparison.Ordinal))
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

    private static string[] FindOutputTemporaryFiles(string directory) =>
        Directory.GetFiles(directory, ".*.tmp", SearchOption.TopDirectoryOnly);

    private string CreateOutputFailureDirectory(string name)
    {
        var directory = Path.Combine(_fixture.RootPath, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task AssertCommandSucceedsAsync(string[] command, params string[] options)
    {
        var args = command.Concat(options).Concat(["--db", _fixture.DatabasePath]).ToArray();
        var result = await RunAsync(args);

        Assert.True(
            result.ExitCode == ExitCodes.Success,
            $"Command failed: {string.Join(' ', args)}{Environment.NewLine}{result.StandardError}");
    }

    private static async Task CreateLegacyDatabaseAsync(
        string databasePath,
        int version,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE;";
        await command.ExecuteScalarAsync(cancellationToken);
        command.CommandText = $"""
            CREATE TABLE schema_info(version INTEGER NOT NULL);
            INSERT INTO schema_info(version) VALUES ({version});
            CREATE TABLE legacy_sentinel(value TEXT NOT NULL);
            INSERT INTO legacy_sentinel(value) VALUES ('preserve-me');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertLegacySentinelAsync(
        string databasePath,
        int version,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version,
                   (SELECT value FROM legacy_sentinel LIMIT 1)
            FROM schema_info;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(version, reader.GetInt32(0));
        Assert.Equal("preserve-me", reader.GetString(1));
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
        CancellationToken cancellationToken,
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
            var exitCode = await Program.RunAsync(args, cancellationToken, outputDestinationFactory);
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed class CancelAfterFirstLineTextWriter(
        Stream stream,
        CancellationTokenSource cancellation) : TextWriter
    {
        private readonly StreamWriter _writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);

        public override Encoding Encoding => _writer.Encoding;

        public override void WriteLine(string? value)
        {
            _writer.WriteLine(value);
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

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
