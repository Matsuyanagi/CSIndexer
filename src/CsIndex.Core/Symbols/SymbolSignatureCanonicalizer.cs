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
        IReadOnlyDictionary<string, int> genericPlaceholders)
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
        return new CanonicalTypeSelector(syntaxText, genericPlaceholders);
    }

    public static bool IsMatch(CanonicalTypeSelector selector, CanonicalTypeSignature candidate)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(candidate);

        var selectorNode = ParseSelectorNode(
            SyntaxFactory.ParseTypeName(selector.SyntaxText),
            selector.GenericPlaceholders);
        var candidateNode = ParseIdentityNode(candidate.IdentityKey);
        return Matches(selectorNode, candidateNode);
    }

    private static void ValidateGenericPlaceholders(IReadOnlyDictionary<string, int> genericPlaceholders)
    {
        foreach (var (name, ordinal) in genericPlaceholders)
        {
            if (string.IsNullOrWhiteSpace(name) || ordinal < 0)
            {
                throw new ArgumentException("Generic placeholder names and ordinals must be non-empty and non-negative.", nameof(genericPlaceholders));
            }
        }
    }

    private static string FormatTypeDisplay(ITypeSymbol type)
    {
        var syntax = SyntaxFactory.ParseTypeName(type.ToDisplayString(TypeDisplayFormat));
        var rewritten = (TypeSyntax)new TupleElementNameOmittingRewriter().Visit(syntax)!;
        return rewritten.NormalizeWhitespace().ToFullString();
    }

    private static TypeNode CreateNode(ITypeSymbol type)
    {
        if (type is IDynamicTypeSymbol)
        {
            return CreateSimpleNamedTypeNode("System.Object", false);
        }

        if (type is ITypeParameterSymbol typeParameter)
        {
            return new PlaceholderTypeNode(typeParameter.Ordinal, typeParameter.TypeParameterKind == TypeParameterKind.Method);
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

        if (type is INamedTypeSymbol named && named.IsTupleType)
        {
            return new TupleTypeNode(named.TupleElements.Select(element => CreateNode(element.Type)).ToArray());
        }

        if (type is INamedTypeSymbol namedType)
        {
            return CreateNamedTypeNode(namedType);
        }

        var fallbackName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fallbackName.StartsWith("global::", StringComparison.Ordinal))
        {
            fallbackName = fallbackName["global::".Length..];
        }

        return ParseNamedTypeIdentity(fallbackName, type.IsValueType);
    }

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
        if (type.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
        {
            segments.AddRange(containingNamespace
                .ToDisplayString()
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => new NamedTypeSegment(name, [])));
        }

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

        return new NamedTypeNode(segments, type.IsValueType);
    }

    private static NamedTypeNode CreateSimpleNamedTypeNode(string qualifiedName, bool? isValueType) =>
        new(
            qualifiedName
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => new NamedTypeSegment(name, []))
                .ToArray(),
            isValueType);

    private static string GetNamedTypeName(INamedTypeSymbol type)
    {
        return string.Join('.', CreateNamedTypeNode(type).Segments.Select(segment => segment.Name));
    }

    private static TypeNode ParseSelectorNode(
        TypeSyntax syntax,
        IReadOnlyDictionary<string, int> genericPlaceholders)
    {
        return syntax switch
        {
            PredefinedTypeSyntax predefined => ParsePredefinedType(predefined),
            IdentifierNameSyntax identifier => ParseIdentifier(identifier.Identifier.ValueText, genericPlaceholders),
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
            ? CreateSimpleNamedTypeNode(identity, ValueTypeNames.Contains(identity))
            : throw new ArgumentException($"Unsupported C# predefined type: {text}", nameof(syntax));
    }

    private static TypeNode ParseIdentifier(
        string name,
        IReadOnlyDictionary<string, int> genericPlaceholders)
    {
        if (genericPlaceholders.TryGetValue(name, out var ordinal))
        {
            return new PlaceholderTypeNode(ordinal, false);
        }

        if (PredefinedTypeNames.TryGetValue(name, out var predefined))
        {
            return CreateSimpleNamedTypeNode(predefined, ValueTypeNames.Contains(predefined));
        }

        if (string.Equals(name, "dynamic", StringComparison.Ordinal))
        {
            return CreateSimpleNamedTypeNode("System.Object", false);
        }

        throw new ArgumentException($"Named selector types must be fully qualified: {name}", nameof(name));
    }

    private static TypeNode ParseGenericName(
        GenericNameSyntax syntax,
        IReadOnlyDictionary<string, int> genericPlaceholders)
    {
        var name = syntax.Identifier.ValueText;
        if (genericPlaceholders.ContainsKey(name) || PredefinedTypeNames.ContainsKey(name))
        {
            throw new ArgumentException($"Generic selector type is not a named type: {name}", nameof(syntax));
        }

        throw new ArgumentException($"Named selector types must be fully qualified: {name}", nameof(syntax));
    }

    private static TypeNode ParseQualifiedName(
        QualifiedNameSyntax syntax,
        IReadOnlyDictionary<string, int> genericPlaceholders) =>
        ParseQualifiedNamedType(syntax, genericPlaceholders);

    private static TypeNode ParseAliasQualifiedName(
        AliasQualifiedNameSyntax syntax,
        IReadOnlyDictionary<string, int> genericPlaceholders)
    {
        if (!string.Equals(syntax.Alias.Identifier.ValueText, "global", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported type alias: {syntax.Alias}", nameof(syntax));
        }

        return ParseQualifiedNamedType(syntax, genericPlaceholders);
    }

    private static NamedTypeNode ParseQualifiedNamedType(
        NameSyntax syntax,
        IReadOnlyDictionary<string, int> genericPlaceholders)
    {
        var segments = ParseNamedTypeSegments(syntax, genericPlaceholders);
        var qualifiedName = string.Join('.', segments.Select(segment => segment.Name));
        return new NamedTypeNode(
            segments,
            ValueTypeNames.Contains(qualifiedName) ? true : null);
    }

    private static IReadOnlyList<NamedTypeSegment> ParseNamedTypeSegments(
        NameSyntax syntax,
        IReadOnlyDictionary<string, int> genericPlaceholders)
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
            case AliasQualifiedNameSyntax aliasQualified
                when aliasQualified.Alias.Identifier.ValueText == "global":
                return ParseNamedTypeSegments(aliasQualified.Name, genericPlaceholders);
            default:
                throw new ArgumentException($"Unsupported qualified selector type: {syntax}", nameof(syntax));
        }
    }

    private static TypeNode ParseArrayType(
        ArrayTypeSyntax syntax,
        IReadOnlyDictionary<string, int> genericPlaceholders)
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
        IReadOnlyDictionary<string, int> genericPlaceholders)
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
        var convention = syntax.CallingConvention?.ToString() ?? string.Empty;
        if (string.Equals(convention, "managed", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        const string customPrefix = "unmanaged[";
        if (!convention.StartsWith(customPrefix, StringComparison.Ordinal) || !convention.EndsWith(']'))
        {
            return convention;
        }

        var shortNames = convention[customPrefix.Length..^1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

    private static bool Matches(TypeNode selector, TypeNode candidate)
    {
        if (selector is NullableTypeNode nullableSelector)
        {
            if (candidate is NamedTypeNode nullableCandidate &&
                nullableCandidate.HasQualifiedName("System.Nullable") &&
                nullableCandidate.Arguments.Count == 1)
            {
                return Matches(nullableSelector.Element, nullableCandidate.Arguments[0]);
            }

            return candidate is not NamedTypeNode { IsValueType: true } && Matches(nullableSelector.Element, candidate);
        }

        if (candidate is NamedTypeNode namedCandidate &&
            namedCandidate.HasQualifiedName("System.Nullable") &&
            namedCandidate.Arguments.Count == 1)
        {
            return false;
        }

        return (selector, candidate) switch
        {
            (PlaceholderTypeNode left, PlaceholderTypeNode right) => left.Ordinal == right.Ordinal,
            (NamedTypeNode left, NamedTypeNode right) => NamedTypesMatch(left, right),
            (ArrayTypeNode left, ArrayTypeNode right) =>
                left.Rank == right.Rank && Matches(left.Element, right.Element),
            (PointerTypeNode left, PointerTypeNode right) => Matches(left.Element, right.Element),
            (TupleTypeNode left, TupleTypeNode right) =>
                left.Elements.Count == right.Elements.Count &&
                left.Elements.Zip(right.Elements).All(pair => Matches(pair.First, pair.Second)),
            (FunctionPointerTypeNode left, FunctionPointerTypeNode right) =>
                string.Equals(left.CallingConvention, right.CallingConvention, StringComparison.Ordinal) &&
                left.Parameters.Count == right.Parameters.Count &&
                left.Parameters.Zip(right.Parameters).All(pair =>
                    pair.First.RefKind == pair.Second.RefKind && Matches(pair.First.Type, pair.Second.Type)) &&
                left.ReturnRefKind == right.ReturnRefKind &&
                Matches(left.ReturnType, right.ReturnType),
            _ => false,
        };
    }

    private static bool NamedTypesMatch(NamedTypeNode left, NamedTypeNode right) =>
        left.Segments.Count == right.Segments.Count &&
        left.Segments.Zip(right.Segments).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            pair.First.Arguments.Count == pair.Second.Arguments.Count &&
            pair.First.Arguments.Zip(pair.Second.Arguments).All(arguments =>
                Matches(arguments.First, arguments.Second)));

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

        if (identity.StartsWith('!') && int.TryParse(identity[1..], out var typeOrdinal))
        {
            return new PlaceholderTypeNode(typeOrdinal, false);
        }

        if (identity.StartsWith('^') && int.TryParse(identity[1..], out var methodOrdinal))
        {
            return new PlaceholderTypeNode(methodOrdinal, true);
        }

        if (identity.StartsWith(ValueTypeIdentityPrefix, StringComparison.Ordinal))
        {
            return ParseNamedTypeIdentity(identity[ValueTypeIdentityPrefix.Length..], true);
        }

        return ParseNamedTypeIdentity(identity);
    }

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

    private static NamedTypeNode ParseNamedTypeIdentity(string identity, bool? isValueType = null)
    {
        var segments = SplitTopLevel(identity, '.')
            .Select(ParseNamedTypeIdentitySegment)
            .ToArray();
        var qualifiedName = string.Join('.', segments.Select(segment => segment.Name));
        return new NamedTypeNode(
            segments,
            isValueType ?? (ValueTypeNames.Contains(qualifiedName) ? true : null));
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
    }

    private sealed record NamedTypeSegment(
        string Name,
        IReadOnlyList<TypeNode> Arguments);

    private sealed record NamedTypeNode(
        IReadOnlyList<NamedTypeSegment> Segments,
        bool? IsValueType) : TypeNode
    {
        public IReadOnlyList<TypeNode> Arguments => Segments[^1].Arguments;

        public override string IdentityKey
        {
            get
            {
                var identity = string.Join(
                    '.',
                    Segments.Select(segment => segment.Arguments.Count == 0
                        ? segment.Name
                        : $"{segment.Name}<{string.Join(',', segment.Arguments.Select(argument => argument.IdentityKey))}>"));
                return IsValueType == true && !ValueTypeNames.Contains(GetQualifiedName())
                    ? ValueTypeIdentityPrefix + identity
                    : identity;
            }
        }

        public bool HasQualifiedName(string qualifiedName) =>
            string.Equals(GetQualifiedName(), qualifiedName, StringComparison.Ordinal);

        private string GetQualifiedName() =>
            string.Join('.', Segments.Select(segment => segment.Name));
    }

    private sealed record PlaceholderTypeNode(int Ordinal, bool IsMethod) : TypeNode
    {
        public override string IdentityKey => IsMethod ? $"^{Ordinal}" : $"!{Ordinal}";
    }

    private sealed record ArrayTypeNode(TypeNode Element, int Rank) : TypeNode
    {
        public override string IdentityKey => $"{Element.IdentityKey}[{new string(',', Rank - 1)}]";
    }

    private sealed record PointerTypeNode(TypeNode Element) : TypeNode
    {
        public override string IdentityKey => $"{Element.IdentityKey}*";
    }

    private sealed record TupleTypeNode(IReadOnlyList<TypeNode> Elements) : TypeNode
    {
        public override string IdentityKey => $"({string.Join(',', Elements.Select(element => element.IdentityKey))})";
    }

    private sealed record NullableTypeNode(TypeNode Element) : TypeNode
    {
        public override string IdentityKey => $"{Element.IdentityKey}?";
    }

    private sealed record FunctionPointerParameterNode(TypeNode Type, int RefKind);

    private sealed record FunctionPointerTypeNode(
        string CallingConvention,
        IReadOnlyList<FunctionPointerParameterNode> Parameters,
        TypeNode ReturnType,
        int ReturnRefKind) : TypeNode
    {
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
