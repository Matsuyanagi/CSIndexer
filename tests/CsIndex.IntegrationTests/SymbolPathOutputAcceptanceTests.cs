using System.Text.Json;
using CsIndex.Cli;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class SymbolPathOutputAcceptanceTests(SemanticIndexFixture fixture)
    : IClassFixture<SemanticIndexFixture>
{
    public static TheoryData<string, string[], string> SymbolBearingPayloads => new()
    {
        {
            "symbol-find-table",
            [
                "symbol", "find", "Alpha.AsyncStatusCases::UniTaskResult()",
                "--symbol-path-style", "explicit", "--short-names",
            ],
            "UniTask<int> **::AsyncStatusCases::UniTaskResult()"
        },
        {
            "symbol-list-json",
            [
                "symbol", "list", "--method-literal", "UniTaskResult()",
                "--output-format", "json", "--symbol-path-style", "explicit",
            ],
            "Alpha::AsyncStatusCases::UniTaskResult()"
        },
        {
            "source-show-table",
            [
                "source", "show", "Alpha.AsyncStatusCases::UniTaskResult()",
                "--source-layout", "multi-line", "--symbol-path-style", "explicit",
            ],
            "Alpha::AsyncStatusCases::UniTaskResult()"
        },
        {
            "source-search-json",
            [
                "source", "search", "--method-literal", "UniTaskResult()",
                "--output-format", "json", "--symbol-path-style", "explicit", "--short-names",
            ],
            "**::AsyncStatusCases::UniTaskResult()"
        },
        {
            "definition-json",
            [
                "definition", "Alpha.AsyncStatusCases::UniTaskResult()",
                "--output-format", "json", "--symbol-path-style", "explicit",
            ],
            "Alpha::AsyncStatusCases::UniTaskResult()"
        },
        {
            "references-json",
            [
                "references", "Alpha.LambdaPlayer::Play()",
                "--output-format", "json", "--symbol-path-style", "explicit", "--short-names",
            ],
            "**::LambdaPlayer::Play()"
        },
        {
            "callers-json",
            [
                "callers", "Alpha.LambdaPlayer::Play()",
                "--output-format", "json", "--symbol-path-style", "explicit",
            ],
            "Alpha::LambdaPlayer::Play()"
        },
        {
            "callees-json",
            [
                "callees", "Alpha.DescendantCallees::Execute()",
                "--output-format", "json", "--symbol-path-style", "explicit", "--short-names",
            ],
            "**::DescendantCallees::Execute()"
        },
        {
            "overrides-json",
            [
                "overrides", "Alpha.AsyncOverrideBase::Run()",
                "--output-format", "json", "--symbol-path-style", "explicit",
            ],
            "Alpha::AsyncOverrideBase::Run()"
        },
        {
            "async-tree",
            [
                "async", "tree", "Alpha.AsyncGraph::Start()",
                "--symbol-path-style", "explicit",
            ],
            "Alpha::AsyncGraph::Start()"
        },
        {
            "async-line",
            [
                "async", "tree", "Alpha.AsyncGraph::Start()", "--output-format", "line",
                "--symbol-path-style", "explicit", "--short-names",
            ],
            "**::AsyncGraph::Start()"
        },
        {
            "async-json",
            [
                "async", "tree", "Alpha.AsyncGraph::Start()", "--output-format", "json",
                "--symbol-path-style", "explicit",
            ],
            "Alpha::AsyncGraph::Start()"
        },
        {
            "callers-tree",
            [
                "callers", "tree", "Alpha.CallerGraph::DirectTarget()",
                "--symbol-path-style", "explicit",
            ],
            "Alpha::CallerGraph::DirectTarget()"
        },
        {
            "callers-mermaid",
            [
                "callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--output-format", "mermaid",
                "--symbol-path-style", "explicit", "--short-names",
            ],
            "**::CallerGraph::DirectTarget()"
        },
        {
            "callers-json",
            [
                "callers", "tree", "Alpha.CallerGraph::DirectTarget()", "--output-format", "json",
                "--symbol-path-style", "explicit",
            ],
            "Alpha::CallerGraph::DirectTarget()"
        },
    };

    [Theory]
    [MemberData(nameof(SymbolBearingPayloads))]
    public async Task EverySymbolBearingCliFamilyUsesSelectedSharedStyle(
        string caseName,
        string[] arguments,
        string expectedDisplayPath)
    {
        await fixture.BuildTask;

        var result = await RealCliRunner.RunAsync(
            [.. arguments, "--db", fixture.DatabasePath]);

        Assert.True(
            result.ExitCode == ExitCodes.Success,
            $"{caseName} failed:{Environment.NewLine}{result.StandardError}");
        Assert.Contains(expectedDisplayPath, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceSearchAlwaysHydratesNormalizedSourceWithoutSourceConditions()
    {
        await fixture.BuildTask;

        var table = await RealCliRunner.RunAsync(
        [
            "source", "search", "--method-literal", "UniTaskResult()",
            "--db", fixture.DatabasePath,
        ]);

        Assert.Equal(ExitCodes.Success, table.ExitCode);
        var tableRow = Assert.Single(
            table.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        var fields = tableRow.Split('\t');
        Assert.Equal(4, fields.Length);
        Assert.False(string.IsNullOrWhiteSpace(fields[3]));
        Assert.Contains("UniTaskResult", fields[3], StringComparison.Ordinal);

        var json = await RealCliRunner.RunAsync(
        [
            "source", "search", "--method-literal", "UniTaskResult()",
            "--output-format", "json", "--db", fixture.DatabasePath,
        ]);

        Assert.Equal(ExitCodes.Success, json.ExitCode);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var match = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        var normalizedSource = match.GetProperty("normalizedSource").GetString();
        Assert.False(string.IsNullOrWhiteSpace(normalizedSource));
        Assert.Contains("UniTaskResult", normalizedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolJsonShortNamesChangePresentationTypeFieldsAcrossStylesAndPreserveIdentityFields()
    {
        await fixture.BuildTask;
        var cases = new (string[] Options, string DisplayName, string Signature)[]
        {
            (
                [],
                "Alpha.AsyncStatusCases::UniTaskResult()",
                "public Cysharp.Threading.Tasks.UniTask<int> Alpha.AsyncStatusCases::UniTaskResult()"),
            (
                ["--short-names"],
                "AsyncStatusCases::UniTaskResult()",
                "public UniTask<int> AsyncStatusCases::UniTaskResult()"),
            (
                ["--symbol-path-style", "explicit"],
                "Alpha::AsyncStatusCases::UniTaskResult()",
                "public Cysharp.Threading.Tasks.UniTask<int> Alpha::AsyncStatusCases::UniTaskResult()"),
            (
                ["--symbol-path-style", "explicit", "--short-names"],
                "**::AsyncStatusCases::UniTaskResult()",
                "public UniTask<int> **::AsyncStatusCases::UniTaskResult()"),
        };

        Dictionary<string, string>? semanticBaseline = null;
        foreach (var (options, expectedDisplayName, expectedSignature) in cases)
        {
            var result = await RealCliRunner.RunAsync(
            [
                "symbol", "find", "Alpha.AsyncStatusCases::UniTaskResult()",
                "--output-format", "json", .. options,
                "--db", fixture.DatabasePath,
            ]);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var symbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
            Assert.Equal(expectedDisplayName, symbol.GetProperty("displayName").GetString());
            Assert.Equal(expectedSignature, symbol.GetProperty("signature").GetString());
            Assert.Equal(
                options.Contains("--short-names", StringComparer.Ordinal)
                    ? "AsyncStatusCases::UniTaskResult()"
                    : "Alpha.AsyncStatusCases::UniTaskResult()",
                symbol.GetProperty("fullyQualifiedName").GetString());
            Assert.Equal(
                options.Contains("--short-names", StringComparer.Ordinal)
                    ? "UniTask<int>"
                    : "Cysharp.Threading.Tasks.UniTask<int>",
                symbol.GetProperty("returnType").GetString());

            var semanticFields = symbol.EnumerateObject()
                .Where(property => property.Name is not "displayName" and not "signature" and
                                   not "fullyQualifiedName" and not "returnType")
                .ToDictionary(property => property.Name, property => property.Value.GetRawText());
            if (semanticBaseline is null)
            {
                semanticBaseline = semanticFields;
            }
            else
            {
                Assert.Equal(semanticBaseline.Keys.Order(StringComparer.Ordinal), semanticFields.Keys.Order(StringComparer.Ordinal));
                foreach (var (name, value) in semanticBaseline)
                {
                    Assert.Equal(value, semanticFields[name]);
                }
            }
        }
    }

    [Theory]
    [InlineData("csharp", "Alpha.AsyncStatusCases::UniTaskResult()")]
    [InlineData("explicit", "Alpha::AsyncStatusCases::UniTaskResult()")]
    public async Task FullDisplayNamesRoundTripAsSelectors(string style, string expectedDisplayName)
    {
        await fixture.BuildTask;
        var first = await RealCliRunner.RunAsync(
        [
            "symbol", "find", "Alpha.AsyncStatusCases::UniTaskResult()",
            "--output-format", "json", "--symbol-path-style", style,
            "--db", fixture.DatabasePath,
        ]);

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        using var firstDocument = JsonDocument.Parse(first.StandardOutput);
        var firstSymbol = Assert.Single(firstDocument.RootElement.GetProperty("matched").EnumerateArray());
        var copiedDisplayName = firstSymbol.GetProperty("displayName").GetString();
        Assert.Equal(expectedDisplayName, copiedDisplayName);

        var second = await RealCliRunner.RunAsync(
        [
            "symbol", "find", Assert.IsType<string>(copiedDisplayName),
            "--output-format", "json", "--db", fixture.DatabasePath,
        ]);

        Assert.Equal(ExitCodes.Success, second.ExitCode);
        using var secondDocument = JsonDocument.Parse(second.StandardOutput);
        Assert.Contains(
            secondDocument.RootElement.GetProperty("matched").EnumerateArray(),
            symbol => symbol.GetProperty("id").GetInt64() == firstSymbol.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ExplicitShortSelectorMayReturnEquivalentOwnersInMultipleNamespaces()
    {
        await fixture.BuildTask;

        var result = await RealCliRunner.RunAsync(
        [
            "symbol", "find", "**::Player::Play()", "--output-format", "json",
            "--symbol-path-style", "explicit", "--short-names",
            "--db", fixture.DatabasePath,
        ]);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var matches = document.RootElement.GetProperty("matched").EnumerateArray().ToArray();
        Assert.Equal(["GameNS", "PianoNS"], matches.Select(match => match.GetProperty("namespaceName").GetString()));
        Assert.All(matches, match =>
            Assert.Equal("**::Player::Play()", match.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task PartialLogicalPayloadsStaySingleAndRoleFreeAtThePreferredImplementation()
    {
        using var resolutionFixture = new SymbolResolutionFixture();
        await resolutionFixture.BuildTask;
        string[][] commands =
        [
            ["symbol", "find", "Partials::PartialHost::PartialWork()"],
            [
                "symbol", "list", "--namespace-literal", "Partials",
                "--type-literal", "PartialHost", "--method-literal", "PartialWork()",
            ],
            ["source", "show", "Partials::PartialHost::PartialWork()"],
        ];

        foreach (var command in commands)
        {
            var result = await RealCliRunner.RunAsync(
            [
                .. command, "--output-format", "json", "--symbol-path-style", "explicit",
                "--db", resolutionFixture.DatabasePath,
            ]);

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var propertyName = command is ["symbol", "list", ..] ? "symbols" : "matched";
            var symbol = Assert.Single(document.RootElement.GetProperty(propertyName).EnumerateArray());
            Assert.Equal("Partials::PartialHost::PartialWork()", symbol.GetProperty("displayName").GetString());
            Assert.EndsWith(
                "PartialImplementation.cs",
                Assert.IsType<string>(symbol.GetProperty("location").GetProperty("path").GetString()),
                StringComparison.Ordinal);
            Assert.False(symbol.TryGetProperty("declarationRole", out _));
            Assert.DoesNotContain("partial-", symbol.GetProperty("displayName").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SourceSearchUsesAllRoleSpellingsAndDefinitionOrdersPartialRoles()
    {
        using var resolutionFixture = new SymbolResolutionFixture();
        await resolutionFixture.BuildTask;

        var sourceSearch = await RealCliRunner.RunAsync(
        [
            "source", "search", "--namespace-regex", "^(Partials|Signatures)$",
            "--output-format", "json", "--symbol-path-style", "explicit",
            "--db", resolutionFixture.DatabasePath,
        ]);
        var definition = await RealCliRunner.RunAsync(
        [
            "definition", "Partials::PartialHost::PartialWork()",
            "--output-format", "json", "--symbol-path-style", "explicit",
            "--db", resolutionFixture.DatabasePath,
        ]);

        Assert.Equal(ExitCodes.Success, sourceSearch.ExitCode);
        using (var sourceDocument = JsonDocument.Parse(sourceSearch.StandardOutput))
        {
            var matches = sourceDocument.RootElement.GetProperty("matched").EnumerateArray().ToArray();
            var roles = matches
                .Select(match => Assert.IsType<string>(match.GetProperty("declarationRole").GetString()))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["ordinary", "partial-definition", "partial-implementation"], roles);
            Assert.All(matches, match =>
            {
                var role = Assert.IsType<string>(match.GetProperty("declarationRole").GetString());
                Assert.Contains(
                    role,
                    new[] { "ordinary", "partial-definition", "partial-implementation" });
                Assert.DoesNotContain("partial-", match.GetProperty("displayName").GetString(), StringComparison.Ordinal);
            });
        }

        Assert.Equal(ExitCodes.Success, definition.ExitCode);
        using var definitionDocument = JsonDocument.Parse(definition.StandardOutput);
        var definitions = definitionDocument.RootElement.GetProperty("definitions").EnumerateArray().ToArray();
        Assert.Equal(
            ["partial-definition", "partial-implementation"],
            definitions.Select(row => row.GetProperty("declarationRole").GetString()));
        Assert.All(definitions, row =>
            Assert.Equal("Partials::PartialHost::PartialWork()", row.GetProperty("displayName").GetString()));
        Assert.All(
            definitionDocument.RootElement.GetProperty("matched").EnumerateArray(),
            matched => Assert.False(matched.TryGetProperty("declarationRole", out _)));
    }

    [Fact]
    public async Task CallerTreeRepresentationsShareCanonicalDepthFirstOrder()
    {
        await fixture.BuildTask;
        string[] expectedOrder =
        [
            "Alpha::CallerGraph::Root()",
            "Alpha::CallerGraph::A()",
            "Alpha::CallerGraph::Z2()",
            "Alpha::CallerGraph::Z()",
            "Alpha::CallerGraph::A2()",
        ];
        var common = new[]
        {
            "callers", "tree", "Alpha.CallerGraph::Root()", "--depth", "2",
            "--symbol-path-style", "explicit", "--db", fixture.DatabasePath,
        };

        var tree = await RealCliRunner.RunAsync(common);
        var mermaid = await RealCliRunner.RunAsync([.. common, "--output-format", "mermaid"]);
        var json = await RealCliRunner.RunAsync([.. common, "--output-format", "json"]);

        Assert.Equal(ExitCodes.Success, tree.ExitCode);
        Assert.Equal(ExitCodes.Success, mermaid.ExitCode);
        Assert.Equal(ExitCodes.Success, json.ExitCode);
        AssertAppearsInOrder(tree.StandardOutput, expectedOrder);
        AssertAppearsInOrder(mermaid.StandardOutput, expectedOrder);

        using var document = JsonDocument.Parse(json.StandardOutput);
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(
            expectedOrder,
            nodes.Select(node => node.GetProperty("symbol").GetProperty("displayName").GetString()));
        var namesById = nodes.ToDictionary(
            node => node.GetProperty("symbol").GetProperty("id").GetInt64(),
            node => node.GetProperty("symbol").GetProperty("displayName").GetString()!);
        Assert.Equal(
            [
                "Alpha::CallerGraph::A()->Alpha::CallerGraph::Root()",
                "Alpha::CallerGraph::Z2()->Alpha::CallerGraph::A()",
                "Alpha::CallerGraph::Z()->Alpha::CallerGraph::Root()",
                "Alpha::CallerGraph::A2()->Alpha::CallerGraph::Z()",
            ],
            document.RootElement.GetProperty("edges").EnumerateArray().Select(edge =>
                $"{namesById[edge.GetProperty("callerSymbolId").GetInt64()]}->" +
                namesById[edge.GetProperty("calleeSymbolId").GetInt64()]));
    }

    private static void AssertAppearsInOrder(string value, IReadOnlyList<string> expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            var index = value.IndexOf(item, StringComparison.Ordinal);
            Assert.True(index > previous, $"Expected '{item}' after index {previous}.{Environment.NewLine}{value}");
            previous = index;
        }
    }
}

internal sealed record CliInvocationResult(int ExitCode, string StandardOutput, string StandardError);

internal static class RealCliRunner
{
    public static async Task<CliInvocationResult> RunAsync(string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.Main(arguments);
            return new CliInvocationResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
