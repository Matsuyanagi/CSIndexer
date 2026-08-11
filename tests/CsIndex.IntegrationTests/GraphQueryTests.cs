using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
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
    public async Task AsyncPath_ResolvesOwnerQualifiedLambdaRootsWithoutFilteringSavedMethodPathNodes()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var filter = new FunctionTargetFilter(IndexedSymbolKind.Lambda, AsyncStatusFilter.Sync);

        var first = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::ALambdaPathOwner()::<lambda#1>",
            filter: filter,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var second = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::ZLambdaPathOwner()::<lambda#1>",
            filter: filter,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(IndexedSymbolKind.Lambda, first.Root.Kind);
        Assert.Equal(AsyncRole.None, first.Root.AsyncRole);
        Assert.Equal(
            [
                "Alpha.AsyncGraph::ALambdaPathOwner()::<lambda#1>",
                "Alpha.AsyncGraph::Start()",
                "Alpha.AsyncGraph::Middle()",
                "Alpha.AsyncGraph::EndAsync()",
            ],
            first.Nodes.Select(node => node.DisplayName));
        Assert.Contains(first.Nodes, node => node.Kind == IndexedSymbolKind.Method);
        Assert.Contains(first.Nodes, node => node.AsyncRole != AsyncRole.None);
        Assert.True(second.Found);
        Assert.Equal("Alpha.AsyncGraph::ZLambdaPathOwner()::<lambda#1>", second.Root.DisplayName);
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
    public async Task AsyncPath_ReindexPersistsTheSameSelectedEqualRoute()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var before = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::EqualStart()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var beforeNextId = Assert.IsType<long>(before.Root.AsyncNextSymbolId);
        var beforeNext = Assert.Single(before.Nodes, node => node.Id == beforeNextId);

        await fixture.ReindexPrimaryProfileAsync();

        var after = await fixture.Query.FindAsyncPathAsync(
            "Alpha.AsyncGraph::EqualStart()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var afterNextId = Assert.IsType<long>(after.Root.AsyncNextSymbolId);
        var afterNext = Assert.Single(after.Nodes, node => node.Id == afterNextId);

        Assert.Equal(before.Nodes.Select(node => node.DisplayName), after.Nodes.Select(node => node.DisplayName));
        Assert.Equal(beforeNext.DisplayName, afterNext.DisplayName);
        Assert.Contains(afterNext.DisplayName, new[] { "Alpha.AsyncGraph::EqualLeft()", "Alpha.AsyncGraph::EqualRight()" });
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
    public async Task AsyncPath_RejectsASynchronousDepthZeroEndpoint()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetAsyncPathStateAsync(
                root.Id,
                AsyncRole.None,
                asyncInvolvementDepth: 0,
                asyncNextSymbolId: null,
                fixture.PrimaryProfileName,
                cancellationToken);

            var exception = await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
            Assert.Contains("non-origin", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await fixture.SetAsyncPathStateAsync(
                root.Id,
                root.AsyncRole,
                root.AsyncInvolvementDepth,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken);
        }
    }

    [Theory]
    [InlineData("origin-with-null-depth")]
    [InlineData("origin-with-nonzero-depth")]
    [InlineData("null-depth-with-next-hop")]
    public async Task AsyncPath_RejectsIncoherentOriginAndNoPathState(string corruption)
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var symbol = await fixture.GetStoredSymbolAsync(
            corruption.StartsWith("origin", StringComparison.Ordinal)
                ? "Alpha.AsyncGraph::EndAsync()"
                : "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var (depth, nextSymbolId, detail) = corruption switch
        {
            "origin-with-null-depth" => ((int?)null, (long?)null, "async origin"),
            "origin-with-nonzero-depth" => ((int?)1, (long?)null, "async origin"),
            "null-depth-with-next-hop" => ((int?)null, symbol.AsyncNextSymbolId, "null depth"),
            _ => throw new InvalidOperationException($"Unknown corruption: {corruption}"),
        };

        try
        {
            await fixture.SetAsyncPathStateAsync(
                symbol.Id,
                symbol.AsyncRole,
                depth,
                nextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken);

            var exception = await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                symbol.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
            Assert.Contains(detail, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await fixture.SetAsyncPathStateAsync(
                symbol.Id,
                symbol.AsyncRole,
                symbol.AsyncInvolvementDepth,
                symbol.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken);
        }
    }

    [Theory]
    [InlineData(IndexedSymbolKind.Type)]
    [InlineData(IndexedSymbolKind.Initializer)]
    public async Task AsyncPath_RejectsNonExecutableFetchedHopBeforeTruncation(IndexedSymbolKind kind)
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = await fixture.Repository.GetProfileAsync(fixture.PrimaryProfileName, cancellationToken);
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var candidates = await fixture.Repository.FindSymbolCandidatesAsync(
            profile.Id,
            kind: kind,
            sourceOnly: false,
            cancellationToken: cancellationToken);
        var target = Assert.Single(kind == IndexedSymbolKind.Type
            ? candidates.Where(symbol => symbol.DisplayName == "Alpha.AsyncGraph")
            : candidates.Take(1));

        try
        {
            await fixture.SetAsyncPathStateAsync(
                root.Id,
                root.AsyncRole,
                root.AsyncInvolvementDepth,
                target.Id,
                fixture.PrimaryProfileName,
                cancellationToken);

            var exception = await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                maxNodes: 1,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
            Assert.Contains("source-backed executable", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await fixture.SetAsyncPathStateAsync(
                root.Id,
                root.AsyncRole,
                root.AsyncInvolvementDepth,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsMetadataFetchedHopBeforeTruncation()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = await fixture.Repository.GetProfileAsync(fixture.PrimaryProfileName, cancellationToken);
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var metadata = (await fixture.Repository.FindExecutableSymbolsAsync(
                profile.Id,
                sourceOnly: false,
                cancellationToken))
            .First(symbol => symbol.DocumentPath is null);

        try
        {
            await fixture.SetAsyncPathStateAsync(
                root.Id,
                root.AsyncRole,
                root.AsyncInvolvementDepth,
                metadata.Id,
                fixture.PrimaryProfileName,
                cancellationToken);

            var metadataException = await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                maxNodes: 1,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
            Assert.Contains("source-backed executable", metadataException.Message, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {
            await fixture.SetAsyncPathStateAsync(
                root.Id,
                root.AsyncRole,
                root.AsyncInvolvementDepth,
                root.AsyncNextSymbolId,
                fixture.PrimaryProfileName,
                cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsSourceLessFetchedHopBeforeTruncation()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);
        var middle = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Middle()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetSymbolSourceDefinitionAsync(
                middle.Id,
                middle.NormalizedSource,
                documentPath: null,
                fixture.PrimaryProfileName,
                cancellationToken);

            var exception = await Assert.ThrowsAsync<IndexDatabaseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                maxNodes: 1,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
            Assert.Contains("source-backed executable", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await fixture.SetSymbolSourceDefinitionAsync(
                middle.Id,
                middle.NormalizedSource,
                middle.DocumentPath,
                fixture.PrimaryProfileName,
                cancellationToken);
        }
    }

    [Fact]
    public async Task AsyncPath_RejectsRootWithoutNormalizedSourceDuringTargetResolution()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = await fixture.GetStoredSymbolAsync(
            "Alpha.AsyncGraph::Start()",
            fixture.PrimaryProfileName,
            cancellationToken);

        try
        {
            await fixture.SetSymbolSourceDefinitionAsync(
                root.Id,
                normalizedSource: null,
                documentPath: root.DocumentPath,
                fixture.PrimaryProfileName,
                cancellationToken);

            var exception = await Assert.ThrowsAsync<SymbolQueryParseException>(() => fixture.Query.FindAsyncPathAsync(
                root.DisplayName,
                profileName: fixture.PrimaryProfileName,
                cancellationToken: cancellationToken));
            Assert.Equal(
                "No source-backed executable matches graph query: Alpha.AsyncGraph::Start()",
                exception.Message);
        }
        finally
        {
            await fixture.SetSymbolSourceDefinitionAsync(
                root.Id,
                root.NormalizedSource,
                root.DocumentPath,
                fixture.PrimaryProfileName,
                cancellationToken);
        }
    }

    [Fact]
    public async Task GraphRootResolutionDistinguishesMissingAndAmbiguousMethodsWithCanonicalCandidates()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;

        var ambiguous = await Assert.ThrowsAsync<SymbolQueryParseException>(() => fixture.Query.FindAsyncPathAsync(
            "Alpha.AClass::Play",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken));
        var missing = await Assert.ThrowsAsync<SymbolQueryParseException>(() => fixture.Query.FindAsyncPathAsync(
            "Alpha.AClass::Missing()",
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken));

        Assert.Equal(
            "Graph query is ambiguous for 'Alpha.AClass::Play'. Candidates: " +
            "Alpha.AClass::Play(), Alpha.AClass::Play(System.String)",
            ambiguous.Message);
        Assert.Equal(
            "No source-backed executable matches graph query: Alpha.AClass::Missing()",
            missing.Message);
    }

    [Fact]
    public async Task GraphRootResolution_ReportsSuffixOnlyLambdaAmbiguityInStableCandidateOrder()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var filter = new FunctionTargetFilter(IndexedSymbolKind.Lambda, AsyncStatusFilter.Sync);

        var asyncPath = await Assert.ThrowsAsync<SymbolQueryParseException>(() => fixture.Query.FindAsyncPathAsync(
            "::<lambda#1>",
            filter: filter,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken));
        var callerTree = await Assert.ThrowsAsync<SymbolQueryParseException>(() => fixture.Query.FindCallerTreeAsync(
            "::<lambda#1>",
            filter: filter,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken));

        Assert.Equal(asyncPath.Message, callerTree.Message);
        Assert.StartsWith(
            "Graph query is ambiguous for '::<lambda#1>'. Candidates: ",
            asyncPath.Message,
            StringComparison.Ordinal);
        var firstCandidate = asyncPath.Message.IndexOf(
            "Alpha.AsyncGraph::ALambdaPathOwner()::<lambda#1>",
            StringComparison.Ordinal);
        var secondCandidate = asyncPath.Message.IndexOf(
            "Alpha.AsyncGraph::ZLambdaPathOwner()::<lambda#1>",
            StringComparison.Ordinal);
        Assert.True(firstCandidate >= 0, asyncPath.Message);
        Assert.True(secondCandidate > firstCandidate, asyncPath.Message);
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
    public async Task CallerTree_RetainsDepthBoundaryEdgesAcrossTreeMermaidAndJson()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::BoundaryTarget()",
            depth: 1,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);
        var root = result.Root;
        var left = Assert.Single(result.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::BoundaryLeft()").Symbol;
        var right = Assert.Single(result.Nodes, node => node.Symbol.DisplayName == "Alpha.CallerGraph::BoundaryRight()").Symbol;
        var expected = new[]
        {
            new CallerTreeEdge(left.Id, root.Id),
            new CallerTreeEdge(right.Id, root.Id),
            new CallerTreeEdge(left.Id, right.Id),
            new CallerTreeEdge(right.Id, left.Id),
        };
        var symbolIds = result.Nodes.ToDictionary(node => node.Symbol.DisplayName, node => node.Symbol.Id, StringComparer.Ordinal);
        var formatter = new GraphOutputFormatter(shortNames: false);

        Assert.Equal(expected.OrderBy(EdgeKey), result.Edges.OrderBy(EdgeKey));

        var tree = CaptureText(() => formatter.WriteCallerTree(result, "tree", cancellationToken));
        Assert.Equal(expected.OrderBy(EdgeKey), ParseTreeEdges(tree, symbolIds).OrderBy(EdgeKey));

        var mermaid = CaptureText(() => formatter.WriteCallerTree(result, "mermaid", cancellationToken));
        Assert.Equal(expected.OrderBy(EdgeKey), ParseMermaidEdges(mermaid).OrderBy(EdgeKey));

        using var json = JsonDocument.Parse(CaptureText(() => formatter.WriteCallerTree(result, "json", cancellationToken)));
        Assert.Equal(expected.OrderBy(EdgeKey), ParseJsonEdges(json).OrderBy(EdgeKey));
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
    public async Task CallerTree_ResolvesALambdaRootWithoutFilteringStoredMethodCallers()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        const string lambdaRoot = "Alpha.CallerGraph::LambdaTreeOwner()::<lambda#1>";
        await fixture.AddResolvedCallAsync(
            "Alpha.CallerGraph::LambdaRootCaller()",
            lambdaRoot,
            fixture.PrimaryProfileName,
            cancellationToken);

        var result = await fixture.Query.FindCallerTreeAsync(
            lambdaRoot,
            filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.Sync),
            depth: 1,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(lambdaRoot, result.Root.DisplayName);
        Assert.Equal(IndexedSymbolKind.Lambda, result.Root.Kind);
        var caller = Assert.Single(result.Nodes, node => node.Depth == 1).Symbol;
        Assert.Equal("Alpha.CallerGraph::LambdaRootCaller()", caller.DisplayName);
        Assert.Equal(IndexedSymbolKind.Method, caller.Kind);
        Assert.DoesNotContain(
            result.Nodes,
            node => node.Symbol.DisplayName == "Alpha.CallerGraph::LambdaTreeOwner()");
        Assert.Contains(new CallerTreeEdge(caller.Id, result.Root.Id), result.Edges);
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
    public async Task CallerTree_ExcludesSystemAndSourceLessReverseCallers()
    {
        await fixture.BuildTask;
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.AddResolvedCallAsync(
            "System.Object::ToString()",
            "Alpha.CallerGraph::FilterTarget()",
            fixture.PrimaryProfileName,
            cancellationToken);

        var result = await fixture.Query.FindCallerTreeAsync(
            "Alpha.CallerGraph::FilterTarget()",
            depth: 1,
            profileName: fixture.PrimaryProfileName,
            cancellationToken: cancellationToken);

        Assert.Equal(
            [
                "Alpha.CallerGraph::FilterTarget()",
                "Alpha.CallerGraph::AllowedFilterCaller()",
            ],
            result.Nodes.Select(node => node.Symbol.DisplayName));
        Assert.All(result.Nodes, node =>
        {
            Assert.NotNull(node.Symbol.DocumentPath);
            Assert.False(node.Symbol.NamespaceName == "System" ||
                         node.Symbol.NamespaceName.StartsWith("System.", StringComparison.Ordinal));
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

    private static IReadOnlyList<CallerTreeEdge> ParseTreeEdges(
        string text,
        IReadOnlyDictionary<string, long> symbolIds)
    {
        var edges = new List<CallerTreeEdge>();
        var nodeAtDepth = new Dictionary<int, long>();
        var additionalEdges = false;
        foreach (var line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line == "Additional edges:")
            {
                additionalEdges = true;
                continue;
            }

            if (additionalEdges)
            {
                var parts = line.Trim().Split(" -> ", StringSplitOptions.None);
                edges.Add(new CallerTreeEdge(symbolIds[parts[0]], symbolIds[parts[1]]));
                continue;
            }

            var marker = line.IndexOf("└─ ", StringComparison.Ordinal);
            if (marker < 0)
            {
                nodeAtDepth[0] = symbolIds[line];
                continue;
            }

            var depth = marker / 3 + 1;
            var callerId = symbolIds[line[(marker + 3)..]];
            edges.Add(new CallerTreeEdge(callerId, nodeAtDepth[depth - 1]));
            nodeAtDepth[depth] = callerId;
        }

        return edges;
    }

    private static IReadOnlyList<CallerTreeEdge> ParseMermaidEdges(string text) => text
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.Contains(" --> ", StringComparison.Ordinal))
        .Select(line => line.Trim().Split(" --> ", StringSplitOptions.None))
        .Select(parts => new CallerTreeEdge(
            long.Parse(parts[0][1..], System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(parts[1][1..], System.Globalization.CultureInfo.InvariantCulture)))
        .ToArray();

    private static IReadOnlyList<CallerTreeEdge> ParseJsonEdges(JsonDocument document) => document.RootElement
        .GetProperty("edges")
        .EnumerateArray()
        .Select(edge => new CallerTreeEdge(
            edge.GetProperty("callerSymbolId").GetInt64(),
            edge.GetProperty("calleeSymbolId").GetInt64()))
        .ToArray();

    private static string EdgeKey(CallerTreeEdge edge) =>
        $"{edge.CallerSymbolId:D20}->{edge.CalleeSymbolId:D20}";
}
