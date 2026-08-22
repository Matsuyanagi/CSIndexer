using CsIndex.Core.Model;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Query.Tests;

public sealed class SymbolCanonicalComparerTests
{
    [Fact]
    public void Compare_UsesEachLogicalKeyIndependently()
    {
        AssertSymbolBefore(
            CreateSymbol(1, "namespace-a", "A", "T", "E", "same.cs", 1),
            CreateSymbol(2, "namespace-b", "B", "T", "E", "same.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "type-a", "N", "A", "E", "same.cs", 1),
            CreateSymbol(2, "type-b", "N", "B", "E", "same.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "executable-a", "N", "T", "A", "same.cs", 1),
            CreateSymbol(2, "executable-b", "N", "T", "B", "same.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "path-a", "N", "T", "E", "a.cs", 1),
            CreateSymbol(2, "path-b", "N", "T", "E", "b.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "start-a", "N", "T", "E", "same.cs", 1),
            CreateSymbol(2, "start-b", "N", "T", "E", "same.cs", 2));

        var stableA = CreateSymbol(1, "stable-a", "N", "T", "E", "same.cs", 1);
        var stableB = CreateSymbol(2, "stable-b", "N", "T", "E", "same.cs", 1);
        Assert.True(SymbolCanonicalComparer.Instance.Compare(stableA, stableB) < 0);

        var expected = new[] { stableA, stableB };
        var random = new Random(0x7A17);
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var shuffled = expected.OrderBy(_ => random.Next()).ToArray();
            var actual = SymbolCanonicalComparer.OrderSymbols(shuffled, CancellationToken.None);
            Assert.Equal(expected.Select(symbol => symbol.StableKey), actual.Select(symbol => symbol.StableKey));
        }
    }

    [Fact]
    public void Compare_UsesNullAndEmptyMetadataPathsAndStartsLast()
    {
        var concretePath = CreateSymbol(1, "concrete-path", "N", "T", "E", "source.cs", 1);
        var emptyPath = CreateSymbol(2, "empty-path", "N", "T", "E", string.Empty, 1);
        var missingPath = CreateSymbol(3, "missing-path", "N", "T", "E", null, 1);
        Assert.True(SymbolCanonicalComparer.Instance.Compare(concretePath, emptyPath) < 0);
        Assert.True(SymbolCanonicalComparer.Instance.Compare(concretePath, missingPath) < 0);

        var concreteStart = CreateSymbol(4, "concrete-start", "N", "T", "E", "source.cs", 1);
        var missingStart = CreateSymbol(5, "missing-start", "N", "T", "E", "source.cs", null);
        Assert.True(SymbolCanonicalComparer.Instance.Compare(concreteStart, missingStart) < 0);
    }

    [Fact]
    public void Compare_RejectsSymbolsWithoutSemanticPathsEvenWhenTheyAreTheSameReference()
    {
        var missing = CreateSymbol(1, "missing", "N", "T", "E", "source.cs", 1) with { Path = null };

        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.Instance.Compare(missing, missing));
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderSymbols([missing], CancellationToken.None));
    }

    [Fact]
    public void CompareDefinitionDeclarations_UsesRolePathStartAndIdThenRejectsUnknownRole()
    {
        var definition = CreateDeclaration(20, DeclarationRole.PartialDefinition, "z.cs", 20);
        var implementation = CreateDeclaration(10, DeclarationRole.PartialImplementation, "a.cs", 1);
        var ordinary = CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1);
        Assert.True(SymbolCanonicalComparer.CompareDefinitionDeclarations(definition, implementation) < 0);
        Assert.True(SymbolCanonicalComparer.CompareDefinitionDeclarations(implementation, ordinary) < 0);

        Assert.True(SymbolCanonicalComparer.CompareDefinitionDeclarations(
            CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1),
            CreateDeclaration(2, DeclarationRole.Ordinary, "b.cs", 1)) < 0);
        Assert.True(SymbolCanonicalComparer.CompareDefinitionDeclarations(
            CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1),
            CreateDeclaration(2, DeclarationRole.Ordinary, "a.cs", 2)) < 0);
        Assert.True(SymbolCanonicalComparer.CompareDefinitionDeclarations(
            CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1),
            CreateDeclaration(2, DeclarationRole.Ordinary, "a.cs", 1)) < 0);

        var unknown = CreateDeclaration(1, (DeclarationRole)99, "a.cs", 1);
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.CompareDefinitionDeclarations(unknown, ordinary));
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.CompareDefinitionDeclarations(ordinary, unknown));
    }

    [Fact]
    public void CompareSourceMatches_UsesLogicalPathDeclarationPathStartRoleAndId()
    {
        var sameLeft = CreateSymbol(1, "same", "A", "T", "E", "z.cs", 9);
        var sameRight = CreateSymbol(2, "same", "A", "T", "E", "z.cs", 9);
        var declaration = CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1);

        var logicalRight = sameRight with
        {
            Path = sameRight.Path! with { NamespacePath = "B" },
        };
        Assert.True(SymbolCanonicalComparer.CompareSourceMatches(
            sameLeft,
            declaration,
            logicalRight,
            declaration) < 0);

        var pathA = CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 9);
        var pathB = CreateDeclaration(2, DeclarationRole.Ordinary, "b.cs", 1);
        Assert.True(SymbolCanonicalComparer.CompareSourceMatches(sameLeft, pathA, sameLeft, pathB) < 0);

        var startA = CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1);
        var startB = CreateDeclaration(2, DeclarationRole.Ordinary, "a.cs", 2);
        Assert.True(SymbolCanonicalComparer.CompareSourceMatches(sameLeft, startA, sameLeft, startB) < 0);

        var roleDefinition = CreateDeclaration(1, DeclarationRole.PartialDefinition, "a.cs", 1);
        var roleImplementation = CreateDeclaration(2, DeclarationRole.PartialImplementation, "a.cs", 1);
        Assert.True(SymbolCanonicalComparer.CompareSourceMatches(sameLeft, roleDefinition, sameLeft, roleImplementation) < 0);

        var idA = CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1);
        var idB = CreateDeclaration(2, DeclarationRole.Ordinary, "a.cs", 1);
        Assert.True(SymbolCanonicalComparer.CompareSourceMatches(sameLeft, idA, sameLeft, idB) < 0);

        var unknown = CreateDeclaration(1, (DeclarationRole)99, "a.cs", 1);
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.CompareSourceMatches(
            sameLeft,
            unknown,
            sameLeft,
            declaration));
    }

    [Fact]
    public void OrderingHelpersPrevalidateSingleDefinitionSourceAndCallerTreeEntries()
    {
        var unknown = CreateDeclaration(1, (DeclarationRole)99, "a.cs", 1);
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderDefinitionDeclarations(
            [unknown],
            CancellationToken.None));

        var source = CreateSymbol(1, "source", "N", "T", "E", "a.cs", 1);
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderSourceMatches(
            [(source, unknown)],
            CancellationToken.None));

        var missingPath = new CallerTreeNode(
            source with { Path = null },
            0);
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderCallerTreeNodes(
            [missingPath],
            CancellationToken.None));
    }

    [Fact]
    public void OrderCalls_UsesCallerDefinitionTargetResolutionAndAllCallKeys()
    {
        var callerA = CreateSymbol(1, "caller-a", "A", "T", "E", "same.cs", 1);
        var callerB = CreateSymbol(2, "caller-b", "B", "T", "E", "same.cs", 1);
        var definitionA = CreateSymbol(3, "definition-a", "A", "T", "E", "same.cs", 1);
        var definitionB = CreateSymbol(4, "definition-b", "B", "T", "E", "same.cs", 1);
        var calleeA = CreateSymbol(5, "callee-a", "A", "T", "E", "same.cs", 1);
        var calleeB = CreateSymbol(6, "callee-b", "B", "T", "E", "same.cs", 1);
        var symbols = new[] { callerA, callerB, definitionA, definitionB, calleeA, calleeB }
            .ToDictionary(symbol => symbol.Id);

        AssertCallBefore(
            CreateCall(1, callerA.Id, calleeB.Id, definitionA.Id),
            CreateCall(2, callerB.Id, calleeA.Id, definitionB.Id),
            symbols);
        AssertCallBefore(
            CreateCall(1, callerA.Id, calleeB.Id, definitionA.Id),
            CreateCall(2, callerA.Id, calleeA.Id, definitionB.Id),
            symbols);
        AssertCallBefore(
            CreateCall(1, callerA.Id, calleeA.Id, definitionA.Id),
            CreateCall(2, callerA.Id, null, null),
            symbols);
        AssertCallBefore(
            CreateCall(1, callerA.Id, calleeA.Id, null),
            CreateCall(2, callerA.Id, calleeB.Id, null),
            symbols);

        var baseCall = CreateCall(1, callerA.Id, calleeA.Id, definitionA.Id, documentPath: "a.cs");
        AssertCallBefore(baseCall, baseCall with { DocumentPath = "b.cs", Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { SourceStart = 2, Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { SourceLength = 2, Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { ReferenceKind = ReferenceKind.ObjectCreation, Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { DispatchKind = DispatchKind.Virtual, Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { ResolutionStatus = ResolutionStatus.Ambiguous, Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { ResolutionReason = ResolutionReason.MissingMetadataReference, Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { AsyncUsageKind = AsyncUsageKind.Awaited, Id = 2 }, symbols);
        AssertCallBefore(baseCall with { UnresolvedName = "name" }, baseCall with { Id = 2 }, symbols);
        AssertCallBefore(baseCall, baseCall with { Id = 2 }, symbols);
    }

    [Fact]
    public void OrderCalls_RejectsEveryMissingNonNullEndpoint()
    {
        var validCaller = CreateSymbol(1, "caller", "N", "T", "E", "same.cs", 1);
        var validCallee = CreateSymbol(2, "callee", "N", "T", "E", "same.cs", 1);
        var validSymbols = new[] { validCaller, validCallee }.ToDictionary(symbol => symbol.Id);
        var cases = new[]
        {
            CreateCall(1, callerId: 99, calleeId: null, definitionId: null),
            CreateCall(2, callerId: validCaller.Id, calleeId: 100, definitionId: null),
            CreateCall(3, callerId: validCaller.Id, calleeId: validCallee.Id, definitionId: 101),
        };

        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderCalls(
            [cases[0]],
            new Dictionary<long, StoredSymbol>(),
            CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderCalls(
            [cases[1]],
            validSymbols,
            CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderCalls(
            [cases[2]],
            validSymbols,
            CancellationToken.None));
    }

    [Fact]
    public void OrderRelations_UsesSourceTargetThenKindAndRejectsMissingEndpoints()
    {
        var sourceA = CreateSymbol(1, "source-a", "A", "T", "E", "a.cs", 1);
        var sourceB = CreateSymbol(2, "source-b", "B", "T", "E", "a.cs", 1);
        var targetA = CreateSymbol(3, "target-a", "A", "T", "E", "a.cs", 1);
        var targetB = CreateSymbol(4, "target-b", "B", "T", "E", "a.cs", 1);
        var symbols = new[] { sourceA, sourceB, targetA, targetB }.ToDictionary(symbol => symbol.Id);
        var relations = new[]
        {
            new StoredRelation(sourceA.Id, targetA.Id, SymbolRelationKind.Overrides),
            new StoredRelation(sourceB.Id, targetA.Id, SymbolRelationKind.Overrides),
            new StoredRelation(sourceA.Id, targetB.Id, SymbolRelationKind.Overrides),
            new StoredRelation(sourceA.Id, targetA.Id, SymbolRelationKind.Inherits),
        };

        var ordered = SymbolCanonicalComparer.OrderRelations(relations, symbols, CancellationToken.None);
        Assert.Equal(
            [(sourceA.Id, targetA.Id, SymbolRelationKind.Inherits),
             (sourceA.Id, targetA.Id, SymbolRelationKind.Overrides),
             (sourceA.Id, targetB.Id, SymbolRelationKind.Overrides),
             (sourceB.Id, targetA.Id, SymbolRelationKind.Overrides)],
            ordered.Select(relation => (relation.SourceSymbolId, relation.TargetSymbolId, relation.Kind)));

        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderRelations(
            [new StoredRelation(99, targetA.Id, SymbolRelationKind.Inherits)], symbols, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderRelations(
            [new StoredRelation(sourceA.Id, 99, SymbolRelationKind.Inherits)], symbols, CancellationToken.None));
    }

    [Fact]
    public void OrderCallerTreeEdges_UsesCoherentParentAndChildKeysAndRejectsMissingNodes()
    {
        var root = new CallerTreeNode(CreateSymbol(1, "root", "N", "T", "Root", "a.cs", 1), 0);
        var parentA = new CallerTreeNode(CreateSymbol(2, "parent-a", "N", "T", "A", "a.cs", 1), 1);
        var parentB = new CallerTreeNode(CreateSymbol(3, "parent-b", "N", "T", "B", "a.cs", 1), 1);
        var childA = new CallerTreeNode(CreateSymbol(4, "child-a", "N", "T", "A", "a.cs", 1), 2);
        var childB = new CallerTreeNode(CreateSymbol(5, "child-b", "N", "T", "B", "a.cs", 1), 2);
        var nodes = new[] { root, parentA, parentB, childA, childB }.ToDictionary(node => node.Symbol.Id);
        var edges = new[]
        {
            new CallerTreeEdge(childB.Symbol.Id, parentB.Symbol.Id),
            new CallerTreeEdge(childA.Symbol.Id, parentB.Symbol.Id),
            new CallerTreeEdge(childB.Symbol.Id, parentA.Symbol.Id),
            new CallerTreeEdge(childA.Symbol.Id, parentA.Symbol.Id),
            new CallerTreeEdge(parentB.Symbol.Id, root.Symbol.Id),
            new CallerTreeEdge(parentA.Symbol.Id, root.Symbol.Id),
        };

        var ordered = SymbolCanonicalComparer.OrderCallerTreeEdges(edges, nodes, CancellationToken.None);
        Assert.Equal(
            [(parentA.Symbol.Id, root.Symbol.Id),
             (parentB.Symbol.Id, root.Symbol.Id),
             (childA.Symbol.Id, parentA.Symbol.Id),
             (childB.Symbol.Id, parentA.Symbol.Id),
             (childA.Symbol.Id, parentB.Symbol.Id),
             (childB.Symbol.Id, parentB.Symbol.Id)],
            ordered.Select(edge => (edge.CallerSymbolId, edge.CalleeSymbolId)));

        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderCallerTreeEdges(
            [new CallerTreeEdge(99, root.Symbol.Id)], nodes, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.OrderCallerTreeEdges(
            [new CallerTreeEdge(childA.Symbol.Id, 99)], nodes, CancellationToken.None));
    }

    [Fact]
    public void OrderingHelpersObserveCancellationDuringMaterializationComparisonAndEdgeOrdering()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => SymbolCanonicalComparer.OrderSymbols(
            CancelBeforeYielding(cancellation), cancellation.Token));

        using var duringComparison = new CancellationTokenSource();
        var comparisons = 0;
        Assert.Throws<OperationCanceledException>(() => SymbolCanonicalComparer.OrderSymbols(
            [CreateSymbol(1, "a", "A", "T", "E", "a.cs", 1), CreateSymbol(2, "b", "B", "T", "E", "a.cs", 1)],
            duringComparison.Token,
            () =>
            {
                comparisons++;
                duringComparison.Cancel();
            }));
        Assert.True(comparisons > 0);

        using var duringEdges = new CancellationTokenSource();
        var root = new CallerTreeNode(CreateSymbol(1, "root", "N", "T", "Root", "a.cs", 1), 0);
        var childA = new CallerTreeNode(CreateSymbol(2, "child-a", "N", "T", "A", "a.cs", 1), 1);
        var childB = new CallerTreeNode(CreateSymbol(3, "child-b", "N", "T", "B", "a.cs", 1), 1);
        var edgeComparisons = 0;
        Assert.Throws<OperationCanceledException>(() => SymbolCanonicalComparer.OrderCallerTreeEdges(
            [new CallerTreeEdge(childB.Symbol.Id, root.Symbol.Id), new CallerTreeEdge(childA.Symbol.Id, root.Symbol.Id)],
            new[] { root, childA, childB }.ToDictionary(node => node.Symbol.Id),
            duringEdges.Token,
            () =>
            {
                edgeComparisons++;
                duringEdges.Cancel();
            }));
        Assert.True(edgeComparisons > 0);
    }

    [Fact]
    public void StyleFormattingDoesNotChangeSemanticStableKeyOrder()
    {
        var symbolA = CreateSymbol(1, "identity-a", "Game", "A", "Run", "same.cs", 1) with
        {
            Path = new SymbolPathData("Game", "z.Display", "A", "Run()", "Run", "Run()", "Run", CallablePathSegmentKind.Named),
        };
        var symbolB = CreateSymbol(2, "identity-b", "Game", "B", "Run", "same.cs", 1) with
        {
            Path = new SymbolPathData("Game", "a.Display", "B", "Run()", "Run", "Run()", "Run", CallablePathSegmentKind.Named),
        };
        var ordered = SymbolCanonicalComparer.OrderSymbols([symbolB, symbolA], CancellationToken.None);
        Assert.Equal(["identity-a", "identity-b"], ordered.Select(symbol => symbol.StableKey));

        var formatter = new CsIndex.Core.Symbols.SymbolPathFormatter();
        foreach (var style in Enum.GetValues<CsIndex.Core.Symbols.SymbolPathStyle>())
        {
            foreach (var shortNames in new[] { false, true })
            {
                var formatted = ordered.Select(symbol => formatter.Format(
                    symbol.Path!,
                    new CsIndex.Core.Symbols.SymbolPathFormatOptions(style, shortNames))).ToArray();
                Assert.True(StringComparer.Ordinal.Compare(formatted[0], formatted[1]) > 0);
                Assert.Equal(["identity-a", "identity-b"], ordered.Select(symbol => symbol.StableKey));
            }
        }
    }

    [Fact]
    public void StoredRelativePathIsTheDecidingTieBreakAcrossCommonBases()
    {
        var pathA = CreateSymbol(1, "z-stable", "N", "T", "E", "src/a.cs", 1);
        var pathB = CreateSymbol(2, "a-stable", "N", "T", "E", "src/b.cs", 1);
        var ordered = SymbolCanonicalComparer.OrderSymbols([pathB, pathA], CancellationToken.None);
        Assert.Equal(["z-stable", "a-stable"], ordered.Select(symbol => symbol.StableKey));

        var baseOne = Path.Combine("C:\\one", "db");
        var baseTwo = Path.Combine("D:\\two", "db");
        Assert.Equal(
            ["z-stable", "a-stable"],
            ordered.OrderBy(symbol => Path.Combine(baseOne, symbol.PreferredDocumentPath!), StringComparer.Ordinal)
                .Select(symbol => symbol.StableKey));
        Assert.Equal(
            ["z-stable", "a-stable"],
            ordered.OrderBy(symbol => Path.Combine(baseTwo, symbol.PreferredDocumentPath!), StringComparer.Ordinal)
                .Select(symbol => symbol.StableKey));
    }

    private static void AssertSymbolBefore(StoredSymbol before, StoredSymbol after) =>
        Assert.True(
            SymbolCanonicalComparer.Instance.Compare(before, after) < 0,
            $"Expected '{before.StableKey}' before '{after.StableKey}'.");

    private static void AssertCallBefore(
        StoredCall before,
        StoredCall after,
        IReadOnlyDictionary<long, StoredSymbol> symbols)
    {
        var ordered = SymbolCanonicalComparer.OrderCalls([after, before], symbols, CancellationToken.None);
        Assert.Equal([before.Id, after.Id], ordered.Select(call => call.Id));
    }

    private static IEnumerable<StoredSymbol> CancelBeforeYielding(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        yield return CreateSymbol(1, "cancelled", "A", "T", "E", "a.cs", 1);
    }

    private static StoredSymbol CreateSymbol(
        long id,
        string stableKey,
        string namespacePath,
        string typeIdentity,
        string executableIdentity,
        string? preferredDocumentPath,
        int? preferredSourceStart) => new StoredSymbol(
        Id: id,
        StableKey: stableKey,
        Kind: IndexedSymbolKind.Method,
        Name: "Method",
        NamespaceName: namespacePath,
        TypeSimpleName: "Type",
        TypeMetadataName: "Type",
        FullyQualifiedName: stableKey,
        DisplayName: stableKey,
        ContainingSymbolId: null,
        Arity: 0,
        ParameterCount: 0,
        MethodKind: null,
        IsStatic: false,
        IsAbstract: false,
        IsVirtual: false,
        IsOverride: false,
        AsyncRole: AsyncRole.None,
        AsyncInvolvementDepth: null,
        AsyncNextSymbolId: null,
        ReturnTypeKey: null,
        NormalizedSource: null,
        NormalizedSourceHash: null,
        DocumentPath: preferredDocumentPath,
        SourceStart: preferredSourceStart,
        SourceLength: 1,
        IsGenerated: false,
        AssemblyName: null,
        Parameters: [],
        TypeKind: null,
        Accessibility: null) with
        {
            PreferredDocumentPath = preferredDocumentPath,
            PreferredSourceStart = preferredSourceStart,
            Path = new SymbolPathData(
                namespacePath,
                typeIdentity,
                typeIdentity,
                executableIdentity,
                executableIdentity,
                executableIdentity,
                executableIdentity,
                CallablePathSegmentKind.Named),
        };

    private static StoredDeclaration CreateDeclaration(long id, DeclarationRole role, string path, int start) =>
        new(id, $"decl-{id}", 1, 1, path, role, start, 1, null, null, false);

    private static StoredCall CreateCall(
        long id,
        long callerId,
        long? calleeId,
        long? definitionId = null,
        ReferenceKind referenceKind = ReferenceKind.Invocation,
        DispatchKind dispatchKind = DispatchKind.Static,
        ResolutionStatus resolutionStatus = ResolutionStatus.Resolved,
        ResolutionReason resolutionReason = ResolutionReason.None,
        AsyncUsageKind asyncUsageKind = AsyncUsageKind.None,
        string documentPath = "calls.cs",
        int sourceStart = 1,
        int sourceLength = 1,
        string? unresolvedName = null) =>
        new(
            id,
            callerId,
            null,
            calleeId,
            definitionId,
            referenceKind,
            dispatchKind,
            resolutionStatus,
            resolutionReason,
            asyncUsageKind,
            1,
            documentPath,
            sourceStart,
            sourceLength,
            false,
            unresolvedName,
            null);
}
