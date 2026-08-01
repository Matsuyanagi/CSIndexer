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
