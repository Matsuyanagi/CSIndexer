using CsIndex.Core.Model;

namespace CsIndex.Storage;

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
    string InputRoot);

public sealed record StoredParameter(int Ordinal, string? Name, string TypeKey, int RefKind, bool IsOptional);

public sealed record StoredSymbol(
    long Id,
    string StableKey,
    IndexedSymbolKind Kind,
    string Name,
    string NamespaceName,
    string? TypeSimpleName,
    string? TypeMetadataName,
    string FullyQualifiedName,
    string DisplayName,
    long? ContainingSymbolId,
    int Arity,
    int? ParameterCount,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual,
    bool IsOverride,
    AsyncRole AsyncRole,
    int? AsyncInvolvementDepth,
    string? DocumentPath,
    int? SourceStart,
    int? SourceLength,
    bool IsGenerated,
    string? AssemblyName,
    IReadOnlyList<StoredParameter> Parameters,
    int? TypeKind,
    int? Accessibility);

public sealed record StoredInterfaceMethodBinding(
    long ImplementingTypeId,
    long InterfaceMethodId,
    long ImplementationMethodId);

public sealed record StoredCall(
    long Id,
    long CallerSymbolId,
    string CallerDisplayName,
    long? CallerContainingSymbolId,
    long? CalleeSymbolId,
    string? CalleeDisplayName,
    long? CalleeDefinitionId,
    string? CalleeDefinitionDisplayName,
    ReferenceKind ReferenceKind,
    DispatchKind DispatchKind,
    ResolutionStatus ResolutionStatus,
    ResolutionReason ResolutionReason,
    AsyncUsageKind AsyncUsageKind,
    long DocumentId,
    string DocumentPath,
    int SourceStart,
    int SourceLength,
    bool IsGenerated,
    string? UnresolvedName,
    string? ReceiverTypeKey);

public sealed record StoredRelation(
    long SourceSymbolId,
    string SourceDisplayName,
    long TargetSymbolId,
    string TargetDisplayName,
    SymbolRelationKind Kind);

public sealed record StoredDocument(long Id, string Path, bool IsGenerated);

public sealed record ConditionalSummary(string SymbolName, int FileCount, int OccurrenceCount, bool IsDefined);
