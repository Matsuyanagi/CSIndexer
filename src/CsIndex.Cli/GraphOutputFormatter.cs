using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal sealed class GraphOutputFormatter
{
    private readonly bool _shortNames;
    private readonly TextWriter? _writer;

    public GraphOutputFormatter(bool shortNames)
    {
        _shortNames = shortNames;
    }

    public GraphOutputFormatter(bool shortNames, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _shortNames = shortNames;
        _writer = writer;
    }

    private TextWriter Writer => _writer ?? Console.Out;

    public void WriteAsyncPath(
        AsyncPathResult result,
        string output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        switch (output)
        {
            case "tree":
                WriteAsyncTree(result, cancellationToken);
                return;
            case "line":
                WriteAsyncLine(result, cancellationToken);
                return;
            case "json":
                WriteAsyncJson(result, cancellationToken);
                return;
            default:
                throw new CliUsageException($"Unknown async tree output: {output}. Use tree, line, or json.");
        }
    }

    public void WriteCallerTree(
        CallerTreeResult result,
        string output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        switch (output)
        {
            case "tree":
                WriteCallerTextTree(result, cancellationToken);
                return;
            case "mermaid":
                WriteMermaid(result, cancellationToken);
                return;
            case "json":
                WriteCallerJson(result, cancellationToken);
                return;
            default:
                throw new CliUsageException($"Unknown callers tree output: {output}. Use tree, mermaid, or json.");
        }
    }

    private void WriteAsyncTree(AsyncPathResult result, CancellationToken cancellationToken)
    {
        if (!result.Found)
        {
            Writer.WriteLine($"No reachable asynchronous function: {DisplayName(result.Root)}");
            return;
        }

        for (var index = 0; index < result.Nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = index == 0 ? string.Empty : string.Concat(Enumerable.Repeat("   ", index - 1)) + "└─ ";
            Writer.WriteLine(prefix + AsyncDisplayName(result.Nodes[index]));
        }

        if (result.Truncated)
        {
            Writer.WriteLine(string.Concat(Enumerable.Repeat("   ", result.Nodes.Count - 1)) + "└─ <truncated>");
        }
    }

    private void WriteAsyncLine(AsyncPathResult result, CancellationToken cancellationToken)
    {
        if (!result.Found)
        {
            Writer.WriteLine($"No reachable asynchronous function: {DisplayName(result.Root)}");
            return;
        }

        var values = new List<string>(result.Nodes.Count + (result.Truncated ? 1 : 0));
        foreach (var node in result.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Add(AsyncDisplayName(node));
        }

        if (result.Truncated)
        {
            values.Add("<truncated>");
        }

        Writer.WriteLine(string.Join(" -> ", values));
    }

    private void WriteAsyncJson(AsyncPathResult result, CancellationToken cancellationToken)
    {
        var nodes = new List<IReadOnlyDictionary<string, object?>>(result.Nodes.Count);
        foreach (var node in result.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(OutputFormatter.ToSymbolObject(node, _shortNames, includeSource: false));
        }

        OutputFormatter.WriteJson(new
        {
            profile = result.Selection.Profile.Name,
            found = result.Found,
            truncated = result.Truncated,
            root = OutputFormatter.ToSymbolObject(result.Root, _shortNames, includeSource: false),
            nodes,
        }, Writer);
    }

    private void WriteCallerTextTree(CallerTreeResult result, CancellationToken cancellationToken)
    {
        var nodesById = new Dictionary<long, CallerTreeNode>();
        foreach (var node in OrderNodes(result.Nodes, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodesById.TryAdd(node.Symbol.Id, node);
        }

        cancellationToken.ThrowIfCancellationRequested();
        nodesById.TryAdd(result.Root.Id, new CallerTreeNode(result.Root, 0));
        var nodes = OrderNodes(nodesById.Values, cancellationToken);
        var nodeOrder = new Dictionary<long, int>(nodes.Count);
        for (var index = 0; index < nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodeOrder.Add(nodes[index].Symbol.Id, index);
        }

        var edges = OrderEdges(result.Edges, cancellationToken);
        var spanningEdges = SelectSpanningEdges(result.Root.Id, nodes, nodesById, nodeOrder, edges, cancellationToken);
        var childrenByParent = new Dictionary<long, List<CallerTreeNode>>();
        foreach (var spanningEdge in spanningEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!childrenByParent.TryGetValue(spanningEdge.CalleeSymbolId, out var children))
            {
                children = [];
                childrenByParent.Add(spanningEdge.CalleeSymbolId, children);
            }

            children.Add(nodesById[spanningEdge.CallerSymbolId]);
        }

        foreach (var children in childrenByParent.Values)
        {
            SortWithCancellation(
                children,
                (left, right) => nodeOrder[left.Symbol.Id].CompareTo(nodeOrder[right.Symbol.Id]),
                cancellationToken,
                afterOrderingComparison: null);
        }

        var spanningEdgeSet = new HashSet<CallerTreeEdge>();
        foreach (var spanningEdge in spanningEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            spanningEdgeSet.Add(spanningEdge);
        }

        var visited = new HashSet<long>();
        var stack = new Stack<(CallerTreeNode Node, int Indent)>();
        stack.Push((nodesById[result.Root.Id], 0));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, indent) = stack.Pop();
            if (!visited.Add(node.Symbol.Id))
            {
                continue;
            }

            var prefix = indent == 0
                ? string.Empty
                : string.Concat(Enumerable.Repeat("   ", indent - 1)) + "└─ ";
            Writer.WriteLine(prefix + DisplayName(node.Symbol));

            if (!childrenByParent.TryGetValue(node.Symbol.Id, out var children))
            {
                continue;
            }

            for (var index = children.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stack.Push((children[index], indent + 1));
            }
        }

        if (result.Truncated)
        {
            Writer.WriteLine("└─ <truncated>");
        }

        var additionalEdges = new List<CallerTreeEdge>();
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!spanningEdgeSet.Contains(edge))
            {
                additionalEdges.Add(edge);
            }
        }

        if (additionalEdges.Count == 0)
        {
            return;
        }

        Writer.WriteLine("Additional edges:");
        foreach (var edge in additionalEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine(
                $"  {GetEdgeDisplayName(edge.CallerSymbolId, nodesById)} -> {GetEdgeDisplayName(edge.CalleeSymbolId, nodesById)}");
        }
    }

    private void WriteMermaid(CallerTreeResult result, CancellationToken cancellationToken)
    {
        Writer.WriteLine("flowchart TD");
        foreach (var node in OrderNodesById(result.Nodes, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine($"    n{node.Symbol.Id}[\"{EscapeMermaidLabel(RawDisplayName(node.Symbol))}\"]");
        }

        foreach (var edge in OrderEdges(result.Edges, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine($"    n{edge.CallerSymbolId} --> n{edge.CalleeSymbolId}");
        }

        if (result.Truncated)
        {
            Writer.WriteLine("    %% truncated");
        }
    }

    private void WriteCallerJson(CallerTreeResult result, CancellationToken cancellationToken)
    {
        var nodes = new List<object>();
        foreach (var node in OrderNodes(result.Nodes, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(new
            {
                symbol = OutputFormatter.ToSymbolObject(node.Symbol, _shortNames, includeSource: false),
                node.Depth,
            });
        }

        var edges = new List<CallerTreeEdge>();
        foreach (var edge in OrderEdges(result.Edges, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            edges.Add(edge);
        }

        OutputFormatter.WriteJson(new
        {
            profile = result.Selection.Profile.Name,
            truncated = result.Truncated,
            root = OutputFormatter.ToSymbolObject(result.Root, _shortNames, includeSource: false),
            nodes,
            edges,
        }, Writer);
    }

    private string DisplayName(StoredSymbol symbol) => NormalizeText(RawDisplayName(symbol));

    private string RawDisplayName(StoredSymbol symbol) =>
        SymbolSignatureFormatter.FormatDisplayName(symbol, _shortNames);

    private string AsyncDisplayName(StoredSymbol symbol) =>
        symbol.AsyncRole == AsyncRole.None ? DisplayName(symbol) : $"async {DisplayName(symbol)}";

    private static IReadOnlyList<CallerTreeEdge> SelectSpanningEdges(
        long rootId,
        IReadOnlyList<CallerTreeNode> nodes,
        IReadOnlyDictionary<long, CallerTreeNode> nodesById,
        IReadOnlyDictionary<long, int> nodeOrder,
        IReadOnlyList<CallerTreeEdge> edges,
        CancellationToken cancellationToken)
    {
        var spanningEdges = new List<CallerTreeEdge>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Symbol.Id == rootId)
            {
                continue;
            }

            CallerTreeEdge? parentEdge = null;
            foreach (var edge in edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edge.CallerSymbolId != node.Symbol.Id ||
                    !nodesById.TryGetValue(edge.CalleeSymbolId, out var parent) ||
                    parent.Depth != node.Depth - 1)
                {
                    continue;
                }

                if (parentEdge is null || CompareParentEdges(edge, parentEdge, nodeOrder) < 0)
                {
                    parentEdge = edge;
                }
            }

            if (parentEdge is not null)
            {
                spanningEdges.Add(parentEdge);
            }
        }

        return spanningEdges;
    }

    private string GetEdgeDisplayName(long symbolId, IReadOnlyDictionary<long, CallerTreeNode> nodesById) =>
        nodesById.TryGetValue(symbolId, out var node)
            ? DisplayName(node.Symbol)
            : $"<unknown:{symbolId}>";

    internal static IReadOnlyList<CallerTreeNode> OrderNodes(
        IEnumerable<CallerTreeNode> nodes,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var ordered = new List<CallerTreeNode>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordered.Add(node);
        }

        SortWithCancellation(ordered, CompareNodes, cancellationToken, afterOrderingComparison);
        return ordered;
    }

    internal static IReadOnlyList<CallerTreeEdge> OrderEdges(
        IEnumerable<CallerTreeEdge> edges,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var ordered = new List<CallerTreeEdge>();
        var seen = new HashSet<CallerTreeEdge>();
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Add(edge))
            {
                ordered.Add(edge);
            }
        }

        SortWithCancellation(ordered, CompareEdges, cancellationToken, afterOrderingComparison);
        return ordered;
    }

    private static IReadOnlyList<CallerTreeNode> OrderNodesById(
        IEnumerable<CallerTreeNode> nodes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var ordered = new List<CallerTreeNode>();
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordered.Add(node);
        }

        SortWithCancellation(
            ordered,
            static (left, right) => left.Symbol.Id.CompareTo(right.Symbol.Id),
            cancellationToken,
            afterOrderingComparison: null);
        return ordered;
    }

    private static int CompareNodes(CallerTreeNode left, CallerTreeNode right)
    {
        var result = left.Depth.CompareTo(right.Depth);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(
            SymbolSignatureFormatter.FormatDisplayName(left.Symbol, shortNames: false),
            SymbolSignatureFormatter.FormatDisplayName(right.Symbol, shortNames: false));
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(
            left.Symbol.DocumentPath ?? string.Empty,
            right.Symbol.DocumentPath ?? string.Empty);
        if (result != 0)
        {
            return result;
        }

        result = (left.Symbol.SourceStart ?? -1).CompareTo(right.Symbol.SourceStart ?? -1);
        return result != 0 ? result : left.Symbol.Id.CompareTo(right.Symbol.Id);
    }

    private static int CompareEdges(CallerTreeEdge left, CallerTreeEdge right)
    {
        var result = left.CallerSymbolId.CompareTo(right.CallerSymbolId);
        return result != 0 ? result : left.CalleeSymbolId.CompareTo(right.CalleeSymbolId);
    }

    private static int CompareParentEdges(
        CallerTreeEdge left,
        CallerTreeEdge right,
        IReadOnlyDictionary<long, int> nodeOrder)
    {
        var result = nodeOrder[left.CalleeSymbolId].CompareTo(nodeOrder[right.CalleeSymbolId]);
        return result != 0 ? result : left.CalleeSymbolId.CompareTo(right.CalleeSymbolId);
    }

    private static void SortWithCancellation<T>(
        List<T> values,
        Comparison<T> comparison,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            values.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = comparison(left, right);
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
    }

    private static string EscapeMermaidLabel(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("[", "&#91;", StringComparison.Ordinal)
        .Replace("]", "&#93;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\r\n", "<br/>", StringComparison.Ordinal)
        .Replace("\r", "<br/>", StringComparison.Ordinal)
        .Replace("\n", "<br/>", StringComparison.Ordinal);

    private static string NormalizeText(string value) => value
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
