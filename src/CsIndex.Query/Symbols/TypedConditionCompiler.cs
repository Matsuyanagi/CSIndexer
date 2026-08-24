using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.Query.Symbols;

public static class TypedConditionCompiler
{
    private static readonly TimeSpan ProductionRegexTimeout = TimeSpan.FromSeconds(2);

    public static CompiledTypedConditions Compile(
        SymbolSelectionRequest request,
        TimeSpan? regexTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Conditions);
        ArgumentNullException.ThrowIfNull(request.Case);
        ValidateCaseMode(request.Case.Namespace, nameof(request.Case));
        ValidateCaseMode(request.Case.Type, nameof(request.Case));
        ValidateCaseMode(request.Case.Method, nameof(request.Case));
        ValidateCaseMode(request.Case.File, nameof(request.Case));
        ValidateCaseMode(request.Case.Source, nameof(request.Case));

        var timeout = regexTimeout ?? ProductionRegexTimeout;
        var namespaces = new List<CompiledPathCondition>();
        var types = new List<CompiledPathCondition>();
        var methods = new List<CompiledPathCondition>();
        var files = new List<CompiledFileCondition>();
        var includes = new List<SourceTextFilter.CompiledSourcePredicate>();
        var excludes = new List<SourceTextFilter.CompiledSourcePredicate>();

