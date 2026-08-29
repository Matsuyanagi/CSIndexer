using System.Globalization;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query.Symbols;

public sealed record ResolvedLogicalRoot(
    StoredSymbol Symbol,
    IReadOnlyList<StoredDeclaration> MatchingDeclarations);

public sealed class SymbolPathResolver
{
    private readonly QueryRepository _repository;

    public SymbolPathResolver(QueryRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public Task<IReadOnlyList<ResolvedLogicalRoot>> ResolveLogicalRootsAsync(
        long profileId,
        SymbolSelectionRequest request,
        bool sourceOnly,
        CancellationToken cancellationToken = default) =>
        ResolveLogicalRootsAsync(
            profileId,
            request,
            sourceOnly,
            includeSourceText: false,
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<ResolvedLogicalRoot>> ResolveLogicalRootsAsync(
        long profileId,
        SymbolSelectionRequest request,
        bool sourceOnly,
        bool includeSourceText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selector = request.Selector is null
            ? null
            : SymbolPathParser.Parse(request.Selector);
        var compiledConditions = TypedConditionCompiler.Compile(
            request with
            {
                FunctionFilter = new FunctionTargetFilter(null, AsyncStatusFilter.All),
            });
        var selectorMatch = selector is null ? null : new CompiledPositionalSelector(selector);
        var hintResult = CreateCandidateHints(selector, request);
        if (hintResult.KindConflict)
        {
            return [];
        }

        var candidates = await _repository.FindLogicalSymbolCandidatesAsync(
            profileId,
            hintResult.Hints,
            sourceOnly,
            cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var uniqueCandidates = candidates
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.First())
            .ToArray();
        var chainSymbols = await _repository.GetSymbolsWithContainingAncestorsAsync(
            profileId,
            uniqueCandidates.Select(candidate => candidate.Id),
            cancellationToken);
        var chainById = chainSymbols.ToDictionary(symbol => symbol.Id);
        var logicalMatches = new List<StoredSymbol>(uniqueCandidates.Length);
        foreach (var candidate in uniqueCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reconstructedPath = ReconstructAndValidatePath(candidate, chainById);
            var reconstructed = candidate with { Path = reconstructedPath };
            if (selectorMatch is not null &&
                !selectorMatch.Matches(reconstructedPath, request.Case, cancellationToken))
            {
                continue;
            }

            if (!compiledConditions.MatchesLogical(reconstructed, cancellationToken) ||
                !MatchesFunctionFilter(request.FunctionFilter, reconstructed))
            {
                continue;
            }

            logicalMatches.Add(reconstructed);
        }

        if (logicalMatches.Count == 0)
        {
            return [];
        }

        var declarations = await _repository.GetDeclarationsAsync(
            profileId,
            logicalMatches.Select(symbol => symbol.Id),
            includeSourceText || compiledConditions.RequiresSourceText,
            cancellationToken);
        var declarationsBySymbol = declarations
            .GroupBy(declaration => declaration.SymbolId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<StoredDeclaration>)group.ToArray());
        var rootsById = new Dictionary<long, ResolvedLogicalRoot>();
        foreach (var symbol in logicalMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var associated = declarationsBySymbol.GetValueOrDefault(symbol.Id) ?? [];
            if (sourceOnly &&
                (symbol.PreferredDeclarationId is null ||
                 symbol.PreferredDocumentPath is null ||
                 associated.Count == 0))
            {
                continue;
            }

            IReadOnlyList<StoredDeclaration> matchingDeclarations;
            if (compiledConditions.HasDeclarationConditions)
            {
                var passing = new List<StoredDeclaration>();
                foreach (var declaration in associated)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (compiledConditions.MatchesDeclaration(symbol, declaration, cancellationToken))
                    {
                        passing.Add(declaration);
                    }
                }

                if (passing.Count == 0)
                {
                    continue;
                }

                matchingDeclarations = SymbolCanonicalComparer.OrderDefinitionDeclarations(
                    passing,
                    cancellationToken);
            }
            else
            {
                matchingDeclarations = SymbolCanonicalComparer.OrderDefinitionDeclarations(
                    associated,
                    cancellationToken);
            }

            rootsById[symbol.Id] = new ResolvedLogicalRoot(symbol, matchingDeclarations);
        }

        var orderedSymbols = SymbolCanonicalComparer.OrderSymbols(
            rootsById.Values.Select(root => root.Symbol),
            cancellationToken);
        return orderedSymbols.Select(symbol => rootsById[symbol.Id]).ToArray();
    }

