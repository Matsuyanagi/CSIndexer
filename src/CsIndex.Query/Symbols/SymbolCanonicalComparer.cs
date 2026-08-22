using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.Query.Symbols;

/// <summary>
/// Orders logical symbols by their persisted semantic identity and preferred source location.
/// </summary>
public sealed class SymbolCanonicalComparer : IComparer<StoredSymbol>
{
    public static SymbolCanonicalComparer Instance { get; } = new();

    private SymbolCanonicalComparer()
    {
    }

    public int Compare(StoredSymbol? left, StoredSymbol? right)
    {
        if (ReferenceEquals(left, right))
        {
            if (left is not null)
            {
                _ = CreateKey(left);
            }

            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        return CompareKeys(CreateKey(left), CreateKey(right));
    }

    internal static IReadOnlyList<StoredSymbol> OrderSymbols(
        IEnumerable<StoredSymbol> symbols,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var materialized = Materialize(symbols, cancellationToken);
        foreach (var symbol in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = CreateKey(symbol);
        }

        return SortMaterialized(
            materialized,
            Instance.Compare,
            cancellationToken,
            afterOrderingComparison);
    }

    internal static IReadOnlyList<StoredDeclaration> OrderDefinitionDeclarations(
        IEnumerable<StoredDeclaration> declarations,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var materialized = Materialize(declarations, cancellationToken);
        foreach (var declaration in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = DeclarationRoleRank(declaration.Role);
        }

        return SortMaterialized(
            materialized,
            CompareDefinitionDeclarations,
            cancellationToken,
            afterOrderingComparison);
    }

    internal static IReadOnlyList<(StoredSymbol Symbol, StoredDeclaration Declaration)> OrderSourceMatches(
        IEnumerable<(StoredSymbol Symbol, StoredDeclaration Declaration)> matches,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var materialized = Materialize(matches, cancellationToken);
        foreach (var match in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = CreateKey(match.Symbol);
            _ = DeclarationRoleRank(match.Declaration.Role);
        }

        return SortMaterialized(
            materialized,
            static (left, right) => CompareSourceMatches(
                left.Symbol,
                left.Declaration,
                right.Symbol,
                right.Declaration),
            cancellationToken,
            afterOrderingComparison);
    }

    internal static int CompareDefinitionDeclarations(
        StoredDeclaration? left,
        StoredDeclaration? right)
    {
        if (ReferenceEquals(left, right))
        {
            if (left is not null)
            {
                _ = DeclarationRoleRank(left.Role);
            }

            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var result = DeclarationRoleRank(left.Role).CompareTo(DeclarationRoleRank(right.Role));
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.DocumentPath, right.DocumentPath);
        if (result != 0)
        {
            return result;
        }

        result = left.SourceStart.CompareTo(right.SourceStart);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
    }

    internal static int CompareSourceMatches(
        StoredSymbol? leftSymbol,
        StoredDeclaration? leftDeclaration,
        StoredSymbol? rightSymbol,
        StoredDeclaration? rightDeclaration)
    {
        if (leftSymbol is null)
        {
            return rightSymbol is null ? 0 : -1;
        }

        if (rightSymbol is null)
        {
            return 1;
        }

        ArgumentNullException.ThrowIfNull(leftDeclaration);
        ArgumentNullException.ThrowIfNull(rightDeclaration);

        var result = Instance.Compare(leftSymbol, rightSymbol);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(leftDeclaration.DocumentPath, rightDeclaration.DocumentPath);
        if (result != 0)
        {
            return result;
        }

        result = leftDeclaration.SourceStart.CompareTo(rightDeclaration.SourceStart);
        if (result != 0)
        {
            return result;
        }

        result = DeclarationRoleRank(leftDeclaration.Role).CompareTo(DeclarationRoleRank(rightDeclaration.Role));
        return result != 0 ? result : leftDeclaration.Id.CompareTo(rightDeclaration.Id);
    }

    internal static IReadOnlyList<StoredCall> OrderCalls(
        IEnumerable<StoredCall> calls,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(symbolsById);
        var materialized = Materialize(calls, cancellationToken);
        foreach (var call in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCallEndpoints(call, symbolsById);
        }

        return SortMaterialized(
            materialized,
            (left, right) => CompareCalls(left, right, symbolsById),
            cancellationToken,
            afterOrderingComparison);
    }

    internal static int CompareCalls(
        StoredCall? left,
        StoredCall? right,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById)
    {
        ArgumentNullException.ThrowIfNull(symbolsById);
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        ValidateCallEndpoints(left, symbolsById);
        ValidateCallEndpoints(right, symbolsById);

        var result = CompareEndpoint(left.CallerSymbolId, right.CallerSymbolId, symbolsById);
        if (result != 0)
        {
            return result;
        }

        result = CompareResolvedCallTarget(left, right, symbolsById);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.DocumentPath, right.DocumentPath);
        if (result != 0)
        {
            return result;
        }

        result = left.SourceStart.CompareTo(right.SourceStart);
        if (result != 0)
        {
            return result;
        }

        result = left.SourceLength.CompareTo(right.SourceLength);
        if (result != 0)
        {
            return result;
        }

        result = ((int)left.ReferenceKind).CompareTo((int)right.ReferenceKind);
        if (result != 0)
        {
            return result;
        }

        result = ((int)left.DispatchKind).CompareTo((int)right.DispatchKind);
        if (result != 0)
        {
            return result;
        }

        result = ((int)left.ResolutionStatus).CompareTo((int)right.ResolutionStatus);
        if (result != 0)
        {
            return result;
        }

        result = ((int)left.ResolutionReason).CompareTo((int)right.ResolutionReason);
        if (result != 0)
        {
            return result;
        }

        result = ((int)left.AsyncUsageKind).CompareTo((int)right.AsyncUsageKind);
        if (result != 0)
        {
            return result;
        }

        result = CompareNullLast(left.UnresolvedName, right.UnresolvedName);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
    }

