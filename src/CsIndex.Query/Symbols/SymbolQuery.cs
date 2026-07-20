namespace CsIndex.Query.Symbols;

public sealed record SymbolQuery(
    string TypeName,
    string? NamespaceName,
    string TypeSimpleName,
    string? MethodName,
    IReadOnlyList<string>? ParameterTypes)
{
    public bool IsMethodQuery => MethodName is not null;
}

public sealed class SymbolQueryParseException(string message) : Exception(message);
