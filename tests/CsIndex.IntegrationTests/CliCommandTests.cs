using System.Text.Json;
using CsIndex.Cli;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class CliCommandTests : IDisposable
{
    private readonly SemanticIndexFixture _fixture = new();

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
        Assert.All(symbols, symbol => Assert.Equal("Lambda", symbol.GetProperty("kind").GetString()));
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
    [InlineData("--help")]
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
