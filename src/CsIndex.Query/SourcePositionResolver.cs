namespace CsIndex.Query;

public static class SourcePositionResolver
{
    public static SourcePoint ResolveOffset(string path, int offset)
    {
        var text = File.ReadAllText(path);
        var bounded = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        var column = 1;
        for (var index = 0; index < bounded; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < bounded && text[index + 1] == '\n')
                {
                    index++;
                }

                line++;
                column = 1;
            }
            else if (text[index] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return new SourcePoint(path, line, column, bounded);
    }

    public static int ResolveLineColumn(string path, int line, int column)
    {
        if (line < 1 || column < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "Line and column are one-based positive values.");
        }

        var text = File.ReadAllText(path);
        var currentLine = 1;
        var index = 0;
        while (currentLine < line && index < text.Length)
        {
            if (text[index] == '\r')
            {
                index++;
                if (index < text.Length && text[index] == '\n')
                {
                    index++;
                }

                currentLine++;
            }
            else if (text[index++] == '\n')
            {
                currentLine++;
            }
        }

        if (currentLine != line)
        {
            throw new ArgumentOutOfRangeException(nameof(line), $"Line {line} is outside '{path}'.");
        }

        var result = index + column - 1;
        var lineEnd = text.IndexOfAny(['\r', '\n'], index);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        if (result > lineEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(column), $"Column {column} is outside line {line} in '{path}'.");
        }

        return result;
    }

    public static (string Path, int Line, int Column) ParseAt(string value)
    {
        var last = value.LastIndexOf(':');
        var secondLast = last <= 0 ? -1 : value.LastIndexOf(':', last - 1);
        if (secondLast <= 0 ||
            !int.TryParse(value[(secondLast + 1)..last], out var line) ||
            !int.TryParse(value[(last + 1)..], out var column))
        {
            throw new FormatException("Location must use 'path:line:column'.");
        }

        return (value[..secondLast], line, column);
    }
}
