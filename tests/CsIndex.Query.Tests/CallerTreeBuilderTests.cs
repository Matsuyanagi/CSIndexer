using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.Query.Tests;

public sealed class CallerTreeBuilderTests
{
    [Fact]
    public void OrderCallers_ObservesCancellationDuringMaterialization()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => CallerTreeBuilder.OrderCallers(
            CancelBeforeYieldingCaller(cancellation),
            cancellation.Token));
    }

    [Fact]
    public void OrderCallers_ObservesCancellationDuringOrdering()
    {
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;

        Assert.Throws<OperationCanceledException>(() => CallerTreeBuilder.OrderCallers(
            [CreateCaller(3, "Example.CallerC()"), CreateCaller(2, "Example.CallerB()"), CreateCaller(1, "Example.CallerA()")],
            cancellation.Token,
            () =>
            {
                comparisons++;
                cancellation.Cancel();
            }));

        Assert.True(comparisons > 0);
    }

    private static IEnumerable<StoredSymbol> CancelBeforeYieldingCaller(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        yield return CreateCaller(1, "Example.Caller()");
    }

    private static StoredSymbol CreateCaller(long id, string displayName) => new StoredSymbol(
        Id: id,
        StableKey: $"symbol-{id}",
        Kind: IndexedSymbolKind.Method,
        Name: "Caller",
        NamespaceName: "Example",
        TypeSimpleName: "Caller",
        TypeMetadataName: "Caller",
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
        DocumentPath: "callers.cs",
        SourceStart: 0,
        SourceLength: 1,
        IsGenerated: false,
        AssemblyName: null,
        Parameters: [],
        TypeKind: null,
        Accessibility: null) with
    {
        Path = new SymbolPathData(
                string.Empty,
                displayName,
                displayName,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                CallablePathSegmentKind.Named),
    };
}
