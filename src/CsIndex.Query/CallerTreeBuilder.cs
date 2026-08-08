using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.Query;

internal sealed class CallerTreeBuilder(QueryRepository repository)
{
    private static readonly IReadOnlySet<ReferenceKind> CallKinds =
        new HashSet<ReferenceKind> { ReferenceKind.Invocation, ReferenceKind.ObjectCreation };

    public async Task<CallerTreeResult> BuildAsync(
        StoredProfile profile,
        StoredSymbol root,
        int depth,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        var rootNode = new CallerTreeNode(root, Depth: 0);
        var nodes = new List<CallerTreeNode> { rootNode };
        var nodesById = new Dictionary<long, CallerTreeNode> { [root.Id] = rootNode };
        var edges = new List<CallerTreeEdge>();
        var edgeSet = new HashSet<CallerTreeEdge>();
        var currentFrontier = new List<CallerTreeNode> { rootNode };
        var truncated = false;

        while (currentFrontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth != 0 && currentFrontier[0].Depth >= depth)
            {
                break;
            }

            var candidateEdges = new List<CallerTreeEdge>();
            var candidateEdgeSet = new HashSet<CallerTreeEdge>();
            foreach (var calleeNode in currentFrontier)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var calls = await repository.GetCallsByCalleeAsync(
                    profile.Id,
                    [calleeNode.Symbol.Id],
                    GeneratedFilter.Include,
                    CallKinds,
                    cancellationToken);
                foreach (var call in calls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TargetsCallee(call, calleeNode.Symbol.Id))
                    {
                        continue;
                    }

                    var candidateEdge = new CallerTreeEdge(call.CallerSymbolId, calleeNode.Symbol.Id);
                    if (candidateEdgeSet.Add(candidateEdge))
                    {
                        candidateEdges.Add(candidateEdge);
                    }
                }
            }

            if (candidateEdges.Count == 0)
            {
                break;
            }

            var callersById = (await repository.GetSymbolsByIdsAsync(
                profile.Id,
                candidateEdges.Select(edge => edge.CallerSymbolId),
                cancellationToken))
                .Where(IsSourceBackedNonSystemExecutable)
                .ToDictionary(symbol => symbol.Id);
            var sortedCallers = callersById.Values
                         .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
                         .ThenBy(symbol => symbol.DocumentPath ?? string.Empty, StringComparer.Ordinal)
                         .ThenBy(symbol => symbol.SourceStart ?? -1)
                         .ThenBy(symbol => symbol.Id)
                         .ToArray();
            var nextFrontier = new List<CallerTreeNode>();
            foreach (var caller in sortedCallers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nodesById.ContainsKey(caller.Id))
                {
                    continue;
                }

                if (nodes.Count == maxNodes)
                {
                    truncated = true;
                    continue;
                }

                var callerNode = new CallerTreeNode(caller, currentFrontier[0].Depth + 1);
                nodesById.Add(caller.Id, callerNode);
                nodes.Add(callerNode);
                nextFrontier.Add(callerNode);
            }

            var candidateEdgesByCaller = candidateEdges.ToLookup(edge => edge.CallerSymbolId);
            foreach (var caller in sortedCallers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidateEdge in candidateEdgesByCaller[caller.Id])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (nodesById.ContainsKey(candidateEdge.CallerSymbolId) &&
                        nodesById.ContainsKey(candidateEdge.CalleeSymbolId) &&
                        edgeSet.Add(candidateEdge))
                    {
                        edges.Add(candidateEdge);
                    }
                }
            }

            currentFrontier = nextFrontier;
        }

        return new CallerTreeResult(profile, root, nodes, edges, truncated);
    }

    private static bool TargetsCallee(StoredCall call, long calleeId) =>
        call.CalleeDefinitionId == calleeId ||
        (call.CalleeDefinitionId is null && call.CalleeSymbolId == calleeId);

    private static bool IsSourceBackedNonSystemExecutable(StoredSymbol symbol) =>
        symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda &&
        symbol.DocumentPath is not null &&
        symbol.NamespaceName != "System" &&
        !symbol.NamespaceName.StartsWith("System.", StringComparison.Ordinal);
}
