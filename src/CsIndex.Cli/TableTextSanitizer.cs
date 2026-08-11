using System.Text;

namespace CsIndex.Cli;

internal static class TableTextSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var sanitized = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
            {
                sanitized.Append(' ');
                index++;
                continue;
            }

            sanitized.Append(character is '\t' or '\r' or '\n' or '\u0085' or '\u2028' or '\u2029'
                ? ' '
                : character);
        }

        return sanitized.ToString();
    }
}
