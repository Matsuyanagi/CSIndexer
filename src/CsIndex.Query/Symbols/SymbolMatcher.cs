using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.Query.Symbols;

public static class SymbolMatcher
{
    public static bool IsMatch(SymbolQuery query, StoredSymbol symbol)
    {
        if (query.IsMethodQuery)
        {
            if (symbol.TypeSimpleName != query.TypeSimpleName)
            {
                return false;
            }
        }
        else if (symbol.Kind != IndexedSymbolKind.Type || symbol.TypeSimpleName != query.TypeSimpleName)
        {
            return false;
        }

        if (query.NamespaceName is not null && symbol.NamespaceName != query.NamespaceName)
        {
            return false;
        }

        return !query.IsMethodQuery || IsMethodSignatureMatch(query, symbol);
    }

    public static bool IsMethodSignatureMatch(SymbolQuery query, StoredSymbol symbol)
    {
        if (!query.IsMethodQuery || symbol.Kind != IndexedSymbolKind.Method ||
            symbol.Name != query.MethodName)
        {
            return false;
        }

        if (query.ParameterTypes is null)
        {
            return true;
        }

        return symbol.Parameters.Count == query.ParameterTypes.Count &&
               symbol.Parameters.Select(parameter => TypeNameNormalizer.Normalize(parameter.TypeKey))
                   .SequenceEqual(query.ParameterTypes);
    }
}
