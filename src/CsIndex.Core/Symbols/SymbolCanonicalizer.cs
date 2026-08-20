using CsIndex.Core.Model;
using CsIndex.Core.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsIndex.Core.Symbols;

public sealed class SymbolCanonicalizer
{
    private readonly AnalysisProfileData _profile;
    private readonly Func<SyntaxTree, string>? _sourceTreePathLookup;

    public SymbolCanonicalizer(AnalysisProfileData profile)
        : this(profile, null)
    {
    }

    internal SymbolCanonicalizer(
        AnalysisProfileData profile,
        Func<SyntaxTree, string>? sourceTreePathLookup)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _sourceTreePathLookup = sourceTreePathLookup;
    }

    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly IReadOnlyDictionary<string, string> OperatorTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op_UnaryPlus"] = "+",
            ["op_UnaryNegation"] = "-",
            ["op_LogicalNot"] = "!",
            ["op_OnesComplement"] = "~",
            ["op_Increment"] = "++",
            ["op_Decrement"] = "--",
            ["op_True"] = "true",
            ["op_False"] = "false",
            ["op_Addition"] = "+",
            ["op_Subtraction"] = "-",
            ["op_Multiply"] = "*",
            ["op_Division"] = "/",
            ["op_Modulus"] = "%",
            ["op_BitwiseAnd"] = "&",
            ["op_BitwiseOr"] = "|",
            ["op_ExclusiveOr"] = "^",
            ["op_LeftShift"] = "<<",
            ["op_RightShift"] = ">>",
            ["op_UnsignedRightShift"] = ">>>",
            ["op_Equality"] = "==",
            ["op_Inequality"] = "!=",
            ["op_LessThan"] = "<",
            ["op_GreaterThan"] = ">",
            ["op_LessThanOrEqual"] = "<=",
            ["op_GreaterThanOrEqual"] = ">=",
            ["op_AdditionAssignment"] = "+=",
            ["op_SubtractionAssignment"] = "-=",
            ["op_MultiplyAssignment"] = "*=",
            ["op_DivisionAssignment"] = "/=",
            ["op_ModulusAssignment"] = "%=",
            ["op_BitwiseAndAssignment"] = "&=",
            ["op_BitwiseOrAssignment"] = "|=",
            ["op_ExclusiveOrAssignment"] = "^=",
            ["op_LeftShiftAssignment"] = "<<=",
            ["op_RightShiftAssignment"] = ">>=",
            ["op_UnsignedRightShiftAssignment"] = ">>>=",
        };

    public IMethodSymbol NormalizeLogicalMethod(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var unreduced = method.ReducedFrom ?? method;
        var definition = unreduced.PartialDefinitionPart ?? unreduced;
        return definition.OriginalDefinition;
    }

    public string GetDefinitionStableKey(ISymbol symbol, string? projectKey = null)
    {
        var normalized = symbol is IMethodSymbol method ? NormalizeLogicalMethod(method) : symbol.OriginalDefinition;
        var documentationId = normalized.GetDocumentationCommentId();
        var identity = documentationId ?? BuildFallbackIdentity(normalized);
        var assembly = normalized.ContainingAssembly?.Identity.Name ?? "source";
        var projectScope = projectKey is null ? string.Empty : $"|project:{projectKey}";
        return $"profile:{_profile.Name}|assembly:{assembly}{projectScope}|tfm:{_profile.TargetFramework ?? "unknown"}|{identity}";
    }

    public string GetDeclarationKey(
        string logicalSymbolKey,
        string storedDocumentPath,
        int sourceStart,
        int sourceLength,
        DeclarationRole role) =>
        $"{logicalSymbolKey}|declaration:{storedDocumentPath}:{sourceStart}:{sourceLength}:{(int)role}";

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
            StableKey = GetDefinitionStableKey(type, projectKey),
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Type,
            Name = type.Name,
            NamespaceName = GetNamespace(type),
            TypeSimpleName = type.Name,
            TypeMetadataName = type.MetadataName,
            FullyQualifiedName = display,
            DisplayName = display,
            ContainingSymbolKey = type.ContainingType is null
                ? null
                : GetDefinitionStableKey(type.ContainingType, projectKey),
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
        string? projectKey = null,
        string? documentKey = null,
        int? sourceStart = null,
        int? sourceLength = null,
        bool isGenerated = false,
        string? containingSymbolKey = null,
        SymbolPathData? containingPath = null)
    {
        var stableKey = GetDefinitionStableKey(NormalizeLogicalMethod(method), projectKey);
        var containingType = method.ContainingType;
        var methodSignature = SymbolSignatureCanonicalizer.CanonicalizeMethod(method);
        var segment = CreateMethodSegment(method, methodSignature);
        var path = CreatePath(containingType, segment, containingPath);
        var displayName = FormatDisplayName(path);
        return new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Method,
            Name = method.Name,
            NamespaceName = path.NamespacePath,
            TypeSimpleName = containingType?.Name,
            TypeMetadataName = containingType?.MetadataName,
            FullyQualifiedName = displayName,
            DisplayName = displayName,
            Path = path,
            ContainingSymbolKey = containingSymbolKey ??
                                  (containingType is null
                                      ? null
                                      : GetDefinitionStableKey(containingType, projectKey)),
            Arity = method.Arity,
            ParameterCount = method.Parameters.Length,
            MethodKind = (int)method.MethodKind,
            ReturnTypeKey = methodSignature.ReturnType?.IdentityKey,
            ReturnTypeDisplay = methodSignature.ReturnType?.DisplayText,
            ConversionTypeKey = methodSignature.ConversionTargetType?.IdentityKey,
            ConversionTypeDisplay = methodSignature.ConversionTargetType?.DisplayText,
            Accessibility = method.MethodKind is MethodKind.LocalFunction or MethodKind.StaticConstructor
                ? (int)Microsoft.CodeAnalysis.Accessibility.NotApplicable
                : (int)method.DeclaredAccessibility,
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            SourceDocumentKey = documentKey,
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            IsGenerated = isGenerated,
            Parameters = CreateParameters(method, methodSignature),
        };
    }

    public SymbolData CreateAnonymousFunction(
        IMethodSymbol method,
        string stableKey,
        string containingSymbolKey,
        SymbolPathData containingPath,
        string marker,
        CallablePathSegmentKind segmentKind,
        string? projectKey = null,
        string? documentKey = null,
        int? sourceStart = null,
        int? sourceLength = null,
        bool isGenerated = false)
    {
        ArgumentNullException.ThrowIfNull(containingPath);
        if (segmentKind is not CallablePathSegmentKind.Lambda and
            not CallablePathSegmentKind.AnonymousMethod)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentKind));
        }

        var methodSignature = SymbolSignatureCanonicalizer.CanonicalizeMethod(method);
        var path = CreatePath(
            method.ContainingType,
            new CallableSegment(marker, marker, segmentKind),
            containingPath);
        var displayName = FormatDisplayName(path);
        return new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Lambda,
            Name = marker,
            NamespaceName = path.NamespacePath,
            TypeSimpleName = method.ContainingType?.Name,
            TypeMetadataName = method.ContainingType?.MetadataName,
            FullyQualifiedName = displayName,
            DisplayName = displayName,
            Path = path,
            ContainingSymbolKey = containingSymbolKey,
            Arity = method.Arity,
            ParameterCount = method.Parameters.Length,
            MethodKind = (int)method.MethodKind,
            ReturnTypeKey = methodSignature.ReturnType?.IdentityKey,
            ReturnTypeDisplay = methodSignature.ReturnType?.DisplayText,
            Accessibility = (int)Microsoft.CodeAnalysis.Accessibility.NotApplicable,
            IsStatic = method.IsStatic,
            SourceDocumentKey = documentKey,
            SourceStart = sourceStart,
            SourceLength = sourceLength,
            IsGenerated = isGenerated,
            Parameters = CreateParameters(method, methodSignature),
        };
    }

    public SymbolPathData CreateSyntheticPath(
        INamedTypeSymbol containingType,
        string segmentDisplay,
        string segmentIdentity,
        CallablePathSegmentKind segmentKind,
        SymbolPathData? containingPath = null) =>
        CreatePath(
            containingType,
            new CallableSegment(segmentDisplay, segmentIdentity, segmentKind),
            containingPath);

    public static SymbolPathData CreateTopLevelPath() =>
        new(
            string.Empty,
            "Program",
            "Program",
            "<top-level-statements>",
            "<top-level-statements>",
            "<top-level-statements>",
            "<top-level-statements>",
            CallablePathSegmentKind.TopLevelStatements);

    public static string FormatDisplayName(SymbolPathData path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var typeOwner = string.IsNullOrEmpty(path.NamespacePath)
            ? path.TypeDisplayPath
            : $"{path.NamespacePath}.{path.TypeDisplayPath}";
        return $"{typeOwner}::{path.ExecutableDisplayPath}";
    }

    public static string FormatType(ITypeSymbol? type) =>
        type is null ? string.Empty : type.ToDisplayString(TypeFormat);

    public static string FormatMethod(IMethodSymbol method)
    {
        var typeName = FormatType(method.ContainingType);
        var parameters = string.Join(",", method.Parameters.Select(parameter => FormatType(parameter.Type)));
        return $"{typeName}::{FormatMethodName(method)}({parameters})";
    }

    private static IReadOnlyList<MethodParameterData> CreateParameters(
        IMethodSymbol method,
        CanonicalMethodSignature methodSignature) =>
        method.Parameters.Select((parameter, ordinal) => new MethodParameterData
        {
            Ordinal = ordinal,
            Name = parameter.Name,
            TypeKey = methodSignature.Parameters[ordinal].Type.IdentityKey,
            TypeDisplay = methodSignature.Parameters[ordinal].Type.DisplayText,
            RefKind = (int)parameter.RefKind,
            IsOptional = parameter.IsOptional,
        }).ToArray();

    private static CallableSegment CreateMethodSegment(
        IMethodSymbol method,
        CanonicalMethodSignature signature)
    {
        var parametersDisplay = FormatParameterList(signature.Parameters, useDisplay: true);
        var parametersIdentity = FormatParameterList(signature.Parameters, useDisplay: false);
        var genericDisplay = FormatGenericDisplay(method.TypeParameters);
        var genericIdentity = method.Arity == 0 ? string.Empty : $"`{method.Arity}";

        string displayBase;
        string identityBase;
        var kind = CallablePathSegmentKind.Named;
        switch (method.MethodKind)
        {
            case MethodKind.Constructor:
                (displayBase, identityBase, kind) = ("[constructor]", "[constructor]", CallablePathSegmentKind.Special);
                break;
            case MethodKind.StaticConstructor:
                (displayBase, identityBase, kind) = ("[static-constructor]", "[static-constructor]", CallablePathSegmentKind.Special);
                break;
            case MethodKind.Destructor:
                (displayBase, identityBase, kind) = ("[destructor]", "[destructor]", CallablePathSegmentKind.Special);
                break;
            case MethodKind.UserDefinedOperator:
                {
                    var token = GetOperatorToken(method);
                    var prefix = IsCheckedOperator(method) ? "checked-operator" : "operator";
                    (displayBase, identityBase, kind) =
                        ($"[{prefix}:{token}]", $"[{prefix}:{token}]", CallablePathSegmentKind.Special);
                    break;
                }
            case MethodKind.Conversion:
                {
                    var target = signature.ConversionTargetType ??
                                 throw new InvalidOperationException("A conversion must have a target type.");
                    var checkedPrefix = IsCheckedOperator(method) ? "checked-conversion" : "conversion";
                    var conversionKind = method.MetadataName.Contains("Implicit", StringComparison.Ordinal)
                        ? "implicit"
                        : "explicit";
                    displayBase = $"[{checkedPrefix}:{conversionKind}:{target.DisplayText}]";
                    identityBase = $"[{checkedPrefix}:{conversionKind}:{target.IdentityKey}]";
                    kind = CallablePathSegmentKind.Special;
                    break;
                }
            case MethodKind.PropertyGet:
            case MethodKind.PropertySet:
                {
                    var property = (IPropertySymbol?)method.AssociatedSymbol ??
                                   throw new InvalidOperationException("A property accessor must have an associated property.");
                    var accessorKind = method.MethodKind == MethodKind.PropertyGet
                        ? "get"
                        : method.IsInitOnly
                            ? "init"
                            : "set";
                    var (memberDisplay, memberIdentity) = GetPropertyNames(property);
                    displayBase = $"[{accessorKind}:{memberDisplay}]";
                    identityBase = $"[{accessorKind}:{memberIdentity}]";
                    kind = CallablePathSegmentKind.Special;
                    break;
                }
            case MethodKind.EventAdd:
            case MethodKind.EventRemove:
                {
                    var eventSymbol = (IEventSymbol?)method.AssociatedSymbol ??
                                      throw new InvalidOperationException("An event accessor must have an associated event.");
                    var accessorKind = method.MethodKind == MethodKind.EventAdd ? "add" : "remove";
                    var (memberDisplay, memberIdentity) = GetEventNames(eventSymbol);
                    displayBase = $"[{accessorKind}:{memberDisplay}]";
                    identityBase = $"[{accessorKind}:{memberIdentity}]";
                    kind = CallablePathSegmentKind.Special;
                    break;
                }
            default:
                if (method.ExplicitInterfaceImplementations.FirstOrDefault() is { } interfaceMethod)
                {
                    var containingInterface = SymbolSignatureCanonicalizer.CanonicalizeType(interfaceMethod.ContainingType);
                    displayBase = $"[explicit:{containingInterface.DisplayText}.{EscapeIdentifier(interfaceMethod.Name)}]";
                    identityBase = $"[explicit:{containingInterface.IdentityKey}.{interfaceMethod.Name}]";
                    kind = CallablePathSegmentKind.Special;
                }
                else
                {
                    displayBase = EscapeIdentifier(method.Name);
                    identityBase = method.Name;
                }

                break;
        }

        return new CallableSegment(
            $"{displayBase}{genericDisplay}{parametersDisplay}",
            $"{identityBase}{genericIdentity}{parametersIdentity}",
            kind);
    }

    private static (string Display, string Identity) GetPropertyNames(IPropertySymbol property)
    {
        if (property.ExplicitInterfaceImplementations.FirstOrDefault() is { } interfaceProperty)
        {
            var interfaceType = SymbolSignatureCanonicalizer.CanonicalizeType(interfaceProperty.ContainingType);
            var memberName = interfaceProperty.MetadataName;
            return (
                $"{interfaceType.DisplayText}.{EscapeIdentifier(memberName)}",
                $"{interfaceType.IdentityKey}.{memberName}");
        }

        return (EscapeIdentifier(property.MetadataName), property.MetadataName);
    }

    private static (string Display, string Identity) GetEventNames(IEventSymbol eventSymbol)
    {
        if (eventSymbol.ExplicitInterfaceImplementations.FirstOrDefault() is { } interfaceEvent)
        {
            var interfaceType = SymbolSignatureCanonicalizer.CanonicalizeType(interfaceEvent.ContainingType);
            return (
                $"{interfaceType.DisplayText}.{EscapeIdentifier(interfaceEvent.Name)}",
                $"{interfaceType.IdentityKey}.{interfaceEvent.Name}");
        }

        return (EscapeIdentifier(eventSymbol.Name), eventSymbol.Name);
    }

    private static SymbolPathData CreatePath(
        INamedTypeSymbol? containingType,
        CallableSegment segment,
        SymbolPathData? containingPath)
    {
        if (containingPath is not null)
        {
            return new SymbolPathData(
                containingPath.NamespacePath,
                containingPath.TypeDisplayPath,
                containingPath.TypeIdentityPath,
                $"{containingPath.ExecutableDisplayPath}.{segment.Display}",
                $"{containingPath.ExecutableIdentityPath}.{segment.Identity}",
                segment.Display,
                segment.Identity,
                segment.Kind);
        }

        if (containingType is null)
        {
            throw new InvalidOperationException("A root callable must have a containing type.");
        }

        return new SymbolPathData(
            GetNamespace(containingType),
            GetTypeDisplayPath(containingType),
            GetTypeIdentityPath(containingType),
            segment.Display,
            segment.Identity,
            segment.Display,
            segment.Identity,
            segment.Kind);
    }

    private static string GetTypeDisplayPath(INamedTypeSymbol type) =>
        string.Join('.', EnumerateContainingTypes(type).Select(current =>
        {
            var genericParameters = current.Arity == 0
                ? string.Empty
                : $"<{string.Join(',', current.TypeParameters.TakeLast(current.Arity).Select(parameter => EscapeIdentifier(parameter.Name)))}>";
            return $"{EscapeIdentifier(current.Name)}{genericParameters}";
        }));

    private static string GetTypeIdentityPath(INamedTypeSymbol type) =>
        string.Join('.', EnumerateContainingTypes(type).Select(current =>
            current.Arity == 0 ? current.Name : $"{current.Name}`{current.Arity}"));

    private static IReadOnlyList<INamedTypeSymbol> EnumerateContainingTypes(INamedTypeSymbol type)
    {
        var stack = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            stack.Push(current);
        }

        return stack.ToArray();
    }

    private static string FormatParameterList(
        IReadOnlyList<CanonicalParameterSignature> parameters,
        bool useDisplay) =>
        $"({string.Join(',', parameters.Select(parameter =>
            $"{GetRefPrefix((RefKind)parameter.RefKind)}{(useDisplay ? parameter.Type.DisplayText : parameter.Type.IdentityKey)}"))})";

    private static string GetRefPrefix(RefKind refKind) =>
        refKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => string.Empty,
        };

    private static string FormatGenericDisplay(IEnumerable<ITypeParameterSymbol> typeParameters)
    {
        var names = typeParameters.Select(parameter => EscapeIdentifier(parameter.Name)).ToArray();
        return names.Length == 0 ? string.Empty : $"<{string.Join(',', names)}>";
    }

    private static bool IsCheckedOperator(IMethodSymbol method) =>
        method.MetadataName.StartsWith("op_Checked", StringComparison.Ordinal);

    private static string GetOperatorToken(IMethodSymbol method)
    {
        var metadataName = IsCheckedOperator(method)
            ? "op_" + method.MetadataName["op_Checked".Length..]
            : method.MetadataName;
        return OperatorTokens.TryGetValue(metadataName, out var token)
            ? token
            : throw new InvalidOperationException($"Unsupported user-defined operator metadata name: {method.MetadataName}");
    }

    private static string FormatMethodName(IMethodSymbol method)
    {
        var name = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            ? method.MetadataName
            : method.Name;
        return method.IsGenericMethod
            ? $"{name}<{string.Join(",", method.TypeArguments.Select(FormatType))}>"
            : name;
    }

    private static string GetNamespace(ISymbol symbol)
    {
        var namespaces = new Stack<string>();
        for (var current = symbol.ContainingNamespace;
             current is { IsGlobalNamespace: false };
             current = current.ContainingNamespace)
        {
            namespaces.Push(EscapeIdentifier(current.Name));
        }

        return string.Join('.', namespaces);
    }

    internal static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? $"@{identifier}"
            : identifier;

    private string BuildFallbackIdentity(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location is null)
        {
            return $"fallback:{symbol.Kind}:{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}:metadata";
        }

        if (location.SourceTree is not { } sourceTree || _sourceTreePathLookup is null)
        {
            throw new InputResolutionException(
                "An in-source symbol fallback was not present in the validated source-tree path map.");
        }

        var storedPath = _sourceTreePathLookup(sourceTree);
        var source = $"{storedPath}:{location.SourceSpan.Start}:{location.SourceSpan.Length}";
        return $"fallback:{symbol.Kind}:{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}:{source}";
    }

    private sealed record CallableSegment(
        string Display,
        string Identity,
        CallablePathSegmentKind Kind);
}
