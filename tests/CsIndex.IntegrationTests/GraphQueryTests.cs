using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

public sealed class GraphQueryTests(SemanticIndexFixture fixture)
    : IClassFixture<SemanticIndexFixture>
{
    [Fact]
    public async Task AsyncPath_FollowsThePersistedShortestPathToAnAsyncOrigin()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::Start()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Found);
        Assert.False(result.Truncated);
        Assert.Equal("Alpha.AsyncGraph::Start()", result.Root.DisplayName);
        Assert.Equal(
            ["Alpha.AsyncGraph::Start()", "Alpha.AsyncGraph::Middle()", "Alpha.AsyncGraph::EndAsync()"],
            result.Nodes.Select(node => node.DisplayName));
        Assert.Equal([2, 1, 0], result.Nodes.Select(node => node.AsyncInvolvementDepth));
        Assert.Null(result.Nodes[^1].AsyncNextSymbolId);
    }

    [Fact]
    public async Task AsyncPath_RepresentsSelfAsyncAndUnreachableMethods()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var self = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::SelfAsync()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var unreachable = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::Unreachable()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.True(self.Found);
        Assert.False(self.Truncated);
        Assert.Equal(["Alpha.AsyncGraph::SelfAsync()"], self.Nodes.Select(node => node.DisplayName));
        Assert.False(unreachable.Found);
        Assert.False(unreachable.Truncated);
        Assert.Equal("Alpha.AsyncGraph::Unreachable()", unreachable.Root.DisplayName);
        Assert.Empty(unreachable.Nodes);
    }

    [Fact]
    public async Task AsyncPath_UsesThePersistedNextHopInsteadOfReselectingAnEqualRoute()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::EqualStart()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var left = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::EqualLeft()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var right = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::EqualRight()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var persistedNext = Assert.IsType<long>(root.AsyncNextSymbolId);
        var selected = persistedNext == left.Id ? right : left;
        Assert.Contains(persistedNext, new[] { left.Id, right.Id });

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                selected.Id,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            var result = await fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            Assert.Equal(
                [root.DisplayName, selected.DisplayName, "Alpha.AsyncGraph::EqualEndAsync()"],
                result.Nodes.Select(node => node.DisplayName));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_TraversesACallCycleThatReachesAnAsyncOrigin()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::CycleStart()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Found);
        Assert.False(result.Truncated);
        Assert.Equal(
            ["Alpha.AsyncGraph::CycleStart()", "Alpha.AsyncGraph::CycleMiddle()", "Alpha.AsyncGraph::EndAsync()"],
            result.Nodes.Select(node => node.DisplayName));
    }

    [Fact]
    public async Task AsyncPath_CountsTheRootAgainstTheNodeLimit()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::Start()",
            maxNodes: 1,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Found);
        Assert.True(result.Truncated);
        Assert.Equal(["Alpha.AsyncGraph::Start()"], result.Nodes.Select(node => node.DisplayName));
    }

    [Fact]
    public async Task AsyncPath_RejectsANonOriginWithoutAPersistedNextHop()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                asyncNextSymbolId: null,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsAMissingPersistedNextHop()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                long.MaxValue,
                fixture.PrimaryProfileName,
                allowMissingTarget: true,
                cancellationToken: cancellationToken);

            await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_ValidatesTheNextHopBeforeReportingTruncation()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                long.MaxValue,
                fixture.PrimaryProfileName,
                allowMissingTarget: true,
                cancellationToken: cancellationToken);

            await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                maxNodes: 1,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsAFetchedNonOriginWithoutANextHopBeforeTruncation()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var middle = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Middle()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                middle.DisplayName,
                asyncNextSymbolId: null,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                "Alpha.AsyncGraph::Start()",
                maxNodes: 1,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                middle.DisplayName,
                middle.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsAFetchedOriginWithANextHopBeforeTruncation()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var middle = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Middle()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var origin = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::EndAsync()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                origin.DisplayName,
                middle.Id,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                middle.DisplayName,
                maxNodes: 1,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                origin.DisplayName,
                origin.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsANextHopFromAnotherProfile()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var otherProfileSymbol = await fixture.GetStoredSymbolAsync(
            "Tokyo.SecondaryOnly::Play()",
            fixture.SecondaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                otherProfileSymbol.Id,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                root.DisplayName,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsCyclicOrOriginNextHops()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var start = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var middle = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Middle()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var origin = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::EndAsync()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                middle.DisplayName,
                start.Id,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            var cycleException = await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                start.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
            Assert.Contains("cycle", cycleException.Message, StringComparison.OrdinalIgnoreCase);

            await fixture.SetAsyncNextSymbolIdAsync(
                origin.DisplayName,
                middle.Id,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);

            await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                origin.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
        }
        finally
        {
            await fixture.SetAsyncNextSymbolIdAsync(
                middle.DisplayName,
                middle.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
            await fixture.SetAsyncNextSymbolIdAsync(
                origin.DisplayName,
                origin.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task CallerTree_UsesBreadthFirstDepthBoundsAndTreatsZeroAsUnlimited()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var bounded = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::DepthTarget()",
            depth: 3,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var unlimited = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::DepthTarget()",
            depth: 0,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(
            [
                ("Alpha.CallerGraph::DepthTarget()", 0),
                ("Alpha.CallerGraph::DepthOne()", 1),
                ("Alpha.CallerGraph::DepthTwo()", 2),
                ("Alpha.CallerGraph::DepthThree()", 3),
            ],
            bounded.Nodes.Select(node => (node.Symbol.DisplayName, node.Depth)));
        Assert.DoesNotContain(bounded.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::DepthFour()");
        Assert.Equal(
            [
                ("Alpha.CallerGraph::DepthTarget()", 0),
                ("Alpha.CallerGraph::DepthOne()", 1),
                ("Alpha.CallerGraph::DepthTwo()", 2),
                ("Alpha.CallerGraph::DepthThree()", 3),
                ("Alpha.CallerGraph::DepthFour()", 4),
            ],
            unlimited.Nodes.Select(node => (node.Symbol.DisplayName, node.Depth)));
    }

    [Fact]
    public async Task CallerTree_UsesCallerToCalleeEdgesForInvocationAndObjectCreation()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var invocation = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::DirectTarget()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var objectCreation = await fixture.Query.FindCallerTreeAsync(
            "Alpha.GraphCreated::.ctor()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var directCaller = Assert.Single(invocation.Nodes, node => node.Depth == 1).Symbol;
        var objectCreator = Assert.Single(objectCreation.Nodes, node => node.Depth == 1).Symbol;

        Assert.Equal("Alpha.CallerGraph::DirectCaller()", directCaller.DisplayName);
        Assert.Contains(new CallerTreeEdge(directCaller.Id, invocation.Root.Id), invocation.Edges);
        Assert.Equal("Alpha.CallerGraph::ObjectCreator()", objectCreator.DisplayName);
        Assert.Contains(new CallerTreeEdge(objectCreator.Id, objectCreation.Root.Id), objectCreation.Edges);
    }

    [Fact]
    public async Task CallerTree_RetainsCycleEdgesWithUniqueNodesAndEdges()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::RecursiveTarget()",
            depth: 0,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);
        var right = Assert.Single(result.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::RecursiveRight()").Symbol;
        var left = Assert.Single(result.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::RecursiveLeft()").Symbol;

        Assert.Equal(result.Nodes.Count, result.Nodes.Select(node => node.Symbol.Id).Distinct().Count());
        Assert.Equal(result.Edges.Count, result.Edges.Distinct().Count());
        Assert.Contains(new CallerTreeEdge(right.Id, result.Root.Id), result.Edges);
        Assert.Contains(new CallerTreeEdge(left.Id, right.Id), result.Edges);
        Assert.Contains(new CallerTreeEdge(right.Id, left.Id), result.Edges);
    }

    [Fact]
    public async Task CallerTree_KeepsLambdaCallersWithoutSynthesizingOwnershipEdges()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::LambdaTarget()",
            depth: 0,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);
        var lambda = Assert.Single(result.Nodes, node => node.Depth == 1).Symbol;

        Assert.Contains("Alpha.CallerGraph::LambdaOwner()::<lambda#1>", lambda.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::LambdaOwner()");
        Assert.Contains(new CallerTreeEdge(lambda.Id, result.Root.Id), result.Edges);
    }

    [Fact]
    public async Task CallerTree_SortsNodesAndExcludesExternalMetadataSymbols()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var ordering = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::OrderingTarget()",
            depth: 0,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var metadata = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::MetadataTarget()",
            depth: 0,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(
            [
                "Alpha.CallerGraph::OrderingTarget()",
                "Alpha.CallerGraph::ACaller()",
                "Alpha.CallerGraph::ZCaller()",
            ],
            ordering.Nodes.Select(node => node.Symbol.DisplayName));
        Assert.All(metadata.Nodes, node =>
        {
            Assert.NotNull(node.Symbol.DocumentPath);
            Assert.False(node.Symbol.NamespaceName == "System" ||
                         node.Symbol.NamespaceName.StartsWith("System.", StringComparison.Ordinal));
            Assert.False(string.Equals(node.Symbol.AssemblyName, "System.Private.CoreLib", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task CallerTree_SortsAllCallersAtTheSameDepthBeforeApplyingTheNodeLimit()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var unlimited = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::Root()",
            depth: 0,
            maxNodes: 100,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var limited = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::Root()",
            depth: 0,
            maxNodes: 4,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(
            [
                "Alpha.CallerGraph::Root()",
                "Alpha.CallerGraph::A()",
                "Alpha.CallerGraph::Z()",
                "Alpha.CallerGraph::A2()",
                "Alpha.CallerGraph::Z2()",
            ],
            unlimited.Nodes.Select(node => node.Symbol.DisplayName));
        Assert.Equal(
            [
                "Alpha.CallerGraph::Root()",
                "Alpha.CallerGraph::A()",
                "Alpha.CallerGraph::Z()",
                "Alpha.CallerGraph::A2()",
            ],
            limited.Nodes.Select(node => node.Symbol.DisplayName));
        Assert.True(limited.Truncated);
        Assert.Contains(limited.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::A2()");
        Assert.DoesNotContain(limited.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::Z2()");
    }

    [Fact]
    public async Task CallerTree_CountsTheRootAgainstMaxNodesAndMarksTruncation()
    {
        await fixture.BuildTask;

        var result = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::OrderingTarget()",
            depth: 0,
            maxNodes: 1,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Truncated);
        var root = Assert.Single(result.Nodes);
        Assert.Equal(result.Root.Id, root.Symbol.Id);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task GraphQueries_AreProfileScopedAndHonorCancellation()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<SymbolQueryParseException>(() => fixture.Query.FindCallerTreeAsync(
            "Tokyo.SecondaryOnly::Play()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken));
        var secondary = await fixture.Query.FindCallerTreeAsync(
            "Tokyo.SecondaryOnly::Play()",
            profileName: fixture.SecondaryProfileName,
            cancellationToken: cancellationToken);
        Assert.Equal(fixture.SecondaryProfileName, secondary.Profile.Name);
        Assert.Equal(["Tokyo.SecondaryOnly::Play()"], secondary.Nodes.Select(node => node.Symbol.DisplayName));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::Start()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::DirectTarget()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellation.Token));
    }
}
