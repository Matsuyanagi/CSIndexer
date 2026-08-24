using CsIndex.Query;

namespace CsIndex.Query.Symbols;

public sealed record SymbolSelectionRequest(
    string? Selector,
    IReadOnlyList<TypedCondition> Conditions,
    SymbolCaseOptions Case,
    FunctionTargetFilter FunctionFilter,
    bool KindSpecified,
    bool AsyncStatusSpecified);
