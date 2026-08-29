using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsIndex.Cli;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class PortablePathOutputTests(SemanticIndexFixture fixture)
    : IClassFixture<SemanticIndexFixture>
{
    public static TheoryData<string, string[]> TextPathConsumers => new()
    {
        {
            "symbol-find-table",
            ["symbol", "find", "Alpha.AsyncPlayer::Sync()"]
        },
        {
            "definition-table",
            ["definition", "Alpha.AsyncPlayer::Sync()"]
        },
        {
            "references-table",
            ["references", "Alpha.LambdaPlayer::Play()"]
        },
        {
            "source-show-multi-line",
            [
                "source", "show", "Alpha.AsyncPlayer::Sync()",
                "--source-layout", "multi-line",
            ]
        },
        {
            "source-search-table",
            ["source", "search", "--method-literal", "UniTaskResult()"]
        },
    };

    public static TheoryData<string, string[]> JsonPathConsumers => new()
    {
        {
            "symbol-find-json",
            ["symbol", "find", "Alpha.AsyncPlayer::Sync()", "--output-format", "json"]
        },
        {
            "definition-json",
            ["definition", "Alpha.AsyncPlayer::Sync()", "--output-format", "json"]
        },
        {
            "references-json",
            ["references", "Alpha.LambdaPlayer::Play()", "--output-format", "json"]
        },
        {
            "source-search-json",
            [
                "source", "search", "--method-literal", "UniTaskResult()",
                "--output-format", "json",
            ]
        },
        {
            "async-graph-json",
            ["async", "tree", "Alpha.AsyncGraph::Start()", "--output-format", "json"]
        },
        {
            "caller-graph-json",
            ["callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--output-format", "json"]
        },
    };

    [Theory]
    [MemberData(nameof(TextPathConsumers))]
    public async Task TextConsumersHonorMovedBaseAndPathStyle(
        string caseName,
        string[] arguments)
    {
        await fixture.BuildTask;
        var (firstBase, secondBase) = CreateCopiedRoots();

        var defaultAbsolute = await RunWithPathAsync(arguments, firstBase, pathStyle: null);
        var firstAbsolute = await RunWithPathAsync(arguments, firstBase, "absolute");
        var secondAbsolute = await RunWithPathAsync(arguments, secondBase, "absolute");
        var firstRelative = await RunWithPathAsync(arguments, firstBase, "relative");
        var secondRelative = await RunWithPathAsync(arguments, secondBase, "relative");

        AssertSuccess(caseName, defaultAbsolute, firstAbsolute, secondAbsolute, firstRelative, secondRelative);
        Assert.Equal(firstAbsolute.StandardOutput, defaultAbsolute.StandardOutput);
        Assert.Equal(firstAbsolute.StandardError, defaultAbsolute.StandardError);
        Assert.Contains(Path.Combine(firstBase, "Main.cs"), firstAbsolute.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(secondBase, "Main.cs"), secondAbsolute.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ReplaceBase(firstAbsolute.StandardOutput, firstBase),
            ReplaceBase(secondAbsolute.StandardOutput, secondBase));
        Assert.Equal(firstAbsolute.StandardError, secondAbsolute.StandardError);
        Assert.Equal(
            Encoding.UTF8.GetBytes(firstRelative.StandardOutput),
            Encoding.UTF8.GetBytes(secondRelative.StandardOutput));
        Assert.Equal(firstRelative.StandardError, secondRelative.StandardError);
        Assert.Contains("Main.cs", firstRelative.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(firstBase, firstRelative.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondBase, secondRelative.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(JsonPathConsumers))]
    public async Task JsonLocationsHonorMovedBaseAndPathStyleWithoutChangingSemanticPayload(
        string caseName,
        string[] arguments)
    {
        await fixture.BuildTask;
        var (firstBase, secondBase) = CreateCopiedRoots();

        var firstAbsolute = await RunWithPathAsync(arguments, firstBase, "absolute");
        var secondAbsolute = await RunWithPathAsync(arguments, secondBase, "absolute");
        var firstRelative = await RunWithPathAsync(arguments, firstBase, "relative");
        var secondRelative = await RunWithPathAsync(arguments, secondBase, "relative");

        AssertSuccess(caseName, firstAbsolute, secondAbsolute, firstRelative, secondRelative);
        AssertPaths(firstAbsolute.StandardOutput, Path.Combine(firstBase, "Main.cs"));
        AssertPaths(secondAbsolute.StandardOutput, Path.Combine(secondBase, "Main.cs"));
        AssertPaths(firstRelative.StandardOutput, "Main.cs");
        AssertPaths(secondRelative.StandardOutput, "Main.cs");

        var normalized = new[]
        {
            NormalizeLocationPaths(firstAbsolute.StandardOutput),
            NormalizeLocationPaths(secondAbsolute.StandardOutput),
            NormalizeLocationPaths(firstRelative.StandardOutput),
            NormalizeLocationPaths(secondRelative.StandardOutput),
        };
        Assert.All(normalized.Skip(1), value => Assert.Equal(normalized[0], value));
    }

    [Theory]
    [InlineData("async", "tree")]
    [InlineData("source", "show")]
    public async Task AmbiguityCandidatesHonorMovedBaseAndPathStyle(
        string command,
        string subcommand)
    {
        await fixture.BuildTask;
        var (firstBase, secondBase) = CreateCopiedRoots();
        var arguments = new[]
        {
            command, subcommand, "Alpha.AClass::Play",
            "--symbol-path-style", "explicit", "--short-names",
        };

        var firstAbsolute = await RunWithPathAsync(arguments, firstBase, "absolute");
        var secondAbsolute = await RunWithPathAsync(arguments, secondBase, "absolute");
        var firstRelative = await RunWithPathAsync(arguments, firstBase, "relative");
        var secondRelative = await RunWithPathAsync(arguments, secondBase, "relative");

        Assert.All(
            new[] { firstAbsolute, secondAbsolute, firstRelative, secondRelative },
            result => Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode));
        AssertCandidate(firstAbsolute.StandardError, Path.Combine(firstBase, "Main.cs"));
        AssertCandidate(secondAbsolute.StandardError, Path.Combine(secondBase, "Main.cs"));
        AssertCandidate(firstRelative.StandardError, "Main.cs");
        AssertCandidate(secondRelative.StandardError, "Main.cs");
        Assert.Equal(
            Encoding.UTF8.GetBytes(firstRelative.StandardError),
            Encoding.UTF8.GetBytes(secondRelative.StandardError));
        Assert.Equal(
            ReplaceBase(firstAbsolute.StandardError, firstBase),
            ReplaceBase(secondAbsolute.StandardError, secondBase));
    }

    [Fact]
    public async Task MissingReconstructedSourcePreservesSentinelAndLeavesNoTemporaryPayload()
    {
        await fixture.BuildTask;
        var missingBase = Path.Combine(fixture.RootPath, $"missing-base-{Guid.NewGuid():N}");
        Directory.CreateDirectory(missingBase);
        var outputDirectory = Path.Combine(fixture.RootPath, $"missing-source-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "result.txt");
        File.WriteAllText(outputPath, "output sentinel");
        var selectedPath = Path.Combine(missingBase, "Main.cs");

        var result = await RealCliRunner.RunAsync(
        [
            "symbol", "find", "Alpha.AsyncPlayer::Sync()",
            "--base-dir", missingBase, "--path-style", "absolute",
            "--output-file", outputPath, "--db", fixture.DatabasePath,
        ]);

        Assert.Equal(ExitCodes.AnalysisFailure, result.ExitCode);
        Assert.True(
            result.StandardError.StartsWith("Input error: ", StringComparison.Ordinal) ||
            result.StandardError.StartsWith("Fatal error: ", StringComparison.Ordinal),
            result.StandardError);
        Assert.Contains(selectedPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("output sentinel", File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(outputDirectory, ".*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task RelativeMissingSourceDiagnosticsAreIndependentOfTheEffectiveBase()
    {
        await fixture.BuildTask;
        var firstMissingBase = Path.Combine(fixture.RootPath, $"missing-first-{Guid.NewGuid():N}");
        var secondMissingBase = Path.Combine(fixture.RootPath, $"missing-second-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstMissingBase);
        Directory.CreateDirectory(secondMissingBase);

        var first = await RunWithPathAsync(
            ["symbol", "find", "Alpha.AsyncPlayer::Sync()"],
            firstMissingBase,
            "relative");
        var second = await RunWithPathAsync(
            ["symbol", "find", "Alpha.AsyncPlayer::Sync()"],
            secondMissingBase,
            "relative");

        Assert.Equal(ExitCodes.AnalysisFailure, first.ExitCode);
        Assert.Equal(ExitCodes.AnalysisFailure, second.ExitCode);
        Assert.Equal(
            Encoding.UTF8.GetBytes(first.StandardError),
            Encoding.UTF8.GetBytes(second.StandardError));
        Assert.Contains("Source file 'Main.cs' could not be read.", first.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(firstMissingBase, first.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondMissingBase, second.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CliInvocationResult> RunWithPathAsync(
        string[] arguments,
        string baseDirectory,
        string? pathStyle)
    {
        var options = new List<string>(arguments);
        options.AddRange(["--base-dir", baseDirectory]);
        if (pathStyle is not null)
        {
            options.AddRange(["--path-style", pathStyle]);
        }

        options.AddRange(["--db", fixture.DatabasePath]);
        return await RealCliRunner.RunAsync(options.ToArray());
    }

    private (string First, string Second) CreateCopiedRoots()
    {
        var first = Path.Combine(fixture.RootPath, $"moved-first-{Guid.NewGuid():N}");
        var second = Path.Combine(fixture.RootPath, $"moved-second-{Guid.NewGuid():N}");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        foreach (var sourcePath in Directory.GetFiles(fixture.RootPath, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(sourcePath);
            File.Copy(sourcePath, Path.Combine(first, fileName));
            File.Copy(sourcePath, Path.Combine(second, fileName));
        }

        return (first, second);
    }

    private static void AssertSuccess(string caseName, params CliInvocationResult[] results) =>
        Assert.All(results, result => Assert.True(
            result.ExitCode == ExitCodes.Success,
            $"{caseName} failed:{Environment.NewLine}{result.StandardError}"));

    private static void AssertCandidate(string diagnostics, string displayPath)
    {
        Assert.Contains($"**::AClass::Play() @ {displayPath}:", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"**::AClass::Play(string) @ {displayPath}:", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPaths(string json, string expectedPath)
    {
        using var document = JsonDocument.Parse(json);
        var paths = CollectLocationPaths(document.RootElement).ToArray();
        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.Equal(expectedPath, path, ignoreCase: OperatingSystem.IsWindows()));
    }

    private static IEnumerable<string> CollectLocationPaths(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("location", out var location) &&
                location.ValueKind == JsonValueKind.Object &&
                location.TryGetProperty("path", out var path) &&
                path.ValueKind == JsonValueKind.String)
            {
                yield return path.GetString()!;
            }

            foreach (var property in value.EnumerateObject())
            {
                foreach (var nested in CollectLocationPaths(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var nested in CollectLocationPaths(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string NormalizeLocationPaths(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON payload was empty.");
        NormalizeLocationPaths(root);
        return root.ToJsonString();
    }

    private static void NormalizeLocationPaths(JsonNode node)
    {
        if (node is JsonObject value)
        {
            if (value.ContainsKey("path") &&
                value.ContainsKey("line") &&
                value.ContainsKey("column") &&
                value.ContainsKey("offset"))
            {
                value["path"] = "<location-path>";
            }

            foreach (var child in value.Select(property => property.Value).Where(child => child is not null).ToArray())
            {
                NormalizeLocationPaths(child!);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child is not null).ToArray())
            {
                NormalizeLocationPaths(child!);
            }
        }
    }

    private static string ReplaceBase(string value, string baseDirectory) =>
        value.Replace(
            baseDirectory,
            "<effective-base>",
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
