namespace CsIndex.Core.Model;

public sealed record AnalysisProfileData
{
    public required string Name { get; init; }
    public required InputMode InputMode { get; init; }
    public string? Configuration { get; init; }
    public string? TargetFramework { get; init; }
    public string? RuntimeIdentifier { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required IReadOnlyList<string> PreprocessorSymbols { get; init; }
    public required byte[] ProfileHash { get; init; }
}

public sealed record ProjectData
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? AssemblyName { get; init; }
    public string? ProjectPath { get; init; }
    public string? TargetFramework { get; init; }
    public required byte[] Fingerprint { get; init; }
}

public sealed record DocumentData
{
    public required string Key { get; init; }
    public required string ProjectKey { get; init; }
    public required string NormalizedPath { get; init; }
    public required byte[] ContentHash { get; init; }
    public byte[]? SemanticHash { get; init; }
    public required bool IsGenerated { get; init; }
    public required GenerationKind GenerationKind { get; init; }
}

public sealed record MethodParameterData
{
    public required int Ordinal { get; init; }
    public string? Name { get; init; }
    public required string TypeKey { get; init; }
    public string TypeDisplay { get; init; } = string.Empty;
    public required int RefKind { get; init; }
    public required bool IsOptional { get; init; }
}

public sealed record SymbolPathData(
    string NamespacePath,
    string TypeDisplayPath,
    string TypeIdentityPath,
    string ExecutableDisplayPath,
    string ExecutableIdentityPath,
    string SegmentDisplay,
    string SegmentIdentity,
    CallablePathSegmentKind SegmentKind);

public sealed record SymbolDeclarationData
{
    public required string Key { get; init; }
    public required string SymbolKey { get; init; }
    public required string DocumentKey { get; init; }
    public required DeclarationRole Role { get; init; }
    public required int SourceStart { get; init; }
    public required int SourceLength { get; init; }
    public required string NormalizedSource { get; init; }
    public required byte[] NormalizedSourceHash { get; init; }
    public required bool IsGenerated { get; init; }
}

public sealed record SymbolData
{
    public required string StableKey { get; init; }
    public string? ProjectKey { get; init; }
    public required IndexedSymbolKind Kind { get; init; }
    public required string Name { get; init; }
    public required string NamespaceName { get; init; }
    public string? TypeSimpleName { get; init; }
    public string? TypeMetadataName { get; init; }
    public required string FullyQualifiedName { get; init; }
    public required string DisplayName { get; init; }
    public SymbolPathData? Path { get; init; }
    public string? PreferredDeclarationKey { get; init; }
    public string? ContainingSymbolKey { get; init; }
    public int Arity { get; init; }
    public int? ParameterCount { get; init; }
    public int? TypeKind { get; init; }
    public int? MethodKind { get; init; }
    public int? Accessibility { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public AsyncRole AsyncRole { get; init; }
    public int? AsyncInvolvementDepth { get; init; }
    public string? AsyncNextSymbolKey { get; init; }
    public string? SourceDocumentKey { get; init; }
    public int? SourceStart { get; init; }
    public int? SourceLength { get; init; }
    public string? ReturnTypeKey { get; init; }
    public string? ReturnTypeDisplay { get; init; }
    public string? ConversionTypeKey { get; init; }
    public string? ConversionTypeDisplay { get; init; }
    public string? NormalizedSource { get; init; }
    public byte[]? NormalizedSourceHash { get; init; }
    public bool IsGenerated { get; init; }
    public IReadOnlyList<MethodParameterData> Parameters { get; init; } = [];
}

public sealed record CallData
{
    public required string CallerSymbolKey { get; init; }
    public string? CalleeSymbolKey { get; init; }
    public string? CalleeDefinitionKey { get; init; }
    public required ReferenceKind ReferenceKind { get; init; }
    public required DispatchKind DispatchKind { get; init; }
    public required ResolutionStatus ResolutionStatus { get; init; }
    public required ResolutionReason ResolutionReason { get; init; }
    public AsyncUsageKind AsyncUsageKind { get; init; }
    public required string DocumentKey { get; init; }
    public required int SourceStart { get; init; }
    public required int SourceLength { get; init; }
    public string? UnresolvedName { get; init; }
    public string? ReceiverTypeKey { get; init; }
    public IReadOnlyList<string> CandidateSymbolKeys { get; init; } = [];
}

public sealed record SymbolRelationData
{
    public required string SourceSymbolKey { get; init; }
    public required string TargetSymbolKey { get; init; }
    public required SymbolRelationKind RelationKind { get; init; }
}

public sealed record InterfaceMethodBindingData
{
    public required string ImplementingTypeKey { get; init; }
    public required string InterfaceMethodKey { get; init; }
    public required string ImplementationMethodKey { get; init; }
}

public sealed record ConditionalSymbolData
{
    public required string DocumentKey { get; init; }
    public required string SymbolName { get; init; }
    public required int OccurrenceCount { get; init; }
}

public sealed record CompilationSummary
{
    public required string ProjectName { get; init; }
    public required int Errors { get; init; }
    public required int Warnings { get; init; }
}

public sealed class IndexSnapshot
{
    public required AnalysisProfileData Profile { get; init; }
    public required string InputRoot { get; set; }
    public string IndexRootAnchor { get; set; } = ".";
    public required byte[] InputFingerprint { get; init; }
    public required byte[] RequestHash { get; init; }
    public List<ProjectData> Projects { get; } = [];
    public List<DocumentData> Documents { get; } = [];
    public Dictionary<string, SymbolData> Symbols { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SymbolDeclarationData> Declarations { get; } = new(StringComparer.Ordinal);
    public List<CallData> Calls { get; } = [];
    public List<SymbolRelationData> Relations { get; } = [];
    public List<InterfaceMethodBindingData> InterfaceMethodBindings { get; } = [];
    public List<ConditionalSymbolData> ConditionalSymbols { get; } = [];
    public List<CompilationSummary> CompilationSummaries { get; } = [];
    public List<string> MetadataReferences { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public int DocumentsExcluded { get; set; }
}

public sealed record AnalysisResult(IndexSnapshot Snapshot, TimeSpan Elapsed);
