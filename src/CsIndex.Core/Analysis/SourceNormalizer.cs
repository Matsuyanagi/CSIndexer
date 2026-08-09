using System.Collections.Concurrent;
using CsIndex.Core.Caching;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsIndex.Core.Analysis;

public sealed record NormalizedSourceData(string Text, byte[] Hash);

public static class SourceNormalizer
{
    private static readonly ConcurrentDictionary<(string PreviousText, string CurrentText), bool>
        PairRoundTrips = new();

    public static NormalizedSourceData Normalize(
        SyntaxNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        return NormalizeTokens(node.DescendantTokens(), cancellationToken);
    }

    internal static NormalizedSourceData NormalizeTokens(
        IEnumerable<SyntaxToken> tokens,
        CancellationToken cancellationToken,
        Action? afterPairRelex = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var builder = new System.Text.StringBuilder();
        SyntaxToken? previous = null;
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previous is { } preceding &&
                RequiresSeparator(preceding, token, cancellationToken, afterPairRelex))
            {
                builder.Append(' ');
            }

            builder.Append(token.Text);
            previous = token;
        }

        var text = builder.ToString();
        return new NormalizedSourceData(text, HashUtilities.Sha256(text));
    }

    private static bool RequiresSeparator(
        SyntaxToken previous,
        SyntaxToken current,
        CancellationToken cancellationToken,
        Action? afterPairRelex)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanConcatenateWithinSameInterpolatedString(previous, current, cancellationToken))
        {
            return false;
        }

        var key = (previous.Text, current.Text);
        var roundTrips = PairRoundTrips.GetOrAdd(key, static (pair, callback) =>
        {
            var tokens = SyntaxFactory.ParseTokens(pair.PreviousText + pair.CurrentText)
                .Where(token => !token.IsMissing && token.RawKind != (int)SyntaxKind.EndOfFileToken)
                .ToArray();
            var roundTrips = tokens.Length == 2 &&
                             tokens[0].Text == pair.PreviousText &&
                             tokens[1].Text == pair.CurrentText;
            callback?.Invoke();
            return roundTrips;
        }, afterPairRelex);
        cancellationToken.ThrowIfCancellationRequested();
        return !roundTrips;
    }

    private static bool CanConcatenateWithinSameInterpolatedString(
        SyntaxToken previous,
        SyntaxToken current,
        CancellationToken cancellationToken)
    {
        var previousLiteral = FindContainingInterpolatedString(previous, cancellationToken);
        if (previousLiteral is null)
        {
            return false;
        }

        var currentLiteral = FindContainingInterpolatedString(current, cancellationToken);
        if (currentLiteral is null || !AreSameSyntaxNode(previousLiteral, currentLiteral))
        {
            return false;
        }

        var previousInterpolation = FindContainingInterpolation(previous, previousLiteral, cancellationToken);
        var currentInterpolation = FindContainingInterpolation(current, currentLiteral, cancellationToken);
        return !AreBothWithinSameInterpolationExpression(
            previous,
            current,
            previousInterpolation,
            currentInterpolation);
    }

    private static bool AreBothWithinSameInterpolationExpression(
        SyntaxToken previous,
        SyntaxToken current,
        InterpolationSyntax? previousInterpolation,
        InterpolationSyntax? currentInterpolation) =>
        previousInterpolation is not null &&
        currentInterpolation is not null &&
        AreSameSyntaxNode(previousInterpolation, currentInterpolation) &&
        IsWithin(previous, previousInterpolation.Expression) &&
        IsWithin(current, currentInterpolation.Expression);

    private static InterpolatedStringExpressionSyntax? FindContainingInterpolatedString(
        SyntaxToken token,
        CancellationToken cancellationToken)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is InterpolatedStringExpressionSyntax literal)
            {
                return literal;
            }
        }

        return null;
    }

    private static InterpolationSyntax? FindContainingInterpolation(
        SyntaxToken token,
        InterpolatedStringExpressionSyntax literal,
        CancellationToken cancellationToken)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AreSameSyntaxNode(node, literal))
            {
                return null;
            }

            if (node is InterpolationSyntax interpolation)
            {
                return interpolation;
            }
        }

        return null;
    }

    private static bool AreSameSyntaxNode(SyntaxNode left, SyntaxNode right) =>
        left.RawKind == right.RawKind &&
        left.Span == right.Span &&
        ReferenceEquals(left.SyntaxTree, right.SyntaxTree);

    private static bool IsWithin(SyntaxToken token, SyntaxNode node) =>
        token.SpanStart >= node.SpanStart && token.Span.End <= node.Span.End;
}
