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

            public void Outer()
            {
                Func<Task> nested = async () => await LeafAsync();
            }

            public async Task Awaited() { await LeafAsync(); }
            public Task Forwarded() => LeafAsync();
            public void Stored() { var task = LeafAsync(); }
            public void Passed() { Consume(LeafAsync()); }
            public void Discarded() { _ = LeafAsync(); }
            public void Unobserved() { LeafAsync(); }
            private static void Consume(Task task) { }

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
