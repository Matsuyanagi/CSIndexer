using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Symbols;

public sealed class SymbolCanonicalizer(AnalysisProfileData profile)
{
    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public IMethodSymbol NormalizeMethod(IMethodSymbol method) =>
        (method.ReducedFrom ?? method).OriginalDefinition;

    public string GetDefinitionStableKey(ISymbol symbol)
    {
        var normalized = symbol is IMethodSymbol method ? NormalizeMethod(method) : symbol.OriginalDefinition;
        var documentationId = normalized.GetDocumentationCommentId();
        var identity = documentationId ?? BuildFallbackIdentity(normalized);
        var assembly = normalized.ContainingAssembly?.Identity.Name ?? "source";
        return $"profile:{profile.Name}|assembly:{assembly}|tfm:{profile.TargetFramework ?? "unknown"}|{identity}";
    }

    public string GetTargetStableKey(IMethodSymbol method)
    {
        var definition = NormalizeMethod(method);
        var definitionKey = GetDefinitionStableKey(definition);
        if (SymbolEqualityComparer.Default.Equals(method, definition))
        {
            return definitionKey;
        }

        var reduced = method.ReducedFrom is null ? "constructed" : "reduced";
        return $"{definitionKey}|{reduced}:{FormatMethod(method)}";
    }

    public string GetSyntheticStableKey(
        string ownerKey,
        string documentPath,
        string syntaxKind,
        int start,
        int length,
        byte[] contentHash) =>
        $"{ownerKey}|document:{documentPath}|kind:{syntaxKind}|span:{start}:{length}|version:{Convert.ToHexString(contentHash)}";

    public SymbolData CreateType(
        INamedTypeSymbol type,
        string? projectKey = null,
        string? documentKey = null,
        int? sourceStart = null,
        int? sourceLength = null,
        bool isGenerated = false)
    {
        var display = FormatType(type);
        return new SymbolData
        {
            StableKey = GetDefinitionStableKey(type),
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Type,
            Name = type.Name,
            NamespaceName = GetNamespace(type),
            TypeSimpleName = type.Name,
            TypeMetadataName = type.MetadataName,
            FullyQualifiedName = display,
            DisplayName = display,
            ContainingSymbolKey = type.ContainingType is null ? null : GetDefinitionStableKey(type.ContainingType),
            Arity = type.Arity,
            TypeKind = (int)type.TypeKind,
            Accessibility = (int)type.DeclaredAccessibility,
            IsStatic = type.IsStatic,
            IsAbstract = type.IsAbstract,
            SourceDocumentKey = documentKey,
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            IsGenerated = isGenerated,
        };
    }

    public SymbolData CreateMethod(
        IMethodSymbol method,
        bool actualTarget,
        string? projectKey = null,
        string? documentKey = null,
        int? sourceStart = null,
        int? sourceLength = null,
        bool isGenerated = false,
        string? containingSymbolKey = null)
    {
        var stableKey = actualTarget ? GetTargetStableKey(method) : GetDefinitionStableKey(method);
        var containingType = method.ContainingType;
        return new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Method,
            Name = method.Name,
            NamespaceName = GetNamespace(method),
            TypeSimpleName = containingType?.Name,
            TypeMetadataName = containingType?.MetadataName,
            FullyQualifiedName = $"{FormatType(containingType)}.{FormatMethodName(method)}",
            DisplayName = FormatMethod(method),
            ContainingSymbolKey = containingSymbolKey ??
                                  (containingType is null ? null : GetDefinitionStableKey(containingType)),
            Arity = method.Arity,
            ParameterCount = method.Parameters.Length,
            MethodKind = (int)method.MethodKind,
            ReturnTypeKey = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                ? null
                : FormatType(method.ReturnType),
            Accessibility = (int)method.DeclaredAccessibility,
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            SourceDocumentKey = documentKey,
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            IsGenerated = isGenerated,
            Parameters = method.Parameters.Select(parameter => new MethodParameterData
            {
                Ordinal = parameter.Ordinal,
                Name = parameter.Name,
                TypeKey = FormatType(parameter.Type),
                RefKind = (int)parameter.RefKind,
                IsOptional = parameter.IsOptional,
            }).ToArray(),
        };
    }

    public static string FormatType(ITypeSymbol? type) =>
        type is null ? string.Empty : type.ToDisplayString(TypeFormat);

    public static string FormatMethod(IMethodSymbol method)
    {
        var typeName = FormatType(method.ContainingType);
        var parameters = string.Join(",", method.Parameters.Select(parameter => FormatType(parameter.Type)));
        return $"{typeName}::{FormatMethodName(method)}({parameters})";
    }

    private static string FormatMethodName(IMethodSymbol method)
    {
        var name = method.MethodKind is Microsoft.CodeAnalysis.MethodKind.Constructor or
            Microsoft.CodeAnalysis.MethodKind.StaticConstructor
            ? method.MetadataName
            : method.Name;
        return method.IsGenericMethod
            ? $"{name}<{string.Join(",", method.TypeArguments.Select(FormatType))}>"
            : name;
    }

    private static string GetNamespace(ISymbol symbol) =>
        symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
            ? containingNamespace.ToDisplayString()
            : string.Empty;

    private static string BuildFallbackIdentity(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        var source = location is null
            ? "metadata"
            : $"{location.SourceTree?.FilePath}:{location.SourceSpan.Start}:{location.SourceSpan.Length}";
        return $"fallback:{symbol.Kind}:{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}:{source}";
    }
}
