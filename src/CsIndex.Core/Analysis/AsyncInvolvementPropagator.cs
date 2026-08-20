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

    public static void Apply(IndexSnapshot snapshot) =>
        Apply(snapshot, CancellationToken.None);

    public static void Apply(IndexSnapshot snapshot, CancellationToken cancellationToken) =>
        Apply(snapshot, cancellationToken, afterOrderingComparison: null);

    internal static void Apply(
        IndexSnapshot snapshot,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var eligibleSymbols = GetSourceBackedExecutableKeys(snapshot, cancellationToken);
        var reverseCalls = BuildReverseCalls(
            snapshot,
            eligibleSymbols,
            cancellationToken,
            afterOrderingComparison);

        var distance = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = new Dictionary<string, string?>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var origin in OrderOrigins(
                     snapshot,
                     eligibleSymbols,
                     cancellationToken,
                     afterOrderingComparison))
        {
            cancellationToken.ThrowIfCancellationRequested();
            distance[origin] = 0;
            next[origin] = null;
            queue.Enqueue(origin);
        }

        while (queue.TryDequeue(out var callee))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reverseCalls.TryGetValue(callee, out var callers))
            {
                continue;
            }

            foreach (var caller in callers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = distance[callee] + 1;
                if (distance.TryGetValue(caller, out var current) && current <= candidate)
                {
                    continue;
                }

                distance[caller] = candidate;
                next[caller] = callee;
                queue.Enqueue(caller);
            }
        }

        var symbolKeys = new List<string>(snapshot.Symbols.Count);
        foreach (var key in snapshot.Symbols.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbolKeys.Add(key);
        }

        foreach (var key in symbolKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.Symbols[key] = snapshot.Symbols[key] with
            {
                AsyncInvolvementDepth = distance.TryGetValue(key, out var value) ? value : null,
                AsyncNextSymbolKey = next.TryGetValue(key, out var nextKey) ? nextKey : null,
            };
        }
    }

    private static Dictionary<string, List<string>> BuildReverseCalls(
        IndexSnapshot snapshot,
        IReadOnlySet<string> eligibleSymbols,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison)
    {
        var calls = new List<(CallData Call, int Sequence)>();
        var sequence = 0;
        foreach (var call in snapshot.Calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (call.ReferenceKind == ReferenceKind.Invocation &&
                call.ResolutionStatus == ResolutionStatus.Resolved &&
                call.CalleeDefinitionKey is { } calleeDefinitionKey &&
                eligibleSymbols.Contains(call.CallerSymbolKey) &&
                eligibleSymbols.Contains(calleeDefinitionKey))
            {
                calls.Add((call, sequence));
            }

            sequence++;
        }

        SortWithCancellation(
            calls,
            static (left, right) => CompareCalls(left.Call, right.Call, left.Sequence, right.Sequence),
            cancellationToken,
            afterOrderingComparison);

        var reverseCalls = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seenCallers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (call, _) in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callee = call.CalleeDefinitionKey!;
            if (!reverseCalls.TryGetValue(callee, out var callers))
            {
                callers = [];
                reverseCalls.Add(callee, callers);
                seenCallers.Add(callee, new HashSet<string>(StringComparer.Ordinal));
            }

            if (seenCallers[callee].Add(call.CallerSymbolKey))
            {
                callers.Add(call.CallerSymbolKey);
            }
        }

        return reverseCalls;
    }

    private static IReadOnlyList<string> OrderOrigins(
        IndexSnapshot snapshot,
        IReadOnlySet<string> eligibleSymbols,
        CancellationToken cancellationToken,
        Action? afterOrderingComparison)
    {
        var origins = new List<(string StableKey, int Sequence)>();
        var sequence = 0;
        foreach (var symbol in snapshot.Symbols.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (eligibleSymbols.Contains(symbol.StableKey) &&
                (symbol.AsyncRole & OriginRoles) != 0)
            {
                origins.Add((symbol.StableKey, sequence));
            }

            sequence++;
        }

        SortWithCancellation(
            origins,
            static (left, right) =>
            {
                var result = StringComparer.Ordinal.Compare(left.StableKey, right.StableKey);
                return result != 0 ? result : left.Sequence.CompareTo(right.Sequence);
            },
            cancellationToken,
            afterOrderingComparison);

        var ordered = new List<string>(origins.Count);
        foreach (var (stableKey, _) in origins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordered.Add(stableKey);
        }

        return ordered;
    }

    private static IReadOnlySet<string> GetSourceBackedExecutableKeys(
        IndexSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in snapshot.Symbols.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (symbol.Kind is IndexedSymbolKind.Method or IndexedSymbolKind.Lambda &&
                HasSourceDeclaration(snapshot, symbol))
            {
                eligible.Add(symbol.StableKey);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return eligible;
    }

    private static bool HasSourceDeclaration(IndexSnapshot snapshot, SymbolData symbol)
    {
        if (symbol.PreferredDeclarationKey is { } declarationKey &&
            snapshot.Declarations.TryGetValue(declarationKey, out var declaration))
        {
            return declaration.SymbolKey == symbol.StableKey;
        }

        // Preserve compatibility for snapshots constructed by callers before
        // declaration rows were introduced.
        return symbol.SourceDocumentKey is not null && symbol.NormalizedSource is not null;
    }

    private static int CompareCalls(CallData left, CallData right, int leftSequence, int rightSequence)
    {
        var result = StringComparer.Ordinal.Compare(left.CalleeDefinitionKey, right.CalleeDefinitionKey);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.DocumentKey, right.DocumentKey);
        if (result != 0)
        {
            return result;
        }

        result = left.SourceStart.CompareTo(right.SourceStart);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.CallerSymbolKey, right.CallerSymbolKey);
        if (result != 0)
        {
            return result;
        }

        result = left.SourceLength.CompareTo(right.SourceLength);
        return result != 0 ? result : leftSequence.CompareTo(rightSequence);
    }

    private static void SortWithCancellation<T>(
        List<T> values,
        Comparison<T> comparison,
        CancellationToken cancellationToken,
        Action? afterComparison)
    {
        try
        {
            values.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = comparison(left, right);
                afterComparison?.Invoke();
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
}
