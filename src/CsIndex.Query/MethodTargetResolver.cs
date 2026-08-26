using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

internal sealed class MethodTargetResolver(QueryRepository repository)
{
    public async Task<RootSelection> ExpandAsync(
        RootSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Roots.Count == 0)
        {
            return selection;
        }

        if (selection.Roots.Any(root => root.Symbol.Kind != IndexedSymbolKind.Method))
        {
            throw new SymbolQueryParseException("--include-overrides requires an exact method query.");
        }

        var methodsById = new Dictionary<long, StoredSymbol>();
        foreach (var root in selection.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            methodsById.TryAdd(root.Symbol.Id, root.Symbol);
        }

        var methods = methodsById.Values.ToArray();
        var chain = await repository.GetSymbolsWithContainingAncestorsAsync(
            selection.Profile.Id,
            methodsById.Keys,
            cancellationToken);
        var chainById = new Dictionary<long, StoredSymbol>();
        foreach (var symbol in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chainById[symbol.Id] = symbol;
        }

        var resolvedRoots = new List<ResolvedMethodRoot>(methods.Length);
        foreach (var method in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolvedRoots.Add(new ResolvedMethodRoot(method, FindContainingType(method, chainById)));
        }

        var targetIds = new HashSet<long>(methodsById.Keys);
        var interfaceSeeds = new List<InterfaceSearchSeed>();
        var overrideSeeds = new List<MethodSearchSeed>();
        foreach (var root in resolvedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.ContainingType.TypeKind == (int)IndexedTypeKind.Interface)
            {
                interfaceSeeds.Add(new InterfaceSearchSeed(root.Method.Id, root.ContainingType.Id));
            }
            else
            {
                overrideSeeds.Add(new MethodSearchSeed(root.Method.Id, root.ContainingType.Id));
            }
        }

        var interfaceIds = await repository.FindInterfaceImplementationMethodIdsAsync(
            selection.Profile.Id,
            interfaceSeeds,
            cancellationToken);
        foreach (var id in interfaceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetIds.Add(id);
        }

        var overrideIds = await repository.ExpandOverrideMethodIdsAsync(
            selection.Profile.Id,
            overrideSeeds,
            cancellationToken);
        foreach (var id in overrideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetIds.Add(id);
        }

        var targets = await repository.GetSymbolsByIdsAsync(
            selection.Profile.Id,
            targetIds,
            cancellationToken);
        var declarations = await repository.GetDeclarationsAsync(
            selection.Profile.Id,
            targetIds,
            includeSourceText: false,
            cancellationToken);
        var declarationsById = new Dictionary<long, List<StoredDeclaration>>();
        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declarationsById.TryGetValue(declaration.SymbolId, out var rowsForSymbol))
            {
                rowsForSymbol = [];
                declarationsById.Add(declaration.SymbolId, rowsForSymbol);
            }

            rowsForSymbol.Add(declaration);
        }

        var originalsById = new Dictionary<long, ResolvedLogicalRoot>();
        foreach (var root in selection.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            originalsById[root.Symbol.Id] = root;
        }

        var roots = new List<ResolvedLogicalRoot>(targets.Count);
        foreach (var symbol in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (originalsById.TryGetValue(symbol.Id, out var original))
            {
                roots.Add(original);
                continue;
            }

            roots.Add(new ResolvedLogicalRoot(
                symbol,
                declarationsById.TryGetValue(symbol.Id, out var rowsForSymbol)
                    ? SymbolCanonicalComparer.OrderDefinitionDeclarations(rowsForSymbol, cancellationToken)
                    : []));
        }

        var orderedSymbols = SymbolCanonicalComparer.OrderSymbols(
            roots.Select(root => root.Symbol),
            cancellationToken);
        var rootsById = new Dictionary<long, ResolvedLogicalRoot>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rootsById[root.Symbol.Id] = root;
        }

        var orderedRoots = new List<ResolvedLogicalRoot>(orderedSymbols.Count);
        foreach (var symbol in orderedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            orderedRoots.Add(rootsById[symbol.Id]);
        }

        return new RootSelection(
            selection.Profile,
            orderedRoots);
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

    private sealed record ResolvedMethodRoot(StoredSymbol Method, StoredSymbol ContainingType);
}
