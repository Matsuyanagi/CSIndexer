namespace CsIndex.Query.Symbols;

internal static class BalancedTextScanner
{
    internal static IReadOnlyList<string> SplitTopLevel(string text, string separator)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(separator);

        var parts = new List<string>();
        var delimiters = new Stack<char>();
        var squareDepth = 0;
        var partStart = 0;

        for (var index = 0; index < text.Length; index++)
        {
            ProcessDelimiter(text[index], delimiters, ref squareDepth, index);

            if (delimiters.Count == 0 &&
                text.AsSpan(index).StartsWith(separator, StringComparison.Ordinal))
            {
                parts.Add(text[partStart..index]);
                index += separator.Length - 1;
                partStart = index + 1;
            }
        }

        if (delimiters.Count != 0)
        {
            throw ParseError($"unmatched '{delimiters.Peek()}' delimiter");
        }

        parts.Add(text[partStart..]);
        return Array.AsReadOnly(parts.ToArray());
    }

    internal static int FindMatchingDelimiter(string text, int openingIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((uint)openingIndex >= (uint)text.Length || !IsOpening(text[openingIndex]))
        {
            throw new ArgumentOutOfRangeException(nameof(openingIndex));
        }

        var delimiters = new Stack<char>();
        delimiters.Push(text[openingIndex]);
        var squareDepth = text[openingIndex] == '[' ? 1 : 0;

        for (var index = openingIndex + 1; index < text.Length; index++)
        {
            ProcessDelimiter(text[index], delimiters, ref squareDepth, index);
            if (delimiters.Count == 0)
            {
                return index;
            }
        }

        throw ParseError($"unmatched '{text[openingIndex]}' delimiter");
    }

    private static void ProcessDelimiter(
        char character,
        Stack<char> delimiters,
        ref int squareDepth,
        int index)
    {
        if (character == '<' && squareDepth > 0)
        {
            return;
        }

        if (character == '>' && squareDepth > 0)
        {
            return;
        }

        if (IsOpening(character))
        {
            delimiters.Push(character);
            if (character == '[')
            {
                squareDepth++;
            }

            return;
        }

        if (!IsClosing(character))
        {
            return;
        }

        var expectedOpening = character switch
        {
            ')' => '(',
            ']' => '[',
            '>' => '<',
            _ => throw new InvalidOperationException(),
        };

        if (delimiters.Count == 0 || delimiters.Peek() != expectedOpening)
        {
            throw ParseError($"misordered '{character}' delimiter at offset {index}");
        }

        delimiters.Pop();
        if (character == ']')
        {
            squareDepth--;
        }
    }

    private static bool IsOpening(char character) => character is '<' or '(' or '[';

    private static bool IsClosing(char character) => character is '>' or ')' or ']';

    private static SymbolQueryParseException ParseError(string detail) =>
        new($"Invalid symbol path delimiters: {detail}.");
}
