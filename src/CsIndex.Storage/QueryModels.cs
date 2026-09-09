using CsIndex.Core.Model;

namespace CsIndex.Storage;

internal enum TraversalOperation
{
    SymbolEndpointsById = 1,
    CallsByCallee = 2,
    CallsByCaller = 3,
    CallsByCallerIncludingLambdaDescendants = 4,
    RelationsByTarget = 5,
    RelationsBySource = 6,
    OverrideInterfaceExpansion = 7,
    CallAtPosition = 8,
}

public enum GeneratedFilter
{
    Include = 0,
    Exclude = 1,
    Only = 2,
}

public sealed record StoredProfile(
    long Id,
    string Name,
    InputMode InputMode,
    string? Configuration,
    string? TargetFramework,
    string? RuntimeIdentifier,
    IReadOnlyList<string> PreprocessorSymbols,
    string InputRoot,
    string IndexRootAnchor = ".");

public sealed record StoredParameter(
    int Ordinal,
    string? Name,
    string TypeKey,
    int RefKind,
    bool IsOptional,
    string TypeDisplay = "");

public sealed record StoredDeclaration(
    long Id,
    string DeclarationKey,
    long SymbolId,
    long DocumentId,
    string DocumentPath,
    DeclarationRole Role,
    int SourceStart,
    int SourceLength,
    int NormalizedStart,
    int NormalizedLength,
    string? NormalizedSource,
    bool IsGenerated);

public sealed record LogicalSymbolCandidateHints(
    string? ExactLeafName,
    IndexedSymbolKind? ExactKind);

public sealed record StoredSymbol
{
    public StoredSymbol(
        long Id,
        string StableKey,
        IndexedSymbolKind Kind,
        string Name,
        string NamespaceName,
        string? TypeSimpleName,
        string? TypeMetadataName,
        long? ContainingSymbolId,
        int Arity,
        int? ParameterCount,
        int? MethodKind,
        bool IsStatic,
        bool IsAbstract,
        bool IsVirtual,
        bool IsOverride,
        AsyncRole AsyncRole,
        int? AsyncInvolvementDepth,
        long? AsyncNextSymbolId,
        string? ReturnTypeKey,
        string? DocumentPath,
        int? SourceStart,
        int? SourceLength,
        bool IsGenerated,
        string? AssemblyName,
        IReadOnlyList<StoredParameter> Parameters,
        int? TypeKind,
        int? Accessibility)
    {
        this.Id = Id;
        this.StableKey = StableKey;
        this.Kind = Kind;
        this.Name = Name;
        this.NamespaceName = NamespaceName;
        this.TypeSimpleName = TypeSimpleName;
        this.TypeMetadataName = TypeMetadataName;
        this.ContainingSymbolId = ContainingSymbolId;
        this.Arity = Arity;
        this.ParameterCount = ParameterCount;
        this.MethodKind = MethodKind;
        this.IsStatic = IsStatic;
        this.IsAbstract = IsAbstract;
        this.IsVirtual = IsVirtual;
        this.IsOverride = IsOverride;
        this.AsyncRole = AsyncRole;
        this.AsyncInvolvementDepth = AsyncInvolvementDepth;
        this.AsyncNextSymbolId = AsyncNextSymbolId;
        this.ReturnTypeKey = ReturnTypeKey;
        this.DocumentPath = DocumentPath;
        this.SourceStart = SourceStart;
        this.IsGenerated = IsGenerated;
        this.AssemblyName = AssemblyName;
        this.Parameters = Parameters;
        this.TypeKind = TypeKind;
        this.Accessibility = Accessibility;
    }

    public long Id { get; init; }
    public string StableKey { get; init; } = string.Empty;
    public IndexedSymbolKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string NamespaceName { get; init; } = string.Empty;
    public string? TypeSimpleName { get; init; }
    public string? TypeMetadataName { get; init; }
    public long? ContainingSymbolId { get; init; }
    public int Arity { get; init; }
    public int? ParameterCount { get; init; }
    public int? MethodKind { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public AsyncRole AsyncRole { get; init; }
    public int? AsyncInvolvementDepth { get; init; }
    public long? AsyncNextSymbolId { get; init; }
    public string? ReturnTypeKey { get; init; }
    public string? ReturnTypeDisplay { get; init; }
    public string? ConversionTypeKey { get; init; }
    public string? ConversionTypeDisplay { get; init; }
    public SymbolPathData? Path { get; init; }
    public long? PreferredDeclarationId { get; init; }
    public string? PreferredDocumentPath { get; init; }
    public int? PreferredSourceStart { get; init; }
    public bool? PreferredIsGenerated { get; init; }
    public StoredDeclaration? PreferredDeclaration { get; init; }
    public string? DocumentPath { get; init; }
    public int? SourceStart { get; init; }
    public int? SourceLength => PreferredDeclaration?.SourceLength;
    public bool IsGenerated { get; init; }
    public string? AssemblyName { get; init; }
    public IReadOnlyList<StoredParameter> Parameters { get; init; } = [];
    public int? TypeKind { get; init; }
    public int? Accessibility { get; init; }
    public string? NormalizedSource => PreferredDeclaration?.NormalizedSource;
}

public sealed record StoredInterfaceMethodBinding(
    long ImplementingTypeId,
    long InterfaceMethodId,
    long ImplementationMethodId);

public sealed record StoredInheritedMethodCandidate(long ReceiverTypeId, long MethodId, int Depth);

public sealed record MethodSearchSeed(long MethodId, long ReceiverTypeId);

public sealed record InterfaceSearchSeed(long InterfaceMethodId, long InterfaceScopeTypeId);

public sealed record StoredCall(
    long Id,
    long CallerSymbolId,
    long? CallerContainingSymbolId,
    long? CalleeSymbolId,
    long? CalleeDefinitionId,
    ReferenceKind ReferenceKind,
    DispatchKind DispatchKind,
    ResolutionStatus ResolutionStatus,
    ResolutionReason ResolutionReason,
    AsyncUsageKind AsyncUsageKind,
    long DocumentId,
    string DocumentPath,
    int SourceStart,
    int SourceLength,
    int NormalizedStart,
    int NormalizedLength,
    string? NormalizedSource,
    bool IsGenerated,
    string? UnresolvedName,
    string? ReceiverTypeKey);

public sealed record StoredRelation(
    long SourceSymbolId,
    long TargetSymbolId,
    SymbolRelationKind Kind);

public sealed record StoredDocument(long Id, string Path, bool IsGenerated);

public sealed record ConditionalSummary(string SymbolName, int FileCount, int OccurrenceCount, bool IsDefined);
