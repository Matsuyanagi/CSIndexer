using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.Query.Symbols;

public static class SymbolMatcher
{
    public static bool IsMatch(SymbolQuery query, StoredSymbol symbol)
    {
        if (query.IsMethodQuery)
        {
            if (symbol.Kind != IndexedSymbolKind.Method ||
                symbol.Name != query.MethodName ||
                symbol.TypeSimpleName != query.TypeSimpleName)
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

        if (query.ParameterTypes is null)
        {
            return true;
        }

        if (symbol.Parameters.Count != query.ParameterTypes.Count)
        {
            return false;
        }

        for (var index = 0; index < symbol.Parameters.Count; index++)
        {
            if (TypeNameNormalizer.Normalize(symbol.Parameters[index].TypeKey) != query.ParameterTypes[index])
            {
                return false;
            }
        }

        return true;
    }
}
