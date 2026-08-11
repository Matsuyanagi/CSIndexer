using System.Security.Cryptography;
using System.Text;
using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
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
        var rawLiteralText = node.DescendantTokens()
            .Single(token => token.RawKind == (int)SyntaxKind.MultiLineRawStringLiteralToken)
            .Text;

        Assert.Contains("Windows();", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Other();", result.Text, StringComparison.Ordinal);
        Assert.Contains(rawLiteralText, result.Text, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("void M(string[] args) { }", "string[]args")]
    [InlineData("void M(int[,] matrix) { }", "int[,]matrix")]
    [InlineData("void M(int[][] values) { }", "int[][]values")]
    [InlineData("void M(string?[] items) { }", "string?[]items")]
    public void Normalize_OmitsZeroWidthArrayRankTokensFromSeparatorDecisions(
        string member,
        string expectedFragment)
    {
        var result = NormalizeMember(member);

        Assert.Contains(expectedFragment, result.Text, StringComparison.Ordinal);
        Assert.Equal(HashUtilities.Sha256(result.Text), result.Hash);
    }

    [Theory]
    [InlineData("void M() { var values = new int[length + 1]; }", "new int[length+1]")]
    [InlineData("[Obsolete] void M() { }", "[Obsolete]void M(){}")]
    [InlineData("int this[int index] => index;", "this[int index]=>index;")]
    [InlineData("List<int> values = [1, 2, 3];", "List<int>values=[1,2,3];")]
    public void Normalize_PreservesNonArrayBracketForms(string member, string expectedFragment)
    {
        var result = NormalizeMember(member);

        Assert.Contains(expectedFragment, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_PreservesLiteralBracketTextWhileRemovingComments()
    {
        const string member =
            "void M(){var interpolated=$\"[{1}]\";var raw=\"\"\"[kept]\"\"\";/*drop*/var value=1;//drop\n}";

        var result = NormalizeMember(member);

        Assert.Contains("$\"[{1}]\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"\"\"[kept]\"\"\"", result.Text, StringComparison.Ordinal);
        Assert.Contains("var value=1;", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("drop", result.Text, StringComparison.Ordinal);
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

    private static NormalizedSourceData NormalizeMember(string member)
    {
        var root = CSharpSyntaxTree.ParseText(
                $"class C {{ {member} }}",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var node = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Single(candidate => candidate is not ClassDeclarationSyntax);

        return SourceNormalizer.Normalize(node, TestContext.Current.CancellationToken);
    }
}