    public async Task<IReadOnlyList<(StoredSymbol Symbol, StoredDeclaration Declaration)>>
        ResolveDeclarationRowsAsync(
            long profileId,
            SymbolSelectionRequest request,
            CancellationToken cancellationToken = default)
    {
        var roots = await ResolveLogicalRootsAsync(
            profileId,
            request,
            sourceOnly: true,
            includeSourceText: true,
            cancellationToken: cancellationToken);
        var rows = roots.SelectMany(root => root.MatchingDeclarations.Select(
            declaration => (Symbol: root.Symbol, Declaration: declaration)));
        return SymbolCanonicalComparer.OrderSourceMatches(rows, cancellationToken);
    }

    private static CandidateHintResult CreateCandidateHints(
        SymbolPathSelector? selector,
        SymbolSelectionRequest request)
    {
        string? exactLeafName = null;
        IndexedSymbolKind? selectorKind = null;
        if (selector?.ExecutableSegments.LastOrDefault() is { } leaf)
        {
            selectorKind = leaf switch
            {
                NamedExecutableSegmentSelector wildcardNamed when IsWholeUnconstrainedStar(wildcardNamed) => null,
                NamedExecutableSegmentSelector => IndexedSymbolKind.Method,
                SpecialExecutableSegmentSelector => IndexedSymbolKind.Method,
                LambdaExecutableSegmentSelector => IndexedSymbolKind.Lambda,
                AnonymousMethodExecutableSegmentSelector => IndexedSymbolKind.Lambda,
                InitializerExecutableSegmentSelector => IndexedSymbolKind.Initializer,
                TopLevelStatementsExecutableSegmentSelector => IndexedSymbolKind.TopLevelStatements,
                _ => null,
            };
            if (leaf is NamedExecutableSegmentSelector named &&
                request.Case.Method == CaseMode.Strict &&
                !named.IdentifierPattern.Contains('*'))
            {
                exactLeafName = named.IdentifierPattern;
            }
        }

        var filterKind = request.FunctionFilter.Kind is
            IndexedSymbolKind.Method or IndexedSymbolKind.Lambda
                ? request.FunctionFilter.Kind
                : null;
        if (selectorKind is not null && filterKind is not null && selectorKind != filterKind)
        {
            return new CandidateHintResult(null, KindConflict: true);
        }

        var exactKind = selectorKind ?? filterKind;
        var hints = exactLeafName is null && exactKind is null
            ? null
            : new LogicalSymbolCandidateHints(exactLeafName, exactKind);
        return new CandidateHintResult(hints, KindConflict: false);
    }

    private static bool IsWholeUnconstrainedStar(NamedExecutableSegmentSelector selector) =>
        selector.IdentifierPattern is "*" or "**" &&
        selector.Arity.GenericState == GenericListState.Omitted &&
        selector.Arity.ParameterState == ParameterListState.Omitted;

    private static bool MatchesFunctionFilter(FunctionTargetFilter filter, StoredSymbol symbol)
    {
        if (filter.Kind is not null && symbol.Kind != filter.Kind)
        {
            return false;
        }

        return filter.AsyncStatus switch
        {
            AsyncStatusFilter.Async => symbol.AsyncRole != AsyncRole.None,
            AsyncStatusFilter.Sync => symbol.AsyncRole == AsyncRole.None,
            _ => true,
        };
    }

