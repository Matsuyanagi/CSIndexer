using CsIndex.Core.Model;

namespace CsIndex.Query.Symbols;

public sealed record SymbolSearchRequest(
    string? Pattern,
    string? NamespacePattern,
    string? TypePattern,
    string? MethodPattern,
    IndexedSymbolKind? Kind,
    bool UseRegex,
    bool IgnoreCase,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes,
    bool ShowSource);
