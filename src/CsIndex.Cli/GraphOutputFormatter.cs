using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal sealed class GraphOutputFormatter(bool shortNames)
{
    private readonly bool _shortNames = shortNames;

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
            Console.WriteLine($"No reachable asynchronous function: {DisplayName(result.Root)}");
            return;
        }

        for (var index = 0; index < result.Nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = index == 0 ? string.Empty : string.Concat(Enumerable.Repeat("   ", index - 1)) + "└─ ";
            Console.WriteLine(prefix + AsyncDisplayName(result.Nodes[index]));
        }

        if (result.Truncated)
        {
            Console.WriteLine(string.Concat(Enumerable.Repeat("   ", result.Nodes.Count - 1)) + "└─ <truncated>");
        }
    }

    private void WriteAsyncLine(AsyncPathResult result, CancellationToken cancellationToken)
    {
        if (!result.Found)
        {
            Console.WriteLine($"No reachable asynchronous function: {DisplayName(result.Root)}");
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

        Console.WriteLine(string.Join(" -> ", values));
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
            profile = result.Profile.Name,
            found = result.Found,
            truncated = result.Truncated,
            root = OutputFormatter.ToSymbolObject(result.Root, _shortNames, includeSource: false),
            nodes,
        });
    }

    private void WriteCallerTextTree(CallerTreeResult result, CancellationToken cancellationToken)
    {
        var nodesById = new Dictionary<long, CallerTreeNode>();
        foreach (var node in OrderNodes(result.Nodes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodesById.TryAdd(node.Symbol.Id, node);
        }

        nodesById.TryAdd(result.Root.Id, new CallerTreeNode(result.Root, 0));
        var nodes = OrderNodes(nodesById.Values);
        var nodeOrder = nodes
            .Select((node, index) => (node.Symbol.Id, index))
            .ToDictionary(entry => entry.Id, entry => entry.index);
        var edges = OrderEdges(result.Edges);
        var spanningEdges = SelectSpanningEdges(result.Root.Id, nodes, nodesById, nodeOrder, edges, cancellationToken);
        var childrenByParent = spanningEdges
            .GroupBy(edge => edge.CalleeSymbolId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(edge => nodesById[edge.CallerSymbolId])
                    .OrderBy(node => nodeOrder[node.Symbol.Id])
                    .ToArray());
        var spanningEdgeSet = spanningEdges.ToHashSet();

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
            Console.WriteLine(prefix + DisplayName(node.Symbol));

            if (!childrenByParent.TryGetValue(node.Symbol.Id, out var children))
            {
                continue;
            }

            for (var index = children.Length - 1; index >= 0; index--)
            {
                stack.Push((children[index], indent + 1));
            }
        }

        if (result.Truncated)
        {
            Console.WriteLine("└─ <truncated>");
        }

        var additionalEdges = edges.Where(edge => !spanningEdgeSet.Contains(edge)).ToArray();
        if (additionalEdges.Length == 0)
        {
            return;
        }

        Console.WriteLine("Additional edges:");
        foreach (var edge in additionalEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine(
                $"  {GetEdgeDisplayName(edge.CallerSymbolId, nodesById)} -> {GetEdgeDisplayName(edge.CalleeSymbolId, nodesById)}");
        }
    }

    private void WriteMermaid(CallerTreeResult result, CancellationToken cancellationToken)
    {
        Console.WriteLine("flowchart TD");
        foreach (var node in result.Nodes.OrderBy(node => node.Symbol.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"    n{node.Symbol.Id}[\"{EscapeMermaidLabel(RawDisplayName(node.Symbol))}\"]");
        }

        foreach (var edge in OrderEdges(result.Edges))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"    n{edge.CallerSymbolId} --> n{edge.CalleeSymbolId}");
        }

        if (result.Truncated)
        {
            Console.WriteLine("    %% truncated");
        }
    }

    private void WriteCallerJson(CallerTreeResult result, CancellationToken cancellationToken)
    {
        var nodes = new List<object>();
        foreach (var node in OrderNodes(result.Nodes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(new
            {
                symbol = OutputFormatter.ToSymbolObject(node.Symbol, _shortNames, includeSource: false),
                node.Depth,
            });
        }

        var edges = new List<CallerTreeEdge>();
        foreach (var edge in OrderEdges(result.Edges))
        {
            cancellationToken.ThrowIfCancellationRequested();
            edges.Add(edge);
        }

        OutputFormatter.WriteJson(new
        {
            profile = result.Profile.Name,
            truncated = result.Truncated,
            root = OutputFormatter.ToSymbolObject(result.Root, _shortNames, includeSource: false),
            nodes,
            edges,
        });
    }

    private string DisplayName(StoredSymbol symbol) => NormalizeText(RawDisplayName(symbol));

    private string RawDisplayName(StoredSymbol symbol) =>
        SymbolSignatureFormatter.FormatDisplayName(symbol.DisplayName, _shortNames);

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

            var parentEdge = edges
                .Where(edge =>
                    edge.CallerSymbolId == node.Symbol.Id &&
                    nodesById.TryGetValue(edge.CalleeSymbolId, out var parent) &&
                    parent.Depth == node.Depth - 1)
                .OrderBy(edge => nodeOrder[edge.CalleeSymbolId])
                .ThenBy(edge => edge.CalleeSymbolId)
                .FirstOrDefault();
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

    private static IReadOnlyList<CallerTreeNode> OrderNodes(IEnumerable<CallerTreeNode> nodes) => nodes
        .OrderBy(node => node.Depth)
        .ThenBy(node => node.Symbol.DisplayName, StringComparer.Ordinal)
        .ThenBy(node => node.Symbol.DocumentPath ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(node => node.Symbol.SourceStart ?? -1)
        .ThenBy(node => node.Symbol.Id)
        .ToArray();

    private static IReadOnlyList<CallerTreeEdge> OrderEdges(IEnumerable<CallerTreeEdge> edges) => edges
        .Distinct()
        .OrderBy(edge => edge.CallerSymbolId)
        .ThenBy(edge => edge.CalleeSymbolId)
        .ToArray();

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