    private static SymbolPathData ReconstructAndValidatePath(
        StoredSymbol leaf,
        IReadOnlyDictionary<long, StoredSymbol> symbolsById)
    {
        var visited = new HashSet<long>();
        var executableNodes = new List<StoredSymbol>();
        var current = leaf;
        while (current.Kind != IndexedSymbolKind.Type)
        {
            if (!visited.Add(current.Id))
            {
                throw InvalidChain(leaf, $"cycle detected at symbol ID {current.Id}");
            }

            if (!IsExecutableKind(current.Kind))
            {
                throw InvalidChain(
                    leaf,
                    $"unexpected non-executable ancestor kind {current.Kind} at symbol ID {current.Id}");
            }

            _ = RequirePath(leaf, current);
            executableNodes.Add(current);
            if (current.ContainingSymbolId is not long containingId)
            {
                throw InvalidChain(
                    leaf,
                    $"symbol ID {current.Id} did not terminate at a containing Type");
            }

            if (!symbolsById.TryGetValue(containingId, out current!))
            {
                throw InvalidChain(
                    leaf,
                    $"missing or cross-profile parent symbol ID {containingId} for child ID {executableNodes[^1].Id}");
            }
        }

        if (!visited.Add(current.Id))
        {
            throw InvalidChain(leaf, $"cycle detected at terminating Type ID {current.Id}");
        }

        var ownerPath = RequirePath(leaf, current);
        executableNodes.Reverse();
        var displayPath = string.Join(
            ".",
            executableNodes.Select(node => RequirePath(leaf, node).SegmentDisplay));
        var identityPath = string.Join(
            ".",
            executableNodes.Select(node => RequirePath(leaf, node).SegmentIdentity));
        var leafPath = RequirePath(leaf, leaf);
        if (!string.Equals(displayPath, leafPath.ExecutableDisplayPath, StringComparison.Ordinal) ||
            !string.Equals(identityPath, leafPath.ExecutableIdentityPath, StringComparison.Ordinal))
        {
            throw InvalidChain(
                leaf,
                $"reconstructed executable segment path '{identityPath}' disagrees with stored complete path '{leafPath.ExecutableIdentityPath}'");
        }

        if (!string.Equals(ownerPath.NamespacePath, leafPath.NamespacePath, StringComparison.Ordinal) ||
            !string.Equals(ownerPath.TypeDisplayPath, leafPath.TypeDisplayPath, StringComparison.Ordinal) ||
            !string.Equals(ownerPath.TypeIdentityPath, leafPath.TypeIdentityPath, StringComparison.Ordinal))
        {
            throw InvalidChain(
                leaf,
                $"terminating Type ID {current.Id} disagrees with the leaf owner path");
        }

        return new SymbolPathData(
            ownerPath.NamespacePath,
            ownerPath.TypeDisplayPath,
            ownerPath.TypeIdentityPath,
            displayPath,
            identityPath,
            leafPath.SegmentDisplay,
            leafPath.SegmentIdentity,
            leafPath.SegmentKind);
    }

    private static SymbolPathData RequirePath(StoredSymbol leaf, StoredSymbol symbol) =>
        symbol.Path ?? throw InvalidChain(
            leaf,
            $"symbol ID {symbol.Id} has no semantic path data");

    private static InvalidOperationException InvalidChain(StoredSymbol leaf, string reason) =>
        new($"Stored containment chain for candidate symbol ID {leaf.Id} is invalid: {reason}.");

    private static bool IsExecutableKind(IndexedSymbolKind kind) => kind is
        IndexedSymbolKind.Method or
        IndexedSymbolKind.Lambda or
        IndexedSymbolKind.Initializer or
        IndexedSymbolKind.TopLevelStatements;

    private static StringComparison ToComparison(CaseMode mode) => mode switch
    {
        CaseMode.Strict => StringComparison.Ordinal,
        CaseMode.Ignore => StringComparison.OrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Undefined case mode."),
    };

    private sealed class CompiledPositionalSelector
    {
        private readonly SymbolPathSelector _selector;
        private readonly TypedConditionCompiler.CompiledHierarchySelector? _namespace;
        private readonly TypedConditionCompiler.CompiledHierarchySelector _type;
        private readonly TypedConditionCompiler.CompiledExecutableSelector _executable;

        internal CompiledPositionalSelector(SymbolPathSelector selector)
        {
            _selector = selector;
            _namespace = selector.Namespace is null
                ? null
                : new TypedConditionCompiler.CompiledHierarchySelector(
                    selector.Namespace,
                    namespaceGenericMismatch: false);
            _type = new TypedConditionCompiler.CompiledHierarchySelector(
                selector.Type,
                namespaceGenericMismatch: false);
            _executable = new TypedConditionCompiler.CompiledExecutableSelector(
                selector.ExecutableSegments);
        }

        internal bool Matches(
            SymbolPathData path,
            SymbolCaseOptions @case,
            CancellationToken cancellationToken)
        {
            var ownerMatches = _selector.Style switch
            {
                SymbolPathStyle.Explicit =>
                    _namespace!.MatchesNamespace(
                        path,
                        ToComparison(@case.Namespace),
                        cancellationToken) &&
                    _type.MatchesType(
                        path,
                        ToComparison(@case.Type),
                        cancellationToken),
                SymbolPathStyle.CSharp => MatchesCsharpOwner(
                    _selector.Type,
                    path,
                    @case,
                    cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Unsupported stored symbol path style {_selector.Style}."),
            };
            return ownerMatches && _executable.Matches(
                path,
                ToComparison(@case.Method),
                ToComparison(@case.Namespace),
                ToComparison(@case.Type),
                cancellationToken);
        }
    }

