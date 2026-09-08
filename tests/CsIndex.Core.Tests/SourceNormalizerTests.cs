using System.Security.Cryptography;
using System.Text;
using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using Microsoft.CodeAnalysis;
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

    [Fact]
    public void NormalizeDocument_EveryIndexedNodeSliceEqualsPerNodeNormalization()
    {
        var root = CSharpSyntaxTree.ParseText(
                SourceFixture,
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var document = SourceNormalizer.NormalizeDocument(root, TestContext.Current.CancellationToken);
        var nodes = root.DescendantNodesAndSelf().Where(node => node is
            BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or
            EqualsValueClauseSyntax or LocalFunctionStatementSyntax or
            AnonymousFunctionExpressionSyntax or GlobalStatementSyntax or
            InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax);

        Assert.NotEmpty(nodes);
        foreach (var node in nodes)
        {
            var range = document.GetRange(node);

            Assert.Equal(SourceNormalizer.Normalize(node, TestContext.Current.CancellationToken).Text,
                document.Slice(range));
            Assert.True(range.Start >= 0);
            Assert.True(range.Length > 0);
            Assert.True(range.Start + range.Length <= document.Text.Length);
        }
    }

    [Fact]
    public void NormalizeDocument_NestedInvocationRangesAreIndependentAndExcludeSemicolon()
    {
        var root = CSharpSyntaxTree.ParseText(
                SourceFixture,
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var document = SourceNormalizer.NormalizeDocument(root, TestContext.Current.CancellationToken);
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var outer = Assert.Single(invocations, invocation => invocation.Expression.ToString() == "B");
        var nested = outer.ArgumentList.Arguments
            .Select(argument => Assert.IsType<InvocationExpressionSyntax>(argument.Expression))
            .ToArray();

        Assert.Equal(2, nested.Length);
        Assert.Equal("A(f)", document.Slice(document.GetRange(nested[0])));
        Assert.Equal("A(10+20)", document.Slice(document.GetRange(nested[1])));
        Assert.Equal("B(A(f),A(10+20))", document.Slice(document.GetRange(outer)));
        Assert.DoesNotContain(';', document.Slice(document.GetRange(outer)));
    }

    [Fact]
    public void NormalizeDocument_PreservesLiteralsAndDropsComments()
    {
        var root = CSharpSyntaxTree.ParseText(
                SourceFixture,
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var document = SourceNormalizer.NormalizeDocument(root, TestContext.Current.CancellationToken);

        Assert.Contains("\"日本語\"", document.Text, StringComparison.Ordinal);
        Assert.Contains("\"😀\"", document.Text, StringComparison.Ordinal);
        Assert.Contains("\"\"\"\n/* keep raw */\n// keep raw\n\"\"\"", document.Text, StringComparison.Ordinal);
        Assert.Contains("$\"value:{1+2}\"", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("outside-ascii", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("外部コメント", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeDocument_ReturnInvocationRangeExcludesOutsideSeparator()
    {
        var root = CSharpSyntaxTree.ParseText(
                "class C { int M() { return M(); } }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var document = SourceNormalizer.NormalizeDocument(root, TestContext.Current.CancellationToken);
        var invocation = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>());

        Assert.Equal("M()", document.Slice(document.GetRange(invocation)));
    }

    [Fact]
    public void NormalizeDocument_GetRangeRejectsForeignAndEmptyNodes()
    {
        var root = CSharpSyntaxTree.ParseText(
                "class C { void M() { } }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var document = SourceNormalizer.NormalizeDocument(root, TestContext.Current.CancellationToken);
        var foreignRoot = CSharpSyntaxTree.ParseText(
                "class C { void M() { } }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var foreignNode = Assert.Single(foreignRoot.DescendantNodes().OfType<MethodDeclarationSyntax>());
        var emptyNode = SyntaxFactory.IdentifierName(SyntaxFactory.MissingToken(SyntaxKind.IdentifierToken));

        Assert.Throws<InvalidOperationException>(() => document.GetRange(foreignNode));
        Assert.Throws<InvalidOperationException>(() => document.GetRange(emptyNode));
    }

    [Fact]
    public void NormalizeDocument_SliceRejectsInvalidRanges()
    {
        var root = CSharpSyntaxTree.ParseText(
                "class C { void M() { } }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var document = SourceNormalizer.NormalizeDocument(root, TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.Slice(new NormalizedSourceRange(-1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.Slice(new NormalizedSourceRange(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.Slice(new NormalizedSourceRange(int.MaxValue, int.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            document.Slice(new NormalizedSourceRange(document.Text.Length, 1)));
    }

    [Fact]
    public void NormalizeDocument_CancellationAfterMappedTokenStopsBeforeCompletion()
    {
        using var source = new CancellationTokenSource();
        var root = CSharpSyntaxTree.ParseText(
                "class C { int M() => 1 + 2; }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);

        Assert.Throws<OperationCanceledException>(() =>
            SourceNormalizer.NormalizeDocumentForTesting(
                root,
                source.Token,
                afterTokenMapped: source.Cancel));
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

    private const string SourceFixture = """"
var topLevel = A(1); // outside-ascii
class Sample
{
    private int _field = 42; /* 外部コメント */
    private string _unicode = "日本語";
    private string _emoji = "😀";
    private string _raw = """
/* keep raw */
// keep raw
""";
    private string _interpolated = $"value:{1 + 2}";

    public int Value
    {
        get => _field;
        set => _field = value;
    }

    public Sample()
    {
        var created = new Sample();
    }

    public int M(int f)
    {
        Func<int, int> lambda = x => M(x);
        Func<int, int> anonymous = delegate(int x) { return M(x); };
        int Local(int x) => M(x);
        B(
            /* 外部コメント */ A(f),
            A(10 + 20)
        );
        return M();
    }
}
"""";
}
