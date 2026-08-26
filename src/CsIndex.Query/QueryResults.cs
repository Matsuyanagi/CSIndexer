using CsIndex.Storage;
using CsIndex.Query.Symbols;

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

public sealed record RootSelection(
    StoredProfile Profile,
    IReadOnlyList<ResolvedLogicalRoot> Roots);

public sealed record DeclarationResultRow(
    StoredSymbol Symbol,
    StoredDeclaration Declaration);

public sealed record LogicalSymbolResultRow(
    StoredSymbol Symbol,
    StoredDeclaration? PreferredDeclaration);

public sealed record SourceSearchResult(
    StoredProfile Profile,
    IReadOnlyList<DeclarationResultRow> Matches);

public sealed record DefinitionResult(
    RootSelection Selection,
    IReadOnlyList<DeclarationResultRow> Definitions);

public sealed record CallResult(
    RootSelection Selection,
    IReadOnlyList<StoredCall> Calls,
    IReadOnlyList<StoredSymbol> EffectiveCallers,
    IReadOnlyList<StoredRelation> PossibleRuntimeTargets,
    IReadOnlyDictionary<long, StoredSymbol> SymbolsById);

public sealed record RelationResult(
    RootSelection Selection,
    IReadOnlyList<StoredRelation> Relations,
    IReadOnlyDictionary<long, StoredSymbol> SymbolsById);

public sealed record ConditionsResult(StoredProfile Profile, IReadOnlyList<ConditionalSummary> Symbols);

public sealed record SourcePoint(string Path, int Line, int Column, int Offset);

public sealed record AsyncPathResult(
    RootSelection Selection,
    StoredSymbol Root,
    IReadOnlyList<StoredSymbol> Nodes,
    bool Found,
    bool Truncated);

public sealed record CallerTreeNode(StoredSymbol Symbol, int Depth);

public sealed record CallerTreeEdge(long CallerSymbolId, long CalleeSymbolId);

public sealed record CallerTreeResult(
    RootSelection Selection,
    StoredSymbol Root,
    IReadOnlyList<CallerTreeNode> Nodes,
    IReadOnlyList<CallerTreeEdge> Edges,
    bool Truncated);
