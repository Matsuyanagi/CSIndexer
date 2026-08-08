using System.Text;
using System.Text.RegularExpressions;
using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.Query.Symbols;

public sealed class SymbolPatternMatcher
{
    private static readonly TimeSpan ProductionRegexTimeout = TimeSpan.FromSeconds(2);

    private readonly IndexedSymbolKind? _kind;
    private readonly CompiledPattern? _pattern;
    private readonly CompiledPattern? _lambdaSuffixPattern;
    private readonly CompiledPattern? _namespacePattern;
    private readonly CompiledPattern? _typePattern;
    private readonly CompiledPattern? _methodPattern;

    public SymbolPatternMatcher(SymbolSearchRequest request, TimeSpan? regexTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = regexTimeout ?? ProductionRegexTimeout;
        _kind = request.Kind;
        _pattern = Compile(request.Pattern, "pattern", request.UseRegex, request.IgnoreCase, timeout);
        _namespacePattern = Compile(
            request.NamespacePattern,
            "namespace pattern",
            request.UseRegex,
            request.IgnoreCase,
            timeout);
        _typePattern = Compile(request.TypePattern, "type pattern", request.UseRegex, request.IgnoreCase, timeout);
        _methodPattern = Compile(
            request.MethodPattern,
            "method pattern",
            request.UseRegex,
            request.IgnoreCase,
            timeout);
        var lambdaMarkerComparison = request.IgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _lambdaSuffixPattern = request.UseRegex || request.Pattern is null ||
                               !request.Pattern.Contains("::<lambda#", lambdaMarkerComparison)
            ? null
            : CompileSuffix(request.Pattern, "pattern", request.IgnoreCase, timeout);
    }

    public bool IsMatch(StoredSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (_kind is not null && symbol.Kind != _kind)
        {
            return false;
        }

        if (!MatchesPattern(_pattern, symbol.DisplayName) &&
            !(symbol.Kind == IndexedSymbolKind.Method &&
              MatchesPattern(_pattern, GetParameterlessMethodDisplay(symbol.DisplayName))) &&
            !(symbol.Kind == IndexedSymbolKind.Lambda &&
              _lambdaSuffixPattern is not null &&
              _lambdaSuffixPattern.IsMatch(symbol.DisplayName)))
        {
            return false;
        }

        return MatchesPattern(_namespacePattern, symbol.NamespaceName) &&
               MatchesPattern(_typePattern, symbol.TypeSimpleName) &&
               MatchesPattern(_methodPattern, symbol.Name);
    }

    private static bool MatchesPattern(CompiledPattern? pattern, string? value) =>
        pattern is null || (value is not null && pattern.IsMatch(value));

    private static string? GetParameterlessMethodDisplay(string displayName)
    {
        var parameterListStart = displayName.LastIndexOf('(');
        return parameterListStart > 0 && displayName.EndsWith(')')
            ? displayName[..parameterListStart]
            : null;
    }

    private static CompiledPattern? Compile(
        string? value,
        string description,
        bool useRegex,
        bool ignoreCase,
        TimeSpan timeout)
    {
        if (value is null)
        {
            return null;
        }

        var expression = useRegex ? value : BuildAnchoredWildcardExpression(value);
        return CreateCompiledPattern(expression, description, ignoreCase, timeout);
    }

    private static CompiledPattern CompileSuffix(
        string value,
        string description,
        bool ignoreCase,
        TimeSpan timeout) =>
        CreateCompiledPattern(BuildWildcardExpression(value) + @"\z", description, ignoreCase, timeout);

    private static CompiledPattern CreateCompiledPattern(
        string expression,
        string description,
        bool ignoreCase,
        TimeSpan timeout)
    {
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            return new CompiledPattern(new Regex(expression, options, timeout), description);
        }
        catch (ArgumentException exception)
        {
            throw new SymbolQueryParseException($"Invalid {description}: {exception.Message}");
        }
    }

    private static string BuildAnchoredWildcardExpression(string value) =>
        @"\A" + BuildWildcardExpression(value) + @"\z";

    private static string BuildWildcardExpression(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            builder.Append(character == '*' ? ".*" : Regex.Escape(character.ToString()));
        }

        return builder.ToString();
    }

    private sealed class CompiledPattern(Regex regex, string description)
    {
        public bool IsMatch(string value)
        {
            try
            {
                return regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new SymbolQueryParseException(
                    $"Regular expression for {description} timed out: {exception.Message}");
            }
        }
    }
}
