using System.Collections.Frozen;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Analysis;

public sealed class PreparedAnalysis : IDisposable
{
    private readonly ResolvedInput _input;
    private readonly IndexPathResolver _paths;
    private LoadedWorkspace? _loadedWorkspace;

    internal PreparedAnalysis(
        ResolvedInput input,
        IndexPathResolver paths,
        LoadedWorkspace loadedWorkspace,
        AnalysisPathMappings mappings)
    {
        _input = input;
        _paths = paths;
        _loadedWorkspace = loadedWorkspace;
        Mappings = mappings;
    }

    public ResolvedInput Input
    {
        get
        {
            ThrowIfDisposed();
            return _input;
        }
    }

    public IndexPathResolver Paths
    {
        get
        {
            ThrowIfDisposed();
            return _paths;
        }
    }

    public IReadOnlyList<Project> Projects
    {
        get
        {
            ThrowIfDisposed();
            return _loadedWorkspace!.Projects;
        }
    }

    public IReadOnlyList<string> MetadataReferences
    {
        get
        {
            ThrowIfDisposed();
            return _loadedWorkspace!.MetadataReferences;
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            ThrowIfDisposed();
            return _loadedWorkspace!.Warnings;
        }
    }

    public int DocumentsExcluded
    {
        get
        {
            ThrowIfDisposed();
            return _loadedWorkspace!.DocumentsExcluded;
        }
    }

    internal AnalysisPathMappings Mappings { get; }

    internal void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_loadedWorkspace is null, this);

    public void Dispose() => Interlocked.Exchange(ref _loadedWorkspace, null)?.Dispose();
}

internal sealed class AnalysisPathMappings
{
    private readonly FrozenDictionary<ProjectId, string> _projectPaths;
    private readonly FrozenDictionary<DocumentId, string> _documentPaths;
    private readonly FrozenDictionary<SyntaxTree, string> _sourceTreePaths;
    private readonly FrozenDictionary<string, string> _runtimePaths;

    private AnalysisPathMappings(
        string runtimeStorageRoot,
        FrozenDictionary<ProjectId, string> projectPaths,
        FrozenDictionary<DocumentId, string> documentPaths,
        FrozenDictionary<SyntaxTree, string> sourceTreePaths,
        FrozenDictionary<string, string> runtimePaths)
    {
        RuntimeStorageRoot = runtimeStorageRoot;
        _projectPaths = projectPaths;
        _documentPaths = documentPaths;
        _sourceTreePaths = sourceTreePaths;
        _runtimePaths = runtimePaths;
    }

    internal string RuntimeStorageRoot { get; }

    internal static async Task<AnalysisPathMappings> CreateAsync(
        string storageRoot,
        IndexPathResolver paths,
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken,
        Action? afterPathValidationItem = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(projects);

        var projectPaths = new Dictionary<ProjectId, string>();
        var documentPaths = new Dictionary<DocumentId, string>();
        var sourceTreePaths = new Dictionary<SyntaxTree, string>(ReferenceEqualityComparer.Instance);
        var runtimePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var storedRoot = ValidatePath(storageRoot, paths, cancellationToken, afterPathValidationItem);
        if (!storedRoot.Equals(".", StringComparison.Ordinal))
        {
            throw new InputResolutionException(
                $"The prepared input root must match the effective storage root; received stored path '{storedRoot}'.");
        }

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.FilePath is { } projectPath)
            {
                var storedProjectPath = ValidatePath(
                    projectPath,
                    paths,
                    cancellationToken,
                    afterPathValidationItem);
                projectPaths.Add(project.Id, storedProjectPath);
                runtimePaths[PathNormalizer.NormalizeAbsolute(projectPath)] = storedProjectPath;
            }

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.FilePath is not { } documentPath)
                {
                    continue;
                }

                var storedDocumentPath = ValidatePath(
                    documentPath,
                    paths,
                    cancellationToken,
                    afterPathValidationItem);
                documentPaths.Add(document.Id, storedDocumentPath);
                runtimePaths[PathNormalizer.NormalizeAbsolute(documentPath)] = storedDocumentPath;

                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(syntaxTree.FilePath))
                {
                    var storedTreePath = ValidatePath(
                        syntaxTree.FilePath,
                        paths,
                        cancellationToken,
                        afterPathValidationItem);
                    if (!storedTreePath.Equals(storedDocumentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InputResolutionException(
                            "A source tree path did not match its validated document path.");
                    }

                    runtimePaths[PathNormalizer.NormalizeAbsolute(syntaxTree.FilePath)] = storedTreePath;
                }

                sourceTreePaths.Add(syntaxTree, storedDocumentPath);
            }
        }

        return new AnalysisPathMappings(
            paths.EffectiveBaseDirectory,
            projectPaths.ToFrozenDictionary(),
            documentPaths.ToFrozenDictionary(),
            sourceTreePaths.ToFrozenDictionary(ReferenceEqualityComparer.Instance),
            runtimePaths.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    internal string? GetProjectPath(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.FilePath is null)
        {
            return null;
        }

        return _projectPaths.TryGetValue(project.Id, out var storedPath)
            ? storedPath
            : throw MissingMapping("project");
    }

    internal string GetProjectKey(Project project) => GetProjectPath(project) is { } storedPath
        ? $"project-path:{storedPath}"
        : $"project-name:{project.Name}";

    internal string GetDocumentPath(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _documentPaths.TryGetValue(document.Id, out var storedPath)
            ? storedPath
            : throw MissingMapping("document");
    }

    internal string GetSourceTreePath(SyntaxTree syntaxTree)
    {
        ArgumentNullException.ThrowIfNull(syntaxTree);
        return _sourceTreePaths.TryGetValue(syntaxTree, out var storedPath)
            ? storedPath
            : throw MissingMapping("source tree");
    }

    internal string GetRuntimePath(string absolutePath)
    {
        var normalized = PathNormalizer.NormalizeAbsolute(absolutePath);
        return _runtimePaths.TryGetValue(normalized, out var storedPath)
            ? storedPath
            : throw MissingMapping("runtime path");
    }

    internal string GetProjectReferenceIdentity(Project project, ProjectReference reference)
    {
        var referencedProject = project.Solution.GetProject(reference.ProjectId);
        if (referencedProject is null)
        {
            return "project-name:unresolved";
        }

        if (referencedProject.FilePath is not null &&
            _projectPaths.TryGetValue(referencedProject.Id, out var storedPath))
        {
            return $"project-path:{storedPath}";
        }

        return $"project-name:{referencedProject.Name}";
    }

    private static string ValidatePath(
        string absolutePath,
        IndexPathResolver paths,
        CancellationToken cancellationToken,
        Action? afterPathValidationItem)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storedPath = paths.ToStoredPath(absolutePath);
        afterPathValidationItem?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return storedPath;
    }

    private static InputResolutionException MissingMapping(string kind) =>
        new($"The {kind} was not present in the validated path map.");
}
