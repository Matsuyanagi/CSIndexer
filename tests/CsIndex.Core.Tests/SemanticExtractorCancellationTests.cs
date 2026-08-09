using CsIndex.Core.Analysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsIndex.Core.Tests;

public sealed class SemanticExtractorCancellationTests
{
    [Fact]
    public void OrderLambdas_ObservesCancellationDuringEnumeration()
    {
        var lambda = CSharpSyntaxTree.ParseText(
                "class C { void Run() { var action = (int value) => value; } }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Single();
        using var cancellation = new CancellationTokenSource();
        var lambdas = Enumerable.Range(0, 2).Select(index =>
        {
            if (index == 0)
            {
                cancellation.Cancel();
                return lambda;
            }

            throw new InvalidOperationException("Cancellation was not observed before continuing enumeration.");
        });

        Assert.Throws<OperationCanceledException>(() =>
            SemanticExtractor.OrderLambdas(lambdas, cancellation.Token));
    }

    [Fact]
    public void OrderLambdas_ObservesCancellationDuringOrdering()
    {
        var root = CSharpSyntaxTree.ParseText(
                "class C { void Run() { var a = (int value) => value; var b = (int value) => value; var c = (int value) => value; } }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var reversed = root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Reverse();
        using var cancellation = new CancellationTokenSource();
        var comparisons = 0;

        Assert.Throws<OperationCanceledException>(() => SemanticExtractor.OrderLambdas(
            reversed,
            cancellation.Token,
            () =>
            {
                comparisons++;
                cancellation.Cancel();
            }));

        Assert.True(comparisons > 0);
    }
}
