using System.Runtime.CompilerServices;
using CsIndex.Core.Model;
using CsIndex.Storage;

[assembly: InternalsVisibleTo("CsIndex.Query.Tests")]

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
        cancellationToken.ThrowIfCancellationRequested();
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
            var atDepthBoundary = depth != 0 && currentFrontier[0].Depth >= depth;

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

            if (atDepthBoundary)
            {
                foreach (var candidateEdge in candidateEdges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (nodesById.ContainsKey(candidateEdge.CallerSymbolId) &&
                        nodesById.ContainsKey(candidateEdge.CalleeSymbolId) &&
                        edgeSet.Add(candidateEdge))
                    {
                        edges.Add(candidateEdge);
                    }
                }

                break;
            }

            var callerIds = new List<long>(candidateEdges.Count);
            foreach (var candidateEdge in candidateEdges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                callerIds.Add(candidateEdge.CallerSymbolId);
            }

            var callerCandidates = await repository.GetSymbolsByIdsAsync(
                profile.Id,
                callerIds,
                cancellationToken);
            var callersById = new Dictionary<long, StoredSymbol>();
            foreach (var caller in callerCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSourceBackedNonSystemExecutable(caller))
                {
                    callersById.Add(caller.Id, caller);
                }
            }

            var sortedCallers = OrderCallers(callersById.Values, cancellationToken);
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

            var candidateEdgesByCaller = new Dictionary<long, List<CallerTreeEdge>>();
            foreach (var candidateEdge in candidateEdges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidateEdgesByCaller.TryGetValue(candidateEdge.CallerSymbolId, out var callerEdges))
                {
                    callerEdges = [];
                    candidateEdgesByCaller.Add(candidateEdge.CallerSymbolId, callerEdges);
                }

                callerEdges.Add(candidateEdge);
            }

            foreach (var caller in sortedCallers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidateEdgesByCaller.TryGetValue(caller.Id, out var callerEdges))
                {
                    continue;
                }

                foreach (var candidateEdge in callerEdges)
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

    internal static IReadOnlyList<StoredSymbol> OrderCallers(
        IEnumerable<StoredSymbol> callers,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(callers);
        cancellationToken.ThrowIfCancellationRequested();

        var ordered = new List<StoredSymbol>();
        foreach (var caller in callers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordered.Add(caller);
        }

        try
        {
            ordered.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = CompareCallers(left, right);
                afterOrderingComparison?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            });
        }
        catch (InvalidOperationException exception) when (
            exception.InnerException is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ordered;
    }

    private static int CompareCallers(StoredSymbol left, StoredSymbol right)
    {
        var result = StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.DocumentPath ?? string.Empty, right.DocumentPath ?? string.Empty);
        if (result != 0)
        {
            return result;
        }

        result = (left.SourceStart ?? -1).CompareTo(right.SourceStart ?? -1);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
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
