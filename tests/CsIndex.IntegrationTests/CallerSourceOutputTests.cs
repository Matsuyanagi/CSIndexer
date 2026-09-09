using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class CallerSourceOutputTests
{
    [Fact]
    public async Task FindCallersShowSourceReturnsIndependentNestedCallSlices()
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        var result = await fixture.FindCallersAsync(showSource: true);

        Assert.True(result.ShowSource);
        Assert.Equal(
            ["A(10+20)", "A(f)", "B(A(f),A(10+20))"],
            result.Calls.Select(call => call.NormalizedSource).Order(StringComparer.Ordinal));
        Assert.All(result.Calls, call =>
        {
            Assert.DoesNotContain(";", call.NormalizedSource!, StringComparison.Ordinal);
            Assert.DoesNotContain("日本語", call.NormalizedSource!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FindCallersShowSourceHydratesFromDatabaseAfterSourceReplacement()
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.SourcePath,
            "class ReplacedAfterIndex { }",
            TestContext.Current.CancellationToken);

        var payloadIds = new List<long>();
        var repository = fixture.CreateRepository();
        repository.NormalizedSourcePayloadReadObserver = payloadIds.Add;
        try
        {
            var result = await fixture.FindCallersAsync(repository, showSource: true);

            Assert.Equal(
                ["A(10+20)", "A(f)", "B(A(f),A(10+20))"],
                result.Calls.Select(call => call.NormalizedSource).Order(StringComparer.Ordinal));
            Assert.Single(payloadIds);
        }
        finally
        {
            repository.NormalizedSourcePayloadReadObserver = null;
        }
    }

    [Fact]
    public async Task FindCallersWithoutShowSourceDoesNotReadPayloadOrAttachText()
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        var payloadIds = new List<long>();
        var repository = fixture.CreateRepository();
        repository.NormalizedSourcePayloadReadObserver = payloadIds.Add;
        try
        {
            var result = await fixture.FindCallersAsync(repository, showSource: false);

            Assert.False(result.ShowSource);
            Assert.Equal(3, result.Calls.Count);
            Assert.Empty(payloadIds);
            Assert.All(result.Calls, call => Assert.Null(call.NormalizedSource));
        }
        finally
        {
            repository.NormalizedSourcePayloadReadObserver = null;
        }
    }

    [Fact]
    public async Task WriteCallsTableAppendsSanitizedNormalizedSourceOnlyWhenFlagged()
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        var result = await fixture.FindCallersAsync(showSource: true);
        var call = Assert.Single(result.Calls, call => call.NormalizedSource == "A(f)");
        const string rawSource = "A(\tfirst\r\nsecond\rthird\ne\u0085f\u2028g\u2029h)";
        var flagged = result with
        {
            Calls = [call with { NormalizedSource = rawSource }],
            EffectiveCallers = [],
            ShowSource = true,
        };

        var output = CaptureText(() => CreateFormatter(fixture, "table").WriteCalls(
            flagged,
            "caller call site(s)",
            TestContext.Current.CancellationToken));

        Assert.Contains("\tA( first second third e f g h)", output, StringComparison.Ordinal);
        Assert.Equal(2, output.ReplaceLineEndings("\n").Count(character => character == '\n'));
        Assert.DoesNotContain("\tfirst", output, StringComparison.Ordinal);
        Assert.EndsWith(
            "\tA( first second third e f g h)" + Environment.NewLine,
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteCallsJsonPreservesExactNormalizedSourceAndOmitsItWithoutFlag()
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        var result = await fixture.FindCallersAsync(showSource: true);
        var call = Assert.Single(result.Calls, call => call.NormalizedSource == "A(f)");
        const string rawSource = "A(\"line\r\n\t\u0085\u2028\u2029\")";

        var flagged = result with
        {
            Calls = [call with { NormalizedSource = rawSource }],
            EffectiveCallers = [],
            ShowSource = true,
        };
        using var flaggedDocument = JsonDocument.Parse(CaptureText(() => CreateFormatter(fixture, "json").WriteCalls(
            flagged,
            "caller call site(s)",
            TestContext.Current.CancellationToken)));
        var flaggedCall = Assert.Single(flaggedDocument.RootElement.GetProperty("calls").EnumerateArray());
        Assert.Equal(rawSource, flaggedCall.GetProperty("normalizedSource").GetString());
        Assert.Equal(
            [
                "id", "caller", "callee", "referenceKind", "dispatchKind", "resolutionStatus",
                "resolutionReason", "asyncUsageKind", "location", "isGenerated", "unresolvedName",
                "receiverTypeKey", "normalizedSource",
            ],
            flaggedCall.EnumerateObject().Select(property => property.Name));

        var unflagged = flagged with { ShowSource = false };
        using var unflaggedDocument = JsonDocument.Parse(CaptureText(() => CreateFormatter(fixture, "json").WriteCalls(
            unflagged,
            "caller call site(s)",
            TestContext.Current.CancellationToken)));
        Assert.False(
            Assert.Single(unflaggedDocument.RootElement.GetProperty("calls").EnumerateArray())
                .TryGetProperty("normalizedSource", out _));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("json")]
    public async Task WriteCallsShowSourceRejectsMissingHydratedTextWithCallAndDocumentIds(string format)
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        var result = await fixture.FindCallersAsync(showSource: true);
        var call = Assert.Single(result.Calls, call => call.NormalizedSource == "A(f)");
        var flagged = result with
        {
            Calls = [call with { NormalizedSource = null }],
            EffectiveCallers = [],
            ShowSource = true,
        };

        var exception = Assert.Throws<IndexDatabaseException>(() => CaptureText(() => CreateFormatter(fixture, format).WriteCalls(
            flagged,
            "caller call site(s)",
            TestContext.Current.CancellationToken)));

        Assert.Contains($"call ID {call.Id}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"document ID {call.DocumentId}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("json")]
    public async Task WriteCallsChecksCancellationBeforeFlaggedProjection(string format)
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        var result = await fixture.FindCallersAsync(showSource: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CaptureText(() => CreateFormatter(fixture, format).WriteCalls(
            result,
            "caller call site(s)",
            cancellation.Token)));
    }

    [Fact]
    public async Task NoFlagTableAndJsonRemainByteIdenticalToTask3BaseSnapshots()
    {
        await using var fixture = await ExactCallerFixture.CreateAsync();
        var result = CreateDeterministicSnapshotResult(await fixture.FindCallersAsync(showSource: false));
        var table = CaptureText(() => CreateFormatter(fixture, "table").WriteCalls(
            result,
            "caller call site(s)",
            TestContext.Current.CancellationToken));
        var json = CaptureText(() => CreateFormatter(fixture, "json").WriteCalls(
            result,
            "caller call site(s)",
            TestContext.Current.CancellationToken));

        const string expectedTable = """
            1 matched symbol(s); 1 caller call site(s):
              Calls.cs:13:17  Calls.Targets::Caller(float) -> Calls.Targets::A(int) [Invocation, Resolved] [None]
            """;
        const string expectedJson = """
            {
              "profile": "snapshot-profile",
              "matched": [
                {
                  "id": 1,
                  "stableKey": "snapshot:1",
                  "kind": "method",
                  "displayName": "Calls.Targets::A(int)",
                  "signature": "public static int Calls.Targets::A(int)",
                  "fullyQualifiedName": "Calls.Targets::A(int)",
                  "namespaceName": "Calls",
                  "typeSimpleName": "Targets",
                  "parameters": [
                    "int"
                  ],
                  "location": {
                    "path": "Calls.cs",
                    "line": 6,
                    "column": 9,
                    "offset": 117
                  },
                  "isGenerated": false,
                  "assemblyName": "snapshot-assembly",
                  "accessibility": "public",
                  "isStatic": true,
                  "isAsync": false,
                  "asyncRole": "None",
                  "isAsyncInvolved": false,
                  "asyncInvolvementDepth": null,
                  "returnType": "int",
                  "methodKind": 10,
                  "sourceAvailable": true
                }
              ],
              "calls": [
                {
                  "id": 3,
                  "caller": "Calls.Targets::Caller(float)",
                  "callee": "Calls.Targets::A(int)",
                  "referenceKind": "Invocation",
                  "dispatchKind": "Static",
                  "resolutionStatus": "Resolved",
                  "resolutionReason": "None",
                  "asyncUsageKind": "None",
                  "location": {
                    "path": "Calls.cs",
                    "line": 13,
                    "column": 17,
                    "offset": 333
                  },
                  "isGenerated": false,
                  "unresolvedName": null,
                  "receiverTypeKey": ""
                }
              ],
              "callers": [],
              "possibleRuntimeTargets": []
            }
            """;
        Assert.Equal(
            expectedTable.Replace("\n", Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine,
            table);
        Assert.Equal(
            expectedJson.Replace("\n", Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine,
            json);
    }

    private static CallResult CreateDeterministicSnapshotResult(CallResult result)
    {
        const string assemblyName = "snapshot-assembly";
        Assert.Equal(3, result.Calls.Count);
        var callee = result.SymbolsById.Values.Single(symbol =>
            symbol.Name == "A" &&
            symbol.Parameters.Count == 1 &&
            (symbol.Parameters[0].TypeDisplay == "int" ||
                symbol.Parameters[0].TypeKey is "int" or "System.Int32"));
        var call = result.Calls.Single(candidate => candidate.CalleeSymbolId == callee.Id);
        var symbolIds = new[] { call.CallerSymbolId, callee.Id };
        var symbolsById = result.SymbolsById
            .Where(pair => symbolIds.Contains(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    StableKey = $"snapshot:{pair.Key}",
                    AssemblyName = assemblyName,
                });
        var selection = result.Selection with
        {
            Profile = result.Selection.Profile with { Name = "snapshot-profile" },
            Roots =
            [
                result.Selection.Roots
                    .Single(root => root.Symbol.Id == callee.Id) with { Symbol = symbolsById[callee.Id] },
            ],
        };
        return new CallResult(selection, [call], [], [], symbolsById, false);
    }

    private static OutputFormatter CreateFormatter(ExactCallerFixture fixture, string format) => new(
        format,
        new SymbolPathFormatOptions(),
        IndexPathResolver.CreateForIndex(fixture.DatabasePath, fixture.RootPath),
        PathDisplayStyle.Relative);

    private static string CaptureText(Action action)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            action();
            return output.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private sealed class ExactCallerFixture : IAsyncDisposable
    {
        private const string Source = """
            namespace Calls
            {
                public static class Targets
                {
                    public static float A(float value) => value;
                    public static int A(int value) => value;
                    public static void B(float first, int second) { }

                    public static void Caller(float f)
                    {
                        B(
                            /* 日本語 */ A(f),
                            A(10 + 20));
                    }
                }
            }
            """;

        private ExactCallerFixture(string rootPath, string sourcePath, string databasePath)
        {
            RootPath = rootPath;
            SourcePath = sourcePath;
            DatabasePath = databasePath;
        }

        public string RootPath { get; }
        public string SourcePath { get; }
        public string DatabasePath { get; }

        public static async Task<ExactCallerFixture> CreateAsync()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "csindex-caller-source-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var sourcePath = Path.Combine(rootPath, "Calls.cs");
            await File.WriteAllTextAsync(sourcePath, Source);
            var databasePath = Path.Combine(rootPath, ".csindex", "index.sqlite");
            var fixture = new ExactCallerFixture(rootPath, sourcePath, databasePath);

            var options = new IndexOptions
            {
                InputPath = rootPath,
                ForcedMode = InputMode.Directory,
            };
            var coordinator = AnalysisCoordinator.CreateDefault();
            var input = coordinator.ResolveInput(options);
            var fingerprint = await coordinator.BuildInputFingerprintAsync(input, options, CancellationToken.None);
            var requestHash = RequestHasher.Build(input, options);
            var result = await coordinator.AnalyzeAsync(input, options, fingerprint, requestHash, CancellationToken.None);
            await new SqliteIndex(databasePath).SaveAsync(result.Snapshot, CancellationToken.None);
            return fixture;
        }

        public QueryRepository CreateRepository() => new SqliteIndex(DatabasePath).CreateQueryRepository();

        public async Task<CallResult> FindCallersAsync(bool showSource) =>
            await FindCallersAsync(CreateRepository(), showSource);

        public async Task<CallResult> FindCallersAsync(QueryRepository repository, bool showSource)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var profile = await repository.GetProfileAsync(null, cancellationToken);
            var symbols = await repository.FindExecutableSymbolsAsync(profile.Id, sourceOnly: true, cancellationToken);
            var roots = symbols
                .Where(symbol => symbol.NamespaceName == "Calls" &&
                    symbol.TypeSimpleName == "Targets" &&
                    symbol.Name is "A" or "B")
                .Select(symbol => new ResolvedLogicalRoot(symbol, []))
                .ToArray();
            Assert.Equal(3, roots.Length);
            var selection = new RootSelection(profile, roots);
            return await new SemanticQueryService(repository).FindCallersAsync(
                selection,
                GeneratedFilter.Include,
                DispatchSearchMode.Static,
                CallerScope.Direct,
                showSource: showSource,
                cancellationToken: cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
