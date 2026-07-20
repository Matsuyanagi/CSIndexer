using CsIndex.Core.Model;

namespace CsIndex.Core.Input;

public sealed class InputModeResolver
{
    public ResolvedInput Resolve(IndexOptions options)
    {
        var inputPath = PathNormalizer.Normalize(options.InputPath);
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            throw new InputResolutionException($"Input does not exist: {inputPath}");
        }

        if (File.Exists(inputPath))
        {
            return ResolveFile(inputPath, options.ForcedMode);
        }

        return ResolveDirectory(inputPath, options);
    }

    private static ResolvedInput ResolveFile(string path, InputMode? forcedMode)
    {
        var extension = Path.GetExtension(path);
        var detected = extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? InputMode.Solution
            : extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? InputMode.Project
                : throw new InputResolutionException($"Unsupported input file: {path}");

        if (forcedMode is not null && forcedMode != detected)
        {
            throw new InputResolutionException(
                $"Input file '{path}' is {detected} mode but --mode requested {forcedMode} mode.");
        }

        return new ResolvedInput(detected, path, Path.GetDirectoryName(path)!, [path]);
    }

    private static ResolvedInput ResolveDirectory(string path, IndexOptions options)
    {
        if (options.ForcedMode == InputMode.Directory)
        {
            return new ResolvedInput(InputMode.Directory, path, path, []);
        }

        var solutions = EnumerateByExtensions(path, [".sln", ".slnx"]);
        if (!string.IsNullOrWhiteSpace(options.SolutionPath))
        {
            var selected = Path.IsPathRooted(options.SolutionPath)
                ? PathNormalizer.Normalize(options.SolutionPath)
                : PathNormalizer.Normalize(Path.Combine(path, options.SolutionPath));
            if (!File.Exists(selected) ||
                !(selected.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                  selected.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InputResolutionException($"Specified solution does not exist: {selected}");
            }

            return new ResolvedInput(InputMode.Solution, path, path, [selected]);
        }

        if (options.ForcedMode == InputMode.Solution || options.ForcedMode is null)
        {
            if (solutions.Count == 1)
            {
                return new ResolvedInput(InputMode.Solution, path, path, solutions);
            }

            if (solutions.Count > 1)
            {
                var candidates = string.Join(Environment.NewLine, solutions.Select(candidate => $"  {candidate}"));
                throw new InputResolutionException(
                    $"Multiple solutions were found. Select one with --solution:{Environment.NewLine}{candidates}");
            }

            if (options.ForcedMode == InputMode.Solution)
            {
                throw new InputResolutionException($"No .sln or .slnx was found under: {path}");
            }
        }

        var projects = EnumerateByExtensions(path, [".csproj"]);
        if (options.ForcedMode == InputMode.Project || projects.Count > 0)
        {
            if (projects.Count == 0)
            {
                throw new InputResolutionException($"No .csproj was found under: {path}");
            }

            return new ResolvedInput(InputMode.Project, path, path, projects);
        }

        return new ResolvedInput(InputMode.Directory, path, path, []);
    }

    private static IReadOnlyList<string> EnumerateByExtensions(string rootPath, IReadOnlyList<string> extensions) =>
        Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => !PathNormalizer.HasPathSegment(rootPath, path, "obj"))
            .Where(path => extensions.Any(extension =>
                extension.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
            .Select(PathNormalizer.Normalize)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
