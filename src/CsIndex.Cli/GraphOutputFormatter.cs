using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal sealed class GraphOutputFormatter
{
    private readonly IndexPathResolver _pathResolver;
    private readonly PathDisplayStyle _pathStyle;
    private readonly SymbolPathFormatOptions _symbolPathOptions;
    private readonly TextWriter _writer;

    public GraphOutputFormatter(
        SymbolPathFormatOptions symbolPathOptions,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle,
        TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(pathResolver);
        _symbolPathOptions = symbolPathOptions;
        _pathResolver = pathResolver;
        _pathStyle = pathStyle;
        _writer = writer;
    }

    private TextWriter Writer => _writer;

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
            nodes.Add(OutputFormatter.ToSymbolObject(
                node,
                _symbolPathOptions,
                _pathResolver,
                _pathStyle,
                includeSource: false));
        }

        OutputFormatter.WriteJson(new
        {
            profile = result.Selection.Profile.Name,
            found = result.Found,
            truncated = result.Truncated,
            root = OutputFormatter.ToSymbolObject(
                result.Root,
                _symbolPathOptions,
                _pathResolver,
                _pathStyle,
                includeSource: false),
            nodes,
        }, Writer);
    }

    private void WriteCallerTextTree(CallerTreeResult result, CancellationToken cancellationToken)
    {
        var presentation = CreateCallerTreePresentation(result, cancellationToken);
        if (!result.ShowSource)
        {
            WriteCallerTextTreeWithoutSources(result, presentation, cancellationToken);
            return;
        }

        foreach (var node in presentation.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indent = presentation.IndentById[node.Symbol.Id];
            var prefix = indent == 0
                ? string.Empty
                : string.Concat(Enumerable.Repeat("   ", indent - 1)) + "└─ ";
            Writer.WriteLine(prefix + DisplayName(node.Symbol));

            if (indent == 0 ||
                !presentation.SpanningEdgeByCallerId.TryGetValue(node.Symbol.Id, out var spanningEdge) ||
                !presentation.CallSitesByEdge.TryGetValue(spanningEdge, out var callSites))
            {
                continue;
            }

            var sitePrefix = string.Concat(Enumerable.Repeat("   ", indent));
            foreach (var callSite in callSites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Writer.WriteLine(FormatTreeCallSite(callSite, sitePrefix, cancellationToken));
            }
        }

        if (result.Truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine("└─ <truncated>");
        }

        if (presentation.AdditionalEdges.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Writer.WriteLine("Additional edges:");
        foreach (var edge in presentation.AdditionalEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine(
                $"  {GetEdgeDisplayName(edge.CallerSymbolId, presentation.NodesById)} -> " +
                $"{GetEdgeDisplayName(edge.CalleeSymbolId, presentation.NodesById)}");

            if (!presentation.CallSitesByEdge.TryGetValue(edge, out var callSites))
            {
                continue;
            }

            foreach (var callSite in callSites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Writer.WriteLine(FormatTreeCallSite(callSite, "    ", cancellationToken));
            }
        }
    }

    private void WriteCallerTextTreeWithoutSources(
        CallerTreeResult result,
        CallerTreePresentation presentation,
        CancellationToken cancellationToken)
    {
        foreach (var node in presentation.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indent = presentation.IndentById[node.Symbol.Id];
            var prefix = indent == 0
                ? string.Empty
                : string.Concat(Enumerable.Repeat("   ", indent - 1)) + "└─ ";
            Writer.WriteLine(prefix + DisplayName(node.Symbol));
        }

        if (result.Truncated)
        {
            Writer.WriteLine("└─ <truncated>");
        }

        if (presentation.AdditionalEdges.Count == 0)
        {
            return;
        }

        Writer.WriteLine("Additional edges:");
        foreach (var edge in presentation.AdditionalEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine(
                $"  {GetEdgeDisplayName(edge.CallerSymbolId, presentation.NodesById)} -> " +
                $"{GetEdgeDisplayName(edge.CalleeSymbolId, presentation.NodesById)}");
        }
    }

    private void WriteMermaid(CallerTreeResult result, CancellationToken cancellationToken)
    {
        var presentation = CreateCallerTreePresentation(result, cancellationToken);
        if (!result.ShowSource)
        {
            WriteMermaidWithoutSources(result, presentation, cancellationToken);
            return;
        }

        Writer.WriteLine("flowchart TD");
        foreach (var node in presentation.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine($"    n{node.Symbol.Id}[\"{EscapeMermaidLabel(RawDisplayName(node.Symbol))}\"]");
        }

        foreach (var edge in presentation.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!presentation.CallSitesByEdge.TryGetValue(edge, out var callSites) || callSites.Count == 0)
            {
                Writer.WriteLine($"    n{edge.CallerSymbolId} --> n{edge.CalleeSymbolId}");
                continue;
            }

            var label = FormatMermaidCallSiteLabel(callSites, cancellationToken);
            Writer.WriteLine($"    n{edge.CallerSymbolId} -->|\"{label}\"| n{edge.CalleeSymbolId}");
        }

        if (result.Truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine("    %% truncated");
        }
    }

    private void WriteMermaidWithoutSources(
        CallerTreeResult result,
        CallerTreePresentation presentation,
        CancellationToken cancellationToken)
    {
        Writer.WriteLine("flowchart TD");
        foreach (var node in presentation.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writer.WriteLine($"    n{node.Symbol.Id}[\"{EscapeMermaidLabel(RawDisplayName(node.Symbol))}\"]");
        }

        foreach (var edge in presentation.Edges)
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
        var presentation = CreateCallerTreePresentation(result, cancellationToken);
        if (!result.ShowSource)
        {
            WriteCallerJsonWithoutSources(result, presentation, cancellationToken);
            return;
        }

        var nodes = new List<object>();
        foreach (var node in presentation.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(new
            {
                symbol = OutputFormatter.ToSymbolObject(
                    node.Symbol,
                    _symbolPathOptions,
                    _pathResolver,
                    _pathStyle,
                    includeSource: false),
                node.Depth,
            });
        }

        var edges = new List<object>();
        foreach (var edge in presentation.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callSites = new List<object>();
            if (presentation.CallSitesByEdge.TryGetValue(edge, out var edgeCallSites))
            {
                foreach (var callSite in edgeCallSites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = RequireCallSiteSource(callSite.Call);
                    cancellationToken.ThrowIfCancellationRequested();
                    var point = OutputFormatter.ResolveLocation(
                        callSite.Call.DocumentPath,
                        callSite.Call.SourceStart,
                        _pathResolver,
                        _pathStyle);
                    cancellationToken.ThrowIfCancellationRequested();
                    callSites.Add(new
                    {
                        id = callSite.Call.Id,
                        location = new
                        {
                            path = point.Path,
                            line = point.Line,
                            column = point.Column,
                            offset = point.Offset,
                        },
                        normalizedSource = source,
                    });
                }
            }

            edges.Add(new
            {
                callerSymbolId = edge.CallerSymbolId,
                calleeSymbolId = edge.CalleeSymbolId,
                callSites,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        OutputFormatter.WriteJson(new
        {
            profile = result.Selection.Profile.Name,
            truncated = result.Truncated,
            root = OutputFormatter.ToSymbolObject(
                result.Root,
                _symbolPathOptions,
                _pathResolver,
                _pathStyle,
                includeSource: false),
            nodes,
            edges,
        }, Writer);
    }

    private void WriteCallerJsonWithoutSources(
        CallerTreeResult result,
        CallerTreePresentation presentation,
        CancellationToken cancellationToken)
    {
        var nodes = new List<object>();
        foreach (var node in presentation.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(new
            {
                symbol = OutputFormatter.ToSymbolObject(
                    node.Symbol,
                    _symbolPathOptions,
                    _pathResolver,
                    _pathStyle,
                    includeSource: false),
                node.Depth,
            });
        }

        var edges = new List<CallerTreeEdge>();
        foreach (var edge in presentation.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            edges.Add(edge);
        }

        OutputFormatter.WriteJson(new
        {
            profile = result.Selection.Profile.Name,
            truncated = result.Truncated,
            root = OutputFormatter.ToSymbolObject(
                result.Root,
                _symbolPathOptions,
                _pathResolver,
                _pathStyle,
                includeSource: false),
            nodes,
            edges,
        }, Writer);
    }

    private string DisplayName(StoredSymbol symbol) => NormalizeText(RawDisplayName(symbol));

    private string RawDisplayName(StoredSymbol symbol) =>
        SymbolSignatureFormatter.FormatDisplayName(symbol, _symbolPathOptions);

    private string AsyncDisplayName(StoredSymbol symbol) =>
        symbol.AsyncRole == AsyncRole.None ? DisplayName(symbol) : $"async {DisplayName(symbol)}";

    private static CallerTreePresentation CreateCallerTreePresentation(
        CallerTreeResult result,
        CancellationToken cancellationToken)
    {
        var nodesById = new Dictionary<long, CallerTreeNode>();
        foreach (var node in OrderNodes(result.Nodes, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodesById.TryAdd(node.Symbol.Id, node);
        }

        nodesById[result.Root.Id] = new CallerTreeNode(result.Root, 0);
        var baselineNodes = OrderNodes(nodesById.Values, cancellationToken);
        var baselineOrder = CreateNodeOrder(baselineNodes, cancellationToken);
        var uniqueEdges = MaterializeUniqueEdges(result.Edges, cancellationToken);
        var spanningEdges = SelectSpanningEdges(
            result.Root.Id,
            baselineNodes,
            nodesById,
            baselineOrder,
            uniqueEdges,
            cancellationToken);

        var childrenByParent = new Dictionary<long, List<CallerTreeNode>>();
        foreach (var edge in spanningEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!childrenByParent.TryGetValue(edge.CalleeSymbolId, out var children))
            {
                children = [];
                childrenByParent.Add(edge.CalleeSymbolId, children);
            }

            children.Add(nodesById[edge.CallerSymbolId]);
        }

        foreach (var children in childrenByParent.Values)
        {
            SortWithCancellation(
                children,
                static (left, right) => SymbolCanonicalComparer.Instance.Compare(left.Symbol, right.Symbol),
                cancellationToken,
                afterOrderingComparison: null);
        }

        var nodes = new List<CallerTreeNode>(baselineNodes.Count);
        var indentById = new Dictionary<long, int>(baselineNodes.Count);
        var visited = new HashSet<long>();

        void AppendDepthFirst(CallerTreeNode start, int startIndent)
        {
            var stack = new Stack<(CallerTreeNode Node, int Indent)>();
            stack.Push((start, startIndent));
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (node, indent) = stack.Pop();
                if (!visited.Add(node.Symbol.Id))
                {
                    continue;
                }

                nodes.Add(node);
                indentById.Add(node.Symbol.Id, indent);
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
        }

        AppendDepthFirst(nodesById[result.Root.Id], startIndent: 0);
        foreach (var node in baselineNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Contains(node.Symbol.Id))
            {
                AppendDepthFirst(node, startIndent: 0);
            }
        }

        var presentationOrder = CreateNodeOrder(nodes, cancellationToken);
        var edges = OrderEdges(uniqueEdges, presentationOrder, cancellationToken);
        var spanningEdgeSet = new HashSet<CallerTreeEdge>(spanningEdges);
        var additionalEdges = edges.Where(edge => !spanningEdgeSet.Contains(edge)).ToArray();
        var spanningEdgeByCallerId = new Dictionary<long, CallerTreeEdge>();
        foreach (var edge in spanningEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            spanningEdgeByCallerId.TryAdd(edge.CallerSymbolId, edge);
        }

        IReadOnlyDictionary<CallerTreeEdge, IReadOnlyList<CallerTreeCallSite>> callSitesByEdge =
            result.ShowSource
                ? GroupCallSites(result.CallSites, uniqueEdges, cancellationToken)
                : new Dictionary<CallerTreeEdge, IReadOnlyList<CallerTreeCallSite>>();
        return new CallerTreePresentation(
            nodes,
            edges,
            additionalEdges,
            nodesById,
            indentById,
            spanningEdges,
            spanningEdgeByCallerId,
            callSitesByEdge);
    }

    private static IReadOnlyDictionary<CallerTreeEdge, IReadOnlyList<CallerTreeCallSite>> GroupCallSites(
        IEnumerable<CallerTreeCallSite> callSites,
        IReadOnlyList<CallerTreeEdge> edges,
        CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<CallerTreeEdge, List<CallerTreeCallSite>>();
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grouped.TryAdd(edge, []);
        }

        foreach (var callSite in callSites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edge = new CallerTreeEdge(callSite.CallerSymbolId, callSite.CalleeSymbolId);
            if (!grouped.TryGetValue(edge, out var edgeCallSites))
            {
                throw new IndexDatabaseException(
                    $"Caller tree call-site association ({callSite.CallerSymbolId}, {callSite.CalleeSymbolId}) " +
                    "is not present in the structural edge set.");
            }

            edgeCallSites.Add(callSite);
        }

        var result = new Dictionary<CallerTreeEdge, IReadOnlyList<CallerTreeCallSite>>(grouped.Count);
        foreach (var pair in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(pair.Key, pair.Value);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static IReadOnlyDictionary<long, int> CreateNodeOrder(
        IReadOnlyList<CallerTreeNode> nodes,
        CancellationToken cancellationToken)
    {
        var order = new Dictionary<long, int>(nodes.Count);
        for (var index = 0; index < nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add(nodes[index].Symbol.Id, index);
        }

        return order;
    }

    private static IReadOnlyList<CallerTreeEdge> MaterializeUniqueEdges(
        IEnumerable<CallerTreeEdge> edges,
        CancellationToken cancellationToken)
    {
        var result = new List<CallerTreeEdge>();
        var seen = new HashSet<CallerTreeEdge>();
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seen.Add(edge))
            {
                result.Add(edge);
            }
        }

        return result;
    }

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

    private string FormatTreeCallSite(
        CallerTreeCallSite callSite,
        string prefix,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = RequireCallSiteSource(callSite.Call);
        cancellationToken.ThrowIfCancellationRequested();
        var point = OutputFormatter.ResolveLocation(
            callSite.Call.DocumentPath,
            callSite.Call.SourceStart,
            _pathResolver,
            _pathStyle);
        cancellationToken.ThrowIfCancellationRequested();
        return $"{prefix}@ {point.Path}:{point.Line}:{point.Column}\t{TableTextSanitizer.Sanitize(source)}";
    }

    private string FormatMermaidCallSiteLabel(
        IReadOnlyList<CallerTreeCallSite> callSites,
        CancellationToken cancellationToken)
    {
        var labels = new List<string>(callSites.Count);
        foreach (var callSite in callSites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = RequireCallSiteSource(callSite.Call);
            cancellationToken.ThrowIfCancellationRequested();
            var point = OutputFormatter.ResolveLocation(
                callSite.Call.DocumentPath,
                callSite.Call.SourceStart,
                _pathResolver,
                _pathStyle);
            cancellationToken.ThrowIfCancellationRequested();
            labels.Add(EscapeMermaidCallSiteLabel($"{point.Path}:{point.Line}:{point.Column} {source}"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return string.Join("<br/>", labels);
    }

    private static string RequireCallSiteSource(StoredCall call)
    {
        if (call.NormalizedSource is null)
        {
            throw new IndexDatabaseException(
                $"call ID {call.Id} in document ID {call.DocumentId} has no hydrated normalized source.");
        }

        return call.NormalizedSource;
    }

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
        IReadOnlyDictionary<long, int> nodeOrder,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(nodeOrder);
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

        SortWithCancellation(
            ordered,
            (left, right) => CompareEdges(left, right, nodeOrder),
            cancellationToken,
            afterOrderingComparison);
        return ordered;
    }

    private static int CompareNodes(CallerTreeNode left, CallerTreeNode right)
    {
        var result = left.Depth.CompareTo(right.Depth);
        if (result != 0)
        {
            return result;
        }

        return SymbolCanonicalComparer.Instance.Compare(left.Symbol, right.Symbol);
    }

    private static int CompareEdges(
        CallerTreeEdge left,
        CallerTreeEdge right,
        IReadOnlyDictionary<long, int> nodeOrder)
    {
        var result = nodeOrder[left.CallerSymbolId].CompareTo(nodeOrder[right.CallerSymbolId]);
        return result != 0
            ? result
            : nodeOrder[left.CalleeSymbolId].CompareTo(nodeOrder[right.CalleeSymbolId]);
    }

    private static int CompareParentEdges(
        CallerTreeEdge left,
        CallerTreeEdge right,
        IReadOnlyDictionary<long, int> nodeOrder)
    {
        return nodeOrder[left.CalleeSymbolId].CompareTo(nodeOrder[right.CalleeSymbolId]);
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

    private static string EscapeMermaidCallSiteLabel(string value) =>
        EscapeMermaidLabel(value).Replace("|", "&#124;", StringComparison.Ordinal);

    private static string NormalizeText(string value) => value
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private sealed record CallerTreePresentation(
        IReadOnlyList<CallerTreeNode> Nodes,
        IReadOnlyList<CallerTreeEdge> Edges,
        IReadOnlyList<CallerTreeEdge> AdditionalEdges,
        IReadOnlyDictionary<long, CallerTreeNode> NodesById,
        IReadOnlyDictionary<long, int> IndentById,
        IReadOnlyList<CallerTreeEdge> SpanningEdges,
        IReadOnlyDictionary<long, CallerTreeEdge> SpanningEdgeByCallerId,
        IReadOnlyDictionary<CallerTreeEdge, IReadOnlyList<CallerTreeCallSite>> CallSitesByEdge);
}
