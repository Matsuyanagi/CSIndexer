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
        var nodes = OrderNodes(result.Nodes);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = node.Depth == 0
                ? string.Empty
                : string.Concat(Enumerable.Repeat("   ", node.Depth - 1)) + "└─ ";
            Console.WriteLine(prefix + DisplayName(node.Symbol));
        }

        if (result.Truncated)
        {
            Console.WriteLine("└─ <truncated>");
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
