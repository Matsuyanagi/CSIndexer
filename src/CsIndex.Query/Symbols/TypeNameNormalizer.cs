namespace CsIndex.Query.Symbols;

public static class TypeNameNormalizer
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "System.Boolean",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["int"] = "System.Int32",
            ["uint"] = "System.UInt32",
            ["long"] = "System.Int64",
            ["ulong"] = "System.UInt64",
            ["nint"] = "System.IntPtr",
            ["nuint"] = "System.UIntPtr",
            ["char"] = "System.Char",
            ["float"] = "System.Single",
            ["double"] = "System.Double",
            ["decimal"] = "System.Decimal",
            ["string"] = "System.String",
            ["object"] = "System.Object",
            ["void"] = "System.Void",
        };

    public static string Normalize(string value)
    {
        var trimmed = value.Trim().Replace("global::", string.Empty, StringComparison.Ordinal);
        return Aliases.TryGetValue(trimmed, out var normalized) ? normalized : trimmed;
    }
}
