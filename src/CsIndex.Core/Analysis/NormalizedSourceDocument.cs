using System.Collections.Frozen;
using System.Runtime.CompilerServices;
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
    private readonly FrozenDictionary<NormalizedTokenKey, NormalizedTokenSpan> _tokenSpans;

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
        _tokenSpans = tokenSpans.ToFrozenDictionary(
            span => new NormalizedTokenKey(span.SyntaxTree, span.OriginalSpan, span.RawKind),
            static span => span,
            NormalizedTokenKeyComparer.Instance);
    }

    public string Text { get; }

    public byte[] Hash { get; }

    public NormalizedSourceRange GetRange(
        SyntaxNode node,
        CancellationToken cancellationToken = default) =>
        GetRangeCore(node, cancellationToken, afterTokenMapProbe: null);

    internal NormalizedSourceRange GetRangeForTesting(
        SyntaxNode node,
        CancellationToken cancellationToken,
        Action? afterTokenMapProbe) =>
        GetRangeCore(node, cancellationToken, afterTokenMapProbe);

    private NormalizedSourceRange GetRangeCore(
        SyntaxNode node,
        CancellationToken cancellationToken,
        Action? afterTokenMapProbe)
    {
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();

        SyntaxToken? first = null;
        SyntaxToken? last = null;
        foreach (var token in node.DescendantTokens())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.IsMissing || token.Text.Length == 0)
            {
                continue;
            }

            first ??= token;
            last = token;
        }

        if (first is not { } firstToken || last is not { } lastToken)
        {
            throw new InvalidOperationException(
                "The node has no emitted token in the normalized source document.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTokenSpan(firstToken, afterTokenMapProbe, out var firstSpan))
        {
            throw new InvalidOperationException(
                "The node does not belong to the normalized source document.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lastSpan = firstSpan;
        if (!firstToken.Equals(lastToken))
        {
            if (!TryGetTokenSpan(lastToken, afterTokenMapProbe, out lastSpan))
            {
                throw new InvalidOperationException(
                    "The node does not belong to the normalized source document.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        var end = lastSpan.NormalizedEnd;
        return new NormalizedSourceRange(firstSpan.NormalizedStart, checked(end - firstSpan.NormalizedStart));
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

    private bool TryGetTokenSpan(
        SyntaxToken token,
        Action? afterTokenMapProbe,
        out NormalizedTokenSpan span)
    {
        var found = _tokenSpans.TryGetValue(
            new NormalizedTokenKey(token.SyntaxTree, token.Span, token.RawKind),
            out span);
        afterTokenMapProbe?.Invoke();
        return found;
    }

    private readonly record struct NormalizedTokenKey(
        SyntaxTree? SyntaxTree,
        TextSpan OriginalSpan,
        int RawKind);

    private sealed class NormalizedTokenKeyComparer : IEqualityComparer<NormalizedTokenKey>
    {
        internal static NormalizedTokenKeyComparer Instance { get; } = new();

        public bool Equals(NormalizedTokenKey left, NormalizedTokenKey right) =>
            ReferenceEquals(left.SyntaxTree, right.SyntaxTree) &&
            left.OriginalSpan == right.OriginalSpan &&
            left.RawKind == right.RawKind;

        public int GetHashCode(NormalizedTokenKey key) => HashCode.Combine(
            key.SyntaxTree is null ? 0 : RuntimeHelpers.GetHashCode(key.SyntaxTree),
            key.OriginalSpan.Start,
            key.OriginalSpan.Length,
            key.RawKind);
    }
}
