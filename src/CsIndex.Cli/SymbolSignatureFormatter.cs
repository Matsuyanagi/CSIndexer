using CsIndex.Core.Model;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal static class SymbolSignatureFormatter
{
    public static string Format(StoredSymbol symbol, bool shortNames)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var parts = new List<string>();
        var accessibility = FormatAccessibility(symbol.Accessibility);
        if (accessibility is not null)
        {
            parts.Add(accessibility);
        }

        if (symbol.IsStatic)
        {
            parts.Add("static");
        }

        if (IsDeclaredAsync(symbol))
        {
            parts.Add("async");
        }

        if (symbol.ReturnTypeKey is not null)
        {
            parts.Add(FormatType(symbol.ReturnTypeKey, shortNames));
        }

        parts.Add(FormatDisplayName(symbol.DisplayName, shortNames));
        return string.Join(' ', parts);
    }

    public static string FormatDisplayName(string displayName, bool shortNames) =>
        shortNames ? SymbolNameShortener.Shorten(displayName) : displayName;

    public static string FormatType(string typeName, bool shortNames) =>
        shortNames ? SymbolNameShortener.Shorten(typeName) : typeName;

    public static bool IsDeclaredAsync(StoredSymbol symbol) =>
        (symbol.AsyncRole & AsyncRole.DeclaredAsync) != 0;

    public static string? FormatAccessibility(int? accessibility) => accessibility is null
        ? null
        : (IndexedAccessibility)accessibility.Value switch
        {
            IndexedAccessibility.NotApplicable => null,
            IndexedAccessibility.Private => "private",
            IndexedAccessibility.ProtectedAndInternal => "private protected",
            IndexedAccessibility.Protected => "protected",
            IndexedAccessibility.Internal => "internal",
            IndexedAccessibility.ProtectedOrInternal => "protected internal",
            IndexedAccessibility.Public => "public",
            _ => null,
        };
}
