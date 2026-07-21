using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Analysis;

public static class AsyncSymbolClassifier
{
    private static readonly string[] AwaitableTypes =
    [
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task`1",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask`1",
        "Cysharp.Threading.Tasks.UniTask",
        "Cysharp.Threading.Tasks.UniTask`1",
    ];

    private static readonly string[] AsyncEnumerableTypes =
    [
        "System.Collections.Generic.IAsyncEnumerable`1",
        "Cysharp.Threading.Tasks.IUniTaskAsyncEnumerable`1",
    ];

    public static AsyncRole Classify(IMethodSymbol method, Compilation compilation)
    {
        var role = AsyncRole.None;
        if (method.IsAsync)
        {
            role |= AsyncRole.DeclaredAsync;
        }

        if (method.IsAsync && method.ReturnsVoid)
        {
            role |= AsyncRole.AsyncVoid;
        }

        if (method.IsAsync && method.IsIterator)
        {
            role |= AsyncRole.AsyncIterator;
        }

        if (Matches(method.ReturnType, compilation, AwaitableTypes))
        {
            role |= AsyncRole.ReturnsAwaitable;
        }

        if (Matches(method.ReturnType, compilation, AsyncEnumerableTypes))
        {
            role |= AsyncRole.ReturnsAsyncEnumerable;
        }

        if (Matches(method.ReturnType, compilation, ["Cysharp.Threading.Tasks.UniTaskVoid"]))
        {
            role |= AsyncRole.UniTaskVoid;
        }

        return role;
    }

    private static bool Matches(
        ITypeSymbol type,
        Compilation compilation,
        IEnumerable<string> metadataNames)
    {
        var definition = type.OriginalDefinition;
        return metadataNames
            .Select(compilation.GetTypeByMetadataName)
            .Where(candidate => candidate is not null)
            .Any(candidate => SymbolEqualityComparer.Default.Equals(definition, candidate));
    }
}
