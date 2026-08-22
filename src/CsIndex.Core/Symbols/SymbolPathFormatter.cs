using CsIndex.Core.Model;

namespace CsIndex.Core.Symbols;

public enum SymbolPathStyle
{
    CSharp = 1,
    Explicit = 2,
}

public readonly record struct SymbolPathFormatOptions(
    SymbolPathStyle Style = SymbolPathStyle.CSharp,
    bool ShortNames = false)
{
    public SymbolPathFormatOptions()
        : this(SymbolPathStyle.CSharp, false)
    {
    }
}

public sealed class SymbolPathFormatter
{
    public string Format(SymbolPathData path, SymbolPathFormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (options.Style is not SymbolPathStyle.CSharp and not SymbolPathStyle.Explicit)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Style, "Unknown symbol path style.");
        }

        if (string.IsNullOrWhiteSpace(path.TypeDisplayPath))
        {
            throw new ArgumentException("A symbol path must contain a non-empty type display path.", nameof(path));
        }

        var owner = options.Style switch
        {
            SymbolPathStyle.CSharp when options.ShortNames || string.IsNullOrEmpty(path.NamespacePath)
                => path.TypeDisplayPath,
            SymbolPathStyle.CSharp => $"{path.NamespacePath}.{path.TypeDisplayPath}",
            SymbolPathStyle.Explicit when options.ShortNames
                => $"**::{path.TypeDisplayPath}",
            SymbolPathStyle.Explicit when string.IsNullOrEmpty(path.NamespacePath)
                => $"global::{path.TypeDisplayPath}",
            SymbolPathStyle.Explicit => $"{path.NamespacePath}::{path.TypeDisplayPath}",
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Style, "Unknown symbol path style."),
        };

        return string.IsNullOrEmpty(path.ExecutableDisplayPath)
            ? owner
            : $"{owner}::{path.ExecutableDisplayPath}";
    }
}
