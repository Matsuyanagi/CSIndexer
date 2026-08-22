using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

internal sealed class MethodTargetResolver(QueryRepository repository)
{
    public async Task<IReadOnlyList<StoredSymbol>> ResolveAsync(
        long profileId,
        SymbolQuery query,
        bool includeOverrides,
        bool sourceOnly,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsMethodQuery)
        {
            return [];
        }

        var receiverCandidates = await repository.FindSymbolCandidatesAsync(
            profileId,
            typeSimpleName: query.TypeSimpleName,
            kind: IndexedSymbolKind.Type,
            sourceOnly: false,
            cancellationToken: cancellationToken);
        var receivers = receiverCandidates
            .Where(receiver => query.NamespaceName is null || receiver.NamespaceName == query.NamespaceName)
            .ToArray();
        if (receivers.Length == 0)
        {
            return [];
        }

        var receiverIds = receivers.Select(receiver => receiver.Id).ToHashSet();
        var declaredCandidates = await repository.FindSymbolCandidatesAsync(
            profileId,
            name: query.MethodName,
            typeSimpleName: query.TypeSimpleName,
            kind: IndexedSymbolKind.Method,
            sourceOnly: sourceOnly,
            cancellationToken: cancellationToken);
        var declaredByReceiver = declaredCandidates
            .Where(method => method.ContainingSymbolId is long receiverId && receiverIds.Contains(receiverId))
            .ToLookup(method => method.ContainingSymbolId!.Value);
        var exactOnlyTargets = declaredCandidates
            .Where(method => method.ContainingSymbolId is not long receiverId || !receiverIds.Contains(receiverId))
            .Where(method => SymbolMatcher.IsMatch(query, method))
            .ToArray();
        var exactOnlyReceiverIds = await FindContainingReceiverTypeIdsAsync(
            profileId,
            exactOnlyTargets,
            receiverIds,
            cancellationToken);

        var roots = new List<ResolvedRoot>();
        var receiversWithoutDeclarations = new List<StoredSymbol>();
        foreach (var receiver in receivers)
        {
            var declaredSameName = declaredByReceiver[receiver.Id].ToArray();
            if (declaredSameName.Length > 0)
            {
                roots.AddRange(declaredSameName
                    .Where(method => SymbolMatcher.IsMethodSignatureMatch(query, method))
                    .Select(method => new ResolvedRoot(method, receiver)));
            }
            else if (includeOverrides && !exactOnlyReceiverIds.Contains(receiver.Id))
            {
                receiversWithoutDeclarations.Add(receiver);
            }
        }

        if (receiversWithoutDeclarations.Count > 0)
        {
            roots.AddRange(await ResolveInheritedRootsAsync(
                profileId,
                query,
                receiversWithoutDeclarations,
                sourceOnly,
                cancellationToken));
        }

        if (roots.Count == 0 && exactOnlyTargets.Length == 0)
        {
            return [];
        }

        var targetIds = exactOnlyTargets.Select(target => target.Id).ToHashSet();
        targetIds.UnionWith(roots.Select(root => root.Method.Id));
        if (includeOverrides)
        {
            var interfaceSeeds = roots
                .Where(root => root.ReceiverType.TypeKind == (int)IndexedTypeKind.Interface)
                .Select(root => new InterfaceSearchSeed(root.Method.Id, root.ReceiverType.Id));
            targetIds.UnionWith(await repository.FindInterfaceImplementationMethodIdsAsync(
                profileId,
                interfaceSeeds,
                cancellationToken));

            var methodSeeds = roots
                .Where(root => root.ReceiverType.TypeKind != (int)IndexedTypeKind.Interface)
                .Select(root => new MethodSearchSeed(root.Method.Id, root.ReceiverType.Id));
            targetIds.UnionWith(await repository.ExpandOverrideMethodIdsAsync(
                profileId,
                methodSeeds,
                cancellationToken));
        }

        var targets = await repository.GetSymbolsByIdsAsync(profileId, targetIds, cancellationToken);
        return sourceOnly
            ? targets.Where(IsSourceBackedMethod).ToArray()
            : targets;
    }

    private async Task<IReadOnlyList<ResolvedRoot>> ResolveInheritedRootsAsync(
        long profileId,
        SymbolQuery query,
        IReadOnlyList<StoredSymbol> receivers,
        bool sourceOnly,
        CancellationToken cancellationToken)
    {
        var inheritedCandidates = await repository.FindInheritedMethodCandidatesAsync(
            profileId,
            receivers.Select(receiver => receiver.Id),
            query.MethodName!,
            cancellationToken);
        var shallowCandidates = inheritedCandidates
            .GroupBy(candidate => candidate.ReceiverTypeId)
            .SelectMany(group =>
            {
                var minimumDepth = group.Min(candidate => candidate.Depth);
                return group.Where(candidate => candidate.Depth == minimumDepth);
            })
            .ToArray();
        var methods = await repository.GetSymbolsByIdsAsync(
            profileId,
            shallowCandidates.Select(candidate => candidate.MethodId),
            cancellationToken);
        var methodsById = methods.ToDictionary(method => method.Id);
        var receiversById = receivers.ToDictionary(receiver => receiver.Id);

        return shallowCandidates
            .Where(candidate => methodsById.ContainsKey(candidate.MethodId))
            .Select(candidate => new ResolvedRoot(
                methodsById[candidate.MethodId],
                receiversById[candidate.ReceiverTypeId]))
            .Where(root => (!sourceOnly || IsSourceBackedMethod(root.Method)) &&
                           SymbolMatcher.IsMethodSignatureMatch(query, root.Method))
            .ToArray();
    }

    private async Task<HashSet<long>> FindContainingReceiverTypeIdsAsync(
        long profileId,
        IReadOnlyList<StoredSymbol> exactOnlyTargets,
        IReadOnlySet<long> receiverIds,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<long>();
        var visited = new HashSet<long>();
        var pending = exactOnlyTargets
            .Where(target => target.ContainingSymbolId is not null)
            .Select(target => target.ContainingSymbolId!.Value)
            .ToHashSet();

        while (pending.Count > 0)
        {
            var batch = pending.Where(visited.Add).ToArray();
            pending.Clear();
            if (batch.Length == 0)
            {
                break;
            }

            var containers = await repository.GetSymbolsByIdsAsync(
                profileId,
                batch,
                cancellationToken);
            foreach (var container in containers)
            {
                if (container.Kind == IndexedSymbolKind.Type)
                {
                    if (receiverIds.Contains(container.Id))
                    {
                        result.Add(container.Id);
                    }

                    continue;
                }

                if (container.ContainingSymbolId is long containingId && !visited.Contains(containingId))
                {
                    pending.Add(containingId);
                }
            }
        }

        return result;
    }

    private static bool IsSourceBackedMethod(StoredSymbol symbol) =>
        symbol.Kind == IndexedSymbolKind.Method &&
        symbol.PreferredDeclarationId is not null &&
        symbol.PreferredDocumentPath is not null;

    private sealed record ResolvedRoot(StoredSymbol Method, StoredSymbol ReceiverType);
}
