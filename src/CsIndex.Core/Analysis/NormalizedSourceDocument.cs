using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CsIndex.Core.Analysis;

public readonly record struct NormalizedSourceRange(int Start, int Length);

internal readonly record struct NormalizedTokenSpan(
    SyntaxTree SyntaxTree,
    TextSpan OriginalSpan,
    int RawKind,
    int NormalizedStart,
    int NormalizedLength)
{
    internal int NormalizedEnd => checked(NormalizedStart + NormalizedLength);
}

public sealed class NormalizedSourceDocument
{
    private readonly NormalizedTokenSpan[] _tokenSpans;

    internal NormalizedSourceDocument(
        string text,
        byte[] hash,
        IReadOnlyList<NormalizedTokenSpan> tokenSpans)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(tokenSpans);

        Text = text;
        Hash = hash;
        _tokenSpans = tokenSpans.ToArray();
    }

    public string Text { get; }

    public byte[] Hash { get; }

    public NormalizedSourceRange GetRange(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        NormalizedTokenSpan? first = null;
        NormalizedTokenSpan? last = null;
        foreach (var token in node.DescendantTokens())
        {
            if (token.IsMissing || token.Text.Length == 0)
            {
                continue;
            }

            var emitted = FindToken(token);
            if (emitted is null)
            {
                throw new InvalidOperationException(
                    "The node does not belong to the normalized source document.");
            }

            first ??= emitted;
            last = emitted;
        }

        if (first is not { } firstToken || last is not { } lastToken)
        {
            throw new InvalidOperationException(
                "The node has no emitted token in the normalized source document.");
        }

        var end = lastToken.NormalizedEnd;
        return new NormalizedSourceRange(firstToken.NormalizedStart, checked(end - firstToken.NormalizedStart));
    }

    public string Slice(NormalizedSourceRange range)
    {
        if (range.Start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The range start must be nonnegative.");
        }

        if (range.Length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The range length must be positive.");
        }

        int end;
        try
        {
            end = checked(range.Start + range.Length);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The range end overflowed.");
        }

        if (end > Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The range exceeds the normalized document.");
        }

        return Text.AsSpan(range.Start, range.Length).ToString();
    }

    private NormalizedTokenSpan? FindToken(SyntaxToken token)
    {
        foreach (var candidate in _tokenSpans)
        {
            if (ReferenceEquals(candidate.SyntaxTree, token.SyntaxTree) &&
                candidate.OriginalSpan == token.Span &&
                candidate.RawKind == token.RawKind)
            {
                return candidate;
            }
        }

        return null;
    }
}
