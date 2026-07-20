namespace CsIndex.Query.Symbols;

public sealed class SymbolQueryParser
{
    public SymbolQuery Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SymbolQueryParseException("Symbol query cannot be empty.");
        }

        var parts = value.Trim().Split("::", StringSplitOptions.None);
        if (parts.Length > 2 || string.IsNullOrWhiteSpace(parts[0]))
        {
            throw new SymbolQueryParseException(
                "Expected '[namespace.]type::method[(parameter-types)]' or '[namespace.]type'.");
        }

        var typeName = parts[0].Trim();
        if (typeName.Contains('<') || typeName.Contains('[') || typeName.Contains('+'))
        {
            throw new SymbolQueryParseException(
                "Nested, generic, and array type query syntax is reserved but not implemented in this phase.");
        }

        var lastDot = typeName.LastIndexOf('.');
        var namespaceName = lastDot < 0 ? null : typeName[..lastDot];
        var typeSimpleName = lastDot < 0 ? typeName : typeName[(lastDot + 1)..];
        if (parts.Length == 1)
        {
            return new SymbolQuery(typeName, namespaceName, typeSimpleName, null, null);
        }

        var methodPart = parts[1].Trim();
        if (methodPart.Length == 0)
        {
            throw new SymbolQueryParseException("Method name cannot be empty after '::'.");
        }

        var open = methodPart.IndexOf('(');
        if (open < 0)
        {
            if (methodPart.Contains(')'))
            {
                throw new SymbolQueryParseException("Method parameter list has an unmatched ')'.");
            }

            return new SymbolQuery(typeName, namespaceName, typeSimpleName, methodPart, null);
        }

        if (!methodPart.EndsWith(')') || methodPart.IndexOf(')', open) != methodPart.Length - 1)
        {
            throw new SymbolQueryParseException("Method parameter list must end with a matching ')'.");
        }

        var methodName = methodPart[..open].Trim();
        if (methodName.Length == 0 || methodName.Contains('<'))
        {
            throw new SymbolQueryParseException(
                "Generic method query syntax is reserved but not implemented in this phase.");
        }

        var parameterText = methodPart[(open + 1)..^1].Trim();
        var parameters = parameterText.Length == 0
            ? Array.Empty<string>()
            : parameterText.Split(',')
                .Select(TypeNameNormalizer.Normalize)
                .ToArray();
        if (parameters.Any(parameter => parameter.Length == 0))
        {
            throw new SymbolQueryParseException("Parameter type cannot be empty.");
        }

        return new SymbolQuery(typeName, namespaceName, typeSimpleName, methodName, parameters);
    }
}
