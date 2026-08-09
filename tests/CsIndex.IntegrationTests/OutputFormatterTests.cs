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
        "DistributedCacheLocker::RunWithHeartbeatAsync(String,TimeSpan,TimeSpan,Func<CancellationToken, Task>,CancellationTokenSource)")]
    [InlineData(
        "Example.Handlers.Worker::Execute(System.Collections.Generic.Dictionary<System.String,System.Collections.Generic.List<Example.Models.Widget?[]>>,System.Nullable<System.Int32>[])",
        "Worker::Execute(Dictionary<String,List<Widget?[]>>,Nullable<Int32>[])")]
    [InlineData(
        "Example.Handlers.Worker::Run(System.Threading.Tasks.Task)::<lambda#1>",
        "Worker::Run(Task)::<lambda#1>")]
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
    public void WriteSymbolsTableUsesDeclarationOrderingAndShortensReturnAndParameterTypes()
    {
        var symbol = CreateSymbol(
            AsyncRole.DeclaredAsync,
            asyncInvolvementDepth: 0,
            displayName: "Tokyo.Gamer::Play(System.String,System.Threading.CancellationToken)",
            parameters:
            [
                new StoredParameter(0, "name", "System.String", 0, false),
                new StoredParameter(1, "token", "System.Threading.CancellationToken", 0, false),
            ],
            namespaceName: "Tokyo",
            isStatic: true,
            returnTypeKey: "System.Threading.Tasks.Task<System.Int32>",
            accessibility: (int)IndexedAccessibility.Public);
        var context = new QueryContext(CreateProfile(), [symbol]);

        var output = CaptureText(() => new OutputFormatter("table", shortNames: true).WriteSymbols(context));

        Assert.Contains("public static async Task<Int32> Gamer::Play(String,CancellationToken)", output);
    }

    [Fact]
    public void WriteSymbolsJsonKeepsCanonicalFieldsAndOnlyShowsSourceWhenRequested()
    {
        var symbol = CreateSymbol(
            AsyncRole.DeclaredAsync,
            asyncInvolvementDepth: 0,
            displayName: "Tokyo.Gamer::Play(System.String)",
            parameters: [new StoredParameter(0, "name", "System.String", 0, false)],
            namespaceName: "Tokyo",
            isStatic: true,
            returnTypeKey: "System.Threading.Tasks.Task<System.Int32>",
            normalizedSource: "public static async Task<int>Play(string name){return 1;}",
            accessibility: (int)IndexedAccessibility.Public);

        using var hiddenSource = CaptureJson(() => new OutputFormatter("json", shortNames: true)
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol])));
        var hiddenSymbol = Assert.Single(hiddenSource.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal("Gamer::Play(String)", hiddenSymbol.GetProperty("displayName").GetString());
        Assert.Equal("public static async Task<Int32> Gamer::Play(String)", hiddenSymbol.GetProperty("signature").GetString());
        Assert.Equal("System.Threading.Tasks.Task<System.Int32>", hiddenSymbol.GetProperty("returnType").GetString());
        Assert.Equal("System.String", Assert.Single(hiddenSymbol.GetProperty("parameters").EnumerateArray()).GetString());
        Assert.Equal("public", hiddenSymbol.GetProperty("accessibility").GetString());
        Assert.True(hiddenSymbol.GetProperty("isStatic").GetBoolean());
        Assert.True(hiddenSymbol.GetProperty("isAsync").GetBoolean());
        Assert.False(hiddenSymbol.TryGetProperty("normalizedSource", out _));

        using var shownSource = CaptureJson(() => new OutputFormatter("json", shortNames: true)
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol], ShowSource: true)));
        var shownSymbol = Assert.Single(shownSource.RootElement.GetProperty("matched").EnumerateArray());
        Assert.Equal("public static async Task<int>Play(string name){return 1;}",
            shownSymbol.GetProperty("normalizedSource").GetString());
    }

    [Fact]
    public void WriteSymbolsFormatsConstructorAndLambdaApplicableFieldsAndGatesSource()
    {
        var constructor = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 101,
            displayName: "Tokyo.Gamer::.ctor(System.String)",
            parameters: [new StoredParameter(0, "name", "System.String", 0, false)],
            namespaceName: "Tokyo",
            name: ".ctor",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.Constructor,
            normalizedSource: "public Gamer(string name){}",
            accessibility: (int)IndexedAccessibility.Public);
        var lambda = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 102,
            displayName: "Tokyo.Gamer::Run()::<lambda#1>",
            namespaceName: "Tokyo",
            kind: IndexedSymbolKind.Lambda,
            name: "<lambda#1>",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.AnonymousFunction,
            returnTypeKey: "System.Int32",
            normalizedSource: "()=>42",
            accessibility: (int)IndexedAccessibility.NotApplicable);
        var hiddenContext = new QueryContext(CreateProfile(), [constructor, lambda]);

        var table = CaptureText(() => new OutputFormatter("table").WriteSymbols(hiddenContext));
        Assert.Equal(
            "Query matched 2 symbol(s):" + Environment.NewLine +
            "  public Tokyo.Gamer::.ctor(System.String)" + Environment.NewLine +
            "  System.Int32 Tokyo.Gamer::Run()::<lambda#1>" + Environment.NewLine,
            table);
        Assert.DoesNotContain("source:", table);

        var shownTable = CaptureText(() => new OutputFormatter("table").WriteSymbols(
            new QueryContext(CreateProfile(), [constructor, lambda], ShowSource: true)));
        Assert.Contains("source: public Gamer(string name){}", shownTable);
        Assert.Contains("source: ()=>42", shownTable);

        using var hiddenJson = CaptureJson(() => new OutputFormatter("json").WriteSymbols(hiddenContext));
        var hiddenConstructor = Assert.Single(hiddenJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("id").GetInt64() == constructor.Id);
        var hiddenLambda = Assert.Single(hiddenJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
            symbol.GetProperty("id").GetInt64() == lambda.Id);
        Assert.Equal("public Tokyo.Gamer::.ctor(System.String)", hiddenConstructor.GetProperty("signature").GetString());
        Assert.Equal(JsonValueKind.Null, hiddenConstructor.GetProperty("returnType").ValueKind);
        Assert.Equal("public", hiddenConstructor.GetProperty("accessibility").GetString());
        Assert.False(hiddenConstructor.TryGetProperty("normalizedSource", out _));
        Assert.Equal("System.Int32 Tokyo.Gamer::Run()::<lambda#1>", hiddenLambda.GetProperty("signature").GetString());
        Assert.Equal("System.Int32", hiddenLambda.GetProperty("returnType").GetString());
        Assert.Equal(JsonValueKind.Null, hiddenLambda.GetProperty("accessibility").ValueKind);
        Assert.False(hiddenLambda.TryGetProperty("normalizedSource", out _));

        using var shownJson = CaptureJson(() => new OutputFormatter("json").WriteSymbols(
            new QueryContext(CreateProfile(), [constructor, lambda], ShowSource: true)));
        Assert.Equal(
            "public Gamer(string name){}",
            Assert.Single(shownJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
                symbol.GetProperty("id").GetInt64() == constructor.Id)
            .GetProperty("normalizedSource").GetString());
        Assert.Equal(
            "()=>42",
            Assert.Single(shownJson.RootElement.GetProperty("matched").EnumerateArray(), symbol =>
                symbol.GetProperty("id").GetInt64() == lambda.Id)
            .GetProperty("normalizedSource").GetString());
    }

    [Fact]
    public void WriteSymbolsFormatsLocalAccessorOperatorAndConversionApplicableFields()
    {
        var local = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 201,
            displayName: "Test.A::Host()::Local()",
            name: "Local",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.LocalFunction,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.NotApplicable);
        var getter = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 202,
            displayName: "Test.A::get_Value()",
            name: "get_Value",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.PropertyGet,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.Public);
        var addition = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 203,
            displayName: "Test.A::op_Addition(Test.A,Test.A)",
            name: "op_Addition",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.UserDefinedOperator,
            isStatic: true,
            returnTypeKey: "Test.A",
            accessibility: (int)IndexedAccessibility.Public);
        var conversion = CreateSymbol(
            AsyncRole.None,
            asyncInvolvementDepth: null,
            id: 204,
            displayName: "Test.A::op_Implicit(Test.A)",
            name: "op_Implicit",
            methodKind: (int)Microsoft.CodeAnalysis.MethodKind.Conversion,
            isStatic: true,
            returnTypeKey: "System.Int32",
            accessibility: (int)IndexedAccessibility.Public);
        var context = new QueryContext(CreateProfile(), [local, getter, addition, conversion]);

        var table = CaptureText(() => new OutputFormatter("table").WriteSymbols(context));
        Assert.Equal(
            "Query matched 4 symbol(s):" + Environment.NewLine +
            "  System.Int32 Test.A::Host()::Local()" + Environment.NewLine +
            "  public System.Int32 Test.A::get_Value()" + Environment.NewLine +
            "  public static Test.A Test.A::op_Addition(Test.A,Test.A)" + Environment.NewLine +
            "  public static System.Int32 Test.A::op_Implicit(Test.A)" + Environment.NewLine,
            table);

        using var json = CaptureJson(() => new OutputFormatter("json").WriteSymbols(context));
        var symbols = json.RootElement.GetProperty("matched").EnumerateArray()
            .ToDictionary(symbol => symbol.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, symbols[local.Id].GetProperty("accessibility").ValueKind);
        Assert.False(symbols[local.Id].GetProperty("isStatic").GetBoolean());
        Assert.Equal("System.Int32", symbols[local.Id].GetProperty("returnType").GetString());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.LocalFunction, symbols[local.Id].GetProperty("methodKind").GetInt32());
        Assert.Equal("public", symbols[getter.Id].GetProperty("accessibility").GetString());
        Assert.False(symbols[getter.Id].GetProperty("isStatic").GetBoolean());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.PropertyGet, symbols[getter.Id].GetProperty("methodKind").GetInt32());
        Assert.All(new[] { addition, conversion }, symbol =>
        {
            Assert.Equal("public", symbols[symbol.Id].GetProperty("accessibility").GetString());
            Assert.True(symbols[symbol.Id].GetProperty("isStatic").GetBoolean());
        });
        Assert.Equal("Test.A", symbols[addition.Id].GetProperty("returnType").GetString());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.UserDefinedOperator, symbols[addition.Id].GetProperty("methodKind").GetInt32());
        Assert.Equal("System.Int32", symbols[conversion.Id].GetProperty("returnType").GetString());
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.Conversion, symbols[conversion.Id].GetProperty("methodKind").GetInt32());
    }

    [Fact]
    public void GraphOutputFormatterWritesAsyncTreeLineAndJsonWithNoPathAndTruncationStates()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        var middle = CreateSymbol(AsyncRole.None, 1, id: 102, displayName: "Example.Middle()");
        var origin = CreateSymbol(AsyncRole.DeclaredAsync, 0, id: 103, displayName: "Example.EndAsync()");
        var result = new AsyncPathResult(CreateProfile(), root, [root, middle, origin], Found: true, Truncated: true);
        var formatter = new GraphOutputFormatter(shortNames: false);

        var tree = CaptureText(() => formatter.WriteAsyncPath(result, "tree"));
        var line = CaptureText(() => formatter.WriteAsyncPath(result, "line"));
        using var json = CaptureJson(() => formatter.WriteAsyncPath(result, "json"));

        Assert.Equal(
            "Example.Root()" + Environment.NewLine +
            "└─ Example.Middle()" + Environment.NewLine +
            "   └─ async Example.EndAsync()" + Environment.NewLine +
            "      └─ <truncated>" + Environment.NewLine,
            tree);
        Assert.Equal(
            "Example.Root() -> Example.Middle() -> async Example.EndAsync() -> <truncated>" + Environment.NewLine,
            line);
        Assert.True(json.RootElement.GetProperty("found").GetBoolean());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(101, json.RootElement.GetProperty("root").GetProperty("id").GetInt64());
        Assert.Equal([101L, 102L, 103L], json.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("id").GetInt64()));

        var noPath = new AsyncPathResult(CreateProfile(), root, [], Found: false, Truncated: false);
        Assert.Equal(
            "No reachable asynchronous function: Example.Root()" + Environment.NewLine,
            CaptureText(() => formatter.WriteAsyncPath(noPath, "tree")));
        Assert.Equal(
            "No reachable asynchronous function: Example.Root()" + Environment.NewLine,
            CaptureText(() => formatter.WriteAsyncPath(noPath, "line")));
        using var noPathJson = CaptureJson(() => formatter.WriteAsyncPath(noPath, "json"));
        Assert.False(noPathJson.RootElement.GetProperty("found").GetBoolean());
        Assert.Empty(noPathJson.RootElement.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public void GraphOutputFormatterWritesCallerTreeMermaidAndJsonWithEscapedUniqueEdges()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Target()");
        var caller = CreateSymbol(
            AsyncRole.None,
            null,
            id: 202,
            displayName: "Example.Caller(\"quoted\")\n[bracket]");
        var result = new CallerTreeResult(
            CreateProfile(),
            root,
            [new CallerTreeNode(root, 0), new CallerTreeNode(caller, 1)],
            [new CallerTreeEdge(caller.Id, root.Id), new CallerTreeEdge(caller.Id, root.Id)],
            Truncated: true);
        var formatter = new GraphOutputFormatter(shortNames: false);

        var tree = CaptureText(() => formatter.WriteCallerTree(result, "tree"));
        var mermaid = CaptureText(() => formatter.WriteCallerTree(result, "mermaid"));
        using var json = CaptureJson(() => formatter.WriteCallerTree(result, "json"));

        Assert.Equal(
            "Example.Target()" + Environment.NewLine +
            "└─ Example.Caller(\"quoted\") [bracket]" + Environment.NewLine +
            "└─ <truncated>" + Environment.NewLine,
            tree);
        Assert.StartsWith("flowchart TD" + Environment.NewLine, mermaid, StringComparison.Ordinal);
        Assert.Contains("n101[\"Example.Target()\"]", mermaid);
        Assert.Contains("n202[\"Example.Caller(&quot;quoted&quot;)<br/>&#91;bracket&#93;\"]", mermaid);
        Assert.Equal(1, mermaid.Split("n202 --> n101", StringSplitOptions.None).Length - 1);
        Assert.Contains("%% truncated", mermaid);
        var callerNode = Assert.Single(json.RootElement.GetProperty("nodes").EnumerateArray(), node =>
            node.GetProperty("symbol").GetProperty("id").GetInt64() == caller.Id);
        Assert.Equal(1, callerNode.GetProperty("depth").GetInt32());
        var edge = Assert.Single(json.RootElement.GetProperty("edges").EnumerateArray());
        Assert.Equal(caller.Id, edge.GetProperty("callerSymbolId").GetInt64());
        Assert.Equal(root.Id, edge.GetProperty("calleeSymbolId").GetInt64());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void GraphOutputFormatterUsesCallerEdgesForBranchesAndAdditionalEdges()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        var a = CreateSymbol(AsyncRole.None, null, id: 102, displayName: "Example.A()");
        var z = CreateSymbol(AsyncRole.None, null, id: 103, displayName: "Example.Z()");
        var a2 = CreateSymbol(AsyncRole.None, null, id: 104, displayName: "Example.A2()");
        var z2 = CreateSymbol(AsyncRole.None, null, id: 105, displayName: "Example.Z2()");
        var shared = CreateSymbol(AsyncRole.None, null, id: 106, displayName: "Example.Shared()");
        var result = new CallerTreeResult(
            CreateProfile(),
            root,
            [
                new CallerTreeNode(root, 0),
                new CallerTreeNode(a, 1),
                new CallerTreeNode(z, 1),
                new CallerTreeNode(a2, 2),
                new CallerTreeNode(z2, 2),
                new CallerTreeNode(shared, 2),
            ],
            [
                new CallerTreeEdge(root.Id, a.Id),
                new CallerTreeEdge(a.Id, root.Id),
                new CallerTreeEdge(z.Id, root.Id),
                new CallerTreeEdge(a2.Id, z.Id),
                new CallerTreeEdge(z2.Id, a.Id),
                new CallerTreeEdge(shared.Id, z.Id),
                new CallerTreeEdge(shared.Id, a.Id),
            ],
            Truncated: true);
        var formatter = new GraphOutputFormatter(shortNames: false);

        var tree = CaptureText(() => formatter.WriteCallerTree(result, "tree"));
        var mermaid = CaptureText(() => formatter.WriteCallerTree(result, "mermaid"));
        using var json = CaptureJson(() => formatter.WriteCallerTree(result, "json"));

        Assert.Equal(
            "Example.Root()" + Environment.NewLine +
            "└─ Example.A()" + Environment.NewLine +
            "   └─ Example.Shared()" + Environment.NewLine +
            "   └─ Example.Z2()" + Environment.NewLine +
            "└─ Example.Z()" + Environment.NewLine +
            "   └─ Example.A2()" + Environment.NewLine +
            "└─ <truncated>" + Environment.NewLine +
            "Additional edges:" + Environment.NewLine +
            "  Example.Root() -> Example.A()" + Environment.NewLine +
            "  Example.Shared() -> Example.Z()" + Environment.NewLine,
            tree);
        Assert.All(
            [
                "n101[\"Example.Root()\"]",
                "n102[\"Example.A()\"]",
                "n103[\"Example.Z()\"]",
                "n104[\"Example.A2()\"]",
                "n105[\"Example.Z2()\"]",
                "n106[\"Example.Shared()\"]",
                "n101 --> n102",
                "n102 --> n101",
                "n103 --> n101",
                "n104 --> n103",
                "n105 --> n102",
                "n106 --> n102",
                "n106 --> n103",
            ],
            edge => Assert.Contains(edge, mermaid));
        Assert.Equal(
            [101L, 102L, 103L, 104L, 106L, 105L],
            json.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(node => node.GetProperty("symbol").GetProperty("id").GetInt64()));
        Assert.Equal(
            ["101->102", "102->101", "103->101", "104->103", "105->102", "106->102", "106->103"],
            json.RootElement.GetProperty("edges").EnumerateArray()
                .Select(edge => $"{edge.GetProperty("callerSymbolId").GetInt64()}->{edge.GetProperty("calleeSymbolId").GetInt64()}"));
    }

    [Fact]
    public void GraphOutputFormatterHonorsCancellationBeforeWriting()
    {
        var root = CreateSymbol(AsyncRole.None, null, id: 101, displayName: "Example.Root()");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new GraphOutputFormatter(shortNames: false).WriteAsyncPath(
            new AsyncPathResult(CreateProfile(), root, [root], Found: true, Truncated: false),
            "tree",
            cancellation.Token));
    }

    [Fact]
    public void OrderNodes_ObservesCancellationDuringMaterialization()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => GraphOutputFormatter.OrderNodes(
            CancelBeforeYieldingNode(cancellation),
            cancellation.Token));
    }

    [Fact]
    public void OrderNodes_ObservesCancellationDuringOrdering()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;

        Assert.Throws<OperationCanceledException>(() => GraphOutputFormatter.OrderNodes(
            [
                new CallerTreeNode(CreateSymbol(AsyncRole.None, null, id: 3, displayName: "Example.C()"), 1),
                new CallerTreeNode(CreateSymbol(AsyncRole.None, null, id: 2, displayName: "Example.B()"), 1),
                new CallerTreeNode(CreateSymbol(AsyncRole.None, null, id: 1, displayName: "Example.A()"), 1),
            ],
            cancellation.Token,
            () =>
            {
                comparisons++;
                cancellation.Cancel();
            }));

        Assert.True(comparisons > 0);
    }

    [Fact]
    public void OrderEdges_ObservesCancellationDuringOrdering()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;

        Assert.Throws<OperationCanceledException>(() => GraphOutputFormatter.OrderEdges(
            [new CallerTreeEdge(3, 1), new CallerTreeEdge(2, 1), new CallerTreeEdge(1, 1)],
            cancellation.Token,
            () =>
            {
                comparisons++;
                cancellation.Cancel();
            }));

        Assert.True(comparisons > 0);
    }

    [Fact]
    public void OutputFormatterHonorsCancellationBeforeWritingJson()
    {
        var symbol = CreateSymbol(AsyncRole.None, null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CaptureText(() => new OutputFormatter("json")
            .WriteSymbols(new QueryContext(CreateProfile(), [symbol]), cancellation.Token)));
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

        Assert.Contains("Caller::Run(String) -> Callee::Execute(Task)", output);
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
        Assert.Equal("Worker::Run(Task)", outputSymbol.GetProperty("displayName").GetString());
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

    private static IEnumerable<CallerTreeNode> CancelBeforeYieldingNode(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        yield return new CallerTreeNode(CreateSymbol(AsyncRole.None, null), 0);
    }

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
        string namespaceName = "Example",
        IndexedSymbolKind kind = IndexedSymbolKind.Method,
        string name = "Method",
        int? methodKind = null,
        bool isStatic = false,
        string? returnTypeKey = null,
        string? normalizedSource = null,
        int? accessibility = null) => new(
        Id: id,
        StableKey: $"symbol-{id}",
        Kind: kind,
        Name: name,
        NamespaceName: namespaceName,
        TypeSimpleName: "Example",
        TypeMetadataName: "Example",
        FullyQualifiedName: displayName,
        DisplayName: displayName,
        ContainingSymbolId: null,
        Arity: 0,
        ParameterCount: 0,
        MethodKind: methodKind,
        IsStatic: isStatic,
        IsAbstract: false,
        IsVirtual: false,
        IsOverride: false,
        AsyncRole: asyncRole,
        AsyncInvolvementDepth: asyncInvolvementDepth,
        AsyncNextSymbolId: null,
        ReturnTypeKey: returnTypeKey,
        NormalizedSource: normalizedSource,
        NormalizedSourceHash: null,
        DocumentPath: null,
        SourceStart: null,
        SourceLength: null,
        IsGenerated: false,
        AssemblyName: null,
        Parameters: parameters ?? [],
        TypeKind: null,
        Accessibility: accessibility);

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
