using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection.Metadata;

namespace CsIndex.Core.Symbols;

/// <summary>
/// Produces the semantic and concrete forms used by callable signatures.
/// </summary>
public static class SymbolSignatureCanonicalizer
{
    private const string ValueTypeIdentityPrefix = "valuetype:";
    private const string ReferenceTypeIdentityPrefix = "reftype:";

    private static readonly SymbolDisplayFormat TypeDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly IReadOnlyDictionary<string, string> PredefinedTypeNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "System.Boolean",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["int"] = "System.Int32",
            ["uint"] = "System.UInt32",
            ["long"] = "System.Int64",
            ["ulong"] = "System.UInt64",
            ["nint"] = "System.IntPtr",
            ["nuint"] = "System.UIntPtr",
            ["char"] = "System.Char",
            ["float"] = "System.Single",
            ["double"] = "System.Double",
            ["decimal"] = "System.Decimal",
            ["string"] = "System.String",
            ["object"] = "System.Object",
            ["void"] = "System.Void",
        };

    private static readonly IReadOnlySet<string> ValueTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Boolean",
        "System.Byte",
        "System.SByte",
        "System.Int16",
        "System.UInt16",
        "System.Int32",
        "System.UInt32",
        "System.Int64",
        "System.UInt64",
        "System.IntPtr",
        "System.UIntPtr",
        "System.Char",
        "System.Single",
        "System.Double",
        "System.Decimal",
        "System.Nullable",
        "System.Void",
    };

    public static CanonicalTypeSignature CanonicalizeType(ITypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var node = CreateNode(type);
        return new CanonicalTypeSignature(node.IdentityKey, FormatTypeDisplay(type));
    }

    public static string FormatTypeDisplay(CanonicalTypeSignature type, bool shortNames)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!shortNames)
        {
            return type.DisplayText;
        }

        var syntax = SyntaxFactory.ParseTypeName(type.DisplayText);
        if (syntax.ContainsDiagnostics)
        {
            if (!string.Equals(type.DisplayText, "void", StringComparison.Ordinal))
            {
                throw CreateDisplayMismatch(type);
            }

            syntax = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        }

        var identity = ParseIdentityNode(type.IdentityKey);
        return RewriteShortDisplay(identity, syntax, type).NormalizeWhitespace().ToFullString();
    }

    public static CanonicalParameterSignature CanonicalizeParameter(IParameterSymbol parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new CanonicalParameterSignature(CanonicalizeType(parameter.Type), (int)parameter.RefKind);
    }

    public static CanonicalMethodSignature CanonicalizeMethod(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var isConstructor = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor;
        var returnType = isConstructor ? null : CanonicalizeType(method.ReturnType);
        var conversionTargetType = method.MethodKind == MethodKind.Conversion
            ? CanonicalizeType(method.ReturnType)
            : null;
        return new CanonicalMethodSignature(
            method.Arity,
            method.TypeParameters.Select(parameter => parameter.Name).ToArray(),
            method.Parameters.Select(CanonicalizeParameter).ToArray(),
            returnType,
            conversionTargetType);
    }

    public static CanonicalTypeSelector ParseSelectorType(
        string syntaxText,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syntaxText);
        ArgumentNullException.ThrowIfNull(genericPlaceholders);
        ValidateGenericPlaceholders(genericPlaceholders);

        var syntax = SyntaxFactory.ParseTypeName(syntaxText);
        if (syntax.ContainsDiagnostics || syntax.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new ArgumentException($"Invalid C# type syntax: {syntaxText}", nameof(syntaxText));
        }

        _ = ParseSelectorNode(syntax, genericPlaceholders);
        var snapshot = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal);
        foreach (var (name, placeholder) in genericPlaceholders)
        {
            snapshot.Add(name, placeholder);
        }

        return new CanonicalTypeSelector(
            syntaxText,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, CanonicalGenericPlaceholder>(snapshot));
    }

    public static bool IsMatch(CanonicalTypeSelector selector, CanonicalTypeSignature candidate)
        => IsMatch(
            selector,
            candidate,
            StringComparison.Ordinal,
            StringComparison.Ordinal);

    public static bool IsMatch(
        CanonicalTypeSelector selector,
        CanonicalTypeSignature candidate,
        StringComparison namespaceComparison,
        StringComparison typeComparison)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateIdentifierComparison(namespaceComparison, nameof(namespaceComparison));
        ValidateIdentifierComparison(typeComparison, nameof(typeComparison));

        var selectorNode = ParseSelectorNode(
            SyntaxFactory.ParseTypeName(selector.SyntaxText),
            selector.GenericPlaceholders);
        var candidateNode = ParseIdentityNode(candidate.IdentityKey);
        return Matches(selectorNode, candidateNode, namespaceComparison, typeComparison);
    }

    private static void ValidateIdentifierComparison(StringComparison comparison, string parameterName)
    {
        if (comparison is not StringComparison.Ordinal and not StringComparison.OrdinalIgnoreCase)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                comparison,
                "Only Ordinal and OrdinalIgnoreCase comparisons are supported.");
        }
    }

    private static void ValidateGenericPlaceholders(
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        var bindings = new HashSet<CanonicalGenericPlaceholder>();
        foreach (var (name, placeholder) in genericPlaceholders)
        {
            if (string.IsNullOrWhiteSpace(name) || placeholder is null || placeholder.Ordinal < 0)
            {
                throw new ArgumentException("Generic placeholder names and ordinals must be non-empty and non-negative.", nameof(genericPlaceholders));
            }

            if (!Enum.IsDefined(placeholder.Scope))
            {
                throw new ArgumentException(
                    "Generic placeholder scopes must be Type or Method.",
                    nameof(genericPlaceholders));
            }

            if (!bindings.Add(placeholder))
            {
                throw new ArgumentException(
                    "Generic placeholder scope and ordinal bindings must be unique.",
                    nameof(genericPlaceholders));
            }
        }
    }

    private static string FormatTypeDisplay(ITypeSymbol type)
    {
        var syntax = SyntaxFactory.ParseTypeName(type.ToDisplayString(TypeDisplayFormat));
        var rewritten = (TypeSyntax)new TupleElementNameOmittingRewriter().Visit(syntax)!;
        return rewritten.NormalizeWhitespace().ToFullString();
    }

    private static TypeSyntax RewriteShortDisplay(
        TypeNode identity,
        TypeSyntax display,
        CanonicalTypeSignature pair) =>
        (identity, display) switch
        {
            (NamedTypeNode named, IdentifierNameSyntax identifier)
                when IsDynamicDisplay(named, identifier) => identifier,
            (NamedTypeNode named, IdentifierNameSyntax identifier)
                when IsPredefinedAliasDisplay(named, identifier) => identifier,
            (NamedTypeNode named, PredefinedTypeSyntax predefined) =>
                RewritePredefinedTypeDisplay(named, predefined, pair),
            (NamedTypeNode named, NameSyntax name) => RewriteNamedTypeDisplay(named, name, pair),
            (NamedTypeNode named, NullableTypeSyntax nullable) =>
                RewriteNullableTypeDisplay(named, nullable, pair),
            (PlaceholderTypeNode, IdentifierNameSyntax identifier) => identifier,
            (NullableTypeNode nullable, NullableTypeSyntax syntax) =>
                syntax.WithElementType(RewriteShortDisplay(nullable.Element, syntax.ElementType, pair)),
            (TypeNode node, NullableTypeSyntax syntax) => RewriteNullableTypeDisplay(node, syntax, pair),
            (ArrayTypeNode array, ArrayTypeSyntax syntax) => RewriteArrayTypeDisplay(array, syntax, pair),
            (PointerTypeNode pointer, PointerTypeSyntax syntax) =>
                syntax.WithElementType(RewriteShortDisplay(pointer.Element, syntax.ElementType, pair)),
            (TupleTypeNode tuple, TupleTypeSyntax syntax) => RewriteTupleTypeDisplay(tuple, syntax, pair),
            (FunctionPointerTypeNode functionPointer, FunctionPointerTypeSyntax syntax) =>
                RewriteFunctionPointerTypeDisplay(functionPointer, syntax, pair),
            _ => throw CreateDisplayMismatch(pair),
        };

    private static NameSyntax RewriteNamedTypeDisplay(
        NamedTypeNode identity,
        NameSyntax display,
        CanonicalTypeSignature pair)
    {
        var displayComponents = FlattenDisplayName(display);
        if (displayComponents.Count != identity.Segments.Count)
        {
            throw CreateDisplayMismatch(pair);
        }

        var rewrittenComponents = new List<SimpleNameSyntax>(displayComponents.Count);
        for (var index = 0; index < identity.Segments.Count; index++)
        {
            var identitySegment = identity.Segments[index];
            var displayComponent = displayComponents[index];
            if (!string.Equals(
                    NormalizeIdentityName(identitySegment.Name),
                    displayComponent.Identifier.ValueText,
                    StringComparison.Ordinal) ||
                identitySegment.Arguments.Count != GetDisplayTypeArgumentCount(displayComponent))
            {
                throw CreateDisplayMismatch(pair);
            }

            if (displayComponent is GenericNameSyntax generic)
            {
                var arguments = generic.TypeArgumentList.Arguments
                    .Zip(identitySegment.Arguments)
                    .Select(argument => RewriteShortDisplay(argument.Second, argument.First, pair))
                    .ToArray();
                displayComponent = generic.WithTypeArgumentList(
                    generic.TypeArgumentList.WithArguments(SyntaxFactory.SeparatedList(arguments)));
            }

            rewrittenComponents.Add(displayComponent);
        }

        var namespaceSegmentCount = identity.NamespaceSegmentCount ?? 0;
        if (namespaceSegmentCount < 0 || namespaceSegmentCount >= rewrittenComponents.Count)
        {
            throw CreateDisplayMismatch(pair);
        }

        NameSyntax rewritten = rewrittenComponents[namespaceSegmentCount];
        for (var index = namespaceSegmentCount + 1; index < rewrittenComponents.Count; index++)
        {
            rewritten = SyntaxFactory.QualifiedName(rewritten, rewrittenComponents[index]);
        }

        return rewritten;
    }

    private static TypeSyntax RewritePredefinedTypeDisplay(
        NamedTypeNode identity,
        PredefinedTypeSyntax display,
        CanonicalTypeSignature pair)
    {
        var keyword = display.Keyword.ValueText;
        if (!PredefinedTypeNames.TryGetValue(keyword, out var qualifiedName) ||
            !identity.HasQualifiedName(qualifiedName) ||
            identity.Segments.Any(segment => segment.Arguments.Count != 0))
        {
            throw CreateDisplayMismatch(pair);
        }

        return display;
    }

    private static TypeSyntax RewriteNullableTypeDisplay(
        TypeNode identity,
        NullableTypeSyntax display,
        CanonicalTypeSignature pair)
    {
        var element = identity switch
        {
            NamedTypeNode named when named.HasQualifiedName("System.Nullable") && named.Arguments.Count == 1 =>
                named.Arguments[0],
            NullableTypeNode nullable => nullable.Element,
            TypeNode node when node.Classification != TypeClassification.Value => node,
            _ => throw CreateDisplayMismatch(pair),
        };

        return display.WithElementType(RewriteShortDisplay(element, display.ElementType, pair));
    }

    private static TypeSyntax RewriteArrayTypeDisplay(
        ArrayTypeNode identity,
        ArrayTypeSyntax display,
        CanonicalTypeSignature pair)
    {
        var identityArrays = new List<ArrayTypeNode>();
        TypeNode element = identity;
        while (element is ArrayTypeNode array)
        {
            identityArrays.Add(array);
            element = array.Element;
        }

        if (display.RankSpecifiers.Count != identityArrays.Count ||
            display.RankSpecifiers
                .Reverse()
                .Zip(identityArrays)
                .Any(pairing => pairing.First.Sizes.Count != pairing.Second.Rank))
        {
            throw CreateDisplayMismatch(pair);
        }

        return display.WithElementType(RewriteShortDisplay(element, display.ElementType, pair));
    }

    private static TypeSyntax RewriteTupleTypeDisplay(
        TupleTypeNode identity,
        TupleTypeSyntax display,
        CanonicalTypeSignature pair)
    {
        if (identity.Elements.Count != display.Elements.Count)
        {
            throw CreateDisplayMismatch(pair);
        }

        var elements = identity.Elements
            .Zip(display.Elements)
            .Select(pairing => pairing.Second.WithType(
                RewriteShortDisplay(pairing.First, pairing.Second.Type, pair)))
            .ToArray();
        return display.WithElements(SyntaxFactory.SeparatedList(elements));
    }

    private static TypeSyntax RewriteFunctionPointerTypeDisplay(
        FunctionPointerTypeNode identity,
        FunctionPointerTypeSyntax display,
        CanonicalTypeSignature pair)
    {
        if (!string.Equals(
                identity.CallingConvention,
                GetSelectorCallingConvention(display),
                StringComparison.Ordinal) ||
            display.ParameterList.Parameters.Count != identity.Parameters.Count + 1)
        {
            throw CreateDisplayMismatch(pair);
        }

        var parameters = new List<FunctionPointerParameterSyntax>(display.ParameterList.Parameters.Count);
        for (var index = 0; index < identity.Parameters.Count; index++)
        {
            var identityParameter = identity.Parameters[index];
            var displayParameter = display.ParameterList.Parameters[index];
            if (identityParameter.RefKind != GetSelectorFunctionPointerRefKind(displayParameter, isReturn: false))
            {
                throw CreateDisplayMismatch(pair);
            }

            parameters.Add(displayParameter.WithType(
                RewriteShortDisplay(identityParameter.Type, displayParameter.Type, pair)));
        }

        var displayReturn = display.ParameterList.Parameters[^1];
        if (identity.ReturnRefKind != GetSelectorFunctionPointerRefKind(displayReturn, isReturn: true))
        {
            throw CreateDisplayMismatch(pair);
        }

        parameters.Add(displayReturn.WithType(
            RewriteShortDisplay(identity.ReturnType, displayReturn.Type, pair)));
        return display.WithParameterList(
            display.ParameterList.WithParameters(SyntaxFactory.SeparatedList(parameters)));
    }

    private static IReadOnlyList<SimpleNameSyntax> FlattenDisplayName(NameSyntax display) =>
        display switch
        {
            IdentifierNameSyntax identifier => [identifier],
            GenericNameSyntax generic => [generic],
            QualifiedNameSyntax qualified =>
            [
                .. FlattenDisplayName(qualified.Left),
                qualified.Right,
            ],
            AliasQualifiedNameSyntax aliasQualified => FlattenDisplayName(aliasQualified.Name),
            _ => throw new InvalidOperationException($"Unsupported display name syntax: {display}"),
        };

    private static int GetDisplayTypeArgumentCount(SimpleNameSyntax display) =>
        display is GenericNameSyntax generic ? generic.TypeArgumentList.Arguments.Count : 0;

    private static string NormalizeIdentityName(string name) =>
        name.StartsWith('@') ? name[1..] : name;

    private static bool IsDynamicDisplay(NamedTypeNode identity, IdentifierNameSyntax display) =>
        !IsEscapedIdentifier(display.Identifier) &&
        string.Equals(display.Identifier.ValueText, "dynamic", StringComparison.Ordinal) &&
        identity.HasQualifiedName("System.Object") &&
        identity.Arguments.Count == 0;

    private static bool IsPredefinedAliasDisplay(NamedTypeNode identity, IdentifierNameSyntax display) =>
        !IsEscapedIdentifier(display.Identifier) &&
        PredefinedTypeNames.TryGetValue(display.Identifier.ValueText, out var qualifiedName) &&
        identity.HasQualifiedName(qualifiedName) &&
        identity.Segments.All(segment => segment.Arguments.Count == 0);

    private static InvalidOperationException CreateDisplayMismatch(CanonicalTypeSignature pair) =>
        new($"Canonical type identity '{pair.IdentityKey}' does not match display '{pair.DisplayText}'.");

    private static TypeNode CreateNode(ITypeSymbol type)
    {
        if (type is IDynamicTypeSymbol)
        {
            return CreateSimpleNamedTypeNode("System.Object", TypeClassification.Reference);
        }

        if (type is ITypeParameterSymbol typeParameter)
        {
            var scope = typeParameter.TypeParameterKind == TypeParameterKind.Method
                ? CanonicalGenericPlaceholderScope.Method
                : CanonicalGenericPlaceholderScope.Type;
            var ordinal = typeParameter.Ordinal;
            if (scope == CanonicalGenericPlaceholderScope.Type)
            {
                for (var containingType = typeParameter.DeclaringType?.ContainingType;
                     containingType is not null;
                     containingType = containingType.ContainingType)
                {
                    ordinal += containingType.Arity;
                }
            }
            else
            {
                for (var containingMethod = typeParameter.DeclaringMethod?.ContainingSymbol as IMethodSymbol;
                     containingMethod is not null;
                     containingMethod = containingMethod.ContainingSymbol as IMethodSymbol)
                {
                    ordinal += containingMethod.Arity;
                }
            }

            var classification = typeParameter.HasValueTypeConstraint || typeParameter.HasUnmanagedTypeConstraint
                ? TypeClassification.Value
                : typeParameter.HasReferenceTypeConstraint
                    ? TypeClassification.Reference
                    : TypeClassification.Unknown;
            return new PlaceholderTypeNode(scope, ordinal, classification);
        }

        if (type is IArrayTypeSymbol array)
        {
            return new ArrayTypeNode(CreateNode(array.ElementType), array.Rank);
        }

        if (type is IPointerTypeSymbol pointer)
        {
            return new PointerTypeNode(CreateNode(pointer.PointedAtType));
        }

        if (type is IFunctionPointerTypeSymbol functionPointer)
        {
            return CreateFunctionPointerNode(functionPointer);
        }

        if (type is INamedTypeSymbol namedType)
        {
            if (TryCreateValueTupleNode(namedType, out var tupleNode))
            {
                return tupleNode;
            }

            return CreateNamedTypeNode(namedType);
        }

        var fallbackName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fallbackName.StartsWith("global::", StringComparison.Ordinal))
        {
            fallbackName = fallbackName["global::".Length..];
        }

        return ParseNamedTypeIdentity(fallbackName, GetTypeClassification(type));
    }

    private static bool TryCreateValueTupleNode(INamedTypeSymbol type, out TupleTypeNode tupleNode)
    {
        tupleNode = null!;
        var elements = new List<TypeNode>();
        if (!TryAppendValueTupleElements(type, allowSingleElementRest: false, elements))
        {
            return false;
        }

        tupleNode = new TupleTypeNode(elements);
        return true;
    }

    private static bool TryAppendValueTupleElements(
        INamedTypeSymbol type,
        bool allowSingleElementRest,
        List<TypeNode> elements)
    {
        if (!IsSystemValueTuple(type))
        {
            return false;
        }

        if (type.Arity == 1)
        {
            if (!allowSingleElementRest)
            {
                return false;
            }

            elements.Add(CreateNode(type.TypeArguments[0]));
            return true;
        }

        if (type.Arity is >= 2 and <= 7)
        {
            elements.AddRange(type.TypeArguments.Select(CreateNode));
            return true;
        }

        if (type.Arity != 8 || type.TypeArguments[7] is not INamedTypeSymbol rest)
        {
            return false;
        }

        var flattened = type.TypeArguments.Take(7).Select(CreateNode).ToList();
        if (!TryAppendValueTupleElements(rest, allowSingleElementRest: true, flattened))
        {
            return false;
        }

        elements.AddRange(flattened);
        return true;
    }

    private static bool IsSystemValueTuple(INamedTypeSymbol type) =>
        type.IsTupleType &&
        type.Name == "ValueTuple" &&
        type.Arity is >= 1 and <= 8 &&
        type.ContainingType is null &&
        type.ContainingNamespace.Name == "System" &&
        type.ContainingNamespace.ContainingNamespace.IsGlobalNamespace;

    private static FunctionPointerTypeNode CreateFunctionPointerNode(IFunctionPointerTypeSymbol functionPointer)
    {
        var signature = functionPointer.Signature;
        var parameters = signature.Parameters
            .Select(parameter => new FunctionPointerParameterNode(CreateNode(parameter.Type), (int)parameter.RefKind))
            .ToArray();
        var convention = GetCallingConvention(functionPointer);
        return new FunctionPointerTypeNode(
            convention,
            parameters,
            CreateNode(signature.ReturnType),
            (int)signature.RefKind);
    }

    private static string GetCallingConvention(IFunctionPointerTypeSymbol functionPointer)
    {
        var convention = functionPointer.Signature.CallingConvention;
        if (convention == SignatureCallingConvention.Default)
        {
            return string.Empty;
        }

        if (convention == SignatureCallingConvention.Unmanaged)
        {
            var custom = functionPointer.Signature.UnmanagedCallingConventionTypes;
            return custom.IsDefaultOrEmpty
                ? "unmanaged"
                : $"unmanaged[{string.Join(",", custom.Select(GetNamedTypeName))}]";
        }

        return convention.ToString().ToLowerInvariant();
    }

    private static IEnumerable<ITypeSymbol> GetOwnTypeArguments(INamedTypeSymbol namedType)
    {
        if (namedType.Arity == 0)
        {
            return [];
        }

        var typeArguments = namedType.TypeArguments;
        return typeArguments.Length <= namedType.Arity
            ? typeArguments
            : typeArguments.Skip(typeArguments.Length - namedType.Arity);
    }

    private static NamedTypeNode CreateNamedTypeNode(INamedTypeSymbol type)
    {
        var segments = new List<NamedTypeSegment>();
        var namespaces = new Stack<string>();
        for (var current = type.ContainingNamespace;
             current is { IsGlobalNamespace: false };
             current = current.ContainingNamespace)
        {
            namespaces.Push(current.Name);
        }

        segments.AddRange(namespaces.Select(name => new NamedTypeSegment(name, [])));
        var namespaceSegmentCount = segments.Count;

        var containingTypes = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            containingTypes.Push(current);
        }

        foreach (var current in containingTypes)
        {
            segments.Add(new NamedTypeSegment(
                current.Name,
                GetOwnTypeArguments(current).Select(CreateNode).ToArray()));
        }

        return new NamedTypeNode(segments, namespaceSegmentCount, GetTypeClassification(type));
    }

    private static NamedTypeNode CreateSimpleNamedTypeNode(
        string qualifiedName,
        TypeClassification classification)
    {
        var segments = qualifiedName
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new NamedTypeSegment(name, []))
            .ToArray();
        return new NamedTypeNode(segments, Math.Max(segments.Length - 1, 0), classification);
    }

    private static TypeClassification GetTypeClassification(ITypeSymbol type) =>
        type.IsValueType
            ? TypeClassification.Value
            : type.IsReferenceType
                ? TypeClassification.Reference
                : TypeClassification.Unknown;

    private static string GetNamedTypeName(INamedTypeSymbol type)
    {
        return string.Join('.', CreateNamedTypeNode(type).Segments.Select(segment => segment.Name));
    }

    private static TypeNode ParseSelectorNode(
        TypeSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        return syntax switch
        {
            PredefinedTypeSyntax predefined => ParsePredefinedType(predefined),
            IdentifierNameSyntax identifier => ParseIdentifier(identifier.Identifier, genericPlaceholders),
            GenericNameSyntax generic => ParseGenericName(generic, genericPlaceholders),
            QualifiedNameSyntax qualified => ParseQualifiedName(qualified, genericPlaceholders),
            AliasQualifiedNameSyntax aliasQualified => ParseAliasQualifiedName(aliasQualified, genericPlaceholders),
            NullableTypeSyntax nullable => new NullableTypeNode(ParseSelectorNode(nullable.ElementType, genericPlaceholders)),
            ArrayTypeSyntax array => ParseArrayType(array, genericPlaceholders),
            PointerTypeSyntax pointer => new PointerTypeNode(ParseSelectorNode(pointer.ElementType, genericPlaceholders)),
            TupleTypeSyntax tuple => new TupleTypeNode(tuple.Elements.Select(element => ParseSelectorNode(element.Type, genericPlaceholders)).ToArray()),
            FunctionPointerTypeSyntax functionPointer => ParseFunctionPointerType(functionPointer, genericPlaceholders),
            _ => throw new ArgumentException($"Unsupported C# type syntax: {syntax}", nameof(syntax)),
        };
    }

    private static TypeNode ParsePredefinedType(PredefinedTypeSyntax syntax)
    {
        var text = syntax.Keyword.ValueText;
        return PredefinedTypeNames.TryGetValue(text, out var identity)
            ? CreateSimpleNamedTypeNode(
                identity,
                ValueTypeNames.Contains(identity) ? TypeClassification.Value : TypeClassification.Reference)
            : throw new ArgumentException($"Unsupported C# predefined type: {text}", nameof(syntax));
    }

    private static TypeNode ParseIdentifier(
        SyntaxToken identifier,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        var name = identifier.ValueText;
        if (genericPlaceholders.TryGetValue(name, out var placeholder))
        {
            return new PlaceholderTypeNode(
                placeholder.Scope,
                placeholder.Ordinal,
                TypeClassification.Unknown);
        }

        if (!IsEscapedIdentifier(identifier) && PredefinedTypeNames.TryGetValue(name, out var predefined))
        {
            return CreateSimpleNamedTypeNode(
                predefined,
                ValueTypeNames.Contains(predefined) ? TypeClassification.Value : TypeClassification.Reference);
        }

        if (!IsEscapedIdentifier(identifier) && string.Equals(name, "dynamic", StringComparison.Ordinal))
        {
            return CreateSimpleNamedTypeNode("System.Object", TypeClassification.Reference);
        }

        throw new ArgumentException($"Named selector types must be fully qualified: {name}", nameof(name));
    }

    private static TypeNode ParseGenericName(
        GenericNameSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        var name = syntax.Identifier.ValueText;
        if (genericPlaceholders.ContainsKey(name) ||
            (!IsEscapedIdentifier(syntax.Identifier) && PredefinedTypeNames.ContainsKey(name)))
        {
            throw new ArgumentException($"Generic selector type is not a named type: {name}", nameof(syntax));
        }

        throw new ArgumentException($"Named selector types must be fully qualified: {name}", nameof(syntax));
    }

    private static TypeNode ParseQualifiedName(
        QualifiedNameSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders) =>
        ParseQualifiedNamedType(syntax, genericPlaceholders);

    private static TypeNode ParseAliasQualifiedName(
        AliasQualifiedNameSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        if (!string.Equals(syntax.Alias.Identifier.Text, "global", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported type alias: {syntax.Alias}", nameof(syntax));
        }

        return ParseQualifiedNamedType(syntax, genericPlaceholders);
    }

    private static TypeNode ParseQualifiedNamedType(
        NameSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        var segments = ParseNamedTypeSegments(syntax, genericPlaceholders);
        var qualifiedName = string.Join('.', segments.Select(segment => segment.Name));
        return new NamedTypeNode(
            segments,
            NamespaceSegmentCount: null,
            ValueTypeNames.Contains(qualifiedName) ? TypeClassification.Value : TypeClassification.Unknown);
    }

    private static bool TryCreateValueTupleNode(
        NamedTypeNode type,
        StringComparison namespaceComparison,
        StringComparison typeComparison,
        out TupleTypeNode tupleNode)
    {
        tupleNode = null!;
        var elements = new List<TypeNode>();
        if (!TryAppendValueTupleElements(
                type.Segments,
                allowSingleElementRest: false,
                namespaceComparison,
                typeComparison,
                elements))
        {
            return false;
        }

        tupleNode = new TupleTypeNode(elements);
        return true;
    }

    private static bool TryAppendValueTupleElements(
        IReadOnlyList<NamedTypeSegment> segments,
        bool allowSingleElementRest,
        StringComparison namespaceComparison,
        StringComparison typeComparison,
        List<TypeNode> elements)
    {
        if (segments.Count != 2 ||
            !string.Equals(segments[0].Name, "System", namespaceComparison) ||
            segments[0].Arguments.Count != 0 ||
            !string.Equals(segments[1].Name, "ValueTuple", typeComparison))
        {
            return false;
        }

        var arguments = segments[1].Arguments;
        if (arguments.Count == 1)
        {
            if (!allowSingleElementRest)
            {
                return false;
            }

            elements.Add(arguments[0]);
            return true;
        }

        if (arguments.Count is >= 2 and <= 7)
        {
            elements.AddRange(arguments);
            return true;
        }

        if (arguments.Count != 8)
        {
            return false;
        }

        var flattened = arguments.Take(7).ToList();
        var restMatched = arguments[7] switch
        {
            TupleTypeNode tupleRest => AddTupleElements(tupleRest, flattened),
            NamedTypeNode namedRest =>
                TryAppendValueTupleElements(
                    namedRest.Segments,
                    allowSingleElementRest: true,
                    namespaceComparison,
                    typeComparison,
                    flattened),
            _ => false,
        };
        if (!restMatched)
        {
            return false;
        }

        elements.AddRange(flattened);
        return true;
    }

    private static bool AddTupleElements(TupleTypeNode tuple, List<TypeNode> elements)
    {
        elements.AddRange(tuple.Elements);
        return true;
    }

    private static IReadOnlyList<NamedTypeSegment> ParseNamedTypeSegments(
        NameSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        switch (syntax)
        {
            case IdentifierNameSyntax identifier:
                if (genericPlaceholders.ContainsKey(identifier.Identifier.ValueText))
                {
                    throw new ArgumentException(
                        $"Generic placeholders must be standalone type arguments: {identifier}",
                        nameof(syntax));
                }

                return [new NamedTypeSegment(identifier.Identifier.ValueText, [])];
            case GenericNameSyntax generic:
                if (genericPlaceholders.ContainsKey(generic.Identifier.ValueText))
                {
                    throw new ArgumentException(
                        $"Generic placeholders must be standalone type arguments: {generic}",
                        nameof(syntax));
                }

                return
                [
                    new NamedTypeSegment(
                        generic.Identifier.ValueText,
                        generic.TypeArgumentList.Arguments
                            .Select(argument => ParseSelectorNode(argument, genericPlaceholders))
                            .ToArray()),
                ];
            case QualifiedNameSyntax qualified:
                return
                [
                    .. ParseNamedTypeSegments(qualified.Left, genericPlaceholders),
                    .. ParseNamedTypeSegments(qualified.Right, genericPlaceholders),
                ];
            case AliasQualifiedNameSyntax aliasQualified:
                if (aliasQualified.Alias.Identifier.Text != "global")
                {
                    throw new ArgumentException($"Unsupported type alias: {aliasQualified.Alias}", nameof(syntax));
                }

                return ParseNamedTypeSegments(aliasQualified.Name, genericPlaceholders);
            default:
                throw new ArgumentException($"Unsupported qualified selector type: {syntax}", nameof(syntax));
        }
    }

    private static bool IsEscapedIdentifier(SyntaxToken identifier) =>
        identifier.Text.StartsWith('@');

    private static TypeNode ParseArrayType(
        ArrayTypeSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        var node = ParseSelectorNode(syntax.ElementType, genericPlaceholders);
        foreach (var rankSpecifier in syntax.RankSpecifiers)
        {
            var rank = rankSpecifier.Sizes.Count;
            node = new ArrayTypeNode(node, rank);
        }

        return node;
    }

    private static FunctionPointerTypeNode ParseFunctionPointerType(
        FunctionPointerTypeSyntax syntax,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
    {
        var convention = GetSelectorCallingConvention(syntax);
        var parameters = syntax.ParameterList.Parameters
            .SkipLast(1)
            .Select(parameter =>
            {
                var refKind = GetSelectorFunctionPointerRefKind(parameter, isReturn: false);
                return new FunctionPointerParameterNode(ParseSelectorNode(parameter.Type, genericPlaceholders), refKind);
            })
            .ToArray();
        var returnParameter = syntax.ParameterList.Parameters.Last();
        return new FunctionPointerTypeNode(
            convention,
            parameters,
            ParseSelectorNode(returnParameter.Type, genericPlaceholders),
            GetSelectorFunctionPointerRefKind(returnParameter, isReturn: true));
    }

    private static string GetSelectorCallingConvention(FunctionPointerTypeSyntax syntax)
    {
        var callingConvention = syntax.CallingConvention;
        if (callingConvention is null ||
            callingConvention.ManagedOrUnmanagedKeyword.ValueText == "managed")
        {
            return string.Empty;
        }

        var conventionList = callingConvention.UnmanagedCallingConventionList;
        if (conventionList is null)
        {
            return callingConvention.ManagedOrUnmanagedKeyword.ValueText;
        }

        var shortNames = conventionList.CallingConventions
            .Select(convention => convention.Name.ValueText)
            .ToArray();
        if (shortNames.Length == 1 && shortNames[0] is "Cdecl" or "Stdcall" or "Thiscall" or "Fastcall")
        {
            return shortNames[0].ToLowerInvariant();
        }

        var names = shortNames.Select(name => $"System.Runtime.CompilerServices.CallConv{name}");
        return $"unmanaged[{string.Join(',', names)}]";
    }

    private static int GetSelectorFunctionPointerRefKind(
        FunctionPointerParameterSyntax parameter,
        bool isReturn)
    {
        if (parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword)))
        {
            return (int)(isReturn ? RefKind.RefReadOnly : RefKind.RefReadOnlyParameter);
        }

        if (parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OutKeyword)))
        {
            return (int)RefKind.Out;
        }

        if (parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.InKeyword)))
        {
            return (int)RefKind.In;
        }

        return parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword))
            ? (int)RefKind.Ref
            : (int)RefKind.None;
    }

    private static bool Matches(
        TypeNode selector,
        TypeNode candidate,
        StringComparison namespaceComparison,
        StringComparison typeComparison)
    {
        if (selector is NullableTypeNode nullableSelector)
        {
            if (candidate is NamedTypeNode nullableCandidate &&
                nullableCandidate.HasQualifiedName("System.Nullable") &&
                nullableCandidate.Arguments.Count == 1)
            {
                return Matches(
                    nullableSelector.Element,
                    nullableCandidate.Arguments[0],
                    namespaceComparison,
                    typeComparison);
            }

            return candidate.Classification != TypeClassification.Value &&
                   Matches(nullableSelector.Element, candidate, namespaceComparison, typeComparison);
        }

        if (candidate is NamedTypeNode namedCandidate &&
            namedCandidate.HasQualifiedName("System.Nullable") &&
            namedCandidate.Arguments.Count == 1)
        {
            return false;
        }

        return (selector, candidate) switch
        {
            (PlaceholderTypeNode left, PlaceholderTypeNode right) =>
                left.Scope == right.Scope && left.Ordinal == right.Ordinal,
            (NamedTypeNode left, TupleTypeNode right) =>
                TryCreateValueTupleNode(
                    left,
                    namespaceComparison,
                    typeComparison,
                    out var normalizedLeft) &&
                Matches(normalizedLeft, right, namespaceComparison, typeComparison),
            (NamedTypeNode left, NamedTypeNode right) =>
                NamedTypesMatch(left, right, namespaceComparison, typeComparison),
            (ArrayTypeNode left, ArrayTypeNode right) =>
                left.Rank == right.Rank &&
                Matches(left.Element, right.Element, namespaceComparison, typeComparison),
            (PointerTypeNode left, PointerTypeNode right) =>
                Matches(left.Element, right.Element, namespaceComparison, typeComparison),
            (TupleTypeNode left, TupleTypeNode right) =>
                left.Elements.Count == right.Elements.Count &&
                left.Elements.Zip(right.Elements).All(pair =>
                    Matches(pair.First, pair.Second, namespaceComparison, typeComparison)),
            (FunctionPointerTypeNode left, FunctionPointerTypeNode right) =>
                string.Equals(left.CallingConvention, right.CallingConvention, StringComparison.Ordinal) &&
                left.Parameters.Count == right.Parameters.Count &&
                left.Parameters.Zip(right.Parameters).All(pair =>
                    pair.First.RefKind == pair.Second.RefKind &&
                    Matches(pair.First.Type, pair.Second.Type, namespaceComparison, typeComparison)) &&
                left.ReturnRefKind == right.ReturnRefKind &&
                Matches(left.ReturnType, right.ReturnType, namespaceComparison, typeComparison),
            _ => false,
        };
    }

    private static bool NamedTypesMatch(
        NamedTypeNode left,
        NamedTypeNode right,
        StringComparison namespaceComparison,
        StringComparison typeComparison) =>
        left.Segments.Count == right.Segments.Count &&
        right.NamespaceSegmentCount is { } namespaceSegmentCount &&
        left.Segments.Zip(right.Segments).Select((pair, index) => (pair, index)).All(item =>
            string.Equals(
                item.pair.First.Name,
                item.pair.Second.Name,
                item.index < namespaceSegmentCount ? namespaceComparison : typeComparison) &&
            item.pair.First.Arguments.Count == item.pair.Second.Arguments.Count &&
            item.pair.First.Arguments.Zip(item.pair.Second.Arguments).All(arguments =>
                Matches(arguments.First, arguments.Second, namespaceComparison, typeComparison)));

    private static TypeNode ParseIdentityNode(string identity)
    {
        if (identity.StartsWith("delegate*", StringComparison.Ordinal))
        {
            return ParseFunctionPointerIdentityNode(identity);
        }

        if (identity.EndsWith('*'))
        {
            return new PointerTypeNode(ParseIdentityNode(identity[..^1]));
        }

        var arrayStart = identity.LastIndexOf('[');
        if (arrayStart > 0 && identity.EndsWith(']'))
        {
            var rank = identity[arrayStart..].Count(character => character == ',') + 1;
            return new ArrayTypeNode(ParseIdentityNode(identity[..arrayStart]), rank);
        }

        if (identity.StartsWith('(') && identity.EndsWith(')'))
        {
            return new TupleTypeNode(SplitTopLevel(identity[1..^1], ',').Select(ParseIdentityNode).ToArray());
        }

        if (identity.EndsWith('?') && identity.Length > 1)
        {
            return new NullableTypeNode(ParseIdentityNode(identity[..^1]));
        }

        if (identity.StartsWith('!') && int.TryParse(identity[1..], out var typeOrdinal))
        {
            return new PlaceholderTypeNode(
                CanonicalGenericPlaceholderScope.Type,
                typeOrdinal,
                TypeClassification.Unknown);
        }

        if (identity.StartsWith('^') && int.TryParse(identity[1..], out var methodOrdinal))
        {
            return new PlaceholderTypeNode(
                CanonicalGenericPlaceholderScope.Method,
                methodOrdinal,
                TypeClassification.Unknown);
        }

        if (HasClassificationPrefix(identity, ValueTypeIdentityPrefix))
        {
            return WithClassification(
                ParseIdentityNode(identity[ValueTypeIdentityPrefix.Length..]),
                TypeClassification.Value);
        }

        if (HasClassificationPrefix(identity, ReferenceTypeIdentityPrefix))
        {
            return WithClassification(
                ParseIdentityNode(identity[ReferenceTypeIdentityPrefix.Length..]),
                TypeClassification.Reference);
        }

        return ParseNamedTypeIdentity(identity);
    }

    private static bool HasClassificationPrefix(string identity, string prefix) =>
        identity.StartsWith(prefix, StringComparison.Ordinal) &&
        (!identity.StartsWith(prefix + ':', StringComparison.Ordinal) ||
         identity.StartsWith(prefix + "::", StringComparison.Ordinal));

    private static TypeNode WithClassification(TypeNode node, TypeClassification classification) =>
        node switch
        {
            NamedTypeNode named => named with { TypeKind = classification },
            PlaceholderTypeNode placeholder => placeholder with { TypeKind = classification },
            _ => throw new ArgumentException(
                $"Type classification prefix is not valid for identity: {node.IdentityKey}",
                nameof(node)),
        };

    private static FunctionPointerTypeNode ParseFunctionPointerIdentityNode(string identity)
    {
        var typeListStart = identity.IndexOf('<');
        if (typeListStart < 0 || !identity.EndsWith('>'))
        {
            throw new ArgumentException($"Invalid function-pointer identity: {identity}", nameof(identity));
        }

        var convention = identity["delegate*".Length..typeListStart].Trim();
        var parts = SplitTopLevel(identity[(typeListStart + 1)..^1], ',')
            .Select(ParseFunctionPointerIdentityPart)
            .ToArray();
        if (parts.Length == 0)
        {
            throw new ArgumentException($"Invalid function-pointer identity: {identity}", nameof(identity));
        }

        return new FunctionPointerTypeNode(
            convention,
            parts[..^1].Select(part => new FunctionPointerParameterNode(part.Type, part.RefKind)).ToArray(),
            parts[^1].Type,
            parts[^1].RefKind);
    }

    private static (TypeNode Type, int RefKind) ParseFunctionPointerIdentityPart(string part)
    {
        var separator = part.IndexOf(':');
        if (separator <= 0 || !int.TryParse(part[..separator], out var refKind))
        {
            throw new ArgumentException($"Invalid function-pointer identity part: {part}", nameof(part));
        }

        return (ParseIdentityNode(part[(separator + 1)..]), refKind);
    }

    private static NamedTypeNode ParseNamedTypeIdentity(
        string identity,
        TypeClassification classification = TypeClassification.Unknown)
    {
        var boundary = identity.IndexOf("::", StringComparison.Ordinal);
        if (boundary < 0)
        {
            throw new ArgumentException($"Named-type identity has no namespace/type boundary: {identity}", nameof(identity));
        }

        var namespaceSegments = identity[..boundary]
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new NamedTypeSegment(name, []))
            .ToArray();
        var typeIdentity = identity[(boundary + 2)..];
        var typeSegments = SplitTopLevel(typeIdentity, '.')
            .Select(ParseNamedTypeIdentitySegment)
            .ToArray();
        if (typeSegments.Length == 0 || typeSegments.Any(segment => string.IsNullOrEmpty(segment.Name)))
        {
            throw new ArgumentException($"Named-type identity has no type component: {identity}", nameof(identity));
        }

        var segments = namespaceSegments.Concat(typeSegments).ToArray();
        var qualifiedName = string.Join('.', segments.Select(segment => segment.Name));
        return new NamedTypeNode(
            segments,
            namespaceSegments.Length,
            classification != TypeClassification.Unknown
                ? classification
                : ValueTypeNames.Contains(qualifiedName)
                    ? TypeClassification.Value
                    : TypeClassification.Unknown);
    }

    private static NamedTypeSegment ParseNamedTypeIdentitySegment(string segment)
    {
        var genericStart = segment.IndexOf('<');
        if (genericStart < 0)
        {
            return new NamedTypeSegment(segment, []);
        }

        if (!segment.EndsWith('>'))
        {
            throw new ArgumentException($"Invalid named-type identity segment: {segment}", nameof(segment));
        }

        return new NamedTypeSegment(
            segment[..genericStart],
            SplitTopLevel(segment[(genericStart + 1)..^1], ',')
                .Select(ParseIdentityNode)
                .ToArray());
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    parenthesisDepth--;
                    break;
                default:
                    if (text[index] == separator && angleDepth == 0 && bracketDepth == 0 && parenthesisDepth == 0)
                    {
                        result.Add(text[start..index]);
                        start = index + 1;
                    }

                    break;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    private abstract record TypeNode
    {
        public abstract string IdentityKey { get; }

        public abstract TypeClassification Classification { get; }
    }

    private enum TypeClassification
    {
        Unknown,
        Value,
        Reference,
    }

    private sealed record NamedTypeSegment(
        string Name,
        IReadOnlyList<TypeNode> Arguments);

    private sealed record NamedTypeNode(
        IReadOnlyList<NamedTypeSegment> Segments,
        int? NamespaceSegmentCount,
        TypeClassification TypeKind) : TypeNode
    {
        public IReadOnlyList<TypeNode> Arguments => Segments[^1].Arguments;

        public override TypeClassification Classification => TypeKind;

        public override string IdentityKey
        {
            get
            {
                var namespaceSegmentCount = NamespaceSegmentCount ?? Math.Max(Segments.Count - 1, 0);
                var namespaceIdentity = string.Join(
                    '.',
                    Segments.Take(namespaceSegmentCount).Select(segment => segment.Name));
                var typeIdentity = string.Join(
                    '.',
                    Segments.Skip(namespaceSegmentCount).Select(segment => segment.Arguments.Count == 0
                        ? segment.Name
                        : $"{segment.Name}<{string.Join(',', segment.Arguments.Select(argument => argument.IdentityKey))}>"));
                var identity = $"{namespaceIdentity}::{typeIdentity}";
                return Classification == TypeClassification.Value && !ValueTypeNames.Contains(GetQualifiedName())
                    ? ValueTypeIdentityPrefix + identity
                    : identity;
            }
        }

        public bool HasQualifiedName(string qualifiedName) =>
            string.Equals(GetQualifiedName(), qualifiedName, StringComparison.Ordinal);

        private string GetQualifiedName() =>
            string.Join('.', Segments.Select(segment => segment.Name));
    }

    private sealed record PlaceholderTypeNode(
        CanonicalGenericPlaceholderScope Scope,
        int Ordinal,
        TypeClassification TypeKind) : TypeNode
    {
        public override TypeClassification Classification => TypeKind;

        public override string IdentityKey
        {
            get
            {
                var identity = Scope == CanonicalGenericPlaceholderScope.Method ? $"^{Ordinal}" : $"!{Ordinal}";
                return Classification switch
                {
                    TypeClassification.Value => ValueTypeIdentityPrefix + identity,
                    TypeClassification.Reference => ReferenceTypeIdentityPrefix + identity,
                    _ => identity,
                };
            }
        }
    }

    private sealed record ArrayTypeNode(TypeNode Element, int Rank) : TypeNode
    {
        public override TypeClassification Classification => TypeClassification.Reference;

        public override string IdentityKey => $"{Element.IdentityKey}[{new string(',', Rank - 1)}]";
    }

    private sealed record PointerTypeNode(TypeNode Element) : TypeNode
    {
        public override TypeClassification Classification => TypeClassification.Value;

        public override string IdentityKey => $"{Element.IdentityKey}*";
    }

    private sealed record TupleTypeNode(IReadOnlyList<TypeNode> Elements) : TypeNode
    {
        public override TypeClassification Classification => TypeClassification.Value;

        public override string IdentityKey => $"({string.Join(',', Elements.Select(element => element.IdentityKey))})";
    }

    private sealed record NullableTypeNode(TypeNode Element) : TypeNode
    {
        public override TypeClassification Classification => TypeClassification.Value;

        public override string IdentityKey => $"{Element.IdentityKey}?";
    }

    private sealed record FunctionPointerParameterNode(TypeNode Type, int RefKind);

    private sealed record FunctionPointerTypeNode(
        string CallingConvention,
        IReadOnlyList<FunctionPointerParameterNode> Parameters,
        TypeNode ReturnType,
        int ReturnRefKind) : TypeNode
    {
        public override TypeClassification Classification => TypeClassification.Value;

        public override string IdentityKey =>
            $"delegate*{(string.IsNullOrEmpty(CallingConvention) ? string.Empty : $" {CallingConvention}")}<" +
            $"{string.Join(',', Parameters.Select(parameter => $"{parameter.RefKind}:{parameter.Type.IdentityKey}"))}," +
            $"{ReturnRefKind}:{ReturnType.IdentityKey}>";
    }

    private sealed class TupleElementNameOmittingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitTupleElement(TupleElementSyntax node) =>
            SyntaxFactory.TupleElement((TypeSyntax)Visit(node.Type)!);
    }
}
