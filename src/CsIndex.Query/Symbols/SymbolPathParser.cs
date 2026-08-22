using System.Globalization;
using System.Text;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsIndex.Query.Symbols;

public static class SymbolPathParser
{
    private static readonly IReadOnlySet<string> OperatorTokens =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "+", "-", "!", "~", "++", "--", "true", "false", "*", "/", "%", "&", "|", "^",
            "<<", ">>", ">>>", "==", "!=", "<", ">", "<=", ">=", "+=", "-=", "*=", "/=", "%=",
            "&=", "|=", "^=", "<<=", ">>=", ">>>=",
        };

    public static SymbolPathSelector Parse(string value)
    {
        var text = RequireText(value, "Symbol path");
        var fields = BalancedTextScanner.SplitTopLevel(text, "::");
        if (fields.Count is not 2 and not 3)
        {
            throw ParseError("expected exactly one or two top-level '::' separators");
        }

        var activePlaceholders = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal);
        var typeOrdinal = 0;

        if (fields.Count == 2)
        {
            var type = ParseHierarchyCore(
                RequireText(fields[0], "CSharp type selector"),
                PatternMode.Glob,
                activePlaceholders,
                ref typeOrdinal,
                allowGenericPlaceholders: true);
            var executable = ParseExecutableCore(
                RequireText(fields[1], "Executable selector"),
                PatternMode.Glob,
                activePlaceholders);
            return new SymbolPathSelector(SymbolPathStyle.CSharp, null, type, executable);
        }

        var namespaceText = RequireText(fields[0], "Namespace selector");
        HierarchySelector namespaceSelector;
        if (namespaceText.Equals("global", StringComparison.Ordinal))
        {
            namespaceSelector = new HierarchySelector(EmptySnapshot<HierarchySegmentSelector>());
        }
        else
        {
            var namespacePlaceholders = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal);
            var unusedOrdinal = 0;
            namespaceSelector = ParseHierarchyCore(
                namespaceText,
                PatternMode.Glob,
                namespacePlaceholders,
                ref unusedOrdinal,
                allowGenericPlaceholders: false);
        }

        var explicitType = ParseHierarchyCore(
            RequireText(fields[1], "Type selector"),
            PatternMode.Glob,
            activePlaceholders,
            ref typeOrdinal,
            allowGenericPlaceholders: true);
        var explicitExecutable = ParseExecutableCore(
            RequireText(fields[2], "Executable selector"),
            PatternMode.Glob,
            activePlaceholders);
        return new SymbolPathSelector(
            SymbolPathStyle.Explicit,
            namespaceSelector,
            explicitType,
            explicitExecutable);
    }

    public static IReadOnlyList<ExecutableSegmentSelector> ParseExecutable(
        string value,
        PatternMode patternMode)
    {
        ValidatePatternMode(patternMode);
        return ParseExecutableCore(
            RequireText(value, "Executable selector"),
            patternMode,
            new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal));
    }

    public static HierarchySelector ParseHierarchy(string value, PatternMode patternMode)
    {
        ValidatePatternMode(patternMode);
        var activePlaceholders = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal);
        var typeOrdinal = 0;
        return ParseHierarchyCore(
            RequireText(value, "Hierarchy selector"),
            patternMode,
            activePlaceholders,
            ref typeOrdinal,
            allowGenericPlaceholders: true);
    }

    private static HierarchySelector ParseHierarchyCore(
        string text,
        PatternMode patternMode,
        Dictionary<string, CanonicalGenericPlaceholder> activePlaceholders,
        ref int typeOrdinal,
        bool allowGenericPlaceholders)
    {
        var rawSegments = BalancedTextScanner.SplitTopLevel(text, ".");
        var segments = new List<HierarchySegmentSelector>(rawSegments.Count);
        foreach (var rawSegment in rawSegments)
        {
            var segmentText = RequireText(rawSegment, "Hierarchy segment");
            var genericOpen = segmentText.IndexOf('<');
            string identifierText;
            IReadOnlyList<string> genericPlaceholders;

            if (genericOpen < 0)
            {
                identifierText = segmentText;
                genericPlaceholders = EmptySnapshot<string>();
            }
            else
            {
                if (!allowGenericPlaceholders)
                {
                    throw ParseError("namespace segments cannot declare generic placeholders");
                }

                var genericClose = BalancedTextScanner.FindMatchingDelimiter(segmentText, genericOpen);
                if (!string.IsNullOrWhiteSpace(segmentText[(genericClose + 1)..]))
                {
                    throw ParseError($"unexpected text after hierarchy generic list in '{segmentText}'");
                }

                identifierText = segmentText[..genericOpen];
                genericPlaceholders = ParseGenericPlaceholders(
                    segmentText[(genericOpen + 1)..genericClose],
                    "Hierarchy generic list");
            }

            var identifierPattern = NormalizeIdentifierPattern(identifierText, patternMode, "Hierarchy identifier");
            foreach (var placeholderName in genericPlaceholders)
            {
                activePlaceholders[placeholderName] = new CanonicalGenericPlaceholder(
                    CanonicalGenericPlaceholderScope.Type,
                    typeOrdinal++);
            }

            segments.Add(new HierarchySegmentSelector(
                identifierPattern,
                patternMode,
                genericPlaceholders));
        }

        return new HierarchySelector(Snapshot(segments));
    }

    private static IReadOnlyList<ExecutableSegmentSelector> ParseExecutableCore(
        string text,
        PatternMode patternMode,
        Dictionary<string, CanonicalGenericPlaceholder> activePlaceholders)
    {
        var rawSegments = BalancedTextScanner.SplitTopLevel(text, ".");
        var segments = new List<ExecutableSegmentSelector>(rawSegments.Count);
        var methodOrdinal = 0;

        foreach (var rawSegment in rawSegments)
        {
            var segmentText = RequireText(rawSegment, "Executable segment");
            var segment = segmentText[0] switch
            {
                '[' => ParseSpecialSegment(
                    segmentText,
                    patternMode,
                    activePlaceholders,
                    ref methodOrdinal),
                '<' => ParseSyntheticSegment(segmentText, patternMode),
                _ => ParseNamedSegment(
                    segmentText,
                    patternMode,
                    activePlaceholders,
                    ref methodOrdinal),
            };
            segments.Add(segment);
        }

        return Snapshot(segments);
    }

    private static NamedExecutableSegmentSelector ParseNamedSegment(
        string text,
        PatternMode patternMode,
        Dictionary<string, CanonicalGenericPlaceholder> activePlaceholders,
        ref int methodOrdinal)
    {
        var suffixStart = FindCallableSuffixStart(text);
        var identifierText = suffixStart < 0 ? text : text[..suffixStart];
        var identifierPattern = NormalizeIdentifierPattern(
            identifierText,
            patternMode,
            "Executable identifier");
        var arity = ParseCallableSuffix(
            text,
            suffixStart < 0 ? text.Length : suffixStart,
            allowGenericPlaceholders: true,
            activePlaceholders,
            ref methodOrdinal,
            "Named callable");
        return new NamedExecutableSegmentSelector(identifierPattern, arity, patternMode);
    }

    private static SpecialExecutableSegmentSelector ParseSpecialSegment(
        string text,
        PatternMode patternMode,
        Dictionary<string, CanonicalGenericPlaceholder> activePlaceholders,
        ref int methodOrdinal)
    {
        var bracketClose = BalancedTextScanner.FindMatchingDelimiter(text, 0);
        var content = RequireText(text[1..bracketClose], "Special callable tag");
        var permitsGenericPlaceholders = content.StartsWith("explicit:", StringComparison.Ordinal);
        var arity = ParseCallableSuffix(
            text,
            bracketClose + 1,
            permitsGenericPlaceholders,
            activePlaceholders,
            ref methodOrdinal,
            "Special callable");

        string tag;
        string? operatorToken = null;
        string? conversionKind = null;
        CanonicalTypeSelector? conversionTarget = null;
        QualifiedMemberSelector? member = null;

        if (content is "constructor" or "static-constructor" or "destructor")
        {
            tag = content;
        }
        else if (TryTakePrefix(content, "operator:", out var uncheckedOperator))
        {
            tag = "operator";
            operatorToken = ParseOperatorToken(uncheckedOperator);
        }
        else if (TryTakePrefix(content, "checked-operator:", out var checkedOperator))
        {
            tag = "checked-operator";
            operatorToken = ParseOperatorToken(checkedOperator);
        }
        else if (TryTakePrefix(content, "conversion:implicit:", out var implicitTarget))
        {
            tag = "conversion";
            conversionKind = "implicit";
            conversionTarget = ParseSelectorType(implicitTarget, activePlaceholders, "Conversion target");
        }
        else if (TryTakePrefix(content, "conversion:explicit:", out var explicitTarget))
        {
            tag = "conversion";
            conversionKind = "explicit";
            conversionTarget = ParseSelectorType(explicitTarget, activePlaceholders, "Conversion target");
        }
        else if (TryTakePrefix(content, "checked-conversion:explicit:", out var checkedTarget))
        {
            tag = "checked-conversion";
            conversionKind = "explicit";
            conversionTarget = ParseSelectorType(checkedTarget, activePlaceholders, "Conversion target");
        }
        else if (TryTakeMemberPayload(content, "get", out var getMember))
        {
            tag = "get";
            member = ParseQualifiedMember(getMember, patternMode, activePlaceholders, requireContainingType: false);
        }
        else if (TryTakeMemberPayload(content, "set", out var setMember))
        {
            tag = "set";
            member = ParseQualifiedMember(setMember, patternMode, activePlaceholders, requireContainingType: false);
        }
        else if (TryTakeMemberPayload(content, "init", out var initMember))
        {
            tag = "init";
            member = ParseQualifiedMember(initMember, patternMode, activePlaceholders, requireContainingType: false);
        }
        else if (TryTakeMemberPayload(content, "add", out var addMember))
        {
            tag = "add";
            member = ParseQualifiedMember(addMember, patternMode, activePlaceholders, requireContainingType: false);
        }
        else if (TryTakeMemberPayload(content, "remove", out var removeMember))
        {
            tag = "remove";
            member = ParseQualifiedMember(removeMember, patternMode, activePlaceholders, requireContainingType: false);
        }
        else if (TryTakeMemberPayload(content, "explicit", out var explicitMember))
        {
            tag = "explicit";
            member = ParseQualifiedMember(explicitMember, patternMode, activePlaceholders, requireContainingType: true);
        }
        else
        {
            throw ParseError($"unknown or malformed special callable tag '[{content}]'");
        }

        return new SpecialExecutableSegmentSelector(
            tag,
            operatorToken,
            conversionKind,
            conversionTarget,
            member,
            arity,
            PatternMode.Literal);
    }

    private static ExecutableSegmentSelector ParseSyntheticSegment(string text, PatternMode patternMode)
    {
        if (TryTakeMarkerPayload(text, "<lambda#", out var lambdaOrdinal))
        {
            return new LambdaExecutableSegmentSelector(ParseOrdinal(lambdaOrdinal, "Lambda"));
        }

        if (TryTakeMarkerPayload(text, "<anonymous-method#", out var anonymousOrdinal))
        {
            return new AnonymousMethodExecutableSegmentSelector(ParseOrdinal(
                anonymousOrdinal,
                "Anonymous method"));
        }

        if (TryTakeMarkerPayload(text, "<initializer:", out var initializerMember))
        {
            return new InitializerExecutableSegmentSelector(
                NormalizeIdentifierPattern(initializerMember, patternMode, "Initializer member"),
                patternMode);
        }

        if (text.Equals("<top-level-statements>", StringComparison.Ordinal))
        {
            return new TopLevelStatementsExecutableSegmentSelector();
        }

        throw ParseError($"unknown or malformed synthetic executable segment '{text}'");
    }

    private static CallableAritySelector ParseCallableSuffix(
        string text,
        int suffixStart,
        bool allowGenericPlaceholders,
        Dictionary<string, CanonicalGenericPlaceholder> activePlaceholders,
        ref int methodOrdinal,
        string context)
    {
        var cursor = SkipWhitespace(text, suffixStart);
        var genericState = GenericListState.Omitted;
        IReadOnlyList<string> genericPlaceholders = EmptySnapshot<string>();

        if (cursor < text.Length && text[cursor] == '<')
        {
            if (!allowGenericPlaceholders)
            {
                throw ParseError($"{context} cannot declare generic placeholders");
            }

            var genericClose = BalancedTextScanner.FindMatchingDelimiter(text, cursor);
            genericPlaceholders = ParseGenericPlaceholders(
                text[(cursor + 1)..genericClose],
                $"{context} generic list");
            genericState = GenericListState.Present;
            foreach (var placeholderName in genericPlaceholders)
            {
                activePlaceholders[placeholderName] = new CanonicalGenericPlaceholder(
                    CanonicalGenericPlaceholderScope.Method,
                    methodOrdinal++);
            }

            cursor = SkipWhitespace(text, genericClose + 1);
        }

        var parameterState = ParameterListState.Omitted;
        IReadOnlyList<CanonicalTypeSelector> parameters = EmptySnapshot<CanonicalTypeSelector>();
        IReadOnlyList<int> parameterRefKinds = EmptySnapshot<int>();

        if (cursor < text.Length && text[cursor] == '(')
        {
            var parameterClose = BalancedTextScanner.FindMatchingDelimiter(text, cursor);
            (parameters, parameterRefKinds) = ParseParameters(
                text[(cursor + 1)..parameterClose],
                activePlaceholders,
                context);
            parameterState = ParameterListState.Present;
            cursor = SkipWhitespace(text, parameterClose + 1);
        }

        if (cursor != text.Length)
        {
            throw ParseError($"unexpected trailing text in {context.ToLowerInvariant()} '{text}'");
        }

        return new CallableAritySelector(
            genericState,
            genericPlaceholders,
            parameterState,
            parameters,
            parameterRefKinds);
    }

    private static (IReadOnlyList<CanonicalTypeSelector> Parameters, IReadOnlyList<int> RefKinds)
        ParseParameters(
            string text,
            IReadOnlyDictionary<string, CanonicalGenericPlaceholder> activePlaceholders,
            string context)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (EmptySnapshot<CanonicalTypeSelector>(), EmptySnapshot<int>());
        }

        var rawParameters = BalancedTextScanner.SplitTopLevel(text, ",");
        var parameters = new List<CanonicalTypeSelector>(rawParameters.Count);
        var refKinds = new List<int>(rawParameters.Count);
        foreach (var rawParameter in rawParameters)
        {
            var parameterText = RequireText(rawParameter, $"{context} parameter type");
            var (typeText, refKind) = StripParameterModifier(parameterText);
            parameters.Add(ParseSelectorType(typeText, activePlaceholders, $"{context} parameter type"));
            refKinds.Add((int)refKind);
        }

        return (Snapshot(parameters), Snapshot(refKinds));
    }

    private static (string TypeText, RefKind RefKind) StripParameterModifier(string text)
    {
        var firstEnd = FindWordEnd(text, 0);
        var firstWord = text[..firstEnd];
        if (firstWord is "params" or "scoped" or "this" or "readonly")
        {
            throw ParseError($"unsupported parameter modifier '{firstWord}'");
        }

        if (firstWord is not "ref" and not "out" and not "in")
        {
            return (text, RefKind.None);
        }

        var typeStart = SkipRequiredWhitespace(text, firstEnd, firstWord);
        if (firstWord == "ref")
        {
            var secondEnd = FindWordEnd(text, typeStart);
            if (text[typeStart..secondEnd].Equals("readonly", StringComparison.Ordinal))
            {
                typeStart = SkipRequiredWhitespace(text, secondEnd, "ref readonly");
                return (RequireText(text[typeStart..], "Parameter type"), RefKind.RefReadOnlyParameter);
            }

            return (RequireText(text[typeStart..], "Parameter type"), RefKind.Ref);
        }

        return (
            RequireText(text[typeStart..], "Parameter type"),
            firstWord == "out" ? RefKind.Out : RefKind.In);
    }

    private static IReadOnlyList<string> ParseGenericPlaceholders(string text, string context)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw ParseError($"{context} cannot be empty");
        }

        var rawPlaceholders = BalancedTextScanner.SplitTopLevel(text, ",");
        var placeholders = new List<string>(rawPlaceholders.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPlaceholder in rawPlaceholders)
        {
            var placeholder = NormalizeIdentifier(
                RequireText(rawPlaceholder, $"{context} placeholder"),
                $"{context} placeholder");
            if (!names.Add(placeholder))
            {
                throw ParseError($"{context} contains duplicate placeholder '{placeholder}'");
            }

            placeholders.Add(placeholder);
        }

        return Snapshot(placeholders);
    }

    private static QualifiedMemberSelector ParseQualifiedMember(
        string text,
        PatternMode patternMode,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> activePlaceholders,
        bool requireContainingType)
    {
        var payload = RequireText(text, "Special callable member payload");
        var parts = BalancedTextScanner.SplitTopLevel(payload, ".");
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            throw ParseError("special callable member payload contains an empty component");
        }

        var memberPattern = NormalizeIdentifierPattern(parts[^1], patternMode, "Special callable member");
        if (parts.Count == 1)
        {
            if (requireContainingType)
            {
                throw ParseError("explicit interface member requires a fully qualified containing type");
            }

            return new QualifiedMemberSelector(null, null, memberPattern, patternMode);
        }

        var containingBuilder = new StringBuilder();
        for (var index = 0; index < parts.Count - 1; index++)
        {
            if (index != 0)
            {
                containingBuilder.Append('.');
            }

            containingBuilder.Append(parts[index]);
        }

        var containingTypePattern = RequireText(containingBuilder.ToString(), "Containing interface type");
        CanonicalTypeSelector? containingType;
        if (containingTypePattern.Contains('*'))
        {
            if (patternMode != PatternMode.Glob)
            {
                throw ParseError("containing interface type wildcards require glob mode");
            }

            _ = ParseSelectorType(
                ReplaceGlobStarsForValidation(containingTypePattern),
                activePlaceholders,
                "Containing interface type pattern");
            containingType = null;
        }
        else
        {
            containingType = ParseSelectorType(
                containingTypePattern,
                activePlaceholders,
                "Containing interface type");
        }

        return new QualifiedMemberSelector(
            containingTypePattern,
            containingType,
            memberPattern,
            patternMode);
    }

    private static CanonicalTypeSelector ParseSelectorType(
        string text,
        IReadOnlyDictionary<string, CanonicalGenericPlaceholder> activePlaceholders,
        string context)
    {
        var typeText = RequireText(text, context);
        try
        {
            return SymbolSignatureCanonicalizer.ParseSelectorType(typeText, activePlaceholders);
        }
        catch (ArgumentException exception)
        {
            throw ParseError($"{context} '{typeText}' is invalid: {exception.Message}");
        }
    }

    private static string ParseOperatorToken(string text)
    {
        var token = RequireText(text, "Operator token");
        if (!OperatorTokens.Contains(token))
        {
            throw ParseError($"unsupported operator token '{token}'");
        }

        return token;
    }

    private static int? ParseOrdinal(string text, string context)
    {
        if (text.Equals("*", StringComparison.Ordinal))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal <= 0)
        {
            throw ParseError($"{context} ordinal must be a positive integer or '*'");
        }

        return ordinal;
    }

    private static string NormalizeIdentifierPattern(string text, PatternMode patternMode, string context)
    {
        var pattern = RequireText(text, context);
        if (!pattern.Contains('*'))
        {
            return NormalizeIdentifier(pattern, context);
        }

        if (patternMode != PatternMode.Glob)
        {
            throw ParseError($"{context} wildcards require glob mode");
        }

        if (pattern[0] == '@')
        {
            pattern = pattern[1..];
        }

        if (pattern.Length == 0 || pattern.Contains('@'))
        {
            throw ParseError($"{context} is not a valid C# identifier glob");
        }

        var validationText = ReplaceGlobStarsForValidation(pattern);
        _ = NormalizeIdentifier(validationText, context);
        return pattern;
    }

    private static string NormalizeIdentifier(string text, string context)
    {
        var identifier = RequireText(text, context);
        var token = SyntaxFactory.ParseToken(identifier);
        if (!token.IsKind(SyntaxKind.IdentifierToken) ||
            token.ContainsDiagnostics ||
            token.FullSpan.Length != identifier.Length)
        {
            throw ParseError($"{context} '{identifier}' is not a valid C# identifier");
        }

        return token.ValueText;
    }

    private static string ReplaceGlobStarsForValidation(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character == '*' ? 'A' : character);
        }

        return builder.ToString();
    }

    private static int FindCallableSuffixStart(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '<' or '(')
            {
                return index;
            }
        }

        return -1;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int SkipRequiredWhitespace(string text, int start, string modifier)
    {
        var typeStart = SkipWhitespace(text, start);
        if (typeStart == start || typeStart == text.Length)
        {
            throw ParseError($"parameter modifier '{modifier}' requires a type");
        }

        return typeStart;
    }

    private static int FindWordEnd(string text, int start)
    {
        var index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool TryTakePrefix(string text, string prefix, out string remainder)
    {
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            remainder = text[prefix.Length..];
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    private static bool TryTakeMemberPayload(string text, string tag, out string payload) =>
        TryTakePrefix(text, $"{tag}:", out payload);

    private static bool TryTakeMarkerPayload(string text, string prefix, out string payload)
    {
        if (text.StartsWith(prefix, StringComparison.Ordinal) && text.EndsWith('>'))
        {
            payload = text[prefix.Length..^1];
            return true;
        }

        payload = string.Empty;
        return false;
    }

    private static string RequireText(string? text, string context)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw ParseError($"{context} cannot be empty");
        }

        return text.Trim();
    }

    private static void ValidatePatternMode(PatternMode patternMode)
    {
        if (patternMode is not PatternMode.Glob and not PatternMode.Literal)
        {
            throw new ArgumentOutOfRangeException(nameof(patternMode), patternMode, "Undefined pattern mode.");
        }
    }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static IReadOnlyList<T> EmptySnapshot<T>() =>
        Array.AsReadOnly(Array.Empty<T>());

    private static SymbolQueryParseException ParseError(string detail) =>
        new($"Invalid symbol path: {detail}.");
}
