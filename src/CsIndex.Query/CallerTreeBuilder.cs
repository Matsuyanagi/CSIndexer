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
        var frontier = new Queue<CallerTreeNode>();
        frontier.Enqueue(rootNode);
        var truncated = false;

        while (frontier.TryDequeue(out var calleeNode))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth != 0 && calleeNode.Depth >= depth)
            {
                continue;
            }

            var calls = await repository.GetCallsByCalleeAsync(
                profile.Id,
                [calleeNode.Symbol.Id],
                GeneratedFilter.Include,
                CallKinds,
                cancellationToken);
            var matchingCalls = new List<StoredCall>();
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TargetsCallee(call, calleeNode.Symbol.Id))
                {
                    matchingCalls.Add(call);
                }
            }

            if (matchingCalls.Count == 0)
            {
                continue;
            }

            var callers = await repository.GetSymbolsByIdsAsync(
                profile.Id,
                matchingCalls.Select(call => call.CallerSymbolId),
                cancellationToken);
            foreach (var caller in callers
                         .Where(IsSourceBackedNonSystemExecutable)
                         .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
                         .ThenBy(symbol => symbol.DocumentPath ?? string.Empty, StringComparer.Ordinal)
                         .ThenBy(symbol => symbol.SourceStart ?? -1)
                         .ThenBy(symbol => symbol.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nodesById.TryGetValue(caller.Id, out var callerNode))
                {
                    if (nodes.Count == maxNodes)
                    {
                        truncated = true;
                        continue;
                    }

                    callerNode = new CallerTreeNode(caller, calleeNode.Depth + 1);
                    nodesById.Add(caller.Id, callerNode);
                    nodes.Add(callerNode);
                    frontier.Enqueue(callerNode);
                }

                var edge = new CallerTreeEdge(callerNode.Symbol.Id, calleeNode.Symbol.Id);
                if (edgeSet.Add(edge))
                {
                    edges.Add(edge);
                }
            }
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
