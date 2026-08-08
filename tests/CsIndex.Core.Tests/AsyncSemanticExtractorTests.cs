using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class AsyncSemanticExtractorTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Cysharp.Threading.Tasks
        {
            public readonly struct UniTask { }
            public readonly struct UniTask<T> { }
            public readonly struct UniTaskVoid { }
            public interface IUniTaskAsyncEnumerable<T> { }
        }

        public sealed class AsyncCases
        {
            public async Task LeafAsync() => await Task.Yield();
            public Task ForwardTask() => LeafAsync();
            public Task<int> GenericTaskResult() => Task.FromResult(1);
            public ValueTask ValueTaskResult() => default;
            public ValueTask<int> GenericValueTaskResult() => default;
            public Cysharp.Threading.Tasks.UniTask UniTaskResult() => default;
            public Cysharp.Threading.Tasks.UniTask<int> GenericUniTaskResult() => default;
            public Cysharp.Threading.Tasks.UniTaskVoid FireAndForget() => default;
            public Cysharp.Threading.Tasks.IUniTaskAsyncEnumerable<int> UniTaskStream() => default!;

            public async IAsyncEnumerable<int> StreamAsync()
            {
                await Task.Yield();
                yield return 1;
            }

            public async Task ConsumeStreamAsync()
            {
                await foreach (var value in StreamAsync()) { }
            }

            public async Task DisposeAsync()
            {
                await using var resource = new AsyncResource();
            }

            public async Task DisposeWithStatementAsync()
            {
                await using (var resource = new AsyncResource()) { }
            }

            public void Outer()
            {
                Func<Task> nested = async () => await LeafAsync();
            }

            public void OuterWithLocal()
            {
                async Task NestedLocalAsync() => await LeafAsync();
            }

            public async Task Awaited() { await LeafAsync(); }
            public Task Forwarded() => LeafAsync();
            public void Stored() { var task = LeafAsync(); }
            public void Passed() { Consume(LeafAsync()); }
            public void Discarded() { _ = LeafAsync(); }
            public void Unobserved() { LeafAsync(); }
            public void NoneUsage() { if (Check()) { } }
            private static void Consume(Task task) { }
            private static bool Check() => true;

            private sealed class AsyncResource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => default;
            }
        }
        """;

    [Fact]
    public async Task AnalyzeAsync_ExtractsAsyncRolesAndKeepsNestedOwnersSeparate()
    {
        var snapshot = await AnalyzeAsync(Source);

        AssertRole(snapshot, "LeafAsync", AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable | AsyncRole.ContainsAwait);
        AssertRole(snapshot, "ForwardTask", AsyncRole.ReturnsAwaitable);
        AssertRole(snapshot, "UniTaskResult", AsyncRole.ReturnsAwaitable);
        AssertRole(snapshot, "GenericUniTaskResult", AsyncRole.ReturnsAwaitable);
        AssertRole(snapshot, "FireAndForget", AsyncRole.UniTaskVoid);
        AssertRole(snapshot, "UniTaskStream", AsyncRole.ReturnsAsyncEnumerable);
        AssertRole(snapshot, "StreamAsync", AsyncRole.DeclaredAsync | AsyncRole.AsyncIterator |
                                                AsyncRole.ReturnsAsyncEnumerable | AsyncRole.ContainsAwait);
        AssertRole(snapshot, "ConsumeStreamAsync", AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable |
                                                      AsyncRole.UsesAwaitForEach);
        AssertRole(snapshot, "DisposeAsync", AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable |
                                                AsyncRole.UsesAwaitUsing,
            typeSimpleName: "AsyncCases");

        var lambda = Assert.Single(snapshot.Symbols.Values, symbol => symbol.Kind == IndexedSymbolKind.Lambda);
        Assert.Equal(AsyncRole.DeclaredAsync | AsyncRole.ContainsAwait | AsyncRole.ReturnsAwaitable, lambda.AsyncRole);
        Assert.Equal(0, lambda.AsyncInvolvementDepth);

        var outer = GetMethod(snapshot, "Outer");
        Assert.Equal(AsyncRole.None, outer.AsyncRole);
        Assert.Null(outer.AsyncInvolvementDepth);
    }

    [Fact]
    public async Task AnalyzeAsync_ClassifiesEveryLeafAsyncInvocationUsage()
    {
        var snapshot = await AnalyzeAsync(Source);
        var leaf = GetMethod(snapshot, "LeafAsync");
        var expected = new Dictionary<string, AsyncUsageKind>(StringComparer.Ordinal)
        {
            ["Awaited"] = AsyncUsageKind.Awaited,
            ["Forwarded"] = AsyncUsageKind.Forwarded,
            ["Stored"] = AsyncUsageKind.Stored,
            ["Passed"] = AsyncUsageKind.Passed,
            ["Discarded"] = AsyncUsageKind.Discarded,
            ["Unobserved"] = AsyncUsageKind.Unobserved,
        };

        foreach (var (callerName, usageKind) in expected)
        {
            var caller = GetMethod(snapshot, callerName);
            var call = Assert.Single(snapshot.Calls, call =>
                call.CallerSymbolKey == caller.StableKey &&
                call.CalleeDefinitionKey == leaf.StableKey);
            Assert.Equal(usageKind, call.AsyncUsageKind);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_RecordsTheAsyncNextHopForASynchronousCaller()
    {
        var snapshot = await AnalyzeAsync(Source);
        var leaf = GetMethod(snapshot, "LeafAsync");
        var stored = GetMethod(snapshot, "Stored");

        Assert.Equal(1, stored.AsyncInvolvementDepth);
        Assert.Equal(leaf.StableKey, stored.AsyncNextSymbolKey);
        Assert.Null(leaf.AsyncNextSymbolKey);
    }

    [Fact]
    public async Task AnalyzeAsync_ClassifiesGenericTaskAsAwaitable()
    {
        var snapshot = await AnalyzeAsync(Source);

        AssertRole(snapshot, "GenericTaskResult", AsyncRole.ReturnsAwaitable);
    }

    [Theory]
    [InlineData("ValueTaskResult")]
    [InlineData("GenericValueTaskResult")]
    public async Task AnalyzeAsync_ClassifiesValueTaskVariantsAsAwaitable(string methodName)
    {
        var snapshot = await AnalyzeAsync(Source);

        AssertRole(snapshot, methodName, AsyncRole.ReturnsAwaitable);
    }

    [Fact]
    public async Task AnalyzeAsync_KeepsAsyncLocalFunctionSeparateFromOuterMethod()
    {
        var snapshot = await AnalyzeAsync(Source);

        var local = GetMethod(snapshot, "NestedLocalAsync");
        Assert.Equal(
            AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable | AsyncRole.ContainsAwait,
            local.AsyncRole);
        Assert.Equal(0, local.AsyncInvolvementDepth);

        var outer = GetMethod(snapshot, "OuterWithLocal");
        Assert.Equal(AsyncRole.None, outer.AsyncRole);
        Assert.Null(outer.AsyncInvolvementDepth);
    }

    [Fact]
    public async Task AnalyzeAsync_ClassifiesAwaitUsingStatementOnOwningMethod()
    {
        var snapshot = await AnalyzeAsync(Source);

        AssertRole(
            snapshot,
            "DisposeWithStatementAsync",
            AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable | AsyncRole.UsesAwaitUsing);
    }

    [Fact]
    public async Task AnalyzeAsync_ClassifiesInvocationWithoutKnownUsageContextAsNone()
    {
        var snapshot = await AnalyzeAsync(Source);
        var caller = GetMethod(snapshot, "NoneUsage");
        var callee = GetMethod(snapshot, "Check");

        var call = Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == caller.StableKey &&
            call.CalleeDefinitionKey == callee.StableKey);
        Assert.Equal(AsyncUsageKind.None, call.AsyncUsageKind);
    }

    [Fact]
    public async Task AnalyzeAsync_StopsUsageClassificationAtLambdaAndLocalFunctionOwners()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            public sealed class OwnerBoundaries
            {
                public static Task LeafAsync() => Task.CompletedTask;

                public void StoreLambda()
                {
                    Action stored = () => LeafAsync();
                }

                public void PassLambda()
                {
                    Register(() => LeafAsync());
                }

                public void OuterWithLocal()
                {
                    void Local() { LeafAsync(); }
                    Register(Local);
                }

                private static void Register(Action action) { }
            }
            """;
        var snapshot = await AnalyzeAsync(source);
        var leaf = GetMethod(snapshot, "LeafAsync");
        var lambdaKeys = snapshot.Symbols.Values
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Lambda)
            .Select(symbol => symbol.StableKey)
            .ToHashSet(StringComparer.Ordinal);
        var local = GetMethod(snapshot, "Local");

        var lambdaCalls = snapshot.Calls
            .Where(call => call.CalleeDefinitionKey == leaf.StableKey &&
                           lambdaKeys.Contains(call.CallerSymbolKey))
            .ToArray();
        Assert.Equal(2, lambdaCalls.Length);
        Assert.All(lambdaCalls, call => Assert.Equal(AsyncUsageKind.Unobserved, call.AsyncUsageKind));
        var localCall = Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == local.StableKey &&
            call.CalleeDefinitionKey == leaf.StableKey);
        Assert.Equal(AsyncUsageKind.Unobserved, localCall.AsyncUsageKind);
    }

    [Fact]
    public async Task AnalyzeAsync_PreservesAwaitedUsageThroughSameOwnerInvocationWrapper()
    {
        const string source = """
            using System.Threading.Tasks;

            public sealed class WrapperCase
            {
                public static Task LeafAsync() => Task.CompletedTask;
                public async Task AwaitWrappedAsync()
                {
                    await LeafAsync().ConfigureAwait(false);
                }
            }
            """;
        var snapshot = await AnalyzeAsync(source);
        var caller = GetMethod(snapshot, "AwaitWrappedAsync");
        var leaf = GetMethod(snapshot, "LeafAsync");

        var call = Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == caller.StableKey &&
            call.CalleeDefinitionKey == leaf.StableKey);
        Assert.Equal(AsyncUsageKind.Awaited, call.AsyncUsageKind);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotClassifyNonAwaitableStorageAsAsyncUsage()
    {
        const string source = """
            public sealed class SynchronousCase
            {
                public static int Parse() => 1;
                public void StoreSync()
                {
                    var value = Parse();
                }
            }
            """;
        var snapshot = await AnalyzeAsync(source);
        var caller = GetMethod(snapshot, "StoreSync");
        var callee = GetMethod(snapshot, "Parse");

        var call = Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == caller.StableKey &&
            call.CalleeDefinitionKey == callee.StableKey);
        Assert.Equal(AsyncUsageKind.None, call.AsyncUsageKind);
    }

    [Fact]
    public async Task AnalyzeAsync_AssignsFieldAndPropertyInitializerLambdaOperationsToLambda()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            public sealed class InitializerCases
            {
                public static Task<int> LeafAsync() => Task.FromResult(1);
                public Func<Task> Field = async () => { var value = await LeafAsync(); };
                public Func<Task> Property { get; } = async () => { var value = await LeafAsync(); };
            }
            """;
        var snapshot = await AnalyzeAsync(source);
        var leaf = GetMethod(snapshot, "LeafAsync");
        var lambdas = snapshot.Symbols.Values
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Lambda)
            .ToArray();

        Assert.Equal(2, lambdas.Length);
        Assert.All(lambdas, lambda =>
        {
            Assert.Equal(
                AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable | AsyncRole.ContainsAwait,
                lambda.AsyncRole);
            Assert.Equal(0, lambda.AsyncInvolvementDepth);
            Assert.Single(snapshot.Calls, call =>
                call.CallerSymbolKey == lambda.StableKey &&
                call.CalleeDefinitionKey == leaf.StableKey);
        });
        Assert.All(
            snapshot.Symbols.Values.Where(symbol => symbol.Kind == IndexedSymbolKind.Initializer),
            initializer => Assert.Equal(AsyncRole.None, initializer.AsyncRole));
    }

    [Fact]
    public async Task AnalyzeAsync_NumbersLambdasByNearestNonLambdaOwner()
    {
        var snapshot = await AnalyzeAsync(LambdaOwnershipSource);

        var updateFirst = GetLambda(snapshot, "Player::Update()::<lambda#1>");
        var updateSecond = GetLambda(snapshot, "Player::Update()::<lambda#3>");
        var doFirst = GetLambda(snapshot, "Player::Do()::<lambda#1>");
        var doSecond = GetLambda(snapshot, "Player::Do()::<lambda#2>");
        var nested = GetLambda(snapshot, "Player::Update()::<lambda#2>");

        Assert.Equal("Player::Update()::<lambda#1>", updateFirst.DisplayName);
        Assert.Equal("Player::Update()::<lambda#3>", updateSecond.DisplayName);
        Assert.Equal("Player::Do()::<lambda#1>", doFirst.DisplayName);
        Assert.Equal("Player::Do()::<lambda#2>", doSecond.DisplayName);
        Assert.Equal("Player::Update()::<lambda#2>", nested.DisplayName);
        Assert.Equal(updateFirst.StableKey, nested.ContainingSymbolKey);
    }

    [Fact]
    public async Task AnalyzeAsync_AssignsNestedLambdaCallsToTheirNearestLambdaOwner()
    {
        var snapshot = await AnalyzeAsync(LambdaOwnershipSource);
        var updateFirst = GetLambda(snapshot, "Player::Update()::<lambda#1>");
        var updateSecond = GetLambda(snapshot, "Player::Update()::<lambda#3>");
        var nested = GetLambda(snapshot, "Player::Update()::<lambda#2>");
        var update = GetMethod(snapshot, "Update", "Player");

        AssertCallOwner(snapshot, "Play", updateFirst);
        AssertCallOwner(snapshot, "CreateCallbackInnerObj", updateSecond);
        var calcCall = AssertCallOwner(snapshot, "Calc", nested);

        Assert.NotEqual(update.StableKey, calcCall.CallerSymbolKey);
        Assert.NotEqual(updateFirst.StableKey, calcCall.CallerSymbolKey);
    }

    [Fact]
    public async Task AnalyzeAsync_AssignsDelegateInvocationsToTheirSyntacticOwners()
    {
        var snapshot = await AnalyzeAsync(LambdaOwnershipSource);
        var update = GetMethod(snapshot, "Update", "Player");
        var updateFirst = GetLambda(snapshot, "Player::Update()::<lambda#1>");
        var nested = GetLambda(snapshot, "Player::Update()::<lambda#2>");

        AssertInvokeOwner(snapshot, update);
        AssertInvokeOwner(snapshot, updateFirst);
        AssertInvokeOwner(snapshot, nested);

        AssertCallOwner(snapshot, "Play", updateFirst);
        AssertCallOwner(snapshot, "CreateCallbackInnerObj", GetLambda(snapshot, "Player::Update()::<lambda#3>"));
        AssertCallOwner(snapshot, "Calc", nested);
    }

    private const string LambdaOwnershipSource = """
        using System;

        public sealed class Player
        {
            public void Play() { }
            public void CreateCallbackInnerObj() { }
            public void Calc() { }
            private static void NoOp() { }

            public void Update()
            {
                Action callbackToInvoke = NoOp;
                Action first = () =>
                {
                    Play();
                    Action nested = () =>
                    {
                        Calc();
                        callbackToInvoke.Invoke();
                    };
                    nested.Invoke();
                };
                Action second = () => CreateCallbackInnerObj();
                callbackToInvoke.Invoke();
            }

            public void Do()
            {
                Action first = () => Play();
                Action second = () => Calc();
            }
        }
        """;

    private static SymbolData GetLambda(IndexSnapshot snapshot, string displayName) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda &&
            symbol.DisplayName == displayName);

    private static CallData AssertCallOwner(IndexSnapshot snapshot, string calleeName, SymbolData expectedCaller)
    {
        Assert.Equal(IndexedSymbolKind.Lambda, expectedCaller.Kind);
        var callee = GetMethod(snapshot, calleeName, "Player");
        return Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == expectedCaller.StableKey &&
            call.CalleeDefinitionKey == callee.StableKey);
    }

    private static CallData AssertInvokeOwner(IndexSnapshot snapshot, SymbolData expectedCaller)
    {
        var call = Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == expectedCaller.StableKey &&
            call.ReferenceKind == ReferenceKind.Invocation &&
            call.CalleeSymbolKey is not null &&
            snapshot.Symbols.TryGetValue(call.CalleeSymbolKey, out var callee) &&
            callee.Name == "Invoke");
        Assert.Equal(expectedCaller.StableKey, call.CallerSymbolKey);
        return call;
    }

    private static void AssertRole(
        IndexSnapshot snapshot,
        string name,
        AsyncRole expected,
        string? typeSimpleName = null)
    {
        var symbol = GetMethod(snapshot, name, typeSimpleName);
        Assert.Equal(expected, symbol.AsyncRole);
    }

    private static SymbolData GetMethod(IndexSnapshot snapshot, string name, string? typeSimpleName = null) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method &&
            symbol.Name == name &&
            (typeSimpleName is null || symbol.TypeSimpleName == typeSimpleName));

    private static async Task<IndexSnapshot> AnalyzeAsync(string source)
    {
        using var temporary = new TempDirectory();
        temporary.Write("Source.cs", source);
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
