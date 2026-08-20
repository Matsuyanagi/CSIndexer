using System.Diagnostics;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Profiles;

namespace CsIndex.Core.Analysis;

public sealed class AnalysisCoordinator(
    InputModeResolver inputModeResolver,
    WorkspaceLoader workspaceLoader,
    InputFingerprintBuilder inputFingerprintBuilder,
    AnalysisProfileBuilder profileBuilder,
    SemanticExtractor semanticExtractor)
{
    private readonly Func<ResolvedInput, IndexOptions, CancellationToken, Task<LoadedWorkspace>> _loadWorkspaceAsync =
        workspaceLoader.LoadAsync;

    internal Action? AfterPathValidationItem { get; set; }

    private AnalysisCoordinator(
        InputModeResolver inputModeResolver,
        WorkspaceLoader workspaceLoader,
        InputFingerprintBuilder inputFingerprintBuilder,
        AnalysisProfileBuilder profileBuilder,
        SemanticExtractor semanticExtractor,
        Func<ResolvedInput, IndexOptions, CancellationToken, Task<LoadedWorkspace>> loadWorkspaceAsync)
        : this(
            inputModeResolver,
            workspaceLoader,
            inputFingerprintBuilder,
            profileBuilder,
            semanticExtractor)
    {
        _loadWorkspaceAsync = loadWorkspaceAsync;
    }

    public ResolvedInput ResolveInput(IndexOptions options) => inputModeResolver.Resolve(options);

    public Task<byte[]> BuildInputFingerprintAsync(
        ResolvedInput input,
        IndexOptions options,
        CancellationToken cancellationToken) =>
        inputFingerprintBuilder.BuildAsync(input, options, cancellationToken);

    public Task<byte[]> BuildInputFingerprintAsync(
        ResolvedInput input,
        IndexOptions options,
        IndexPathResolver paths,
        CancellationToken cancellationToken) =>
        inputFingerprintBuilder.BuildAsync(input, options, paths, cancellationToken);

    public async Task<PreparedAnalysis> PrepareAsync(
        ResolvedInput input,
        IndexOptions options,
        IndexPathResolver paths,
        CancellationToken cancellationToken)
    {
        LoadedWorkspace? loaded = null;
        try
        {
            loaded = await _loadWorkspaceAsync(input, options, cancellationToken);
            var mappings = await AnalysisPathMappings.CreateAsync(
                input.RootPath,
                paths,
                loaded.Projects,
                cancellationToken,
                AfterPathValidationItem);
            return new PreparedAnalysis(input, paths, loaded, mappings);
        }
        catch
        {
            loaded?.Dispose();
            throw;
        }
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        PreparedAnalysis prepared,
        IndexOptions options,
        byte[] inputFingerprint,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        prepared.ThrowIfDisposed();

        var stopwatch = Stopwatch.StartNew();
        var profile = await profileBuilder.BuildAsync(
            prepared.Input,
            options,
            prepared.Projects,
            prepared.MetadataReferences,
            cancellationToken);
        var snapshot = new IndexSnapshot
        {
            Profile = profile,
            InputRoot = ".",
            IndexRootAnchor = prepared.Paths.IndexRootAnchor,
            InputFingerprint = inputFingerprint,
            RequestHash = requestHash,
            DocumentsExcluded = prepared.DocumentsExcluded,
        };
        snapshot.MetadataReferences.AddRange(prepared.MetadataReferences);
        snapshot.Warnings.AddRange(prepared.Warnings);
        await semanticExtractor.ExtractAsync(prepared, snapshot, options.Diagnostics, cancellationToken);
        stopwatch.Stop();
        return new AnalysisResult(snapshot, stopwatch.Elapsed);
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        ResolvedInput input,
        IndexOptions options,
        byte[] inputFingerprint,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var paths = CreateStandardPaths(input.RootPath);
        using var prepared = await PrepareAsync(input, options, paths, cancellationToken);
        return await AnalyzeAsync(
            prepared,
            options,
            inputFingerprint,
            requestHash,
            cancellationToken);
    }

    public static AnalysisCoordinator CreateDefault()
    {
        var sourceEnumerator = new SourceFileEnumerator();
        return new AnalysisCoordinator(
            new InputModeResolver(),
            new WorkspaceLoader(sourceEnumerator),
            new InputFingerprintBuilder(),
            new AnalysisProfileBuilder(),
            new SemanticExtractor(new ProjectFingerprintBuilder()));
    }

    internal static AnalysisCoordinator CreateForTesting(
        Func<ResolvedInput, IndexOptions, CancellationToken, Task<LoadedWorkspace>> loadWorkspaceAsync)
    {
        ArgumentNullException.ThrowIfNull(loadWorkspaceAsync);
        var sourceEnumerator = new SourceFileEnumerator();
        return new AnalysisCoordinator(
            new InputModeResolver(),
            new WorkspaceLoader(sourceEnumerator),
            new InputFingerprintBuilder(),
            new AnalysisProfileBuilder(),
            new SemanticExtractor(new ProjectFingerprintBuilder()),
            loadWorkspaceAsync);
    }

    private static IndexPathResolver CreateStandardPaths(string storageRoot) =>
        IndexPathResolver.CreateForIndex(
            Path.Combine(storageRoot, ".csindex", "index.sqlite"),
            storageRoot);
}
