using System.Text.Json;
using CsIndex.Cli;
using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.IntegrationTests;

[Collection(ConsoleOutputCollection.Name)]
public sealed class CallerTreeSourceOutputTests
{
    [Fact]
    public async Task CallerTreeRetainsEveryPhysicalSiteAndForwardsSourceIntentThroughAllOverloads()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var repository = fixture.CreateRepository();
        var payloadIds = new List<long>();
        repository.NormalizedSourcePayloadReadObserver = payloadIds.Add;
        try
        {
            var service = new SemanticQueryService(repository);
            var selection = await fixture.SelectTargetAsync(repository, TestContext.Current.CancellationToken);

            var selectionResult = await service.FindCallerTreeAsync(
                selection,
                depth: 0,
                maxNodes: 100,
                showSource: true,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(selectionResult.ShowSource);
            AssertPhysicalTargetSites(selectionResult, fixture);
            Assert.NotEmpty(payloadIds);
            Assert.Single(payloadIds.Distinct());

            payloadIds.Clear();
            var stringResult = await service.FindCallerTreeAsync(
                fixture.TargetQuery,
                depth: 0,
                maxNodes: 100,
                profileName: null,
                showSource: true,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(stringResult.ShowSource);
            AssertPhysicalTargetSites(stringResult, fixture);
            Assert.NotEmpty(payloadIds);
            Assert.Single(payloadIds.Distinct());

            payloadIds.Clear();
            var filteredResult = await service.FindCallerTreeAsync(
                fixture.TargetQuery,
                new FunctionTargetFilter(IndexedSymbolKind.Method, AsyncStatusFilter.All),
                depth: 0,
                maxNodes: 100,
                profileName: null,
                showSource: true,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(filteredResult.ShowSource);
            AssertPhysicalTargetSites(filteredResult, fixture);
            Assert.NotEmpty(payloadIds);
            Assert.Single(payloadIds.Distinct());
        }
        finally
        {
            repository.NormalizedSourcePayloadReadObserver = null;
        }
    }

    [Fact]
    public async Task CallerTreeWithoutShowSourceRetainsMetadataButReadsNoPayloadText()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var repository = fixture.CreateRepository();
        var payloadIds = new List<long>();
        repository.NormalizedSourcePayloadReadObserver = payloadIds.Add;
        try
        {
            var result = await new SemanticQueryService(repository).FindCallerTreeAsync(
                fixture.TargetQuery,
                depth: 0,
                maxNodes: 100,
                profileName: null,
                showSource: false,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.ShowSource);
            Assert.Equal(5, result.CallSites.Count);
            Assert.Empty(payloadIds);
            Assert.All(result.CallSites, site => Assert.Null(site.Call.NormalizedSource));
            Assert.All(result.CallSites, site =>
            {
                Assert.Contains(result.Edges, edge =>
                    edge.CallerSymbolId == site.CallerSymbolId && edge.CalleeSymbolId == site.CalleeSymbolId);
                Assert.Equal(site.Call.CallerSymbolId, site.CallerSymbolId);
            });
        }
        finally
        {
            repository.NormalizedSourcePayloadReadObserver = null;
        }
    }

    [Fact]
    public async Task CallerTreeHydratesFromPersistedSourceAfterIndexedFileReplacement()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.SourcePath,
            "namespace Replaced { public class Source { } }",
            TestContext.Current.CancellationToken);

