using System.Collections.Concurrent;
using CsIndex.Core.Caching;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsIndex.Core.Analysis;

public sealed record NormalizedSourceData(string Text, byte[] Hash);

public static class SourceNormalizer
{
    private static readonly ConcurrentDictionary<(string PreviousText, string CurrentText), bool>
        PairRoundTrips = new();

    public static NormalizedSourceData Normalize(SyntaxNode node)
    {
        var builder = new System.Text.StringBuilder();
        SyntaxToken? previous = null;
        foreach (var token in node.DescendantTokens())
        {
            if (previous is { } preceding && RequiresSeparator(preceding, token))
            {
                builder.Append(' ');
            }

            builder.Append(token.Text);
            previous = token;
        }

        var text = builder.ToString();
        return new NormalizedSourceData(text, HashUtilities.Sha256(text));
    }

    private static bool RequiresSeparator(SyntaxToken previous, SyntaxToken current)
    {
        var key = (previous.Text, current.Text);
        return !PairRoundTrips.GetOrAdd(key, static pair =>
        {
            var tokens = SyntaxFactory.ParseTokens(pair.PreviousText + pair.CurrentText)
                .Where(token => !token.IsMissing && token.RawKind != (int)SyntaxKind.EndOfFileToken)
                .ToArray();
            return tokens.Length == 2 &&
                   tokens[0].Text == pair.PreviousText &&
                   tokens[1].Text == pair.CurrentText;
        });
    }
}
