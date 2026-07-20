using System.Text;
using System.Text.RegularExpressions;

namespace CsIndex.Core.Input;

public sealed class GlobMatcher
{
    private readonly Regex[] _patterns;

    public GlobMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(CreateRegex)
            .ToArray();
    }

    public bool IsMatch(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return _patterns.Any(pattern => pattern.IsMatch(normalized));
    }

    private static Regex CreateRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/').TrimStart('/');
        var builder = new StringBuilder("^");
        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            if (current == '*' && index + 1 < normalized.Length && normalized[index + 1] == '*')
            {
                index++;
                if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                {
                    index++;
                    builder.Append("(?:.*/)?");
                }
                else
                {
                    builder.Append(".*");
                }
            }
            else if (current == '*')
            {
                builder.Append("[^/]*");
            }
            else if (current == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(current.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