        foreach (var condition in request.Conditions)
        {
            ArgumentNullException.ThrowIfNull(condition);
            ArgumentNullException.ThrowIfNull(condition.Value);
            if (condition.Syntax is not ConditionSyntax.Glob and
                not ConditionSyntax.Literal and
                not ConditionSyntax.Regex)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(condition),
                    condition.Syntax,
                    "Undefined condition syntax.");
            }
            switch (condition.Category)
            {
                case ConditionCategory.Namespace:
                    namespaces.Add(CompilePathCondition(
                    condition,
                    request.Case.Namespace,
                    false,
                    timeout,
                    namespaceComparison: ToComparison(request.Case.Namespace),
                    typeComparison: ToComparison(request.Case.Type)));
                    break;
                case ConditionCategory.Type:
                    types.Add(CompilePathCondition(
                    condition,
                    request.Case.Type,
                    true,
                    timeout,
                    namespaceComparison: ToComparison(request.Case.Namespace),
                    typeComparison: ToComparison(request.Case.Type)));
                    break;
                case ConditionCategory.Method:
                    methods.Add(CompilePathCondition(
                        condition,
                        request.Case.Method,
                        false,
                        timeout,
                    isMethod: true,
                    namespaceComparison: ToComparison(request.Case.Namespace),
                    typeComparison: ToComparison(request.Case.Type)));
                    break;
                case ConditionCategory.File:
                    files.Add(CompileFileCondition(condition, request.Case.File, timeout));
                    break;
                case ConditionCategory.Include:
                    includes.Add(SourceTextFilter.CompileCondition(
                        condition,
                        request.Case.Source,
                        timeout));
                    break;
                case ConditionCategory.Exclude:
                    excludes.Add(SourceTextFilter.CompileCondition(
                        condition,
                        request.Case.Source,
                        timeout));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(condition),
                        condition.Category,
                        "Undefined condition category.");
            }
        }

        return new CompiledTypedConditions(
            namespaces.ToArray(),
            types.ToArray(),
            methods.ToArray(),
            files.ToArray(),
            includes.ToArray(),
            excludes.ToArray(),
            request.FunctionFilter);
    }

    private static CompiledPathCondition CompilePathCondition(
        TypedCondition condition,
        CaseMode caseMode,
        bool isType,
        TimeSpan regexTimeout,
        bool isMethod = false,
        StringComparison? namespaceComparison = null,
        StringComparison? typeComparison = null)
    {
        var comparison = ToComparison(caseMode);
        namespaceComparison ??= comparison;
        typeComparison ??= comparison;
        if (condition.Syntax == ConditionSyntax.Regex)
        {
            return new CompiledPathCondition(
                new CompiledRegex(
                    $"\\A(?:{condition.Value})\\z",
                    comparison,
                    regexTimeout),
                null,
                null,
                isMethod,
                isType,
                comparison,
                namespaceComparison.Value,
                typeComparison.Value);
        }

        if (isMethod)
        {
            var patternMode = condition.Syntax == ConditionSyntax.Glob
                ? PatternMode.Glob
                : PatternMode.Literal;
            var selector = ParseExecutableCondition(condition.Value, patternMode);
            return new CompiledPathCondition(
                null,
                null,
                new CompiledExecutableSelector(selector),
                true,
                false,
                comparison,
                namespaceComparison.Value,
                typeComparison.Value);
        }

        var hierarchy = SymbolPathParser.ParseHierarchy(
            condition.Value,
            condition.Syntax == ConditionSyntax.Glob ? PatternMode.Glob : PatternMode.Literal);
        if (!isType && hierarchy.Segments.Any(segment => segment.GenericArity != 0))
        {
            // Namespace components do not have generic arity.  Keeping the
            // parsed generic tokens out of namespace matching prevents the
            // type-only syntax from acquiring accidental namespace semantics.
            return new CompiledPathCondition(
                null,
                new CompiledHierarchySelector(hierarchy, namespaceGenericMismatch: true),
                null,
                false,
                false,
                comparison,
                namespaceComparison.Value,
                typeComparison.Value);
        }

        return new CompiledPathCondition(
            null,
            new CompiledHierarchySelector(hierarchy, namespaceGenericMismatch: false),
            null,
            false,
            isType,
            comparison,
            namespaceComparison.Value,
            typeComparison.Value);
    }

    private static IReadOnlyList<ExecutableSegmentSelector> ParseExecutableCondition(
        string value,
        PatternMode patternMode)
    {
        if (patternMode != PatternMode.Glob)
        {
            return SymbolPathParser.ParseExecutable(value, patternMode);
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var parserInput = ReplaceWholeExplicitOwnerTypeWildcards(value, replacements);
        var selectors = SymbolPathParser.ParseExecutable(parserInput, patternMode);
        if (replacements.Count == 0)
        {
            return selectors;
        }

        return selectors.Select(selector => RestoreWholeTypeWildcards(selector, replacements)).ToArray();
    }

    private static string ReplaceWholeExplicitOwnerTypeWildcards(
        string value,
        IDictionary<string, string> replacements)
    {
        var result = new StringBuilder(value.Length);
        var explicitPayloadEnd = -1;
        var sentinelOrdinal = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (index > explicitPayloadEnd &&
                value.AsSpan(index).StartsWith("[explicit:", StringComparison.Ordinal))
            {
                explicitPayloadEnd = BalancedTextScanner.FindMatchingDelimiter(value, index);
            }

            if (index <= explicitPayloadEnd && value[index] == '*')
            {
                var wildcardLength = index + 1 < value.Length && value[index + 1] == '*' ? 2 : 1;
                if (IsWholeGenericArgument(value, index, wildcardLength))
                {
                    string sentinelRoot;
                    do
                    {
                        sentinelRoot = $"System.__CsIndexAnyTypeWildcard{sentinelOrdinal++}";
                    }
                    while (value.Contains(sentinelRoot, StringComparison.Ordinal));

                    var wildcard = wildcardLength == 1 ? "*" : "**";
                    var sentinel = sentinelRoot + wildcard;
                    replacements.Add(sentinel, wildcard);
                    result.Append(sentinel);
                    index += wildcardLength - 1;
                    continue;
                }
            }

            result.Append(value[index]);
        }

        return result.ToString();
    }

    private static bool IsWholeGenericArgument(string value, int start, int length)
    {
        var before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(value[before]))
        {
            before--;
        }

        var after = start + length;
        while (after < value.Length && char.IsWhiteSpace(value[after]))
        {
            after++;
        }

        return before >= 0 &&
               after < value.Length &&
               value[before] is '<' or ',' &&
               value[after] is '>' or ',';
    }

    private static ExecutableSegmentSelector RestoreWholeTypeWildcards(
        ExecutableSegmentSelector selector,
        IReadOnlyDictionary<string, string> replacements)
    {
        if (selector is not SpecialExecutableSegmentSelector
            {
                Member.ContainingTypePattern: { } containingTypePattern,
            } special)
        {
            return selector;
        }

        var restoredPattern = containingTypePattern;
        foreach (var (sentinel, wildcard) in replacements)
        {
            restoredPattern = restoredPattern.Replace(
                sentinel,
                wildcard,
                StringComparison.Ordinal);
        }

        if (string.Equals(restoredPattern, containingTypePattern, StringComparison.Ordinal))
        {
            return selector;
        }

        return special with
        {
            Member = special.Member! with
            {
                ContainingTypePattern = restoredPattern,
                ContainingType = null,
            },
        };
    }

    private static CompiledFileCondition CompileFileCondition(
        TypedCondition condition,
        CaseMode caseMode,
        TimeSpan regexTimeout)
    {
        var comparison = ToComparison(caseMode);
        return condition.Syntax switch
        {
            ConditionSyntax.Literal => new CompiledFileCondition(
                condition.Value.Replace('\\', '/'),
                null,
                false,
                comparison),
            ConditionSyntax.Glob => new CompiledFileCondition(
                condition.Value,
                null,
                true,
                comparison),
            ConditionSyntax.Regex => new CompiledFileCondition(
                null,
                new CompiledRegex(
                    $"\\A(?:{condition.Value})\\z",
                    comparison,
                    regexTimeout),
                false,
                comparison),
            _ => throw new ArgumentOutOfRangeException(
                nameof(condition),
                condition.Syntax,
                "Undefined condition syntax."),
        };
    }

    private static StringComparison ToComparison(CaseMode mode) =>
        mode switch
        {
            CaseMode.Strict => StringComparison.Ordinal,
            CaseMode.Ignore => StringComparison.OrdinalIgnoreCase,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Undefined case mode."),
        };

    private static void ValidateCaseMode(CaseMode mode, string parameterName)
    {
        if (mode is not CaseMode.Strict and not CaseMode.Ignore)
        {
            throw new ArgumentOutOfRangeException(parameterName, mode, "Undefined case mode.");
        }
    }

    internal sealed class CompiledPathCondition(
        CompiledRegex? regex,
        CompiledHierarchySelector? hierarchy,
        CompiledExecutableSelector? executable,
        bool isMethod,
        bool isType,
        StringComparison comparison,
        StringComparison namespaceComparison,
        StringComparison typeComparison)
    {
        public bool Matches(
            StoredSymbol symbol,
            SymbolPathData path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool result;
            if (regex is not null)
            {
                var value = isMethod
                    ? path.ExecutableDisplayPath
                    : isType
                        ? path.TypeDisplayPath
                        : path.NamespacePath;
                result = regex.IsMatch(value, cancellationToken);
            }
            else if (isMethod)
            {
                result = executable!.Matches(
                    path,
                    comparison,
                    namespaceComparison,
                    typeComparison,
                    cancellationToken);
            }
            else if (isType)
            {
                result = hierarchy!.MatchesType(path, comparison, cancellationToken);
            }
            else
            {
                result = hierarchy!.MatchesNamespace(path, comparison, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    internal sealed class CompiledHierarchySelector(
        HierarchySelector selector,
        bool namespaceGenericMismatch)
    {
        public bool MatchesNamespace(
            SymbolPathData path,
            StringComparison comparison,
            CancellationToken cancellationToken)
        {
            if (namespaceGenericMismatch)
            {
                return false;
            }

            var pattern = selector.Segments;
            IReadOnlyList<string> candidate;
            try
            {
                candidate = SplitComponents(path.NamespacePath)
                    .Select(NormalizeIdentifier)
                    .ToArray();
            }
            catch (SymbolQueryParseException exception)
            {
                throw new InvalidOperationException(
                    "Stored namespace path data is malformed.",
                    exception);
            }

            return StructuralGlobMatcher.MatchSequence(
                pattern,
                candidate,
                segment => segment.IdentifierPattern == "**" && segment.GenericArity == 0,
                (segment, value, state) =>
                    StructuralGlobMatcher.MatchComponent(
                        segment.IdentifierPattern,
                        value,
                        state.Comparison,
                        state.CancellationToken),
                new MatcherState(comparison, cancellationToken),
                cancellationToken);
        }

        public bool MatchesType(
            SymbolPathData path,
            StringComparison comparison,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(path.TypeDisplayPath) || string.IsNullOrEmpty(path.TypeIdentityPath))
            {
                throw new InvalidOperationException("Stored type paths must be non-empty.");
            }

            IReadOnlyList<string> display;
            IReadOnlyList<string> identity;
            try
            {
                display = BalancedTextScanner.SplitTopLevel(path.TypeDisplayPath, ".");
                identity = BalancedTextScanner.SplitTopLevel(path.TypeIdentityPath, ".");
            }
            catch (SymbolQueryParseException exception)
            {
                throw new InvalidOperationException("Stored type path data is malformed.", exception);
            }
            if (display.Count != identity.Count)
            {
                throw new InvalidOperationException(
                    "Stored type display and identity paths have different component counts.");
            }

            var candidate = new TypeCandidate[display.Count];
            for (var index = 0; index < display.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    candidate[index] = DecodeTypeCandidate(display[index], identity[index]);
                }
                catch (SymbolQueryParseException exception)
                {
                    throw new InvalidOperationException("Stored type component data is malformed.", exception);
                }
            }

            return StructuralGlobMatcher.MatchSequence(
                selector.Segments,
                candidate,
                segment => segment.IdentifierPattern == "**" && segment.GenericArity == 0,
                (segment, value, state) => MatchTypeComponent(segment, value, state),
                new TypeMatcherState(comparison, cancellationToken),
                cancellationToken);
        }

        private static bool MatchTypeComponent(
            HierarchySegmentSelector selector,
            TypeCandidate candidate,
            TypeMatcherState state)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var identifierMatches = StructuralGlobMatcher.MatchComponent(
                selector.IdentifierPattern,
                candidate.DisplayIdentifier,
                state.Comparison,
                state.CancellationToken);
            if (!identifierMatches)
            {
                return false;
            }

            if (selector.GenericArity != 0)
            {
                return selector.GenericArity == candidate.GenericArity;
            }

            var wildcardIdentifier = selector.PatternMode == PatternMode.Glob &&
                selector.IdentifierPattern.Contains('*');
            return wildcardIdentifier || candidate.GenericArity == 0;
        }

        private static TypeCandidate DecodeTypeCandidate(string display, string identity)
        {
            var displayOpen = display.IndexOf('<');
            var displayIdentifier = displayOpen < 0 ? display : display[..displayOpen];
            var displayArity = 0;
            if (displayOpen >= 0)
            {
                var close = BalancedTextScanner.FindMatchingDelimiter(display, displayOpen);
                if (!string.IsNullOrWhiteSpace(display[(close + 1)..]))
                {
                    throw new InvalidOperationException("Stored type display path has trailing component text.");
                }

                displayArity = BalancedTextScanner.SplitTopLevel(
                    display[(displayOpen + 1)..close],
                    ",").Count;
            }

            var identityTick = identity.LastIndexOf('`');
            var identityIdentifier = identity;
            var identityArity = 0;
            if (identityTick >= 0)
            {
                identityIdentifier = identity[..identityTick];
                if (!int.TryParse(
                        identity[(identityTick + 1)..],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out identityArity) || identityArity < 0)
                {
                    throw new InvalidOperationException("Stored type identity path has an invalid generic arity.");
                }
            }

            if (displayArity != identityArity)
            {
                throw new InvalidOperationException(
                    "Stored type display and identity components have different generic arities.");
            }

            var normalizedDisplayIdentifier = NormalizeIdentifier(displayIdentifier);
            var normalizedIdentityIdentifier = NormalizeIdentifier(identityIdentifier);
            if (!string.Equals(
                    normalizedDisplayIdentifier,
                    normalizedIdentityIdentifier,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Stored type display and identity components have different identifiers.");
            }

            return new TypeCandidate(
                normalizedDisplayIdentifier,
                normalizedIdentityIdentifier,
                identityArity);
        }
    }

    internal sealed class CompiledExecutableSelector
    {
        private readonly IReadOnlyList<CompiledExecutableSegmentSelector> _selector;

        public CompiledExecutableSelector(IReadOnlyList<ExecutableSegmentSelector> selector)
        {
            ArgumentNullException.ThrowIfNull(selector);
            _selector = CompileSegments(selector);
        }

        public bool Matches(
            SymbolPathData path,
            StringComparison methodComparison,
            StringComparison namespaceComparison,
            StringComparison typeComparison,
            CancellationToken cancellationToken)
        {
            var candidates = DecodeCandidates(path, cancellationToken);
            return StructuralGlobMatcher.MatchSequence(
                _selector,
                candidates,
                IsRecursiveExecutableStar,
                (segment, candidate, state) => MatchExecutableSegment(segment, candidate, state),
                new ExecutableMatcherState(
                    methodComparison,
                    namespaceComparison,
                    typeComparison,
                    cancellationToken),
                cancellationToken);
        }

        private static IReadOnlyList<CompiledExecutableSegmentSelector> CompileSegments(
            IReadOnlyList<ExecutableSegmentSelector> selectors)
        {
            var activePlaceholders =
                new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal);
            var methodOrdinal = 0;
            var compiled = new CompiledExecutableSegmentSelector[selectors.Count];
            for (var index = 0; index < selectors.Count; index++)
            {
                var selector = selectors[index];
                var arity = selector switch
                {
                    NamedExecutableSegmentSelector named => named.Arity,
                    SpecialExecutableSegmentSelector special => special.Arity,
                    _ => null,
                };
                if (arity is not null)
                {
                    foreach (var placeholder in arity.GenericPlaceholders)
                    {
                        activePlaceholders[placeholder] = new CanonicalGenericPlaceholder(
                            CanonicalGenericPlaceholderScope.Method,
                            methodOrdinal++);
                    }
                }

                CompiledContainingTypeGlob? containingTypeGlob = null;
                if (selector is SpecialExecutableSegmentSelector
                    {
                        Member.ContainingTypePattern: { } containingTypePattern,
                        Member.ContainingType: null,
                    })
                {
                    containingTypeGlob = CompiledContainingTypeGlob.Compile(
                        containingTypePattern,
                        activePlaceholders,
                        CanonicalOwnerPairMode.RequireAlignedOuterOwner);
                }

                compiled[index] = new CompiledExecutableSegmentSelector(
                    selector,
                    containingTypeGlob);
            }

            return compiled;
        }

        private static bool IsRecursiveExecutableStar(CompiledExecutableSegmentSelector segment) =>
            segment.Selector is NamedExecutableSegmentSelector named &&
            named.IdentifierPattern == "**" &&
            named.Arity.GenericState == GenericListState.Omitted &&
            named.Arity.ParameterState == ParameterListState.Omitted;

        private static bool MatchExecutableSegment(
            CompiledExecutableSegmentSelector compiledSelector,
            CandidateExecutableSegment candidate,
            ExecutableMatcherState state)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var result = compiledSelector.Selector switch
            {
                NamedExecutableSegmentSelector named => MatchNamed(named, candidate, state),
                SpecialExecutableSegmentSelector special => MatchSpecial(
                    special,
                    compiledSelector.ContainingTypeGlob,
                    candidate,
                    state),
                LambdaExecutableSegmentSelector lambda => candidate.Kind == CandidateSegmentKind.Lambda &&
                    (lambda.Ordinal is null || lambda.Ordinal == candidate.Ordinal),
                AnonymousMethodExecutableSegmentSelector anonymous =>
                    candidate.Kind == CandidateSegmentKind.AnonymousMethod &&
                    (anonymous.Ordinal is null || anonymous.Ordinal == candidate.Ordinal),
                InitializerExecutableSegmentSelector initializer =>
                    candidate.Kind == CandidateSegmentKind.Initializer &&
                    MatchIdentifier(
                        initializer.MemberPattern,
                        initializer.PatternMode,
                        candidate.MemberName!,
                        state.MethodComparison,
                        state.CancellationToken),
                TopLevelStatementsExecutableSegmentSelector =>
                    candidate.Kind == CandidateSegmentKind.TopLevelStatements,
                _ => false,
            };
            state.CancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private static bool MatchNamed(
            NamedExecutableSegmentSelector selector,
            CandidateExecutableSegment candidate,
            ExecutableMatcherState state)
        {
            if (candidate.Kind != CandidateSegmentKind.Named)
            {
                return selector.IdentifierPattern == "*" &&
                    selector.Arity.GenericState == GenericListState.Omitted &&
                    selector.Arity.ParameterState == ParameterListState.Omitted;
            }

            if (!MatchIdentifier(
                    selector.IdentifierPattern,
                    selector.PatternMode,
                    candidate.Identifier!,
                    state.MethodComparison,
                    state.CancellationToken))
            {
                return false;
            }

            return MatchArity(selector.Arity, candidate, state);
        }

        private static bool MatchSpecial(
            SpecialExecutableSegmentSelector selector,
            CompiledContainingTypeGlob? containingTypeGlob,
            CandidateExecutableSegment candidate,
            ExecutableMatcherState state)
        {
            if (candidate.Kind != CandidateSegmentKind.Special ||
                !string.Equals(selector.Tag, candidate.Tag, StringComparison.Ordinal))
            {
                return false;
            }

            if (selector.OperatorToken is not null &&
                !string.Equals(selector.OperatorToken, candidate.OperatorToken, StringComparison.Ordinal))
            {
                return false;
            }

            if (selector.ConversionKind is not null &&
                !string.Equals(selector.ConversionKind, candidate.ConversionKind, StringComparison.Ordinal))
            {
                return false;
            }

            if (selector.ConversionTarget is not null &&
                (candidate.ConversionTarget is null ||
                 !MatchCanonicalType(
                     selector.ConversionTarget,
                     candidate.ConversionTarget,
                     state.NamespaceComparison,
                     state.TypeComparison)))
            {
                return false;
            }

            if (selector.Member is not null &&
                !MatchMember(selector.Member, containingTypeGlob, candidate, state))
            {
                return false;
            }

            return MatchArity(selector.Arity, candidate, state);
        }

        private static bool MatchMember(
            QualifiedMemberSelector selector,
            CompiledContainingTypeGlob? containingTypeGlob,
            CandidateExecutableSegment candidate,
            ExecutableMatcherState state)
        {
            if (!MatchIdentifier(
                    selector.MemberPattern,
                    selector.PatternMode,
                    candidate.MemberName ?? string.Empty,
                    state.MethodComparison,
                    state.CancellationToken))
            {
                return false;
            }

            if (selector.ContainingType is not null)
            {
                return candidate.ContainingType is not null &&
                    MatchCanonicalType(
                        selector.ContainingType,
                        candidate.ContainingType,
                        state.NamespaceComparison,
                        state.TypeComparison);
            }

            if (selector.ContainingTypePattern is null)
            {
                return candidate.ContainingType is null;
            }

            return candidate.ContainingType is not null &&
                (containingTypeGlob ?? throw new InvalidOperationException(
                    "A wildcard containing-type selector was not compiled."))
                .Matches(candidate.ContainingType, state);
        }

        private enum CanonicalOwnerPairMode
        {
            RequireAlignedOuterOwner,
            AllowUnalignedNestedArgument,
        }

        private sealed class CompiledContainingTypeGlob(
            IReadOnlyList<CompiledOwnerPart> parts,
            CanonicalOwnerPairMode canonicalPairMode)
        {
            public static CompiledContainingTypeGlob Compile(
                string pattern,
                IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders,
                CanonicalOwnerPairMode canonicalPairMode)
            {
                ArgumentNullException.ThrowIfNull(pattern);
                ArgumentNullException.ThrowIfNull(genericPlaceholders);
                var normalizedPattern = pattern.Trim();
                if (normalizedPattern.StartsWith("global::", StringComparison.Ordinal))
                {
                    normalizedPattern = normalizedPattern["global::".Length..];
                }

                var rawParts = BalancedTextScanner.SplitTopLevel(normalizedPattern, ".");
                return new CompiledContainingTypeGlob(rawParts
                    .Select(part => CompilePart(part, genericPlaceholders))
                    .ToArray(), canonicalPairMode);
            }

            public bool Matches(
                CanonicalTypeSignature candidate,
                ExecutableMatcherState state)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                var displayText = candidate.DisplayText.Trim();
                if (displayText.StartsWith("global::", StringComparison.Ordinal))
                {
                    displayText = displayText["global::".Length..];
                }

                var displayParts = BalancedTextScanner.SplitTopLevel(displayText, ".");
                var identityText = StripTypeClassification(candidate.IdentityKey);
                var identityBoundary = identityText.IndexOf("::", StringComparison.Ordinal);
                if (identityBoundary < 0 &&
                    canonicalPairMode == CanonicalOwnerPairMode.RequireAlignedOuterOwner)
                {
                    throw new InvalidOperationException(
                        "Stored explicit-interface owner identity has no namespace/type boundary.");
                }

                var identityParts = new List<string>();
                var namespaceCount = 0;
                if (identityBoundary >= 0)
                {
                    var identityNamespace = identityText[..identityBoundary];
                    var identityType = identityText[(identityBoundary + 2)..];
                    if (!string.IsNullOrEmpty(identityNamespace))
                    {
                        var namespaceParts = identityNamespace.Split('.');
                        identityParts.AddRange(namespaceParts);
                        namespaceCount = namespaceParts.Length;
                    }

                    identityParts.AddRange(BalancedTextScanner.SplitTopLevel(identityType, "."));
                }
                else
                {
                    identityParts.Add(identityText);
                }

                if (displayParts.Count != identityParts.Count &&
                    canonicalPairMode == CanonicalOwnerPairMode.RequireAlignedOuterOwner)
                {
                    throw new InvalidOperationException(
                        "Stored explicit-interface owner display and identity paths have different component counts.");
                }

                if (parts.Count != displayParts.Count)
                {
                    return false;
                }

                var identityIsAlignable = identityBoundary >= 0 &&
                    displayParts.Count == identityParts.Count;
                for (var index = 0; index < parts.Count; index++)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var comparison = identityIsAlignable && index < namespaceCount
                        ? state.NamespaceComparison
                        : state.TypeComparison;
                    if (!parts[index].Matches(
                            displayParts[index],
                            identityIsAlignable ? identityParts[index] : null,
                            comparison,
                            state))
                    {
                        return false;
                    }
                }

                state.CancellationToken.ThrowIfCancellationRequested();
                return true;
            }

            private static CompiledOwnerPart CompilePart(
                string text,
                IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
            {
                var part = text.Trim();
                var genericOpen = part.IndexOf('<');
                var identifierPattern = NormalizeIdentifier(
                    genericOpen < 0 ? part : part[..genericOpen]);
                if (genericOpen < 0)
                {
                    return new CompiledOwnerPart(identifierPattern, null);
                }

                var genericClose = BalancedTextScanner.FindMatchingDelimiter(part, genericOpen);
                if (!string.IsNullOrWhiteSpace(part[(genericClose + 1)..]))
                {
                    throw new SymbolQueryParseException(
                        "Containing interface type pattern has trailing text.");
                }

                var arguments = BalancedTextScanner.SplitTopLevel(
                        part[(genericOpen + 1)..genericClose],
                        ",")
                    .Select(argument => CompileArgument(argument.Trim(), genericPlaceholders))
                    .ToArray();
                return new CompiledOwnerPart(identifierPattern, arguments);
            }

            private static CompiledOwnerArgument CompileArgument(
                string pattern,
                IReadOnlyDictionary<string, CanonicalGenericPlaceholder> genericPlaceholders)
            {
                if (pattern is "*" or "**")
                {
                    return new CompiledOwnerArgument(null, null, MatchesAnyType: true);
                }

                if (pattern.Contains('*'))
                {
                    return new CompiledOwnerArgument(
                        null,
                        Compile(
                            pattern,
                            genericPlaceholders,
                            CanonicalOwnerPairMode.AllowUnalignedNestedArgument),
                        MatchesAnyType: false);
                }

                try
                {
                    return new CompiledOwnerArgument(
                        SymbolSignatureCanonicalizer.ParseSelectorType(pattern, genericPlaceholders),
                        null,
                        MatchesAnyType: false);
                }
                catch (ArgumentException exception)
                {
                    throw new SymbolQueryParseException(
                        $"Containing interface type argument '{pattern}' is invalid: {exception.Message}");
                }
            }

            private static string StripTypeClassification(string identity)
            {
                const string valuePrefix = "valuetype:";
                const string referencePrefix = "reftype:";
                if (HasTypeClassificationPrefix(identity, valuePrefix))
                {
                    return identity[valuePrefix.Length..];
                }

                return HasTypeClassificationPrefix(identity, referencePrefix)
                    ? identity[referencePrefix.Length..]
                    : identity;
            }

            private static bool HasTypeClassificationPrefix(string identity, string prefix)
            {
                if (!identity.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }

                var payload = identity.AsSpan(prefix.Length);
                return payload.Length != 0 &&
                       (payload[0] != ':' || payload.StartsWith("::", StringComparison.Ordinal));
            }
        }

        private sealed record CompiledOwnerPart(
            string IdentifierPattern,
            IReadOnlyList<CompiledOwnerArgument>? Arguments)
        {
            public bool Matches(
                string display,
                string? identity,
                StringComparison comparison,
                ExecutableMatcherState state)
            {
                var displayPart = DecodeCandidatePart(display);
                CandidateOwnerPart? identityPart = null;
                if (identity is not null)
                {
                    identityPart = DecodeCandidatePart(identity);
                    if (!string.Equals(
                            displayPart.Identifier,
                            identityPart.Identifier,
                            StringComparison.Ordinal) ||
                        displayPart.Arguments.Count != identityPart.Arguments.Count)
                    {
                        throw new InvalidOperationException(
                            "Stored explicit-interface owner display and identity components disagree.");
                    }
                }

                if (!StructuralGlobMatcher.MatchComponent(
                        IdentifierPattern,
                        displayPart.Identifier,
                        comparison,
                        state.CancellationToken))
                {
                    return false;
                }

                if (Arguments is null)
                {
                    return IdentifierPattern.Contains('*') || displayPart.Arguments.Count == 0;
                }

                if (Arguments.Count != displayPart.Arguments.Count)
                {
                    return false;
                }

                if (identityPart is null)
                {
                    return false;
                }

                for (var index = 0; index < Arguments.Count; index++)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    var candidate = new CanonicalTypeSignature(
                        identityPart.Arguments[index],
                        displayPart.Arguments[index]);
                    if (!Arguments[index].Matches(candidate, state))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static CandidateOwnerPart DecodeCandidatePart(string text)
            {
                var part = text.Trim();
                var genericOpen = part.IndexOf('<');
                if (genericOpen < 0)
                {
                    return new CandidateOwnerPart(NormalizeIdentifier(part), []);
                }

                var genericClose = BalancedTextScanner.FindMatchingDelimiter(part, genericOpen);
                if (!string.IsNullOrWhiteSpace(part[(genericClose + 1)..]))
                {
                    throw new InvalidOperationException(
                        "Stored explicit-interface owner component has trailing text.");
                }

                var arguments = BalancedTextScanner.SplitTopLevel(
                    part[(genericOpen + 1)..genericClose],
                    ",");
                return new CandidateOwnerPart(
                    NormalizeIdentifier(part[..genericOpen]),
                    arguments);
            }
        }

        private sealed record CompiledOwnerArgument(
            CanonicalTypeSelector? Exact,
            CompiledContainingTypeGlob? Glob,
            bool MatchesAnyType)
        {
            public bool Matches(CanonicalTypeSignature candidate, ExecutableMatcherState state) =>
                MatchesAnyType ||
                (Exact is not null
                    ? MatchCanonicalType(
                        Exact,
                        candidate,
                        state.NamespaceComparison,
                        state.TypeComparison)
                    : (Glob ?? throw new InvalidOperationException(
                        "A containing-type argument has no compiled matcher."))
                    .Matches(candidate, state));
        }

        private sealed record CandidateOwnerPart(
            string Identifier,
            IReadOnlyList<string> Arguments);

        private sealed record CompiledExecutableSegmentSelector(
            ExecutableSegmentSelector Selector,
            CompiledContainingTypeGlob? ContainingTypeGlob);

        private static bool MatchArity(
            CallableAritySelector selector,
            CandidateExecutableSegment candidate,
            ExecutableMatcherState state)
        {
            if (selector.GenericState == GenericListState.Present &&
                selector.GenericPlaceholders.Count != candidate.GenericArity)
            {
                return false;
            }

            if (selector.GenericState == GenericListState.Omitted &&
                selector.ParameterState == ParameterListState.Present &&
                candidate.GenericArity != 0)
            {
                return false;
            }

            if (selector.ParameterState == ParameterListState.Omitted)
            {
                return true;
            }

            if (!candidate.ParameterListPresent || selector.Parameters.Count != candidate.Parameters.Count)
            {
                return false;
            }

            for (var index = 0; index < selector.Parameters.Count; index++)
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                if (selector.ParameterRefKinds[index] != candidate.Parameters[index].RefKind ||
                    !MatchCanonicalType(
                        selector.Parameters[index],
                        candidate.Parameters[index].Type,
                        state.NamespaceComparison,
                        state.TypeComparison))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchCanonicalType(
            CanonicalTypeSelector selector,
            CanonicalTypeSignature candidate,
            StringComparison namespaceComparison,
            StringComparison typeComparison)
        {
            try
            {
                return SymbolSignatureCanonicalizer.IsMatch(
                    selector,
                    candidate,
                    namespaceComparison,
                    typeComparison);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    "Stored canonical type signature data is malformed.",
                    exception);
            }
        }

        private static bool MatchIdentifier(
            string pattern,
            PatternMode mode,
            string candidate,
            StringComparison comparison,
            CancellationToken cancellationToken) =>
            mode == PatternMode.Literal
                ? string.Equals(pattern, candidate, comparison)
                : StructuralGlobMatcher.MatchComponent(pattern, candidate, comparison, cancellationToken);

        private static CandidateExecutableSegment[] DecodeCandidates(
            SymbolPathData path,
            CancellationToken cancellationToken)
        {
            try
            {
                var display = string.IsNullOrEmpty(path.ExecutableDisplayPath)
                    ? []
                    : BalancedTextScanner.SplitTopLevel(path.ExecutableDisplayPath, ".");
                var identity = string.IsNullOrEmpty(path.ExecutableIdentityPath)
                    ? []
                    : BalancedTextScanner.SplitTopLevel(path.ExecutableIdentityPath, ".");
                if (display.Count != identity.Count)
                {
                    throw new InvalidOperationException(
                        "Stored executable display and identity paths have different component counts.");
                }

                var result = new CandidateExecutableSegment[display.Count];
                for (var index = 0; index < display.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result[index] = DecodeCandidate(display[index], identity[index]);
                }

                return result;
            }
            catch (SymbolQueryParseException exception)
            {
                throw new InvalidOperationException(
                    "Stored executable path data is malformed.",
                    exception);
            }
        }

        private static CandidateExecutableSegment DecodeCandidate(string display, string identity)
        {
            if (display.StartsWith("<lambda#", StringComparison.Ordinal) ||
                display.StartsWith("<anonymous-method#", StringComparison.Ordinal) ||
                display.StartsWith("<initializer:", StringComparison.Ordinal) ||
                display == "<top-level-statements>")
            {
                if (display.StartsWith("<lambda#", StringComparison.Ordinal))
                {
                    if (!string.Equals(display, identity, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Stored lambda display and identity differ.");
                    }

                    return new CandidateExecutableSegment(
                        CandidateSegmentKind.Lambda,
                        null,
                        0,
                        false,
                        [],
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        ParseMarkerOrdinal(display, "<lambda#"));
                }

                if (display.StartsWith("<anonymous-method#", StringComparison.Ordinal))
                {
                    if (!string.Equals(display, identity, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Stored anonymous-method display and identity differ.");
                    }

                    return new CandidateExecutableSegment(
                        CandidateSegmentKind.AnonymousMethod,
                        null,
                        0,
                        false,
                        [],
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        ParseMarkerOrdinal(display, "<anonymous-method#"));
                }

                if (display.StartsWith("<initializer:", StringComparison.Ordinal))
                {
                    if (!identity.StartsWith("<initializer:", StringComparison.Ordinal) ||
                        !identity.EndsWith('>'))
                    {
                        throw new InvalidOperationException("Stored initializer display and identity differ.");
                    }

                    var member = NormalizeIdentifier(display["<initializer:".Length..^1]);
                    var identityMember = NormalizeIdentifier(identity["<initializer:".Length..^1]);
                    if (!string.Equals(member, identityMember, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Stored initializer names differ.");
                    }

                    return new CandidateExecutableSegment(
                        CandidateSegmentKind.Initializer,
                        null,
                        0,
                        false,
                        [],
                        member,
                        null,
                        null,
                        null,
                        null,
                        null);
                }

                if (!string.Equals(display, identity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Stored top-level marker display and identity differ.");
                }

                return new CandidateExecutableSegment(
                    CandidateSegmentKind.TopLevelStatements,
                    null,
                    0,
                    false,
                    [],
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);
            }

            var displayCallable = ParseCallableText(display, isIdentity: false);
            var identityCallable = ParseCallableText(identity, isIdentity: true);
            if (displayCallable.GenericArity != identityCallable.GenericArity ||
                displayCallable.ParameterListPresent != identityCallable.ParameterListPresent ||
                displayCallable.Parameters.Count != identityCallable.Parameters.Count)
            {
                throw new InvalidOperationException("Stored executable display and identity signatures differ.");
            }

            var parameters = new CandidateParameter[displayCallable.Parameters.Count];
            for (var index = 0; index < parameters.Length; index++)
            {
                var displayParameter = StripRefPrefix(displayCallable.Parameters[index]);
                var identityParameter = StripRefPrefix(identityCallable.Parameters[index]);
                if (displayParameter.RefKind != identityParameter.RefKind)
                {
                    throw new InvalidOperationException("Stored parameter ref kinds differ between display and identity.");
                }

                parameters[index] = new CandidateParameter(
                    new CanonicalTypeSignature(identityParameter.Type, displayParameter.Type),
                    displayParameter.RefKind);
            }

            if (displayCallable.Base.StartsWith("[", StringComparison.Ordinal))
            {
                return DecodeSpecialCandidate(
                    displayCallable,
                    identityCallable,
                    parameters);
            }

            var displayIdentifier = NormalizeIdentifier(displayCallable.Base);
            var identityIdentifier = identityCallable.Base;
            if (identityIdentifier.EndsWith('`'))
            {
                throw new InvalidOperationException("Stored executable identity has a malformed generic arity.");
            }

            var identityTick = identityIdentifier.LastIndexOf('`');
            if (identityTick >= 0)
            {
                identityIdentifier = identityIdentifier[..identityTick];
            }

            if (!string.Equals(displayIdentifier, NormalizeIdentifier(identityIdentifier), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stored executable display and identity names differ.");
            }

            return new CandidateExecutableSegment(
                CandidateSegmentKind.Named,
                displayIdentifier,
                displayCallable.GenericArity,
                displayCallable.ParameterListPresent,
                parameters,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        private static CandidateExecutableSegment DecodeSpecialCandidate(
            CallableText display,
            CallableText identity,
            IReadOnlyList<CandidateParameter> parameters)
        {
            var displayTag = GetSpecialTag(display.Base);
            var identityTag = GetSpecialTag(identity.Base);
            if (!string.Equals(displayTag, identityTag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stored special callable tags differ.");
            }

            var candidate = new CandidateExecutableSegment(
                CandidateSegmentKind.Special,
                null,
                display.GenericArity,
                display.ParameterListPresent,
                parameters,
                null,
                displayTag,
                null,
                null,
                null,
                null);
            var displayPayload = display.Base[1..^1];
            var identityPayload = identity.Base[1..^1];
            var colon = displayPayload.IndexOf(':');
            if (colon < 0)
            {
                return candidate;
            }

            var tag = displayPayload[..colon];
            var displayValue = displayPayload[(colon + 1)..];
            var identityValue = identityPayload[(identityPayload.IndexOf(':') + 1)..];
            if (tag is "operator" or "checked-operator")
            {
                if (!string.Equals(displayValue, identityValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Stored operator tokens differ.");
                }

                return candidate with { OperatorToken = displayValue };
            }

            if (tag is "conversion" or "checked-conversion")
            {
                var displayParts = displayValue.Split(':', 2);
                var identityParts = identityValue.Split(':', 2);
                if (displayParts.Length != 2 || identityParts.Length != 2 ||
                    !string.Equals(displayParts[0], identityParts[0], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Stored conversion payloads differ.");
                }

                return candidate with
                {
                    ConversionKind = displayParts[0],
                    ConversionTarget = new CanonicalTypeSignature(identityParts[1], displayParts[1]),
                };
            }

            if (tag is "get" or "set" or "init" or "add" or "remove" or "explicit")
            {
                var displayParts = BalancedTextScanner.SplitTopLevel(displayValue, ".");
                var identityParts = BalancedTextScanner.SplitTopLevel(identityValue, ".");
                if (displayParts.Count == 0 || identityParts.Count == 0)
                {
                    throw new InvalidOperationException("Stored member payloads differ.");
                }

                var member = NormalizeIdentifier(displayParts[^1]);
                if (!string.Equals(member, NormalizeIdentifier(identityParts[^1]), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Stored member names differ.");
                }

                if (displayParts.Count == 1)
                {
                    return candidate with { MemberName = member };
                }

                var displayOwner = string.Join('.', displayParts.Take(displayParts.Count - 1));
                var identityOwner = string.Join('.', identityParts.Take(identityParts.Count - 1));
                return candidate with
                {
                    MemberName = member,
                    ContainingType = new CanonicalTypeSignature(identityOwner, displayOwner),
                };
            }

            throw new InvalidOperationException($"Stored special callable tag '{tag}' is unknown.");
        }

        private static CallableText ParseCallableText(string text, bool isIdentity)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Stored executable path contains an empty segment.");
            }

            var baseEnd = text.StartsWith("[", StringComparison.Ordinal)
                ? BalancedTextScanner.FindMatchingDelimiter(text, 0) + 1
                : FindFirst(text, '<', '(');
            if (baseEnd < 0)
            {
                baseEnd = text.Length;
            }

            var baseText = text[..baseEnd];
            var cursor = baseEnd;
            var genericArity = 0;
            if (cursor < text.Length && text[cursor] == '<')
            {
                var close = BalancedTextScanner.FindMatchingDelimiter(text, cursor);
                genericArity = BalancedTextScanner.SplitTopLevel(text[(cursor + 1)..close], ",").Count;
                cursor = close + 1;
            }
            else if (isIdentity)
            {
                var tick = baseText.LastIndexOf('`');
                if (cursor < text.Length && text[cursor] == '`')
                {
                    tick = cursor;
                    var genericEnd = text.IndexOf('(', cursor);
                    if (genericEnd < 0)
                    {
                        genericEnd = text.Length;
                    }

                    if (!int.TryParse(
                            text[(cursor + 1)..genericEnd],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out genericArity))
                    {
                        throw new InvalidOperationException("Stored executable identity has invalid generic arity.");
                    }

                    cursor = genericEnd;
                }

                if (tick >= 0 && cursor == baseEnd)
                {
                    if (!int.TryParse(
                            baseText[(tick + 1)..],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out genericArity))
                    {
                        throw new InvalidOperationException("Stored executable identity has invalid generic arity.");
                    }

                    baseText = baseText[..tick];
                }
            }

            var parameterPresent = false;
            IReadOnlyList<string> parameters = [];
            if (cursor < text.Length && text[cursor] == '(')
            {
                var close = BalancedTextScanner.FindMatchingDelimiter(text, cursor);
                if (!string.IsNullOrWhiteSpace(text[(close + 1)..]))
                {
                    throw new InvalidOperationException("Stored executable segment has trailing text.");
                }

                parameterPresent = true;
                parameters = string.IsNullOrWhiteSpace(text[(cursor + 1)..close])
                    ? []
                    : BalancedTextScanner.SplitTopLevel(text[(cursor + 1)..close], ",");
            }
            else if (cursor != text.Length)
            {
                throw new InvalidOperationException("Stored executable segment has malformed callable suffix.");
            }

            return new CallableText(baseText, genericArity, parameterPresent, parameters);
        }

        private static int FindFirst(string text, char first, char second)
        {
            var firstIndex = text.IndexOf(first);
            var secondIndex = text.IndexOf(second);
            return firstIndex < 0
                ? secondIndex
                : secondIndex < 0
                    ? firstIndex
                    : Math.Min(firstIndex, secondIndex);
        }

        private static (string Type, int RefKind) StripRefPrefix(string parameter)
        {
            var text = parameter.Trim();
            foreach (var (prefix, refKind) in new[]
                     {
                         ("ref readonly ", (int)Microsoft.CodeAnalysis.RefKind.RefReadOnlyParameter),
                         ("ref ", (int)Microsoft.CodeAnalysis.RefKind.Ref),
                         ("out ", (int)Microsoft.CodeAnalysis.RefKind.Out),
                         ("in ", (int)Microsoft.CodeAnalysis.RefKind.In),
                     })
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return (text[prefix.Length..].Trim(), refKind);
                }
            }

            return (text, (int)Microsoft.CodeAnalysis.RefKind.None);
        }

        private static string GetSpecialTag(string text)
        {
            if (!text.StartsWith("[", StringComparison.Ordinal) || !text.EndsWith(']'))
            {
                throw new InvalidOperationException("Stored special callable payload is malformed.");
            }

            var close = text.IndexOf(':');
            return close < 0 ? text[1..^1] : text[1..close];
        }

        private static int ParseMarkerOrdinal(string text, string prefix)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('>') ||
                !int.TryParse(text[prefix.Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) ||
                ordinal <= 0)
            {
                throw new InvalidOperationException("Stored synthetic executable ordinal is malformed.");
            }

            return ordinal;
        }

        private static string NormalizeIdentifier(string text)
        {
            var value = text.Trim();
            return value.StartsWith('@') ? value[1..] : value;
        }
    }

    private readonly record struct MatcherState(
        StringComparison Comparison,
        CancellationToken CancellationToken);

    private readonly record struct TypeMatcherState(
        StringComparison Comparison,
        CancellationToken CancellationToken);

    private readonly record struct ExecutableMatcherState(
        StringComparison MethodComparison,
        StringComparison NamespaceComparison,
        StringComparison TypeComparison,
        CancellationToken CancellationToken);

    private readonly record struct TypeCandidate(
        string DisplayIdentifier,
        string IdentityIdentifier,
        int GenericArity);

    private enum CandidateSegmentKind
    {
        Named,
        Special,
        Lambda,
        AnonymousMethod,
        Initializer,
        TopLevelStatements,
    }

    private sealed record CandidateParameter(CanonicalTypeSignature Type, int RefKind);

    private sealed record CandidateExecutableSegment(
        CandidateSegmentKind Kind,
        string? Identifier,
        int GenericArity,
        bool ParameterListPresent,
        IReadOnlyList<CandidateParameter> Parameters,
        string? MemberName,
        string? Tag,
        string? OperatorToken,
        string? ConversionKind,
        CanonicalTypeSignature? ConversionTarget,
        CanonicalTypeSignature? ContainingType,
        int? Ordinal = null);

    private sealed record CallableText(
        string Base,
        int GenericArity,
        bool ParameterListPresent,
        IReadOnlyList<string> Parameters);

    internal sealed class CompiledFileCondition(
        string? text,
        CompiledRegex? regex,
        bool isGlob,
        StringComparison comparison)
    {
        public bool Matches(StoredDeclaration declaration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = declaration.DocumentPath;
            bool result;
            if (regex is not null)
            {
                result = regex.IsMatch(path, cancellationToken);
            }
            else if (text is null)
            {
                throw new InvalidOperationException("A file condition has no compiled expression.");
            }
            else if (isGlob)
            {
                result = StructuralGlobMatcher.MatchFile(text, path, comparison, cancellationToken);
            }
            else
            {
                result = string.Equals(
                    text,
                    path,
                    comparison);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    internal sealed class CompiledRegex(string expression, StringComparison comparison, TimeSpan timeout)
    {
        private readonly Regex _regex = Create(expression, comparison, timeout);

        public bool IsMatch(string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = _regex.IsMatch(value);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new SymbolQueryParseException(
                    $"Regular expression condition timed out: {exception.Message}");
            }
        }

        private static Regex Create(
            string expression,
            StringComparison comparison,
            TimeSpan timeout)
        {
            var options = RegexOptions.CultureInvariant;
            if (comparison == StringComparison.OrdinalIgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            try
            {
                return new Regex(expression, options, timeout);
            }
            catch (ArgumentException exception)
            {
                throw new SymbolQueryParseException(
                    $"Invalid regular expression condition: {exception.Message}");
            }
        }
    }

    private static string NormalizeIdentifier(string text)
    {
        var value = text.Trim();
        return value.StartsWith('@') ? value[1..] : value;
    }

    private static IReadOnlyList<string> SplitComponents(string value) =>
        string.IsNullOrEmpty(value) ? [] : BalancedTextScanner.SplitTopLevel(value, ".");
}

public sealed class CompiledTypedConditions
{
    private readonly IReadOnlyList<TypedConditionCompiler.CompiledPathCondition> _namespaces;
    private readonly IReadOnlyList<TypedConditionCompiler.CompiledPathCondition> _types;
    private readonly IReadOnlyList<TypedConditionCompiler.CompiledPathCondition> _methods;
    private readonly IReadOnlyList<TypedConditionCompiler.CompiledFileCondition> _files;
    private readonly IReadOnlyList<SourceTextFilter.CompiledSourcePredicate> _includes;
    private readonly IReadOnlyList<SourceTextFilter.CompiledSourcePredicate> _excludes;
    private readonly FunctionTargetFilter _functionFilter;

    internal CompiledTypedConditions(
        IReadOnlyList<TypedConditionCompiler.CompiledPathCondition> namespaces,
        IReadOnlyList<TypedConditionCompiler.CompiledPathCondition> types,
        IReadOnlyList<TypedConditionCompiler.CompiledPathCondition> methods,
        IReadOnlyList<TypedConditionCompiler.CompiledFileCondition> files,
        IReadOnlyList<SourceTextFilter.CompiledSourcePredicate> includes,
        IReadOnlyList<SourceTextFilter.CompiledSourcePredicate> excludes,
        FunctionTargetFilter functionFilter)
    {
        _namespaces = namespaces;
        _types = types;
        _methods = methods;
        _files = files;
        _includes = includes;
        _excludes = excludes;
        _functionFilter = functionFilter;
        HasDeclarationConditions = files.Count != 0 || includes.Count != 0 || excludes.Count != 0;
        RequiresSourceText = includes.Count != 0 || excludes.Count != 0;
    }

    public bool HasDeclarationConditions { get; }

    public bool RequiresSourceText { get; }

    public bool MatchesLogical(StoredSymbol symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        cancellationToken.ThrowIfCancellationRequested();
        var path = symbol.Path ?? throw new InvalidOperationException(
            $"Symbol ID {symbol.Id} has no semantic path data.");

        if (!MatchesAny(_namespaces, symbol, path, cancellationToken) ||
            !MatchesAny(_types, symbol, path, cancellationToken) ||
            !MatchesAny(_methods, symbol, path, cancellationToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var functionMatched = _functionFilter.Matches(symbol);
        cancellationToken.ThrowIfCancellationRequested();
        return functionMatched;
    }

    public bool MatchesDeclaration(
        StoredSymbol symbol,
        StoredDeclaration declaration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(declaration);
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesLogical(symbol, cancellationToken))
        {
            return false;
        }

        if (_files.Count != 0 && !MatchesAnyFile(declaration, cancellationToken))
        {
            return false;
        }

        if (!RequiresSourceText)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        // The source is read only on this branch.  In particular, a file-only
        // request never touches declaration.NormalizedSource.
        return SourceTextFilter.IsMatch(
            declaration.NormalizedSource,
            _includes,
            _excludes,
            cancellationToken);
    }

    private static bool MatchesAny(
        IReadOnlyList<TypedConditionCompiler.CompiledPathCondition> predicates,
        StoredSymbol symbol,
        SymbolPathData path,
        CancellationToken cancellationToken)
    {
        if (predicates.Count == 0)
        {
            return true;
        }

        foreach (var predicate in predicates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matched = predicate.Matches(symbol, path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesAnyFile(
        StoredDeclaration declaration,
        CancellationToken cancellationToken)
    {
        foreach (var predicate in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matched = predicate.Matches(declaration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
