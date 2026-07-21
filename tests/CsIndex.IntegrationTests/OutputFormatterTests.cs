using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Console output";
}

[Collection(ConsoleOutputCollection.Name)]
public sealed class OutputFormatterTests
{
    [Fact]
    public void WriteSymbolsJsonIncludesAsyncAnalysis()
    {
        var symbol = CreateSymbol(
            AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable,
            asyncInvolvementDepth: 0);
        var context = new QueryContext(CreateProfile(), [symbol]);

        using var document = CaptureJson(() => new OutputFormatter("json").WriteSymbols(context));

        var outputSymbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal("DeclaredAsync, ReturnsAwaitable", outputSymbol.GetProperty("asyncRole").GetString());
        Assert.True(outputSymbol.GetProperty("isAsyncInvolved").GetBoolean());
        Assert.Equal(0, outputSymbol.GetProperty("asyncInvolvementDepth").GetInt32());
    }

    [Fact]
    public void WriteCallsJsonIncludesAsyncUsageKind()
    {
        var context = new QueryContext(CreateProfile(), []);
        var result = new CallResult(context, [CreateCall(AsyncUsageKind.Awaited)], [], []);

        using var document = CaptureJson(() => new OutputFormatter("json").WriteCalls(result, "call(s)"));

        var call = Assert.Single(document.RootElement.GetProperty("calls").EnumerateArray());
        Assert.Equal("Awaited", call.GetProperty("asyncUsageKind").GetString());
    }

    [Fact]
    public void WriteSymbolsTableShowsAsyncRoleAndDepthOnlyWhenAsyncAnalysisExists()
    {
        var asyncSymbol = CreateSymbol(
            AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable,
            asyncInvolvementDepth: 0,
            displayName: "Example.AsyncMethod()");
        var synchronousSymbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 2,
            displayName: "Example.SyncMethod()");
        var context = new QueryContext(CreateProfile(), [asyncSymbol, synchronousSymbol]);

        var output = CaptureText(() => new OutputFormatter("table").WriteSymbols(context));

        Assert.Contains(
            "Example.AsyncMethod() [async: DeclaredAsync, ReturnsAwaitable; depth: 0]",
            output);
        Assert.Contains("  Example.SyncMethod()", output);
        Assert.DoesNotContain("Example.SyncMethod() [async:", output);
    }

    [Fact]
    public void WriteCallsTableShowsAsyncUsageKind()
    {
        var context = new QueryContext(CreateProfile(), []);
        var result = new CallResult(context, [CreateCall(AsyncUsageKind.Awaited)], [], []);

        var output = CaptureText(() => new OutputFormatter("table").WriteCalls(result, "call(s)"));

        Assert.Contains("[Awaited]", output);
    }

    private static JsonDocument CaptureJson(Action write) => JsonDocument.Parse(CaptureText(write));

    private static string CaptureText(Action write)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            write();
            return output.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static StoredProfile CreateProfile() => new(
        Id: 1,
        Name: "default",
        InputMode: InputMode.Directory,
        Configuration: null,
        TargetFramework: null,
        RuntimeIdentifier: null,
        PreprocessorSymbols: [],
        InputRoot: ".");

    private static StoredSymbol CreateSymbol(
        AsyncRole asyncRole,
        int? asyncInvolvementDepth,
        long id = 1,
        string displayName = "Example.Method()") => new(
        Id: id,
        StableKey: $"symbol-{id}",
        Kind: IndexedSymbolKind.Method,
        Name: "Method",
        NamespaceName: "Example",
        TypeSimpleName: "Example",
        TypeMetadataName: "Example",
        FullyQualifiedName: displayName,
        DisplayName: displayName,
        ContainingSymbolId: null,
        Arity: 0,
        ParameterCount: 0,
        IsStatic: false,
        IsAbstract: false,
        IsVirtual: false,
        IsOverride: false,
        AsyncRole: asyncRole,
        AsyncInvolvementDepth: asyncInvolvementDepth,
        DocumentPath: null,
        SourceStart: null,
        SourceLength: null,
        IsGenerated: false,
        AssemblyName: null,
        Parameters: []);

    private static StoredCall CreateCall(AsyncUsageKind asyncUsageKind) => new(
        Id: 1,
        CallerSymbolId: 1,
        CallerDisplayName: "Example.Caller()",
        CallerContainingSymbolId: null,
        CalleeSymbolId: 2,
        CalleeDisplayName: "Example.Callee()",
        CalleeDefinitionId: 2,
        CalleeDefinitionDisplayName: "Example.Callee()",
        ReferenceKind: ReferenceKind.Invocation,
        DispatchKind: DispatchKind.Static,
        ResolutionStatus: ResolutionStatus.Resolved,
        ResolutionReason: ResolutionReason.None,
        AsyncUsageKind: asyncUsageKind,
        DocumentId: 1,
        DocumentPath: "missing.cs",
        SourceStart: 0,
        SourceLength: 1,
        IsGenerated: false,
        UnresolvedName: null,
        ReceiverTypeKey: null);
}
