using System.Text.RegularExpressions;

namespace CsIndex.Cli;

internal static class SymbolNameShortener
{
    private static readonly Regex QualifiedTypeToken = new(
        @"(?<![\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}@])(?:@?[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}_]*\.)+@?[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}_]*",
        RegexOptions.CultureInvariant);

    public static string Shorten(string name) => QualifiedTypeToken.Replace(name, static match =>
    {
        var shortName = match.Value[(match.Value.LastIndexOf('.') + 1)..];
        return shortName;
    });
}