    internal static IReadOnlyList<StoredRelation> OrderRelations(
        IEnumerable<StoredRelation> relations,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(symbolsById);
        var materialized = Materialize(relations, cancellationToken);
        foreach (var relation in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GetSymbol(relation.SourceSymbolId, symbolsById, "Relation source");
            _ = GetSymbol(relation.TargetSymbolId, symbolsById, "Relation target");
        }

        return SortMaterialized(
            materialized,
            (left, right) => CompareRelations(left, right, symbolsById),
            cancellationToken,
            afterOrderingComparison);
    }

    internal static int CompareRelations(
        StoredRelation? left,
        StoredRelation? right,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById)
    {
        ArgumentNullException.ThrowIfNull(symbolsById);
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var result = CompareEndpoint(left.SourceSymbolId, right.SourceSymbolId, symbolsById);
        if (result != 0)
        {
            return result;
        }

        result = CompareEndpoint(left.TargetSymbolId, right.TargetSymbolId, symbolsById);
        return result != 0 ? result : ((int)left.Kind).CompareTo((int)right.Kind);
    }

    internal static IReadOnlyList<CallerTreeNode> OrderCallerTreeNodes(
        IEnumerable<CallerTreeNode> nodes,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var materialized = Materialize(nodes, cancellationToken);
        foreach (var node in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = CreateKey(node.Symbol);
        }

        return SortMaterialized(
            materialized,
            CompareCallerTreeNodes,
            cancellationToken,
            afterOrderingComparison);
    }

    internal static int CompareCallerTreeNodes(CallerTreeNode? left, CallerTreeNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            if (left is not null)
            {
                _ = CreateKey(left.Symbol);
            }

            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var result = left.Depth.CompareTo(right.Depth);
        if (result != 0)
        {
            return result;
        }

        result = Instance.Compare(left.Symbol, right.Symbol);
        return result != 0 ? result : left.Symbol.Id.CompareTo(right.Symbol.Id);
    }

