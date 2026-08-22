using CsIndex.Core.Symbols;

namespace CsIndex.Query.Symbols;

public enum PatternMode
{
    Glob = 1,
    Literal = 2,
}

public enum GenericListState
{
    Omitted = 0,
    Present = 1,
}

public enum ParameterListState
{
    Omitted = 0,
    Present = 1,
}

public sealed record SymbolPathSelector(
    SymbolPathStyle Style,
    HierarchySelector? Namespace,
    HierarchySelector Type,
    IReadOnlyList<ExecutableSegmentSelector> ExecutableSegments);

public sealed record HierarchySelector(
    IReadOnlyList<HierarchySegmentSelector> Segments);

public sealed record HierarchySegmentSelector(
    string IdentifierPattern,
    PatternMode PatternMode,
    IReadOnlyList<string> GenericPlaceholders)
{
    public int GenericArity => GenericPlaceholders.Count;
}

public sealed record CallableAritySelector(
    GenericListState GenericState,
    IReadOnlyList<string> GenericPlaceholders,
    ParameterListState ParameterState,
    IReadOnlyList<CanonicalTypeSelector> Parameters,
    IReadOnlyList<int> ParameterRefKinds);

public abstract record ExecutableSegmentSelector(PatternMode PatternMode);

public sealed record NamedExecutableSegmentSelector(
    string IdentifierPattern,
    CallableAritySelector Arity,
    PatternMode PatternMode)
    : ExecutableSegmentSelector(PatternMode);

public sealed record QualifiedMemberSelector(
    string? ContainingTypePattern,
    CanonicalTypeSelector? ContainingType,
    string MemberPattern,
    PatternMode PatternMode);

public sealed record SpecialExecutableSegmentSelector(
    string Tag,
    string? OperatorToken,
    string? ConversionKind,
    CanonicalTypeSelector? ConversionTarget,
    QualifiedMemberSelector? Member,
    CallableAritySelector Arity,
    PatternMode PatternMode)
    : ExecutableSegmentSelector(PatternMode);

public sealed record LambdaExecutableSegmentSelector(int? Ordinal)
    : ExecutableSegmentSelector(PatternMode.Literal);

public sealed record AnonymousMethodExecutableSegmentSelector(int? Ordinal)
    : ExecutableSegmentSelector(PatternMode.Literal);

public sealed record InitializerExecutableSegmentSelector(
    string MemberPattern,
    PatternMode PatternMode)
    : ExecutableSegmentSelector(PatternMode);

public sealed record TopLevelStatementsExecutableSegmentSelector()
    : ExecutableSegmentSelector(PatternMode.Literal);
