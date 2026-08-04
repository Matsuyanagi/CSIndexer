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
            sourceOnly: sourceOnly,
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
            kind: IndexedSymbolKind.Method,
            sourceOnly: sourceOnly,
            cancellationToken: cancellationToken);
        var declaredByReceiver = declaredCandidates
            .Where(method => method.ContainingSymbolId is long receiverId && receiverIds.Contains(receiverId))
            .ToLookup(method => method.ContainingSymbolId!.Value);

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
            else if (includeOverrides)
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

        if (roots.Count == 0)
        {
            return [];
        }

        var targetIds = roots.Select(root => root.Method.Id).ToHashSet();
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
            ? targets.Where(target => target.DocumentPath is not null).ToArray()
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
            .Where(root => (!sourceOnly || root.Method.DocumentPath is not null) &&
                           SymbolMatcher.IsMethodSignatureMatch(query, root.Method))
            .ToArray();
    }

    private sealed record ResolvedRoot(StoredSymbol Method, StoredSymbol ReceiverType);
}
