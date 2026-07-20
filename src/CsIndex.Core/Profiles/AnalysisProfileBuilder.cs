using System.Runtime.InteropServices;
using System.Text.Json;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsIndex.Core.Profiles;

public sealed class AnalysisProfileBuilder
{
    public async Task<AnalysisProfileData> BuildAsync(
        ResolvedInput input,
        IndexOptions options,
        IReadOnlyList<Project> projects,
        IReadOnlyList<string> metadataReferences,
        CancellationToken cancellationToken)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (await project.GetCompilationAsync(cancellationToken) is CSharpCompilation compilation &&
                compilation.SyntaxTrees.FirstOrDefault()?.Options is CSharpParseOptions parseOptions)
            {
                symbols.UnionWith(parseOptions.PreprocessorSymbolNames);
            }
            else if (project.ParseOptions is CSharpParseOptions projectParseOptions)
            {
                symbols.UnionWith(projectParseOptions.PreprocessorSymbolNames);
            }
        }

        if (input.Mode == InputMode.Directory)
        {
            symbols.Add("WINDOWS");
            if (!string.IsNullOrWhiteSpace(options.TargetFramework))
            {
                AddTargetFrameworkSymbols(symbols, options.TargetFramework);
            }
        }

        foreach (var defineFile in options.DefineFiles)
        {
            if (!File.Exists(defineFile))
            {
                throw new InputResolutionException($"Define file does not exist: {defineFile}");
            }

            foreach (var line in await File.ReadAllLinesAsync(defineFile, cancellationToken))
            {
                var value = line.Trim();
                if (value.Length > 0 && !value.StartsWith('#'))
                {
                    symbols.Add(value);
                }
            }
        }

        symbols.UnionWith(options.Defines);
        symbols.ExceptWith(options.Undefines);
        var orderedSymbols = symbols.Order(StringComparer.Ordinal).ToArray();
        var framework = options.TargetFramework ?? projects.Select(project => project.ParseOptions)
            .Select(_ => (string?)null)
            .FirstOrDefault();
        var name = options.ProfileName ?? (input.Mode == InputMode.Directory
            ? "generic-windows-x64"
            : $"{options.Configuration ?? "Debug"}-windows-x64");
        var referenceIdentities = new List<string>();
        foreach (var reference in metadataReferences.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            referenceIdentities.Add(File.Exists(reference)
                ? $"{reference}|{HashUtilities.ToHex(await HashUtilities.HashFileAsync(reference, cancellationToken))}"
                : $"{reference}|unavailable");
        }

        var hashInput = new
        {
            RequestHasher.ToolVersion,
            RequestHasher.SchemaVersion,
            ProfileName = name,
            RoslynVersion = typeof(Compilation).Assembly.GetName().Version?.ToString(),
            InputMode = input.Mode.ToString(),
            Configuration = options.Configuration,
            TargetFramework = framework,
            options.RuntimeIdentifier,
            OperatingSystem = "Windows",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Symbols = orderedSymbols,
            References = referenceIdentities,
            Excludes = options.Excludes.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            GeneratedSource = options.GeneratedSourceMode.ToString(),
        };

        return new AnalysisProfileData
        {
            Name = name,
            InputMode = input.Mode,
            Configuration = options.Configuration,
            TargetFramework = framework,
            RuntimeIdentifier = options.RuntimeIdentifier,
            OperatingSystem = "Windows",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            PreprocessorSymbols = orderedSymbols,
            ProfileHash = HashUtilities.Sha256(JsonSerializer.Serialize(hashInput)),
        };
    }

    public static IReadOnlyList<string> BuildDirectorySymbols(IndexOptions options)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal) { "WINDOWS" };
        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            AddTargetFrameworkSymbols(symbols, options.TargetFramework);
        }

        symbols.UnionWith(options.Defines);
        symbols.ExceptWith(options.Undefines);
        return symbols.Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddTargetFrameworkSymbols(ISet<string> symbols, string targetFramework)
    {
        var normalized = targetFramework.Trim().ToLowerInvariant();
        if (normalized.Contains("-windows", StringComparison.Ordinal))
        {
            symbols.Add("WINDOWS");
        }

        if (!normalized.StartsWith("net", StringComparison.Ordinal) ||
            normalized.StartsWith("netstandard", StringComparison.Ordinal) ||
            normalized.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            return;
        }

        var versionPart = normalized[3..].Split('-')[0];
        var components = versionPart.Split('.');
        if (components.Length == 0 || !int.TryParse(components[0], out var major))
        {
            return;
        }

        var minor = components.Length > 1 && int.TryParse(components[1], out var parsedMinor) ? parsedMinor : 0;
        symbols.Add("NET");
        symbols.Add($"NET{major}_{minor}");
        for (var current = 5; current <= major; current++)
        {
            symbols.Add($"NET{current}_0_OR_GREATER");
        }
    }
}
