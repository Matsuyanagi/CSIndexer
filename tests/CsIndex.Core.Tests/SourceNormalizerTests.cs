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

        var result = SourceNormalizer.Normalize(node);

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

        var result = SourceNormalizer.Normalize(node);

        Assert.Contains("Windows();", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Other();", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"\"\"\n/*keep*/\n//keep\n\"\"\"", result.Text, StringComparison.Ordinal);
    }
}