        var repository = fixture.CreateRepository();
        var payloadIds = new List<long>();
        repository.NormalizedSourcePayloadReadObserver = payloadIds.Add;
        try
        {
            var result = await new SemanticQueryService(repository).FindCallerTreeAsync(
                fixture.TargetQuery,
                depth: 1,
                maxNodes: 100,
                profileName: null,
                showSource: true,
                cancellationToken: TestContext.Current.CancellationToken);

            AssertPhysicalTargetSites(result, fixture);
            Assert.NotEmpty(payloadIds);
            Assert.Single(payloadIds.Distinct());
            Assert.DoesNotContain("Replaced", result.CallSites.Select(site => site.Call.NormalizedSource));
        }
        finally
        {
            repository.NormalizedSourcePayloadReadObserver = null;
        }
    }

    [Fact]
    public async Task CallerTreeRetainsCycleCrossAndDepthBoundarySitesButRejectsMaxNodeCallers()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var service = new SemanticQueryService(fixture.CreateRepository());

        var boundary = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 1,
            maxNodes: 100,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, boundary.CallSites.Count);
        Assert.Contains(boundary.Edges, edge => edge.CallerSymbolId != boundary.Root.Id &&
            edge.CalleeSymbolId != boundary.Root.Id);
        Assert.All(boundary.CallSites, site => Assert.Contains(boundary.Edges, edge =>
            edge.CallerSymbolId == site.CallerSymbolId && edge.CalleeSymbolId == site.CalleeSymbolId));

        var limited = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 0,
            maxNodes: 2,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(limited.Truncated);
        Assert.Equal(2, limited.CallSites.Count);
        var retainedCaller = Assert.Single(limited.Nodes, node => node.Depth == 1).Symbol;
        Assert.Equal("Caller", retainedCaller.Name);
        Assert.Equal(
            ["Target(value)", "Target(value+1)"],
            limited.CallSites.Select(site => site.Call.NormalizedSource));
        Assert.All(limited.CallSites, site =>
        {
            Assert.Equal(retainedCaller.Id, site.CallerSymbolId);
            Assert.Equal(limited.Root.Id, site.CalleeSymbolId);
        });
        Assert.Equal([new CallerTreeEdge(retainedCaller.Id, limited.Root.Id)], limited.Edges);
        Assert.DoesNotContain(limited.CallSites, site =>
            site.Call.NormalizedSource is "Target(value+2)" or "Cross(value)" or "Caller(value)");
        var rejectedCrossId = Assert.Single(boundary.Nodes, node => node.Symbol.Name == "Cross").Symbol.Id;
        Assert.DoesNotContain(limited.Edges, edge =>
            edge.CallerSymbolId == rejectedCrossId);

    }

    [Fact]
    public async Task CallerTreeFormatsTruncatedPhysicalSitesAfterAcceptedNodes()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var service = new SemanticQueryService(fixture.CreateRepository());
        var limited = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 0,
            maxNodes: 2,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);
        var retainedCaller = Assert.Single(limited.Nodes, node => node.Depth == 1).Symbol;

        var limitedTree = fixture.Render(limited, "tree", TestContext.Current.CancellationToken);
        var expectedLimitedTree = string.Join(
            Environment.NewLine,
            [
                "Calls.Graph::Target(int)",
                "└─ Calls.Graph::Caller(int)",
                FormatTreeSite(fixture, limited.CallSites[0], indent: 3),
                FormatTreeSite(fixture, limited.CallSites[1], indent: 3),
                "└─ <truncated>",
            ]) + Environment.NewLine;
        Assert.Equal(expectedLimitedTree, limitedTree);

        var limitedMermaid = fixture.Render(limited, "mermaid", TestContext.Current.CancellationToken);
        var expectedLimitedMermaid = string.Join(
            Environment.NewLine,
            [
                "flowchart TD",
                $"    n{limited.Root.Id}[\"Calls.Graph::Target(int)\"]",
                $"    n{retainedCaller.Id}[\"Calls.Graph::Caller(int)\"]",
                FormatMermaidEdge(fixture, retainedCaller.Id, limited.Root.Id, limited.CallSites),
                "    %% truncated",
            ]) + Environment.NewLine;
        Assert.Equal(expectedLimitedMermaid, limitedMermaid);

        var limitedJsonText = fixture.Render(limited, "json", TestContext.Current.CancellationToken);
        Assert.EndsWith(Environment.NewLine, limitedJsonText, StringComparison.Ordinal);
        using var limitedJson = JsonDocument.Parse(limitedJsonText);
        Assert.True(limitedJson.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            ["profile", "truncated", "root", "nodes", "edges"],
            limitedJson.RootElement.EnumerateObject().Select(property => property.Name));
        var limitedJsonEdges = limitedJson.RootElement.GetProperty("edges").EnumerateArray().ToArray();
        var limitedJsonEdge = Assert.Single(limitedJsonEdges);
        Assert.Equal(
            ["callerSymbolId", "calleeSymbolId", "callSites"],
            limitedJsonEdge.EnumerateObject().Select(property => property.Name));
        Assert.Equal(retainedCaller.Id, limitedJsonEdge.GetProperty("callerSymbolId").GetInt64());
        Assert.Equal(limited.Root.Id, limitedJsonEdge.GetProperty("calleeSymbolId").GetInt64());
        var limitedJsonSites = limitedJsonEdge.GetProperty("callSites").EnumerateArray().ToArray();
        Assert.Equal(2, limitedJsonSites.Length);
        Assert.Equal(
            ["Target(value)", "Target(value+1)"],
            limitedJsonSites.Select(site => site.GetProperty("normalizedSource").GetString()));
        Assert.Equal(
            limited.CallSites.Select(site => site.Call.Id),
            limitedJsonSites.Select(site => site.GetProperty("id").GetInt64()));
        Assert.All(limitedJsonSites, site =>
            Assert.Equal(["id", "location", "normalizedSource"],
                site.EnumerateObject().Select(property => property.Name)));
        Assert.DoesNotContain(
            limitedJsonSites,
            site => site.GetProperty("normalizedSource").GetString() is "Target(value+2)" or "Cross(value)" or "Caller(value)");
    }

    [Fact]
    public async Task CallerTreeFormatsTreeMermaidAndJsonWithPhysicalSiteContracts()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var service = new SemanticQueryService(fixture.CreateRepository());
        var result = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 1,
            maxNodes: 100,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);
        var caller = Assert.Single(result.Nodes, node => node.Symbol.Name == "Caller").Symbol;
        var cross = Assert.Single(result.Nodes, node => node.Symbol.Name == "Cross").Symbol;
        var callerTargetSites = result.CallSites
            .Where(site => site.CallerSymbolId == caller.Id && site.CalleeSymbolId == result.Root.Id)
            .OrderBy(site => site.Call.SourceStart)
            .ToArray();
        var crossTargetSites = result.CallSites
            .Where(site => site.CallerSymbolId == cross.Id && site.CalleeSymbolId == result.Root.Id)
            .OrderBy(site => site.Call.SourceStart)
            .ToArray();
        var crossCallerSites = result.CallSites
            .Where(site => site.CallerSymbolId == cross.Id && site.CalleeSymbolId == caller.Id)
            .OrderBy(site => site.Call.SourceStart)
            .ToArray();
        var callerCrossSites = result.CallSites
            .Where(site => site.CallerSymbolId == caller.Id && site.CalleeSymbolId == cross.Id)
            .OrderBy(site => site.Call.SourceStart)
            .ToArray();
        Assert.Equal(["Target(value)", "Target(value+1)"], callerTargetSites.Select(site => site.Call.NormalizedSource));
        Assert.Equal(["Target(value+2)"], crossTargetSites.Select(site => site.Call.NormalizedSource));
        Assert.Equal(["Caller(value)"], crossCallerSites.Select(site => site.Call.NormalizedSource));
        Assert.Equal(["Cross(value)"], callerCrossSites.Select(site => site.Call.NormalizedSource));

        var tree = fixture.Render(result, "tree", TestContext.Current.CancellationToken);
        var expectedTree = string.Join(
            Environment.NewLine,
            [
                "Calls.Graph::Target(int)",
                "└─ Calls.Graph::Caller(int)",
                FormatTreeSite(fixture, callerTargetSites[0], indent: 3),
                FormatTreeSite(fixture, callerTargetSites[1], indent: 3),
                "└─ Calls.Graph::Cross(int)",
                FormatTreeSite(fixture, crossTargetSites[0], indent: 3),
                "Additional edges:",
                "  Calls.Graph::Caller(int) -> Calls.Graph::Cross(int)",
                FormatTreeSite(fixture, callerCrossSites[0], indent: 4),
                "  Calls.Graph::Cross(int) -> Calls.Graph::Caller(int)",
                FormatTreeSite(fixture, crossCallerSites[0], indent: 4),
            ]) + Environment.NewLine;
        Assert.Equal(expectedTree, tree);

        var mermaid = fixture.Render(result, "mermaid", TestContext.Current.CancellationToken);
        var expectedMermaid = string.Join(
            Environment.NewLine,
            [
                "flowchart TD",
                $"    n{result.Root.Id}[\"Calls.Graph::Target(int)\"]",
                $"    n{caller.Id}[\"Calls.Graph::Caller(int)\"]",
                $"    n{cross.Id}[\"Calls.Graph::Cross(int)\"]",
                FormatMermaidEdge(fixture, caller.Id, result.Root.Id, callerTargetSites),
                FormatMermaidEdge(fixture, caller.Id, cross.Id, callerCrossSites),
                FormatMermaidEdge(fixture, cross.Id, result.Root.Id, crossTargetSites),
                FormatMermaidEdge(fixture, cross.Id, caller.Id, crossCallerSites),
            ]) + Environment.NewLine;
        Assert.Equal(expectedMermaid, mermaid);

        var jsonText = fixture.Render(result, "json", TestContext.Current.CancellationToken);
        Assert.EndsWith(Environment.NewLine, jsonText, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(jsonText);
        Assert.Equal(
            ["profile", "truncated", "root", "nodes", "edges"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
        var jsonEdges = json.RootElement.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(4, jsonEdges.Length);
        var expectedJsonEdges = new[]
        {
            (caller.Id, result.Root.Id, callerTargetSites),
            (caller.Id, cross.Id, callerCrossSites),
            (cross.Id, result.Root.Id, crossTargetSites),
            (cross.Id, caller.Id, crossCallerSites),
        };
        Assert.Equal(
            expectedJsonEdges.Select(edge => (edge.Item1, edge.Item2)),
            jsonEdges.Select(edge => (edge.GetProperty("callerSymbolId").GetInt64(), edge.GetProperty("calleeSymbolId").GetInt64())));
        foreach (var (edge, expected) in jsonEdges.Zip(expectedJsonEdges))
        {
            Assert.Equal(
                ["callerSymbolId", "calleeSymbolId", "callSites"],
                edge.EnumerateObject().Select(property => property.Name));
            var sites = edge.GetProperty("callSites").EnumerateArray().ToArray();
            Assert.Equal(expected.Item3.Length, sites.Length);
            foreach (var (site, expectedSite) in sites.Zip(expected.Item3))
            {
                Assert.Equal(["id", "location", "normalizedSource"], site.EnumerateObject().Select(property => property.Name));
                Assert.Equal(expectedSite.Call.Id, site.GetProperty("id").GetInt64());
                Assert.Equal(expectedSite.Call.NormalizedSource, site.GetProperty("normalizedSource").GetString());
                Assert.Equal(["path", "line", "column", "offset"],
                    site.GetProperty("location").EnumerateObject().Select(property => property.Name));
                var point = SourcePositionResolver.ResolveOffset(fixture.SourcePath, expectedSite.Call.SourceStart);
                var jsonLocation = site.GetProperty("location");
                Assert.Equal("Graph.cs", jsonLocation.GetProperty("path").GetString());
                Assert.Equal(point.Line, jsonLocation.GetProperty("line").GetInt32());
                Assert.Equal(point.Column, jsonLocation.GetProperty("column").GetInt32());
                Assert.Equal(expectedSite.Call.SourceStart, jsonLocation.GetProperty("offset").GetInt32());
            }
        }

        var rawSource = "Target(\"line\r\n\t| [quoted] <angle> &\u0085\u2028\u2029\")";
        var rawResult = result with
        {
            CallSites = [result.CallSites[0] with
            {
                Call = result.CallSites[0].Call with { NormalizedSource = rawSource },
            }],
        };
        var rawTree = fixture.Render(rawResult, "tree", TestContext.Current.CancellationToken);
        Assert.Equal(
            1,
            rawTree.ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Contains("@ ", StringComparison.Ordinal)));
        Assert.Contains(TableTextSanitizer.Sanitize(rawSource), rawTree, StringComparison.Ordinal);
        mermaid = fixture.Render(rawResult, "mermaid", TestContext.Current.CancellationToken);
        Assert.Contains("-->|\"", mermaid, StringComparison.Ordinal);
        Assert.Contains("&#124;", mermaid, StringComparison.Ordinal);
        Assert.Contains("&quot;", mermaid, StringComparison.Ordinal);
        Assert.Contains("&#91;", mermaid, StringComparison.Ordinal);
        Assert.Contains("&lt;", mermaid, StringComparison.Ordinal);
        Assert.Contains("&amp;", mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;#124;", mermaid, StringComparison.Ordinal);
        var hostileSite = rawResult.CallSites.Single(site => site.Call.NormalizedSource == rawSource);
        var hostilePoint = SourcePositionResolver.ResolveOffset(fixture.SourcePath, hostileSite.Call.SourceStart);
        var hostileLabel = $"Graph.cs:{hostilePoint.Line}:{hostilePoint.Column} {EscapeMermaidExpectation(rawSource)}";
        Assert.Contains(
            $"    n{hostileSite.CallerSymbolId} -->|\"{hostileLabel}\"| n{hostileSite.CalleeSymbolId}",
            mermaid,
            StringComparison.Ordinal);

        using var hostileJson = JsonDocument.Parse(
            fixture.Render(rawResult, "json", TestContext.Current.CancellationToken));
        var jsonEdge = Assert.Single(hostileJson.RootElement.GetProperty("edges").EnumerateArray(), edge =>
            edge.GetProperty("callSites").GetArrayLength() == 1);
        Assert.Equal(
            ["callerSymbolId", "calleeSymbolId", "callSites"],
            jsonEdge.EnumerateObject().Select(property => property.Name));
        var jsonSite = Assert.Single(jsonEdge.GetProperty("callSites").EnumerateArray());
        Assert.Equal(["id", "location", "normalizedSource"], jsonSite.EnumerateObject().Select(property => property.Name));
        Assert.Equal(rawSource, jsonSite.GetProperty("normalizedSource").GetString());
        Assert.Equal(
            ["path", "line", "column", "offset"],
            jsonSite.GetProperty("location").EnumerateObject().Select(property => property.Name));
        var expectedPoint = SourcePositionResolver.ResolveOffset(
            fixture.SourcePath,
            rawResult.CallSites[0].Call.SourceStart);
        var hostileLocation = jsonSite.GetProperty("location");
        Assert.Equal("Graph.cs", hostileLocation.GetProperty("path").GetString());
        Assert.Equal(expectedPoint.Line, hostileLocation.GetProperty("line").GetInt32());
        Assert.Equal(expectedPoint.Column, hostileLocation.GetProperty("column").GetInt32());
        Assert.Equal(rawResult.CallSites[0].Call.SourceStart, hostileLocation.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task CallerTreeNoFlagFormatsMatchTheExistingProjectionAndOmitCallSites()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var service = new SemanticQueryService(fixture.CreateRepository());
        var flagged = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 1,
            maxNodes: 100,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);
        var unflagged = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 1,
            maxNodes: 100,
            profileName: null,
            showSource: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var noSites = flagged with { ShowSource = false, CallSites = [] };
        Assert.Equal(
            fixture.Render(noSites, "tree", TestContext.Current.CancellationToken),
            fixture.Render(unflagged, "tree", TestContext.Current.CancellationToken));
        Assert.Equal(
            fixture.Render(noSites, "mermaid", TestContext.Current.CancellationToken),
            fixture.Render(unflagged, "mermaid", TestContext.Current.CancellationToken));
        var expectedJson = fixture.Render(noSites, "json", TestContext.Current.CancellationToken);
        var actualJson = fixture.Render(unflagged, "json", TestContext.Current.CancellationToken);
        Assert.Equal(expectedJson, actualJson);
        Assert.DoesNotContain("callSites", actualJson, StringComparison.Ordinal);
        Assert.DoesNotContain("normalizedSource", actualJson, StringComparison.Ordinal);

        var caller = Assert.Single(unflagged.Nodes, node => node.Symbol.Name == "Caller").Symbol;
        var cross = Assert.Single(unflagged.Nodes, node => node.Symbol.Name == "Cross").Symbol;
        var expectedBaseTree = string.Join(
            Environment.NewLine,
            [
                "Calls.Graph::Target(int)",
                "└─ Calls.Graph::Caller(int)",
                "└─ Calls.Graph::Cross(int)",
                "Additional edges:",
                "  Calls.Graph::Caller(int) -> Calls.Graph::Cross(int)",
                "  Calls.Graph::Cross(int) -> Calls.Graph::Caller(int)",
            ]) + Environment.NewLine;
        Assert.Equal(
            expectedBaseTree,
            fixture.Render(unflagged, "tree", TestContext.Current.CancellationToken));

        var expectedBaseMermaid = string.Join(
            Environment.NewLine,
            [
                "flowchart TD",
                $"    n{unflagged.Root.Id}[\"Calls.Graph::Target(int)\"]",
                $"    n{caller.Id}[\"Calls.Graph::Caller(int)\"]",
                $"    n{cross.Id}[\"Calls.Graph::Cross(int)\"]",
                $"    n{caller.Id} --> n{unflagged.Root.Id}",
                $"    n{caller.Id} --> n{cross.Id}",
                $"    n{cross.Id} --> n{unflagged.Root.Id}",
                $"    n{cross.Id} --> n{caller.Id}",
            ]) + Environment.NewLine;
        Assert.Equal(
            expectedBaseMermaid,
            fixture.Render(unflagged, "mermaid", TestContext.Current.CancellationToken));

        Assert.EndsWith(Environment.NewLine, actualJson, StringComparison.Ordinal);
        using var baseJson = JsonDocument.Parse(actualJson);
        Assert.Equal(
            ["profile", "truncated", "root", "nodes", "edges"],
            baseJson.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            [
                $"{caller.Id}->{unflagged.Root.Id}",
                $"{caller.Id}->{cross.Id}",
                $"{cross.Id}->{unflagged.Root.Id}",
                $"{cross.Id}->{caller.Id}",
            ],
            baseJson.RootElement.GetProperty("edges").EnumerateArray()
                .Select(edge => $"{edge.GetProperty("callerSymbolId").GetInt64()}->{edge.GetProperty("calleeSymbolId").GetInt64()}"));
        Assert.All(baseJson.RootElement.GetProperty("edges").EnumerateArray(), edge =>
        {
            Assert.Equal(["callerSymbolId", "calleeSymbolId"], edge.EnumerateObject().Select(property => property.Name));
            Assert.False(edge.TryGetProperty("callSites", out _));
        });
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("mermaid")]
    [InlineData("json")]
    public async Task CallerTreeFormatsRejectMissingHydratedSourceWithCallAndDocumentIds(string format)
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var service = new SemanticQueryService(fixture.CreateRepository());
        var result = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 1,
            maxNodes: 100,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);
        var missing = result with
        {
            CallSites = [result.CallSites[0] with
            {
                Call = result.CallSites[0].Call with { NormalizedSource = null },
            }],
        };

        var exception = Assert.Throws<IndexDatabaseException>(() =>
        {
            _ = fixture.Render(missing, format, TestContext.Current.CancellationToken);
        });
        Assert.Contains($"call ID {missing.CallSites[0].Call.Id}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"document ID {missing.CallSites[0].Call.DocumentId}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("mermaid")]
    [InlineData("json")]
    public async Task CallerTreeFormatsObserveCancellationBeforeAndDuringWrites(string format)
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var service = new SemanticQueryService(fixture.CreateRepository());
        var result = await service.FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 1,
            maxNodes: 100,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = fixture.Render(result, format, preCancelled.Token);
        });

        using var duringGrouping = new CancellationTokenSource();
        var cancellableResult = result with
        {
            CallSites = new CancellingCallSiteList(result.CallSites, duringGrouping, cancelOnIndex: 1),
        };
        Assert.Throws<OperationCanceledException>(() =>
        {
            fixture.CreateFormatter(TextWriter.Null).WriteCallerTree(cancellableResult, format, duringGrouping.Token);
        });

        using var duringProjection = new CancellationTokenSource();
        var projectionResult = result with
        {
            Nodes = new CancellingNodeList(result.Nodes, duringProjection, cancelOnIndex: 1),
        };
        Assert.Throws<OperationCanceledException>(() =>
        {
            fixture.CreateFormatter(TextWriter.Null).WriteCallerTree(projectionResult, format, duringProjection.Token);
        });

        if (format is "tree" or "mermaid")
        {
            using var duringWrite = new CancellationTokenSource();
            using var writer = new CancellingTextWriter(duringWrite, cancelOnWrite: 1);
            Assert.Throws<OperationCanceledException>(() =>
            {
                fixture.CreateFormatter(writer).WriteCallerTree(result, format, duringWrite.Token);
            });
        }
    }

    [Fact]
    public async Task CallerTreeTextChecksCancellationBeforeAdditionalEdgesHeader()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var result = await new SemanticQueryService(fixture.CreateRepository()).FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 1,
            maxNodes: 100,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        using var writer = new CancellingTextWriter(cancellation, cancelOnWrite: 6);
        Assert.Throws<OperationCanceledException>(() =>
        {
            fixture.CreateFormatter(writer).WriteCallerTree(result, "tree", cancellation.Token);
        });
        Assert.DoesNotContain("Additional edges:", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerTreeMermaidChecksCancellationBeforeTruncatedMarker()
    {
        await using var fixture = await CallerTreeFixture.CreateAsync();
        var result = await new SemanticQueryService(fixture.CreateRepository()).FindCallerTreeAsync(
            fixture.TargetQuery,
            depth: 0,
            maxNodes: 2,
            profileName: null,
            showSource: true,
            cancellationToken: TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        using var writer = new CancellingTextWriter(cancellation, cancelOnWrite: 4);
        Assert.Throws<OperationCanceledException>(() =>
        {
            fixture.CreateFormatter(writer).WriteCallerTree(result, "mermaid", cancellation.Token);
        });
        Assert.DoesNotContain("%% truncated", writer.ToString(), StringComparison.Ordinal);
    }

    private static void AssertPhysicalTargetSites(CallerTreeResult result, CallerTreeFixture fixture)
    {
        var caller = Assert.Single(result.Nodes, node => node.Symbol.Name == "Caller").Symbol;
        var cross = Assert.Single(result.Nodes, node => node.Symbol.Name == "Cross").Symbol;
        var expectedEdges = new[]
        {
            new CallerTreeEdge(caller.Id, result.Root.Id),
            new CallerTreeEdge(cross.Id, result.Root.Id),
            new CallerTreeEdge(cross.Id, caller.Id),
            new CallerTreeEdge(caller.Id, cross.Id),
        };
        Assert.Equal(expectedEdges, result.Edges);

        var expectedSites = new[]
        {
            (caller.Id, result.Root.Id, "Target(value)"),
            (caller.Id, result.Root.Id, "Target(value+1)"),
            (cross.Id, result.Root.Id, "Target(value+2)"),
            (cross.Id, caller.Id, "Caller(value)"),
            (caller.Id, cross.Id, "Cross(value)"),
        };
        Assert.Equal(
            expectedSites,
            result.CallSites.Select(site => (site.CallerSymbolId, site.CalleeSymbolId, site.Call.NormalizedSource!)));
        Assert.Equal(
            expectedSites.Length,
            result.CallSites.Select(site => (site.CallerSymbolId, site.CalleeSymbolId, site.Call.Id)).Distinct().Count());

        var targetSites = result.CallSites
            .Where(site => site.CalleeSymbolId == result.Root.Id && site.CallerSymbolId != result.Root.Id)
            .ToArray();
        Assert.Equal(3, targetSites.Length);
        Assert.Equal(2, targetSites.Count(site => site.Call.NormalizedSource is "Target(value)" or "Target(value+1)"));
        Assert.Contains(targetSites, site => site.Call.NormalizedSource == "Target(value)");
        Assert.Contains(targetSites, site => site.Call.NormalizedSource == "Target(value+1)");
        Assert.Contains(targetSites, site => site.Call.NormalizedSource == "Target(value+2)");
        Assert.Equal(3, targetSites.Select(site => site.Call.Id).Distinct().Count());
        Assert.Equal(3, targetSites.Select(site => (site.CallerSymbolId, site.CalleeSymbolId, site.Call.Id)).Distinct().Count());
        Assert.All(targetSites, site =>
        {
            Assert.Equal(site.CallerSymbolId, site.Call.CallerSymbolId);
            Assert.Equal(site.CalleeSymbolId, site.Call.CalleeDefinitionId);
            Assert.Equal("Graph.cs", site.Call.DocumentPath);
        });
        var expectedPhysicalSites = new[]
        {
            ("Target(value)", fixture.SourceText.IndexOf("Target(value)", StringComparison.Ordinal), "Target(value)".Length),
            ("Target(value+1)", fixture.SourceText.IndexOf("Target(value + 1)", StringComparison.Ordinal), "Target(value + 1)".Length),
            ("Target(value+2)", fixture.SourceText.IndexOf("Target(value + 2)", StringComparison.Ordinal), "Target(value + 2)".Length),
        };
        Assert.Equal(
            expectedPhysicalSites,
            targetSites
                .OrderBy(site => site.Call.SourceStart)
                .Select(site => (site.Call.NormalizedSource!, site.Call.SourceStart, site.Call.SourceLength)));
    }

    private static string FormatTreeSite(CallerTreeFixture fixture, CallerTreeCallSite site, int indent)
    {
        var point = SourcePositionResolver.ResolveOffset(fixture.SourcePath, site.Call.SourceStart);
        return new string(' ', indent) +
            $"@ Graph.cs:{point.Line}:{point.Column}\t{TableTextSanitizer.Sanitize(site.Call.NormalizedSource)}";
    }

    private static string FormatMermaidEdge(
        CallerTreeFixture fixture,
        long callerId,
        long calleeId,
        IReadOnlyList<CallerTreeCallSite> sites)
    {
        var label = string.Join(
            "<br/>",
            sites.Select(site =>
            {
                var point = SourcePositionResolver.ResolveOffset(fixture.SourcePath, site.Call.SourceStart);
                return $"Graph.cs:{point.Line}:{point.Column} {EscapeMermaidExpectation(site.Call.NormalizedSource!)}";
            }));
        return $"    n{callerId} -->|\"{label}\"| n{calleeId}";
    }

    private static string EscapeMermaidExpectation(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("[", "&#91;", StringComparison.Ordinal)
        .Replace("]", "&#93;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal)
        .Replace("\r\n", "<br/>", StringComparison.Ordinal)
        .Replace("\r", "<br/>", StringComparison.Ordinal)
        .Replace("\n", "<br/>", StringComparison.Ordinal);

    private sealed class CancellingCallSiteList(
        IReadOnlyList<CallerTreeCallSite> values,
        CancellationTokenSource cancellation,
        int cancelOnIndex) : IReadOnlyList<CallerTreeCallSite>
    {
        public int Count => values.Count;

        public CallerTreeCallSite this[int index]
        {
            get
            {
                if (index >= cancelOnIndex)
                {
                    cancellation.Cancel();
                }

                return values[index];
            }
        }

        public IEnumerator<CallerTreeCallSite> GetEnumerator()
        {
            for (var index = 0; index < values.Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingNodeList(
        IReadOnlyList<CallerTreeNode> values,
        CancellationTokenSource cancellation,
        int cancelOnIndex) : IReadOnlyList<CallerTreeNode>
    {
        public int Count => values.Count;

        public CallerTreeNode this[int index]
        {
            get
            {
                if (index >= cancelOnIndex)
                {
                    cancellation.Cancel();
                }

                return values[index];
            }
        }

        public IEnumerator<CallerTreeNode> GetEnumerator()
        {
            for (var index = 0; index < values.Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingTextWriter(
        CancellationTokenSource cancellation,
        int cancelOnWrite) : StringWriter
    {
        private int _writes;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (++_writes >= cancelOnWrite)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class CallerTreeFixture : IAsyncDisposable
    {
        public const string Source = """"
            namespace Calls
            {
                public static class Graph
                {
                    public static void Target(int value) { }

                    public static void Caller(int value)
                    {
                        Target(value);
                        Target(value + 1);
                        Cross(value);
                    }

                    public static void Cross(int value)
                    {
                        Target(value + 2);
                        Caller(value);
                    }
                }
            }
            """";

        private CallerTreeFixture(string rootPath, string sourcePath, string databasePath)
        {
            RootPath = rootPath;
            SourcePath = sourcePath;
            DatabasePath = databasePath;
        }

        public string RootPath { get; }
        public string SourcePath { get; }
        public string DatabasePath { get; }
        public string TargetQuery => "Calls.Graph::Target(int)";
        public string SourceText => Source;

        public static async Task<CallerTreeFixture> CreateAsync()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "csindex-caller-tree-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var sourcePath = Path.Combine(rootPath, "Graph.cs");
            await File.WriteAllTextAsync(sourcePath, Source, TestContext.Current.CancellationToken);
            var databasePath = Path.Combine(rootPath, ".csindex", "index.sqlite");
            var fixture = new CallerTreeFixture(rootPath, sourcePath, databasePath);

            var options = new IndexOptions
            {
                InputPath = rootPath,
                ForcedMode = InputMode.Directory,
            };
            var coordinator = AnalysisCoordinator.CreateDefault();
            var input = coordinator.ResolveInput(options);
            var fingerprint = await coordinator.BuildInputFingerprintAsync(input, options, CancellationToken.None);
            var requestHash = RequestHasher.Build(input, options);
            var result = await coordinator.AnalyzeAsync(input, options, fingerprint, requestHash, CancellationToken.None);
            await new SqliteIndex(databasePath).SaveAsync(result.Snapshot, CancellationToken.None);
            return fixture;
        }

        public QueryRepository CreateRepository() => new SqliteIndex(DatabasePath).CreateQueryRepository();

        public async Task<RootSelection> SelectTargetAsync(
            QueryRepository repository,
            CancellationToken cancellationToken)
        {
            var profile = await repository.GetProfileAsync(null, cancellationToken);
            var symbols = await repository.FindExecutableSymbolsAsync(profile.Id, sourceOnly: true, cancellationToken);
            var target = Assert.Single(symbols, symbol => symbol.Name == "Target" && symbol.ParameterCount == 1);
            return new RootSelection(profile, [new ResolvedLogicalRoot(target, [])]);
        }

        public GraphOutputFormatter CreateFormatter(TextWriter writer) => new(
            new SymbolPathFormatOptions(SymbolPathStyle.CSharp, ShortNames: false),
            IndexPathResolver.CreateForIndex(DatabasePath, RootPath),
            PathDisplayStyle.Relative,
            writer);

        public string Render(
            CallerTreeResult result,
            string format,
            CancellationToken cancellationToken = default)
        {
            using var writer = new StringWriter();
            CreateFormatter(writer).WriteCallerTree(result, format, cancellationToken);
            return writer.ToString();
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
