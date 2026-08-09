using CsIndex.Core.Analysis;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class AsyncInvolvementPropagatorTests
{
    [Fact(Timeout = 5_000)]
    public void Apply_PropagatesShortestDistanceThroughCycle()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "a", AsyncRole.None);
        AddMethod(snapshot, "b", AsyncRole.None);
        AddMethod(snapshot, "c", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "a", "b");
        AddCall(snapshot, "b", "a");
        AddCall(snapshot, "b", "c");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Symbols["a"].AsyncInvolvementDepth);
        Assert.Equal(1, snapshot.Symbols["b"].AsyncInvolvementDepth);
        Assert.Equal(0, snapshot.Symbols["c"].AsyncInvolvementDepth);
    }

    [Fact(Timeout = 5_000)]
    public void Apply_LeavesCycleWithoutAsyncOriginUnrelated()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "a", AsyncRole.None);
        AddMethod(snapshot, "b", AsyncRole.None);
        AddCall(snapshot, "a", "b");
        AddCall(snapshot, "b", "a");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Null(snapshot.Symbols["a"].AsyncInvolvementDepth);
        Assert.Null(snapshot.Symbols["b"].AsyncInvolvementDepth);
    }

    [Fact]
    public void Apply_UsesShortestPathAcrossMultipleOrigins()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "caller", AsyncRole.None);
        AddMethod(snapshot, "bridge", AsyncRole.None);
        AddMethod(snapshot, "near", AsyncRole.ReturnsAwaitable);
        AddMethod(snapshot, "far", AsyncRole.ContainsAwait);
        AddCall(snapshot, "caller", "near");
        AddCall(snapshot, "caller", "bridge");
        AddCall(snapshot, "bridge", "far");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.Symbols["caller"].AsyncInvolvementDepth);
    }

    [Fact]
    public void Apply_RecordsOneNextHopAndDoesNotReplaceAnEqualPath()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "root", AsyncRole.None);
        AddMethod(snapshot, "middle-a", AsyncRole.None);
        AddMethod(snapshot, "middle-b", AsyncRole.None);
        AddMethod(snapshot, "async-a", AsyncRole.DeclaredAsync);
        AddMethod(snapshot, "async-b", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "root", "middle-a");
        AddCall(snapshot, "root", "middle-b");
        AddCall(snapshot, "middle-a", "async-a");
        AddCall(snapshot, "middle-b", "async-b");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Symbols["root"].AsyncInvolvementDepth);
        Assert.Equal("middle-a", snapshot.Symbols["root"].AsyncNextSymbolKey);
    }

    [Fact]
    public void Apply_UsesStableOrderingWhenSymbolsAndCallsAreInsertedInTheOppositeOrder()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "root", AsyncRole.None);
        AddMethod(snapshot, "middle-b", AsyncRole.None);
        AddMethod(snapshot, "async-b", AsyncRole.DeclaredAsync);
        AddMethod(snapshot, "middle-a", AsyncRole.None);
        AddMethod(snapshot, "async-a", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "root", "middle-b");
        AddCall(snapshot, "middle-b", "async-b");
        AddCall(snapshot, "root", "middle-a");
        AddCall(snapshot, "middle-a", "async-a");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);
        var firstNext = snapshot.Symbols["root"].AsyncNextSymbolKey;
        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal("middle-a", firstNext);
        Assert.Equal(firstNext, snapshot.Symbols["root"].AsyncNextSymbolKey);
    }

    [Fact]
    public void Apply_UpdatesNextHopWhenAStrictlyShorterPathIsFound()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "root", AsyncRole.None);
        AddMethod(snapshot, "bridge", AsyncRole.None);
        AddMethod(snapshot, "far", AsyncRole.DeclaredAsync);
        AddMethod(snapshot, "near", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "root", "bridge");
        AddCall(snapshot, "bridge", "far");
        AddCall(snapshot, "root", "near");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.Symbols["root"].AsyncInvolvementDepth);
        Assert.Equal("near", snapshot.Symbols["root"].AsyncNextSymbolKey);
    }

    [Fact]
    public void Apply_OriginsHaveNullNextAndCyclesTerminate()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "a", AsyncRole.None);
        AddMethod(snapshot, "b", AsyncRole.None);
        AddMethod(snapshot, "origin", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "a", "b");
        AddCall(snapshot, "b", "a");
        AddCall(snapshot, "b", "origin");
        AddCall(snapshot, "origin", "origin");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal("b", snapshot.Symbols["a"].AsyncNextSymbolKey);
        Assert.Equal("origin", snapshot.Symbols["b"].AsyncNextSymbolKey);
        Assert.Null(snapshot.Symbols["origin"].AsyncNextSymbolKey);
    }

    [Fact(Timeout = 5_000)]
    public void Apply_SelfRecursiveOriginTerminatesAtDepthZero()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "origin", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "origin", "origin");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(0, snapshot.Symbols["origin"].AsyncInvolvementDepth);
    }

    [Fact]
    public void Apply_DoesNotPropagateFromAsyncOriginToSynchronousCallee()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "origin", AsyncRole.DeclaredAsync);
        AddMethod(snapshot, "synchronousCallee", AsyncRole.None);
        AddCall(snapshot, "origin", "synchronousCallee");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(0, snapshot.Symbols["origin"].AsyncInvolvementDepth);
        Assert.Null(snapshot.Symbols["synchronousCallee"].AsyncInvolvementDepth);
    }

    [Fact]
    public void Apply_DoesNotPersistAPathThroughMetadataAwaitableCallee()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "source-caller", AsyncRole.None);
        AddMethod(snapshot, "metadata-awaitable", AsyncRole.ReturnsAwaitable, sourceBacked: false);
        AddCall(snapshot, "source-caller", "metadata-awaitable");

        AsyncInvolvementPropagator.Apply(snapshot, TestContext.Current.CancellationToken);

        Assert.Null(snapshot.Symbols["source-caller"].AsyncInvolvementDepth);
        Assert.Null(snapshot.Symbols["source-caller"].AsyncNextSymbolKey);
        Assert.Null(snapshot.Symbols["metadata-awaitable"].AsyncInvolvementDepth);
        Assert.Null(snapshot.Symbols["metadata-awaitable"].AsyncNextSymbolKey);
    }

    [Fact]
    public void Apply_ObservesCancellationDuringCallOrdering()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "caller-c", AsyncRole.None);
        AddMethod(snapshot, "origin-c", AsyncRole.DeclaredAsync);
        AddMethod(snapshot, "caller-b", AsyncRole.None);
        AddMethod(snapshot, "origin-b", AsyncRole.DeclaredAsync);
        AddMethod(snapshot, "caller-a", AsyncRole.None);
        AddMethod(snapshot, "origin-a", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "caller-c", "origin-c");
        AddCall(snapshot, "caller-b", "origin-b");
        AddCall(snapshot, "caller-a", "origin-a");
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;

        Assert.Throws<OperationCanceledException>(() => AsyncInvolvementPropagator.Apply(
            snapshot,
            cancellation.Token,
            () =>
            {
                comparisons++;
                cancellation.Cancel();
            }));

        Assert.True(comparisons > 0);
    }

    private static void AddMethod(
        IndexSnapshot snapshot,
        string key,
        AsyncRole role,
        bool sourceBacked = true) =>
        snapshot.Symbols[key] = new SymbolData
        {
            StableKey = key,
            Kind = IndexedSymbolKind.Method,
            Name = key,
            NamespaceName = string.Empty,
            FullyQualifiedName = key,
            DisplayName = key,
            AsyncRole = role,
            SourceDocumentKey = sourceBacked ? "document" : null,
            NormalizedSource = sourceBacked ? $"void {key}(){{}}" : null,
        };

    private static void AddCall(IndexSnapshot snapshot, string caller, string callee) =>
        snapshot.Calls.Add(new CallData
        {
            CallerSymbolKey = caller,
            CalleeSymbolKey = callee,
            CalleeDefinitionKey = callee,
            ReferenceKind = ReferenceKind.Invocation,
            DispatchKind = DispatchKind.Static,
            ResolutionStatus = ResolutionStatus.Resolved,
            ResolutionReason = ResolutionReason.None,
            DocumentKey = "document",
            SourceStart = 0,
            SourceLength = 1,
        });

    private static IndexSnapshot CreateSnapshot() => new()
    {
        InputRoot = "root",
        InputFingerprint = [],
        RequestHash = [],
        Profile = new AnalysisProfileData
        {
            Name = "test",
            InputMode = InputMode.Directory,
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        },
    };
}
