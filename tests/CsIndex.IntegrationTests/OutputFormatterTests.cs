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
    [Theory]
    [InlineData(
        "Nop.Core.Caching.DistributedCacheLocker::RunWithHeartbeatAsync(System.String,System.TimeSpan,System.TimeSpan,System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>,System.Threading.CancellationTokenSource)",
        "DistributedCacheLocker::RunWithHeartbeatAsync(String,TimeSpan,TimeSpan,Func<CancellationToken, Tasks.Task>,CancellationTokenSource)")]
    [InlineData(
        "Example.Handlers.Worker::Execute(System.Collections.Generic.Dictionary<System.String,System.Collections.Generic.List<Example.Models.Widget?[]>>,System.Nullable<System.Int32>[])",
        "Worker::Execute(Dictionary<String,List<Widget?[]>>,Nullable<Int32>[])")]
    [InlineData(
        "Example.Handlers.Worker::Run(System.Threading.Tasks.Task)::<lambda#1>",
        "Worker::Run(Tasks.Task)::<lambda#1>")]
    [InlineData(
        "会社.モデル.サービス::実行(会社.モデル.入力)",
        "サービス::実行(入力)")]
    public void SymbolNameShortenerRemovesNamespacesAndKeepsTypeSyntax(string name, string expected)
    {
        Assert.Equal(expected, SymbolNameShortener.Shorten(name));
    }

    [Fact]
    public void WriteSymbolsJsonShortensDisplayNameButPreservesCanonicalFields()
    {
        const string displayName = "Nop.Core.Caching.DistributedCacheLocker::RunWithHeartbeatAsync(System.String)";
        var symbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            displayName: displayName,
            parameters: [new StoredParameter(0, "name", "System.String", 0, false)],
            namespaceName: "Nop.Core.Caching");
        var context = new QueryContext(CreateProfile(), [symbol]);

        using var document = CaptureJson(() => new OutputFormatter("json", shortNames: true).WriteSymbols(context));

        var outputSymbol = Assert.Single(document.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal(
            "DistributedCacheLocker::RunWithHeartbeatAsync(String)",
            outputSymbol.GetProperty("displayName").GetString());
        Assert.Equal("symbol-1", outputSymbol.GetProperty("stableKey").GetString());
        Assert.Equal("Nop.Core.Caching", outputSymbol.GetProperty("namespaceName").GetString());
        Assert.Equal(displayName, outputSymbol.GetProperty("fullyQualifiedName").GetString());
        Assert.Equal("System.String", Assert.Single(outputSymbol.GetProperty("parameters").EnumerateArray()).GetString());
    }

    [Fact]
    public void WriteCallsTableShortensCallerAndCalleeNames()
    {
        var context = new QueryContext(CreateProfile(), []);
        var call = CreateCall(AsyncUsageKind.None) with
        {
            CallerDisplayName = "Example.Features.Caller::Run(System.String)",
            CalleeDisplayName = "Example.Services.Callee::Execute(System.Threading.Tasks.Task)",
            CalleeDefinitionDisplayName = "Example.Services.Callee::Execute(System.Threading.Tasks.Task)",
        };
        var result = new CallResult(context, [call], [], []);

        var output = CaptureText(() => new OutputFormatter("table", shortNames: true).WriteCalls(result, "call(s)"));

        Assert.Contains("Caller::Run(String) -> Callee::Execute(Tasks.Task)", output);
    }

    [Fact]
    public void WriteSymbolsTableKeepsCanonicalNamesByDefault()
    {
        const string displayName = "Example.Features.Worker::Run(System.Threading.Tasks.Task)";
        var symbol = CreateSymbol(AsyncRole.None, asyncInvolvementDepth: null, displayName: displayName);
        var context = new QueryContext(CreateProfile(), [symbol]);

        var output = CaptureText(() => new OutputFormatter("table").WriteSymbols(context));

        Assert.Equal(
            $"Query matched 1 symbol(s):{Environment.NewLine}  {displayName}{Environment.NewLine}",
            output);
    }

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
    public void WriteSymbolListJsonUsesSymbolsAndShortDisplayNames()
    {
        var symbol = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            displayName: "Example.Features.Worker::Run(System.Threading.Tasks.Task)");
        var context = new QueryContext(CreateProfile(), [symbol]);

        using var document = CaptureJson(() => new OutputFormatter("json", shortNames: true).WriteSymbolList(context));

        Assert.Equal("default", document.RootElement.GetProperty("profile").GetString());
        var outputSymbol = Assert.Single(document.RootElement.GetProperty("symbols").EnumerateArray());
        Assert.Equal("Worker::Run(Tasks.Task)", outputSymbol.GetProperty("displayName").GetString());
        Assert.False(document.RootElement.TryGetProperty("matched", out _));
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
        string displayName = "Example.Method()",
        IReadOnlyList<StoredParameter>? parameters = null,
        string namespaceName = "Example") => new(
        Id: id,
        StableKey: $"symbol-{id}",
        Kind: IndexedSymbolKind.Method,
        Name: "Method",
        NamespaceName: namespaceName,
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
        Parameters: parameters ?? [],
        TypeKind: null,
        Accessibility: null);

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
