using System.Security.Cryptography;
using System.Text;
using CsIndex.Core.Analysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsIndex.Core.Tests;

public sealed class SourceNormalizerTests
{
    [Fact]
    public void Normalize_RemovesTriviaWithoutJoiningTokensOrChangingLiterals()
    {
        var node = CSharpSyntaxTree.ParseText("""
            class C
            {
                public static int Func()
                {
                    var a = 1; // initialize
                    var text = "/*keep*/ //keep";
                    int/*gap*/value = a;
                    return value;
                }
            }
            """, cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var result = SourceNormalizer.Normalize(node, TestContext.Current.CancellationToken);

        Assert.Equal(
            "public static int Func(){var a=1;var text=\"/*keep*/ //keep\";int value=a;return value;}",
            result.Text);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(result.Text)), result.Hash);
    }

    [Fact]
    public void Normalize_ExcludesDirectivesAndDisabledTextAndPreservesRawStrings()
    {
        const string source = """"
class C
{
    string Run()
    {
#if WINDOWS
        Windows();
#else
        Other();
#endif
        return """
/*keep*/
//keep
""";
    }
}
"""";
        var options = new CSharpParseOptions(preprocessorSymbols: ["WINDOWS"]);
        var tree = CSharpSyntaxTree.ParseText(source, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tree.GetDiagnostics(TestContext.Current.CancellationToken));
        var node = tree
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var result = SourceNormalizer.Normalize(node, TestContext.Current.CancellationToken);

        Assert.Contains("Windows();", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Other();", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"\"\"\n/*keep*/\n//keep\n\"\"\"", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_PreservesCharacterInterpolatedAndInterpolatedRawLiteralTokenText()
    {
        const string source =
            "class C\n" +
            "{\n" +
            "    string Run(int value)\n" +
            "    {\n" +
            "        var character = '/';\n" +
            "        var interpolated = $\"/*{value}*/\";\n" +
            "        return $$\"\"\"\n" +
            "        line {{value}}\n" +
            "        //keep\n" +
            "        \"\"\";\n" +
            "    }\n" +
            "}\n";
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tree.GetDiagnostics(TestContext.Current.CancellationToken));
        var node = tree
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var result = SourceNormalizer.Normalize(node, TestContext.Current.CancellationToken);

        Assert.Contains("'/'", result.Text, StringComparison.Ordinal);
        Assert.Contains("$\"/*{value}*/\"", result.Text, StringComparison.Ordinal);
        Assert.Contains(
            "$$\"\"\"\n        line {{value}}\n        //keep\n        \"\"\"",
            result.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_PreservesInterpolationDelimitersAndExpressionTokenBoundaries()
    {
        const string source =
            "class C\n" +
            "{\n" +
            "    string Run(object value)\n" +
            "    {\n" +
            "        var ordinary = $\"{value is int item}\";\n" +
            "        var raw = $$\"\"\"{{value is int rawItem}}\"\"\";\n" +
            "        return ordinary + raw;\n" +
            "    }\n" +
            "}\n";
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tree.GetDiagnostics(TestContext.Current.CancellationToken));
        var node = tree
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var result = SourceNormalizer.Normalize(node, TestContext.Current.CancellationToken);

        Assert.Contains("$\"{value is int item}\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("$$\"\"\"{{value is int rawItem}}\"\"\"", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeTokens_ObservesCancellationAfterEnumerationHasStarted()
    {
        var node = CSharpSyntaxTree.ParseText(
                "class C { void Run() { One(); Two(); Three(); } }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => SourceNormalizer.NormalizeTokens(
            CancelAfterTwoTokens(node.DescendantTokens(), cancellation),
            cancellation.Token));
    }

    [Fact]
    public void NormalizeTokens_ObservesCancellationAfterPairRelex()
    {
        using var cancellation = new CancellationTokenSource();
        var relexed = false;
        var tokens = new[]
        {
            SyntaxFactory.Identifier($"first{Guid.NewGuid():N}"),
            SyntaxFactory.Identifier($"second{Guid.NewGuid():N}"),
        };

        Assert.Throws<OperationCanceledException>(() => SourceNormalizer.NormalizeTokens(
            tokens,
            cancellation.Token,
            () =>
            {
                relexed = true;
                cancellation.Cancel();
            }));
        Assert.True(relexed);
    }

    private static IEnumerable<Microsoft.CodeAnalysis.SyntaxToken> CancelAfterTwoTokens(
        IEnumerable<Microsoft.CodeAnalysis.SyntaxToken> tokens,
        CancellationTokenSource cancellation)
    {
        var yielded = 0;
        foreach (var token in tokens)
        {
            yield return token;
            if (++yielded == 2)
            {
                cancellation.Cancel();
            }
        }
    }
}
