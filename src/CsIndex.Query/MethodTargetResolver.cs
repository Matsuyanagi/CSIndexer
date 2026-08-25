using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

internal sealed class MethodTargetResolver(QueryRepository repository)
{
    public async Task<IReadOnlyList<StoredSymbol>> ExpandAsync(
        long profileId,
        IReadOnlyList<ResolvedLogicalRoot> roots,
        bool sourceOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            return [];
        }

        var methods = roots
            .Select(root => root.Symbol)
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Method)
            .GroupBy(symbol => symbol.Id)
            .Select(group => group.First())
            .ToArray();
        if (methods.Length != roots.Select(root => root.Symbol.Id).Distinct().Count())
        {
            throw new SymbolQueryParseException("--include-overrides requires an exact method query.");
        }

        var chain = await repository.GetSymbolsWithContainingAncestorsAsync(
            profileId,
            methods.Select(method => method.Id),
            cancellationToken);
        var chainById = chain.ToDictionary(symbol => symbol.Id);
        var resolvedRoots = methods.Select(method => new ResolvedMethodRoot(
            method,
            FindContainingType(method, chainById))).ToArray();

        var targetIds = methods.Select(method => method.Id).ToHashSet();
        targetIds.UnionWith(await repository.FindInterfaceImplementationMethodIdsAsync(
            profileId,
            resolvedRoots
                .Where(root => root.ContainingType.TypeKind == (int)IndexedTypeKind.Interface)
                .Select(root => new InterfaceSearchSeed(root.Method.Id, root.ContainingType.Id)),
            cancellationToken));
        targetIds.UnionWith(await repository.ExpandOverrideMethodIdsAsync(
            profileId,
            resolvedRoots
                .Where(root => root.ContainingType.TypeKind != (int)IndexedTypeKind.Interface)
                .Select(root => new MethodSearchSeed(root.Method.Id, root.ContainingType.Id)),
            cancellationToken));

        var targets = await repository.GetSymbolsByIdsAsync(profileId, targetIds, cancellationToken);
        var selected = sourceOnly
            ? targets.Where(IsSourceBackedMethod)
            : targets;
        return SymbolCanonicalComparer.OrderSymbols(selected, cancellationToken);
    }

    private static StoredSymbol FindContainingType(
        StoredSymbol method,
        IReadOnlyDictionary<long, StoredSymbol> chainById)
    {
        var visited = new HashSet<long> { method.Id };
        var current = method;
        while (current.Kind != IndexedSymbolKind.Type)
        {
            if (current.ContainingSymbolId is not long parentId ||
                !chainById.TryGetValue(parentId, out current!))
            {
                throw new InvalidOperationException(
                    $"Stored containment chain for method symbol ID {method.Id} does not terminate at a Type.");
            }

            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException(
                    $"Stored containment chain for method symbol ID {method.Id} contains a cycle.");
            }
        }

        return current;
    }

    private static bool IsSourceBackedMethod(StoredSymbol symbol) =>
        symbol.Kind == IndexedSymbolKind.Method &&
        symbol.PreferredDeclarationId is not null &&
        symbol.PreferredDocumentPath is not null;

    private sealed record ResolvedMethodRoot(StoredSymbol Method, StoredSymbol ContainingType);
}
