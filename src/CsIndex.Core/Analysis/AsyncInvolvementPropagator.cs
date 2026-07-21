using CsIndex.Core.Model;

namespace CsIndex.Core.Analysis;

public static class AsyncInvolvementPropagator
{
    private const AsyncRole OriginRoles =
        AsyncRole.DeclaredAsync |
        AsyncRole.ReturnsAwaitable |
        AsyncRole.ContainsAwait |
        AsyncRole.AsyncIterator |
        AsyncRole.ReturnsAsyncEnumerable |
        AsyncRole.AsyncVoid |
        AsyncRole.UniTaskVoid |
        AsyncRole.UsesAwaitForEach |
        AsyncRole.UsesAwaitUsing;

    public static void Apply(IndexSnapshot snapshot)
    {
        var reverseCalls = snapshot.Calls
            .Where(call => call.ReferenceKind == ReferenceKind.Invocation &&
                           call.ResolutionStatus == ResolutionStatus.Resolved &&
                           call.CalleeDefinitionKey is not null)
            .GroupBy(call => call.CalleeDefinitionKey!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(call => call.CallerSymbolKey).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var distance = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var symbol in snapshot.Symbols.Values.Where(symbol => (symbol.AsyncRole & OriginRoles) != 0))
        {
            distance[symbol.StableKey] = 0;
            queue.Enqueue(symbol.StableKey);
        }

        while (queue.TryDequeue(out var callee))
        {
            if (!reverseCalls.TryGetValue(callee, out var callers))
            {
                continue;
            }

            foreach (var caller in callers)
            {
                var candidate = distance[callee] + 1;
                if (distance.TryGetValue(caller, out var current) && current <= candidate)
                {
                    continue;
                }

                distance[caller] = candidate;
                queue.Enqueue(caller);
            }
        }

        foreach (var key in snapshot.Symbols.Keys.ToArray())
        {
            snapshot.Symbols[key] = snapshot.Symbols[key] with
            {
                AsyncInvolvementDepth = distance.TryGetValue(key, out var value) ? value : null,
            };
        }
    }
}
