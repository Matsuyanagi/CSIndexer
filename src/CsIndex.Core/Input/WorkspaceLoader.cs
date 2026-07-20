using System.Text;
using CsIndex.Core.Model;
using CsIndex.Core.Profiles;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace CsIndex.Core.Input;

public sealed class WorkspaceLoader(SourceFileEnumerator sourceFileEnumerator)
{
    public async Task<LoadedWorkspace> LoadAsync(
        ResolvedInput input,
        IndexOptions options,
        CancellationToken cancellationToken)
    {
        return input.Mode == InputMode.Directory
            ? await LoadDirectoryAsync(input, options, cancellationToken)
            : await LoadMsBuildAsync(input, options, cancellationToken);
    }

    private static async Task<LoadedWorkspace> LoadMsBuildAsync(
        ResolvedInput input,
        IndexOptions options,
        CancellationToken cancellationToken)
    {
        MSBuildBootstrapper.EnsureRegistered();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.Configuration))
        {
            properties["Configuration"] = options.Configuration;
        }

        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
        {
            properties["TargetFramework"] = options.TargetFramework;
        }

        if (!string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
        {
            properties["RuntimeIdentifier"] = options.RuntimeIdentifier;
        }

        var workspace = MSBuildWorkspace.Create(properties);
        var warnings = new List<string>();
        workspace.RegisterWorkspaceFailedHandler(args => warnings.Add(args.Diagnostic.Message));

        try
        {
            if (input.Mode == InputMode.Solution)
            {
                await workspace.OpenSolutionAsync(input.EntryPaths.Single(), cancellationToken: cancellationToken);
            }
            else
            {
                foreach (var projectPath in input.EntryPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
                    }
                    catch (Exception exception) when (input.EntryPaths.Count > 1)
                    {
                        warnings.Add($"Project could not be loaded: {projectPath}: {exception.Message}");
                    }
                }
            }

            if (!workspace.CurrentSolution.Projects.Any())
            {
                throw new InputResolutionException("No project could be loaded by MSBuildWorkspace.");
            }

            var explicitReferences = ValidateReferences(options.References);
            var defineFileSymbols = await ReadDefineFilesAsync(options.DefineFiles, cancellationToken);
            var solution = workspace.CurrentSolution;
            foreach (var project in solution.Projects.ToArray())
            {
                if (project.Language != LanguageNames.CSharp)
                {
                    continue;
                }

                if (project.ParseOptions is CSharpParseOptions parseOptions)
                {
                    var symbols = new HashSet<string>(parseOptions.PreprocessorSymbolNames, StringComparer.Ordinal);
                    symbols.UnionWith(defineFileSymbols);
                    symbols.UnionWith(options.Defines);
                    symbols.ExceptWith(options.Undefines);
                    solution = solution.WithProjectParseOptions(
                        project.Id,
                        parseOptions.WithPreprocessorSymbols(symbols.Order(StringComparer.Ordinal)));
                }

                var existingPaths = project.MetadataReferences
                    .OfType<PortableExecutableReference>()
                    .Select(reference => reference.FilePath)
                    .Where(path => path is not null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var referencePath in explicitReferences.Where(path => !existingPaths.Contains(path)))
                {
                    solution = solution.AddMetadataReference(project.Id, MetadataReference.CreateFromFile(referencePath));
                }
            }

            var loadedProjects = solution.Projects
                .Where(project => project.Language == LanguageNames.CSharp)
                .OrderBy(project => project.FilePath ?? project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var projects = loadedProjects
                .Where(project => project.Documents.Any(document =>
                    document.FilePath is not null &&
                    document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(document.FilePath)))
                .ToArray();
            foreach (var project in loadedProjects.Except(projects))
            {
                warnings.Add($"Project contains no loadable physical C# documents and was skipped: {project.Name}");
            }

            if (projects.Length == 0)
            {
                throw new InputResolutionException(
                    "MSBuildWorkspace did not load any physical C# documents from the selected input.");
            }

            var references = projects.SelectMany(project => project.MetadataReferences)
                .OfType<PortableExecutableReference>()
                .Select(reference => reference.FilePath)
                .Where(path => path is not null)
                .Cast<string>()
                .Select(PathNormalizer.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new LoadedWorkspace(workspace, projects, references, warnings, 0);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private async Task<LoadedWorkspace> LoadDirectoryAsync(
        ResolvedInput input,
        IndexOptions options,
        CancellationToken cancellationToken)
    {
        var enumeration = sourceFileEnumerator.Enumerate(input.RootPath, options.Excludes);
        if (enumeration.IncludedFiles.Count == 0)
        {
            throw new InputResolutionException($"No C# source files were found under: {input.RootPath}");
        }

        var symbols = new HashSet<string>(AnalysisProfileBuilder.BuildDirectorySymbols(options), StringComparer.Ordinal);
        symbols.UnionWith(await ReadDefineFilesAsync(options.DefineFiles, cancellationToken));
        symbols.ExceptWith(options.Undefines);
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Latest,
            DocumentationMode.Parse,
            SourceCodeKind.Regular,
            symbols.Order(StringComparer.Ordinal));
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable,
            allowUnsafe: true);
        var referencePaths = GetTrustedPlatformAssemblyPaths()
            .Concat(ValidateReferences(options.References))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();

        var workspace = new AdhocWorkspace();
        try
        {
            var projectId = ProjectId.CreateNewId();
            var projectName = new DirectoryInfo(input.RootPath).Name;
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                LanguageNames.CSharp,
                filePath: null,
                outputFilePath: null,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                metadataReferences: references);
            workspace.AddProject(projectInfo);

            foreach (var filePath in enumeration.IncludedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = SourceText.From(
                    await File.ReadAllTextAsync(filePath, cancellationToken),
                    Encoding.UTF8);
                workspace.AddDocument(DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(filePath),
                    loader: TextLoader.From(TextAndVersion.Create(source, VersionStamp.Create(), filePath)),
                    filePath: filePath));
            }

            return new LoadedWorkspace(
                workspace,
                [workspace.CurrentSolution.GetProject(projectId)!],
                referencePaths,
                [],
                enumeration.ExcludedFiles.Count);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static string[] GetTrustedPlatformAssemblyPaths()
    {
        var value = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        return string.IsNullOrWhiteSpace(value)
            ? [typeof(object).Assembly.Location]
            : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] ValidateReferences(IEnumerable<string> references)
    {
        var result = new List<string>();
        foreach (var reference in references)
        {
            var path = PathNormalizer.Normalize(reference);
            if (!File.Exists(path))
            {
                throw new InputResolutionException($"Required metadata reference does not exist: {path}");
            }

            result.Add(path);
        }

        return result.ToArray();
    }

    private static async Task<IReadOnlyList<string>> ReadDefineFilesAsync(
        IEnumerable<string> defineFiles,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var defineFile in defineFiles)
        {
            var path = PathNormalizer.Normalize(defineFile);
            if (!File.Exists(path))
            {
                throw new InputResolutionException($"Define file does not exist: {path}");
            }

            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
            {
                var symbol = line.Trim();
                if (symbol.Length > 0 && !symbol.StartsWith('#'))
                {
                    result.Add(symbol);
                }
            }
        }

        return result.ToArray();
    }
}
