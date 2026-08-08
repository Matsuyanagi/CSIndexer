using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class ExecutableSymbolExtractionTests
{
    [Fact]
    public async Task AnalyzeAsync_ExtractsReturnTypesAndNormalizedSourceForExecutableSymbols()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            namespace Test;

            public sealed class A
            {
                public A() { }

                public async Task<int> ExecuteAsync()
                {
                    await Task.Yield();
                    return 1;
                }

                public int Value
                {
                    get { return 7; }
                    set { _ = value; }
                }

                public static A operator +(A left, A right) => left;
                public static implicit operator int(A value) => 0;

                public void Host()
                {
                    int Local() => 2;
                    Func<int> callback = () => Local();
                    _ = callback();
                }
            }
            """;

        var snapshot = await AnalyzeAsync(("Executables.cs", source));

        Assert.Equal("System.Threading.Tasks.Task<System.Int32>", Find(snapshot, "ExecuteAsync").ReturnTypeKey);
        Assert.Null(Find(snapshot, ".ctor").ReturnTypeKey);
        Assert.Equal("System.Int32", FindLambda(snapshot).ReturnTypeKey);
        Assert.Equal("System.Int32", Find(snapshot, "get_Value").ReturnTypeKey);
        Assert.NotNull(Find(snapshot, "ExecuteAsync").NormalizedSource);
        Assert.NotNull(FindLambda(snapshot).NormalizedSourceHash);
        Assert.NotNull(Find(snapshot, "op_Addition").NormalizedSource);
        Assert.NotNull(Find(snapshot, "op_Implicit").NormalizedSource);
        Assert.NotNull(Find(snapshot, "Local").NormalizedSource);

        var getter = Find(snapshot, "get_Value");
        var setter = Find(snapshot, "set_Value");
        Assert.Equal(IndexedSymbolKind.Method, getter.Kind);
        Assert.Equal(IndexedSymbolKind.Method, setter.Kind);
        Assert.Equal("get{return 7;}", getter.NormalizedSource);
        Assert.Equal("set{_=value;}", setter.NormalizedSource);
        Assert.NotNull(getter.SourceStart);
        Assert.NotNull(getter.SourceLength);
        Assert.NotNull(setter.SourceStart);
        Assert.NotNull(setter.SourceLength);
        Assert.Equal("get { return 7; }", source.Substring(getter.SourceStart.Value, getter.SourceLength.Value));
        Assert.Equal("set { _ = value; }", source.Substring(setter.SourceStart.Value, setter.SourceLength.Value));
    }

    [Fact]
    public async Task AnalyzeAsync_NamesLambdasByNearestNonLambdaOwnerWhileKeepingImmediateContainment()
    {
        const string declarations = """
            using System;

            namespace Test;

            public partial class A
            {
                public Action member1 = () => { };
                public Action member2 = () => { };
                public Func<int> Property { get; } = () => 1;
                public event Action Changed = () => { };
            }
            """;
        const string implementation = """
            using System;

            namespace Test;

            public partial class A
            {
                public void Run()
                {
                    Action first = () => { };
                    Action second = () =>
                    {
                        Action nested = () => Target();
                        nested();
                    };
                }

                private static void Target() { }
            }
            """;

        var snapshot = await AnalyzeAsync(("Declarations.cs", declarations), ("Implementation.cs", implementation));

        var expectedNames = new[]
        {
            "Test.A::<initializer:member1>::<lambda#1>",
            "Test.A::<initializer:member2>::<lambda#1>",
            "Test.A::<initializer:Property>::<lambda#1>",
            "Test.A::<initializer:Changed>::<lambda#1>",
            "Test.A::Run()::<lambda#1>",
            "Test.A::Run()::<lambda#2>",
            "Test.A::Run()::<lambda#3>",
        };
        var actualNames = snapshot.Symbols.Values
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Lambda)
            .Select(symbol => symbol.DisplayName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNames.OrderBy(name => name, StringComparer.Ordinal), actualNames);

        var runSecond = FindLambda(snapshot, "Test.A::Run()::<lambda#2>");
        var nested = FindLambda(snapshot, "Test.A::Run()::<lambda#3>");
        Assert.Equal(runSecond.StableKey, nested.ContainingSymbolKey);

        var target = Find(snapshot, "Target");
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == nested.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
    }

    [Fact]
    public async Task AnalyzeAsync_IndexesInitializersInLaterPartialDocument()
    {
        const string firstPart = """
            namespace Test;

            public partial class A
            {
                private static void Target() { }
            }
            """;
        const string laterPart = """
            using System;

            namespace Test;

            public partial class A
            {
                public Action LaterField = () => Target();
                public Func<int> LaterProperty { get; } = () => { Target(); return 1; };
                public event Action LaterEvent = () => Target();
            }
            """;

        var snapshot = await AnalyzeAsync(("First.cs", firstPart), ("Later.cs", laterPart));

        var expectedNames = new[]
        {
            "Test.A::<initializer:LaterField>::<lambda#1>",
            "Test.A::<initializer:LaterProperty>::<lambda#1>",
            "Test.A::<initializer:LaterEvent>::<lambda#1>",
        };
        var actualNames = snapshot.Symbols.Values
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Lambda)
            .Select(symbol => symbol.DisplayName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNames.OrderBy(name => name, StringComparer.Ordinal), actualNames);

        var eventLambda = FindLambda(snapshot, "Test.A::<initializer:LaterEvent>::<lambda#1>");
        var target = Find(snapshot, "Target");
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == eventLambda.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
    }

    [Fact]
    public async Task AnalyzeAsync_IndexesExpressionBodiedPropertyAndIndexerGettersAsLambdaOwners()
    {
        const string source = """
            using System;

            namespace Test;

            public sealed class A
            {
                public Func<int> Factory => () => Target();
                public Func<int> this[int index] => () => Target();

                private static int Target() => 1;
            }
            """;

        var snapshot = await AnalyzeAsync(("ExpressionBodiedMembers.cs", source));

        var factoryGetter = Find(snapshot, "get_Factory");
        var indexerGetter = Find(snapshot, "get_Item");
        Assert.Equal(IndexedSymbolKind.Method, factoryGetter.Kind);
        Assert.Equal(IndexedSymbolKind.Method, indexerGetter.Kind);
        Assert.Equal("System.Func<System.Int32>", factoryGetter.ReturnTypeKey);
        Assert.Equal("System.Func<System.Int32>", indexerGetter.ReturnTypeKey);
        Assert.Equal("public Func<int>Factory=>()=>Target();", factoryGetter.NormalizedSource);
        Assert.Equal("public Func<int>this[int index]=>()=>Target();", indexerGetter.NormalizedSource);
        Assert.NotNull(factoryGetter.SourceStart);
        Assert.NotNull(factoryGetter.SourceLength);
        Assert.NotNull(indexerGetter.SourceStart);
        Assert.NotNull(indexerGetter.SourceLength);
        Assert.Equal(
            "public Func<int> Factory => () => Target();",
            source.Substring(factoryGetter.SourceStart.Value, factoryGetter.SourceLength.Value));
        Assert.Equal(
            "public Func<int> this[int index] => () => Target();",
            source.Substring(indexerGetter.SourceStart.Value, indexerGetter.SourceLength.Value));

        var factoryLambda = FindLambdaOwnedBy(snapshot, factoryGetter);
        var indexerLambda = FindLambdaOwnedBy(snapshot, indexerGetter);
        Assert.Equal("()=>Target()", factoryLambda.NormalizedSource);
        Assert.Equal("()=>Target()", indexerLambda.NormalizedSource);
        Assert.Equal("System.Int32", factoryLambda.ReturnTypeKey);
        Assert.Equal("System.Int32", indexerLambda.ReturnTypeKey);
        Assert.Equal(factoryGetter.StableKey, factoryLambda.ContainingSymbolKey);
        Assert.Equal(indexerGetter.StableKey, indexerLambda.ContainingSymbolKey);

        var target = Find(snapshot, "Target");
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == factoryLambda.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == indexerLambda.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
    }

    private static SymbolData Find(IndexSnapshot snapshot, string name) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == name);

    private static SymbolData FindLambda(IndexSnapshot snapshot) =>
        Assert.Single(snapshot.Symbols.Values, symbol => symbol.Kind == IndexedSymbolKind.Lambda);

    private static SymbolData FindLambda(IndexSnapshot snapshot, string displayName) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda && symbol.DisplayName == displayName);

    private static SymbolData FindLambdaOwnedBy(IndexSnapshot snapshot, SymbolData owner) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda && symbol.ContainingSymbolKey == owner.StableKey);

    private static async Task<IndexSnapshot> AnalyzeAsync(params (string Path, string Source)[] documents)
    {
        using var temporary = new TempDirectory();
        foreach (var (path, source) in documents)
        {
            temporary.Write(path, source);
        }

        var options = new IndexOptions
        {
            InputPath = temporary.Path,
            ForcedMode = InputMode.Directory,
        };
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(
            input,
            options,
            TestContext.Current.CancellationToken);
        var result = await coordinator.AnalyzeAsync(
            input,
            options,
            fingerprint,
            RequestHasher.Build(input, options),
            TestContext.Current.CancellationToken);
        return result.Snapshot;
    }
}
