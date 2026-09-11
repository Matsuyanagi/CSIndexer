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

        if (FormatReturnType(symbol, symbolPathOptions) is { } returnType)
        {
            parts.Add(returnType);
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

    public static string FormatType(
        string typeKey,
        string typeDisplay,
        SymbolPathFormatOptions symbolPathOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeDisplay);
        return SymbolSignatureCanonicalizer.FormatTypeDisplay(
            new CanonicalTypeSignature(typeKey, typeDisplay),
            symbolPathOptions.ShortNames);
    }

    internal static string? FormatReturnType(
        StoredSymbol symbol,
        SymbolPathFormatOptions symbolPathOptions)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.ReturnTypeKey is null && symbol.ReturnTypeDisplay is null)
        {
            return null;
        }

        if (symbol.ReturnTypeKey is null || symbol.ReturnTypeDisplay is null)
        {
            throw new InvalidOperationException(
                $"Symbol ID {symbol.Id} has an incomplete return type identity/display pair.");
        }

        return FormatType(symbol.ReturnTypeKey, symbol.ReturnTypeDisplay, symbolPathOptions);
    }

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
