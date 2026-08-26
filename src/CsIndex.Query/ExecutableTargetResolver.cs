using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query;

public readonly record struct FunctionTargetFilter(
    IndexedSymbolKind? Kind,
    AsyncStatusFilter AsyncStatus)
{
    public bool Matches(StoredSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (Kind is not null && symbol.Kind != Kind)
        {
            return false;
        }

        if (AsyncStatus is not (AsyncStatusFilter.Async or AsyncStatusFilter.Sync))
        {
            return true;
        }

        return AsyncStatus == AsyncStatusFilter.Async
            ? symbol.AsyncRole != AsyncRole.None
            : symbol.AsyncRole == AsyncRole.None;
    }
}

internal sealed class ExecutableTargetResolver(
    SymbolPathResolver symbolPathResolver)
{
    public async Task<IReadOnlyList<ResolvedLogicalRoot>> ResolveAsync(
        long profileId,
        SymbolSelectionRequest request,
        bool sourceOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var roots = await symbolPathResolver.ResolveLogicalRootsAsync(
            profileId,
            request,
            sourceOnly,
            cancellationToken);
        return roots;
    }
}
