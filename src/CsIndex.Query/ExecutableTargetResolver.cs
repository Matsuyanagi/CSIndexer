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
    SymbolPathResolver symbolPathResolver,
    MethodTargetResolver methodTargetResolver)
{
    public async Task<IReadOnlyList<StoredSymbol>> ResolveAsync(
        long profileId,
        SymbolSelectionRequest request,
        bool sourceOnly,
        bool includeOverrides,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (includeOverrides &&
            request.KindSpecified &&
            request.FunctionFilter.Kind == IndexedSymbolKind.Lambda)
        {
            throw new SymbolQueryParseException("--kind lambda cannot be combined with --include-overrides.");
        }

        var roots = await symbolPathResolver.ResolveLogicalRootsAsync(
            profileId,
            request,
            sourceOnly,
            cancellationToken);
        if (!includeOverrides)
        {
            return roots.Select(root => root.Symbol).ToArray();
        }

        if (roots.Any(root => root.Symbol.Kind != IndexedSymbolKind.Method))
        {
            throw new SymbolQueryParseException("--include-overrides requires an exact method query.");
        }

        return await methodTargetResolver.ExpandAsync(
            profileId,
            roots,
            sourceOnly,
            cancellationToken);
    }
}
