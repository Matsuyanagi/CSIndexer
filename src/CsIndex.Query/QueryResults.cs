using CsIndex.Storage;

namespace CsIndex.Query;

public enum DispatchSearchMode
{
    Static = 1,
    Virtual = 2,
    All = 3,
}

public sealed record QueryContext(
    StoredProfile Profile,
    IReadOnlyList<StoredSymbol> MatchedSymbols,
    bool ShowSource = false);

public sealed record DefinitionResult(QueryContext Context, IReadOnlyList<StoredSymbol> Definitions);

public sealed record CallResult(
    QueryContext Context,
    IReadOnlyList<StoredCall> Calls,
    IReadOnlyList<StoredSymbol> EffectiveCallers,
    IReadOnlyList<StoredRelation> PossibleRuntimeTargets);

public sealed record RelationResult(QueryContext Context, IReadOnlyList<StoredRelation> Relations);

public sealed record ConditionsResult(StoredProfile Profile, IReadOnlyList<ConditionalSummary> Symbols);

public sealed record SourcePoint(string Path, int Line, int Column, int Offset);

public sealed record AsyncPathResult(
    StoredProfile Profile,
    StoredSymbol Root,
    IReadOnlyList<StoredSymbol> Nodes,
    bool Found,
    bool Truncated);

public sealed record CallerTreeNode(StoredSymbol Symbol, int Depth);

public sealed record CallerTreeEdge(long CallerSymbolId, long CalleeSymbolId);

public sealed record CallerTreeResult(
    StoredProfile Profile,
    StoredSymbol Root,
    IReadOnlyList<CallerTreeNode> Nodes,
    IReadOnlyList<CallerTreeEdge> Edges,
    bool Truncated);
