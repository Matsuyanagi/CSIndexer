using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal static class SymbolSignatureFormatter
{
    private static readonly SymbolPathFormatter PathFormatter = new();

    public static string Format(StoredSymbol symbol, SymbolPathFormatOptions symbolPathOptions)
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

        if (symbol.ReturnTypeDisplay is not null || symbol.ReturnTypeKey is not null)
        {
            parts.Add(symbol.ReturnTypeDisplay ?? symbol.ReturnTypeKey!);
        }

        parts.Add(FormatDisplayName(symbol, symbolPathOptions));
        return string.Join(' ', parts);
    }

    public static string FormatDisplayName(
        StoredSymbol symbol,
        SymbolPathFormatOptions symbolPathOptions)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Path is null)
        {
            throw new InvalidOperationException(
                $"Symbol ID {symbol.Id} has no semantic path data for presentation.");
        }

        return PathFormatter.Format(symbol.Path, symbolPathOptions);
    }

    public static string FormatType(string typeName, SymbolPathFormatOptions symbolPathOptions) => typeName;

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
