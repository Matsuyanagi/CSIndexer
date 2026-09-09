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
            CreateSymbol(1, "z-later", "A", "T", "E", "same.cs", 1),
            CreateSymbol(1, "a-later", "B", "T", "E", "same.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "z-later", "N", "A", "E", "same.cs", 1),
            CreateSymbol(1, "a-later", "N", "B", "E", "same.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "z-later", "N", "T", "A", "same.cs", 1),
            CreateSymbol(1, "a-later", "N", "T", "B", "same.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "z-later", "N", "T", "E", "a.cs", 1),
            CreateSymbol(1, "a-later", "N", "T", "E", "b.cs", 1));
        AssertSymbolBefore(
            CreateSymbol(1, "z-later", "N", "T", "E", "same.cs", 1),
            CreateSymbol(1, "a-later", "N", "T", "E", "same.cs", 2));

        var stableA = CreateSymbol(1, "stable-a", "N", "T", "E", "same.cs", 1);
        var stableB = CreateSymbol(1, "stable-b", "N", "T", "E", "same.cs", 1);
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
        var concretePath = CreateSymbol(1, "z-later", "N", "T", "E", "source.cs", 1);
        var emptyPath = CreateSymbol(1, "a-empty-later", "N", "T", "E", string.Empty, 1);
        var missingPath = CreateSymbol(1, "a-missing-later", "N", "T", "E", null, 1);
        Assert.True(SymbolCanonicalComparer.Instance.Compare(concretePath, emptyPath) < 0);
        Assert.True(SymbolCanonicalComparer.Instance.Compare(concretePath, missingPath) < 0);

        var concreteStart = CreateSymbol(1, "z-later", "N", "T", "E", "source.cs", 1);
        var missingStart = CreateSymbol(1, "a-later", "N", "T", "E", "source.cs", null);
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
        AssertDefinitionBefore(
            CreateDeclaration(20, DeclarationRole.PartialDefinition, "z.cs", 9),
            CreateDeclaration(10, DeclarationRole.PartialImplementation, "a.cs", 1));
        AssertDefinitionBefore(
            CreateDeclaration(20, DeclarationRole.PartialImplementation, "z.cs", 9),
            CreateDeclaration(10, DeclarationRole.Ordinary, "a.cs", 1));
        AssertDefinitionBefore(
            CreateDeclaration(20, DeclarationRole.Ordinary, "a.cs", 9),
            CreateDeclaration(10, DeclarationRole.Ordinary, "b.cs", 1));
        AssertDefinitionBefore(
            CreateDeclaration(20, DeclarationRole.Ordinary, "a.cs", 1),
            CreateDeclaration(10, DeclarationRole.Ordinary, "a.cs", 2));
        AssertDefinitionBefore(
            CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1),
            CreateDeclaration(2, DeclarationRole.Ordinary, "a.cs", 1));

        var unknown = CreateDeclaration(1, (DeclarationRole)99, "a.cs", 1);
        var ordinary = CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1);
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.CompareDefinitionDeclarations(unknown, ordinary));
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.CompareDefinitionDeclarations(ordinary, unknown));
    }

    [Fact]
    public void CompareSourceMatches_UsesLogicalPathDeclarationPathStartRoleAndId()
    {
        var logicalLeft = CreateSymbol(1, "same", "A", "T", "E", "same.cs", 1);
        var logicalRight = CreateSymbol(1, "same", "B", "T", "E", "same.cs", 1);
        var sameSymbol = CreateSymbol(1, "same", "N", "T", "E", "same.cs", 1);

        AssertSourceMatchBefore(
            logicalLeft,
            CreateDeclaration(20, DeclarationRole.Ordinary, "z.cs", 9),
            logicalRight,
            CreateDeclaration(10, DeclarationRole.PartialDefinition, "a.cs", 1));
        AssertSourceMatchBefore(
            sameSymbol,
            CreateDeclaration(20, DeclarationRole.Ordinary, "a.cs", 9),
            sameSymbol,
            CreateDeclaration(10, DeclarationRole.PartialDefinition, "b.cs", 1));
        AssertSourceMatchBefore(
            sameSymbol,
            CreateDeclaration(20, DeclarationRole.Ordinary, "a.cs", 1),
            sameSymbol,
            CreateDeclaration(10, DeclarationRole.PartialDefinition, "a.cs", 2));
        AssertSourceMatchBefore(
            sameSymbol,
            CreateDeclaration(20, DeclarationRole.PartialDefinition, "a.cs", 1),
            sameSymbol,
            CreateDeclaration(10, DeclarationRole.PartialImplementation, "a.cs", 1));
        AssertSourceMatchBefore(
            sameSymbol,
            CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1),
            sameSymbol,
            CreateDeclaration(2, DeclarationRole.Ordinary, "a.cs", 1));

        var unknown = CreateDeclaration(1, (DeclarationRole)99, "a.cs", 1);
        var declaration = CreateDeclaration(1, DeclarationRole.Ordinary, "a.cs", 1);
        Assert.Throws<InvalidOperationException>(() => SymbolCanonicalComparer.CompareSourceMatches(
            sameSymbol,
            unknown,
            sameSymbol,
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
        var callerA = CreateSymbol(2, "z-caller-later", "A", "T", "E", "same.cs", 1);
        var callerB = CreateSymbol(1, "a-caller-later", "B", "T", "E", "same.cs", 1);
        var definitionA = CreateSymbol(4, "z-definition-later", "A", "T", "E", "same.cs", 1);
        var definitionB = CreateSymbol(3, "a-definition-later", "B", "T", "E", "same.cs", 1);
        var calleeA = CreateSymbol(6, "z-callee-later", "A", "T", "E", "same.cs", 1);
        var calleeB = CreateSymbol(5, "a-callee-later", "B", "T", "E", "same.cs", 1);
        var commonCaller = CreateSymbol(7, "common-caller", "N", "T", "Caller", "same.cs", 1);
        var commonTarget = CreateSymbol(8, "common-target", "N", "T", "Target", "same.cs", 1);
        var symbols = new[]
        {
            callerA,
            callerB,
            definitionA,
            definitionB,
            calleeA,
            calleeB,
            commonCaller,
            commonTarget,
        }
            .ToDictionary(symbol => symbol.Id);

        AssertCallBefore(
            CreateCall(20, callerA.Id, commonTarget.Id, commonTarget.Id),
            CreateCall(10, callerB.Id, commonTarget.Id, commonTarget.Id),
            symbols);
        AssertCallBefore(
            CreateCall(20, commonCaller.Id, calleeB.Id, definitionA.Id),
            CreateCall(10, commonCaller.Id, calleeA.Id, definitionB.Id),
            symbols);
        AssertCallBefore(
            CreateCall(20, commonCaller.Id, commonTarget.Id, commonTarget.Id),
            CreateCall(10, commonCaller.Id, null, null),
            symbols);
        AssertCallBefore(
            CreateCall(20, commonCaller.Id, calleeA.Id, null),
            CreateCall(10, commonCaller.Id, calleeB.Id, null),
            symbols);

        var baseCall = CreateCall(
            20,
            commonCaller.Id,
            commonTarget.Id,
            commonTarget.Id,
            documentPath: "a.cs");
        AssertCallBefore(baseCall, baseCall with { DocumentPath = "b.cs", Id = 10 }, symbols);
        AssertCallBefore(baseCall, baseCall with { SourceStart = 2, Id = 10 }, symbols);
        AssertCallBefore(baseCall, baseCall with { SourceLength = 2, Id = 10 }, symbols);
        AssertCallBefore(baseCall, baseCall with { ReferenceKind = ReferenceKind.ObjectCreation, Id = 10 }, symbols);
        AssertCallBefore(baseCall, baseCall with { DispatchKind = DispatchKind.Virtual, Id = 10 }, symbols);
        AssertCallBefore(baseCall, baseCall with { ResolutionStatus = ResolutionStatus.Ambiguous, Id = 10 }, symbols);
        AssertCallBefore(baseCall, baseCall with { ResolutionReason = ResolutionReason.MissingMetadataReference, Id = 10 }, symbols);
        AssertCallBefore(baseCall, baseCall with { AsyncUsageKind = AsyncUsageKind.Awaited, Id = 10 }, symbols);
        AssertCallBefore(
            baseCall with { UnresolvedName = "a-name" },
            baseCall with { UnresolvedName = "b-name", Id = 10 },
            symbols);
        AssertCallBefore(
            baseCall with { UnresolvedName = "name" },
            baseCall with { Id = 10 },
            symbols);
        AssertCallBefore(baseCall with { Id = 1 }, baseCall with { Id = 2 }, symbols);
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
        var sourceA = CreateSymbol(2, "z-source-later", "A", "T", "E", "a.cs", 1);
        var sourceB = CreateSymbol(1, "a-source-later", "B", "T", "E", "a.cs", 1);
        var targetA = CreateSymbol(4, "z-target-later", "A", "T", "E", "a.cs", 1);
        var targetB = CreateSymbol(3, "a-target-later", "B", "T", "E", "a.cs", 1);
        var commonSource = CreateSymbol(5, "common-source", "N", "T", "Source", "a.cs", 1);
        var commonTarget = CreateSymbol(6, "common-target", "N", "T", "Target", "a.cs", 1);
        var symbols = new[] { sourceA, sourceB, targetA, targetB, commonSource, commonTarget }
            .ToDictionary(symbol => symbol.Id);

        AssertRelationBefore(
            new StoredRelation(sourceA.Id, targetB.Id, SymbolRelationKind.Overrides),
            new StoredRelation(sourceB.Id, targetA.Id, SymbolRelationKind.Inherits),
            symbols);
        AssertRelationBefore(
            new StoredRelation(commonSource.Id, targetA.Id, SymbolRelationKind.Overrides),
            new StoredRelation(commonSource.Id, targetB.Id, SymbolRelationKind.Inherits),
            symbols);
        AssertRelationBefore(
            new StoredRelation(commonSource.Id, commonTarget.Id, SymbolRelationKind.Inherits),
            new StoredRelation(commonSource.Id, commonTarget.Id, SymbolRelationKind.Overrides),
            symbols);

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
    public void CallerTreeOrderingUsesSemanticIdentityIndependentOfFormattingStyle()
    {
        var symbolA = CreateSymbol(1, "z-identity-later", "Game", "A", "Run", "same.cs", 1) with
        {
            Path = new SymbolPathData("Game", "z.Display", "A", "Run()", "Run", "Run()", "Run", CallablePathSegmentKind.Named),
        };
        var symbolB = CreateSymbol(2, "a-identity-later", "Game", "B", "Run", "same.cs", 1) with
        {
            Path = new SymbolPathData("Game", "a.Display", "B", "Run()", "Run", "Run()", "Run", CallablePathSegmentKind.Named),
        };
        var ordered = CallerTreeBuilder.OrderCallers([symbolB, symbolA], CancellationToken.None);
        Assert.Equal(["z-identity-later", "a-identity-later"], ordered.Select(symbol => symbol.StableKey));

        var formatter = new CsIndex.Core.Symbols.SymbolPathFormatter();
        foreach (var style in Enum.GetValues<CsIndex.Core.Symbols.SymbolPathStyle>())
        {
            foreach (var shortNames in new[] { false, true })
            {
                var formatted = ordered.Select(symbol => formatter.Format(
                    symbol.Path!,
                    new CsIndex.Core.Symbols.SymbolPathFormatOptions(style, shortNames))).ToArray();
                Assert.True(StringComparer.Ordinal.Compare(formatted[0], formatted[1]) > 0);
                Assert.Equal(["z-identity-later", "a-identity-later"], ordered.Select(symbol => symbol.StableKey));
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

    private static void AssertDefinitionBefore(StoredDeclaration before, StoredDeclaration after) =>
        Assert.True(
            SymbolCanonicalComparer.CompareDefinitionDeclarations(before, after) < 0,
            $"Expected declaration {before.Id} before declaration {after.Id}.");

    private static void AssertSourceMatchBefore(
        StoredSymbol beforeSymbol,
        StoredDeclaration beforeDeclaration,
        StoredSymbol afterSymbol,
        StoredDeclaration afterDeclaration) =>
        Assert.True(
            SymbolCanonicalComparer.CompareSourceMatches(
                beforeSymbol,
                beforeDeclaration,
                afterSymbol,
                afterDeclaration) < 0,
            $"Expected source match {beforeDeclaration.Id} before source match {afterDeclaration.Id}.");

    private static void AssertCallBefore(
        StoredCall before,
        StoredCall after,
        IReadOnlyDictionary<long, StoredSymbol> symbols)
    {
        var ordered = SymbolCanonicalComparer.OrderCalls([after, before], symbols, CancellationToken.None);
        Assert.Same(before, ordered[0]);
        Assert.Same(after, ordered[1]);
    }

    private static void AssertRelationBefore(
        StoredRelation before,
        StoredRelation after,
        IReadOnlyDictionary<long, StoredSymbol> symbols)
    {
        var ordered = SymbolCanonicalComparer.OrderRelations([after, before], symbols, CancellationToken.None);
        Assert.Same(before, ordered[0]);
        Assert.Same(after, ordered[1]);
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
        new(id, $"decl-{id}", 1, 1, path, role, start, 1, 0, 1, null, false);

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
            0,
            1,
            null,
            false,
            unresolvedName,
            null);
}