    internal static IReadOnlyList<CallerTreeEdge> OrderCallerTreeEdges(
        IEnumerable<CallerTreeEdge> edges,
        IReadOnlyDictionary<long, CallerTreeNode> nodesById,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison = null)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(nodesById);
        var materialized = Materialize(edges, cancellationToken);
        foreach (var edge in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GetNode(edge.CallerSymbolId, nodesById, "Caller tree caller");
            _ = GetNode(edge.CalleeSymbolId, nodesById, "Caller tree callee");
        }

        return SortMaterialized(
            materialized,
            (left, right) => CompareCallerTreeEdges(left, right, nodesById),
            cancellationToken,
            afterOrderingComparison);
    }

    internal static int CompareCallerTreeEdges(
        CallerTreeEdge? left,
        CallerTreeEdge? right,
        IReadOnlyDictionary<long, CallerTreeNode> nodesById)
    {
        ArgumentNullException.ThrowIfNull(nodesById);
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftParent = GetNode(left.CalleeSymbolId, nodesById, "Caller tree callee");
        var rightParent = GetNode(right.CalleeSymbolId, nodesById, "Caller tree callee");
        var leftChild = GetNode(left.CallerSymbolId, nodesById, "Caller tree caller");
        var rightChild = GetNode(right.CallerSymbolId, nodesById, "Caller tree caller");

        var result = leftParent.Depth.CompareTo(rightParent.Depth);
        if (result != 0)
        {
            return result;
        }

        result = Instance.Compare(leftParent.Symbol, rightParent.Symbol);
        if (result != 0)
        {
            return result;
        }

        result = leftChild.Depth.CompareTo(rightChild.Depth);
        if (result != 0)
        {
            return result;
        }

        result = Instance.Compare(leftChild.Symbol, rightChild.Symbol);
        if (result != 0)
        {
            return result;
        }

        result = leftParent.Symbol.Id.CompareTo(rightParent.Symbol.Id);
        if (result != 0)
        {
            return result;
        }

        result = leftChild.Symbol.Id.CompareTo(rightChild.Symbol.Id);
        if (result != 0)
        {
            return result;
        }

        result = left.CallerSymbolId.CompareTo(right.CallerSymbolId);
        return result != 0 ? result : left.CalleeSymbolId.CompareTo(right.CalleeSymbolId);
    }

    private static int CompareResolvedCallTarget(
        StoredCall left,
        StoredCall right,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById)
    {
        var leftTarget = GetCallTarget(left, symbolsById);
        var rightTarget = GetCallTarget(right, symbolsById);
        if (leftTarget is null)
        {
            return rightTarget is null ? 0 : 1;
        }

        if (rightTarget is null)
        {
            return -1;
        }

        return CompareKeys(CreateKey(leftTarget), CreateKey(rightTarget));
    }

    private static StoredSymbol? GetCallTarget(
        StoredCall call,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById) =>
        call.CalleeDefinitionId is long definitionId
            ? GetSymbol(definitionId, symbolsById, "Call definition endpoint")
            : call.CalleeSymbolId is long calleeId
                ? GetSymbol(calleeId, symbolsById, "Call callee endpoint")
                : null;

    private static void ValidateCallEndpoints(
        StoredCall call,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById)
    {
        _ = GetSymbol(call.CallerSymbolId, symbolsById, "Call caller endpoint");
        if (call.CalleeSymbolId is long calleeId)
        {
            _ = GetSymbol(calleeId, symbolsById, "Call callee endpoint");
        }

        if (call.CalleeDefinitionId is long definitionId)
        {
            _ = GetSymbol(definitionId, symbolsById, "Call definition endpoint");
        }
    }

    private static int CompareEndpoint(
        long leftId,
        long rightId,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById)
    {
        var left = GetSymbol(leftId, symbolsById, "Endpoint");
        var right = GetSymbol(rightId, symbolsById, "Endpoint");
        return Instance.Compare(left, right);
    }

    private static StoredSymbol GetSymbol(
        long id,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById,
        string description)
    {
        if (!symbolsById.TryGetValue(id, out var symbol))
        {
            throw new InvalidOperationException(
                $"{description} symbol ID {id} could not be resolved in the selected profile.");
        }

        _ = CreateKey(symbol);
        return symbol;
    }

    private static CallerTreeNode GetNode(
        long id,
        IReadOnlyDictionary<long, CallerTreeNode> nodesById,
        string description)
    {
        if (!nodesById.TryGetValue(id, out var node))
        {
            throw new InvalidOperationException($"{description} node ID {id} is not present in the caller tree.");
        }

        _ = CreateKey(node.Symbol);
        return node;
    }

    private static int DeclarationRoleRank(DeclarationRole role) => role switch
    {
        DeclarationRole.PartialDefinition => 1,
        DeclarationRole.PartialImplementation => 2,
        DeclarationRole.Ordinary => 3,
        _ => throw new InvalidOperationException($"Unknown declaration role value: {(int)role}."),
    };

    private static CanonicalKey CreateKey(StoredSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Path is not SymbolPathData path)
        {
            throw new InvalidOperationException(
                $"Symbol ID {symbol.Id} has no semantic path data for canonical ordering.");
        }

        return new CanonicalKey(
            path.NamespacePath,
            path.TypeIdentityPath,
            path.ExecutableIdentityPath,
            symbol.PreferredDocumentPath,
            symbol.PreferredSourceStart,
            symbol.StableKey);
    }

    private static int CompareKeys(CanonicalKey left, CanonicalKey right)
    {
        var result = StringComparer.Ordinal.Compare(left.NamespacePath, right.NamespacePath);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.TypeIdentityPath, right.TypeIdentityPath);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.ExecutableIdentityPath, right.ExecutableIdentityPath);
        if (result != 0)
        {
            return result;
        }

        result = CompareMissingLast(left.PreferredDocumentPath, right.PreferredDocumentPath);
        if (result != 0)
        {
            return result;
        }

        result = CompareMissingLast(left.PreferredSourceStart, right.PreferredSourceStart);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.StableKey, right.StableKey);
    }

    private static int CompareMissingLast(string? left, string? right)
    {
        var leftMissing = string.IsNullOrEmpty(left);
        var rightMissing = string.IsNullOrEmpty(right);
        if (leftMissing || rightMissing)
        {
            if (leftMissing == rightMissing)
            {
                return 0;
            }

            return leftMissing ? 1 : -1;
        }

        return StringComparer.Ordinal.Compare(left, right);
    }

    private static int CompareMissingLast(int? left, int? right)
    {
        if (left is null || right is null)
        {
            if (left is null && right is null)
            {
                return 0;
            }

            return left is null ? 1 : -1;
        }

        return left.Value.CompareTo(right.Value);
    }

    private static int CompareNullLast(string? left, string? right)
    {
        if (left is null || right is null)
        {
            if (left is null && right is null)
            {
                return 0;
            }

            return left is null ? 1 : -1;
        }

        return StringComparer.Ordinal.Compare(left, right);
    }

    private static List<T> Materialize<T>(
        IEnumerable<T> values,
        CancellationToken cancellationToken)
    {
        var materialized = new List<T>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            materialized.Add(value);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return materialized;
    }

    private static IReadOnlyList<T> SortMaterialized<T>(
        List<T> materialized,
        Comparison<T> comparison,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison)
    {
        try
        {
            materialized.Sort((left, right) =>
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
        return materialized;
    }

    private readonly record struct CanonicalKey(
        string NamespacePath,
        string TypeIdentityPath,
        string ExecutableIdentityPath,
        string? PreferredDocumentPath,
        int? PreferredSourceStart,
        string StableKey);
}
