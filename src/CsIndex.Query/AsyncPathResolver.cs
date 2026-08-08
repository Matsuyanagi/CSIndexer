using CsIndex.Storage;

namespace CsIndex.Query;

internal sealed class AsyncPathResolver(QueryRepository repository)
{
    public async Task<AsyncPathResult> ResolveAsync(
        StoredProfile profile,
        StoredSymbol root,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (root.AsyncInvolvementDepth is null)
        {
            return new AsyncPathResult(profile, root, [], Found: false, Truncated: false);
        }

        var nodes = new List<StoredSymbol>();
        var visitedIds = new HashSet<long>();
        var current = root;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedIds.Add(current.Id))
            {
                throw IntegrityFailure($"a cycle was encountered at symbol ID {current.Id}");
            }

            var currentDepth = current.AsyncInvolvementDepth;
            if (currentDepth is null || currentDepth < 0)
            {
                throw IntegrityFailure($"symbol ID {current.Id} has an invalid async involvement depth");
            }

            nodes.Add(current);
            if (currentDepth == 0)
            {
                if (current.AsyncNextSymbolId is not null)
                {
                    throw IntegrityFailure($"async origin symbol ID {current.Id} has a next-hop ID");
                }

                return new AsyncPathResult(profile, root, nodes, Found: true, Truncated: false);
            }

            if (current.AsyncNextSymbolId is not long nextSymbolId)
            {
                throw IntegrityFailure($"non-origin symbol ID {current.Id} has no next-hop ID");
            }

            var next = await GetNextSymbolAsync(profile.Id, nextSymbolId, cancellationToken);
            if (visitedIds.Contains(next.Id))
            {
                throw IntegrityFailure($"a cycle was encountered through symbol ID {next.Id}");
            }

            if (next.AsyncInvolvementDepth is not int nextDepth || nextDepth != currentDepth - 1)
            {
                throw IntegrityFailure(
                    $"next-hop symbol ID {nextSymbolId} does not decrease depth from {currentDepth} by exactly one");
            }

            if (nodes.Count == maxNodes)
            {
                return new AsyncPathResult(profile, root, nodes, Found: true, Truncated: true);
            }

            current = next;
        }
    }

    private async Task<StoredSymbol> GetNextSymbolAsync(
        long profileId,
        long nextSymbolId,
        CancellationToken cancellationToken)
    {
        var matches = await repository.GetSymbolsByIdsAsync(profileId, [nextSymbolId], cancellationToken);
        if (matches.Count != 1)
        {
            throw IntegrityFailure(
                $"next-hop symbol ID {nextSymbolId} is missing or does not belong to the selected profile");
        }

        return matches[0];
    }

    private static IndexDatabaseException IntegrityFailure(string detail) =>
        new($"Async path integrity failure: {detail}.");
}
