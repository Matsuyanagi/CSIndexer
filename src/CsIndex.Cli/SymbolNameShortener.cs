using System.Text.RegularExpressions;

namespace CsIndex.Cli;

internal static class SymbolNameShortener
{
    private static readonly Regex QualifiedTypeToken = new(
        @"(?<![A-Za-z0-9_@])(?:@?[A-Za-z_][A-Za-z0-9_]*\.)+@?[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    public static string Shorten(string name) => QualifiedTypeToken.Replace(name, static match =>
        match.Value[(match.Value.LastIndexOf('.') + 1)..]);
}
