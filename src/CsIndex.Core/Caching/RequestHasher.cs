using System.Text.Json;
using CsIndex.Core.Input;
using CsIndex.Core.Model;

namespace CsIndex.Core.Caching;

public static class RequestHasher
{
    public const string ToolVersion = "0.1.0";
    public const int SchemaVersion = 5;
    public const int AnalysisCacheVersion = 3;

    public static byte[] Build(ResolvedInput input, IndexOptions options)
    {
        var value = new
        {
            ToolVersion,
            SchemaVersion,
            AnalysisCacheVersion,
            InputMode = input.Mode.ToString(),
            Configuration = options.Configuration,
            TargetFramework = options.TargetFramework,
            options.RuntimeIdentifier,
            ProfileName = options.ProfileName,
            Defines = options.Defines.Order(StringComparer.Ordinal).ToArray(),
            Undefines = options.Undefines.Order(StringComparer.Ordinal).ToArray(),
            References = options.References.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Excludes = options.Excludes.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            GeneratedSource = options.GeneratedSourceMode.ToString(),
            UnityEditor = options.UnityEditorPath is null ? null : Path.GetFullPath(options.UnityEditorPath),
        };
        return HashUtilities.Sha256(JsonSerializer.Serialize(value));
    }
}