    private static bool MatchesCsharpOwner(
        HierarchySelector selector,
        SymbolPathData path,
        SymbolCaseOptions @case,
        CancellationToken cancellationToken)
    {
        var candidate = DecodeOwnerCandidate(path, cancellationToken);
        for (var start = 0; start <= candidate.Count; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = candidate.Skip(start).ToArray();
            if (StructuralGlobMatcher.MatchSequence(
                    selector.Segments,
                    suffix,
                    segment => segment.IdentifierPattern == "**" && segment.GenericArity == 0,
                    (pattern, value, state) => MatchOwnerComponent(pattern, value, state),
                    new OwnerMatcherState(@case, cancellationToken),
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchOwnerComponent(
        HierarchySegmentSelector pattern,
        OwnerCandidateComponent candidate,
        OwnerMatcherState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        var comparison = candidate.IsNamespace
            ? ToComparison(state.Case.Namespace)
            : ToComparison(state.Case.Type);
        if (!StructuralGlobMatcher.MatchComponent(
                pattern.IdentifierPattern,
                candidate.Identifier,
                comparison,
                state.CancellationToken))
        {
            return false;
        }

        if (candidate.IsNamespace)
        {
            return pattern.GenericArity == 0;
        }

        if (pattern.GenericArity != 0)
        {
            return pattern.GenericArity == candidate.GenericArity;
        }

        var wildcardIdentifier = pattern.PatternMode == PatternMode.Glob &&
            pattern.IdentifierPattern.Contains('*');
        return wildcardIdentifier || candidate.GenericArity == 0;
    }

    private static IReadOnlyList<OwnerCandidateComponent> DecodeOwnerCandidate(
        SymbolPathData path,
        CancellationToken cancellationToken)
    {
        var result = new List<OwnerCandidateComponent>();
        try
        {
            if (!string.IsNullOrEmpty(path.NamespacePath))
            {
                foreach (var component in BalancedTextScanner.SplitTopLevel(path.NamespacePath, "."))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(new OwnerCandidateComponent(
                        NormalizeIdentifier(component),
                        GenericArity: 0,
                        IsNamespace: true));
                }
            }

            var displayComponents = BalancedTextScanner.SplitTopLevel(path.TypeDisplayPath, ".");
            var identityComponents = BalancedTextScanner.SplitTopLevel(path.TypeIdentityPath, ".");
            if (displayComponents.Count != identityComponents.Count || displayComponents.Count == 0)
            {
                throw new InvalidOperationException(
                    "Stored type display and identity paths have different component counts.");
            }

            for (var index = 0; index < displayComponents.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(DecodeTypeComponent(displayComponents[index], identityComponents[index]));
            }
        }
        catch (SymbolQueryParseException exception)
        {
            throw new InvalidOperationException("Stored owner path data is malformed.", exception);
        }

        return result;
    }

    private static OwnerCandidateComponent DecodeTypeComponent(string display, string identity)
    {
        var displayOpen = display.IndexOf('<');
        var displayIdentifier = displayOpen < 0 ? display : display[..displayOpen];
        var displayArity = 0;
        if (displayOpen >= 0)
        {
            var close = BalancedTextScanner.FindMatchingDelimiter(display, displayOpen);
            if (!string.IsNullOrWhiteSpace(display[(close + 1)..]))
            {
                throw new InvalidOperationException(
                    "Stored type display component has trailing text.");
            }

            displayArity = BalancedTextScanner.SplitTopLevel(
                display[(displayOpen + 1)..close],
                ",").Count;
        }

        var identityTick = identity.LastIndexOf('`');
        var identityIdentifier = identityTick < 0 ? identity : identity[..identityTick];
        var identityArity = 0;
        if (identityTick >= 0 &&
            (!int.TryParse(
                identity[(identityTick + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out identityArity) || identityArity < 0))
        {
            throw new InvalidOperationException(
                "Stored type identity component has invalid generic arity.");
        }

        var normalizedDisplay = NormalizeIdentifier(displayIdentifier);
        var normalizedIdentity = NormalizeIdentifier(identityIdentifier);
        if (displayArity != identityArity ||
            !string.Equals(normalizedDisplay, normalizedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Stored type display and identity components disagree.");
        }

        return new OwnerCandidateComponent(
            normalizedDisplay,
            identityArity,
            IsNamespace: false);
    }

    private static string NormalizeIdentifier(string value) =>
        value.StartsWith('@') ? value[1..] : value;

    private readonly record struct CandidateHintResult(
        LogicalSymbolCandidateHints? Hints,
        bool KindConflict);

    private readonly record struct OwnerMatcherState(
        SymbolCaseOptions Case,
        CancellationToken CancellationToken);

    private readonly record struct OwnerCandidateComponent(
        string Identifier,
        int GenericArity,
        bool IsNamespace);
}
