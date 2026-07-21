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

        AsyncInvolvementPropagator.Apply(snapshot);

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

        AsyncInvolvementPropagator.Apply(snapshot);

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

        AsyncInvolvementPropagator.Apply(snapshot);

        Assert.Equal(1, snapshot.Symbols["caller"].AsyncInvolvementDepth);
    }

    private static void AddMethod(IndexSnapshot snapshot, string key, AsyncRole role) =>
        snapshot.Symbols[key] = new SymbolData
        {
            StableKey = key,
            Kind = IndexedSymbolKind.Method,
            Name = key,
            NamespaceName = string.Empty,
            FullyQualifiedName = key,
            DisplayName = key,
            AsyncRole = role,
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
