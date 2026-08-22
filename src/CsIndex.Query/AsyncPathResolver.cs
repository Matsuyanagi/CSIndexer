using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.Query;

internal sealed class AsyncPathResolver(QueryRepository repository)
{
    private const AsyncRole AsyncOriginRoles =
        AsyncRole.DeclaredAsync |
        AsyncRole.ReturnsAwaitable |
        AsyncRole.ContainsAwait |
        AsyncRole.AsyncIterator |
        AsyncRole.ReturnsAsyncEnumerable |
        AsyncRole.AsyncVoid |
        AsyncRole.UniTaskVoid |
        AsyncRole.UsesAwaitForEach |
        AsyncRole.UsesAwaitUsing;

    public async Task<AsyncPathResult> ResolveAsync(
        StoredProfile profile,
        StoredSymbol root,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = new List<StoredSymbol>();
        var visitedIds = new HashSet<long>();
        var current = root;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateNode(current);
            if (current.AsyncInvolvementDepth is null)
            {
                return new AsyncPathResult(profile, root, [], Found: false, Truncated: false);
            }

            if (!visitedIds.Add(current.Id))
            {
                throw IntegrityFailure($"a cycle was encountered at symbol ID {current.Id}");
            }

            var currentDepth = current.AsyncInvolvementDepth.Value;

            nodes.Add(current);
            if (currentDepth == 0)
            {
                return new AsyncPathResult(profile, root, nodes, Found: true, Truncated: false);
            }

            var nextSymbolId = current.AsyncNextSymbolId!.Value;

            var next = await GetNextSymbolAsync(profile.Id, nextSymbolId, cancellationToken);
            if (visitedIds.Contains(next.Id))
            {
                throw IntegrityFailure($"a cycle was encountered through symbol ID {next.Id}");
            }

            ValidateNode(next);
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

    private static void ValidateNode(StoredSymbol symbol)
    {
        if (!IsSourceBackedExecutable(symbol))
        {
            throw IntegrityFailure(
                $"symbol ID {symbol.Id} is not a source-backed executable " +
                $"(kind {symbol.Kind}, document path {(symbol.DocumentPath is null ? "missing" : "present")}, " +
                $"normalized source {(symbol.NormalizedSource is null ? "missing" : "present")})");
        }

        var isAsyncOrigin = IsAsyncOrigin(symbol);
        var depth = symbol.AsyncInvolvementDepth;
        var nextSymbolId = symbol.AsyncNextSymbolId;
        if (depth is null)
        {
            if (isAsyncOrigin)
            {
                throw IntegrityFailure($"async origin symbol ID {symbol.Id} has null depth");
            }

            if (nextSymbolId is not null)
            {
                throw IntegrityFailure($"symbol ID {symbol.Id} has null depth with a next-hop ID");
            }

            return;
        }

        if (depth < 0)
        {
            throw IntegrityFailure($"symbol ID {symbol.Id} has a negative async involvement depth");
        }

        if (isAsyncOrigin)
        {
            if (depth != 0)
            {
                throw IntegrityFailure($"async origin symbol ID {symbol.Id} has nonzero depth {depth}");
            }

            if (nextSymbolId is not null)
            {
                throw IntegrityFailure($"async origin symbol ID {symbol.Id} has a next-hop ID");
            }

            return;
        }

        if (depth == 0)
        {
            throw IntegrityFailure($"non-origin symbol ID {symbol.Id} has depth zero");
        }

        if (nextSymbolId is null)
        {
            throw IntegrityFailure($"non-origin symbol ID {symbol.Id} has no next-hop ID");
        }
    }

    private static bool IsSourceBackedExecutable(StoredSymbol symbol) =>
        (symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda) &&
        symbol.PreferredDeclarationId is not null &&
        symbol.PreferredDocumentPath is not null;

    private static bool IsAsyncOrigin(StoredSymbol symbol) =>
        (symbol.AsyncRole & AsyncOriginRoles) != 0;
}
