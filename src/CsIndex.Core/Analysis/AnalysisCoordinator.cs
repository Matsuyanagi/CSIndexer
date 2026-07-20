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
    public ResolvedInput ResolveInput(IndexOptions options) => inputModeResolver.Resolve(options);

    public Task<byte[]> BuildInputFingerprintAsync(
        ResolvedInput input,
        IndexOptions options,
        CancellationToken cancellationToken) =>
        inputFingerprintBuilder.BuildAsync(input, options, cancellationToken);

    public async Task<AnalysisResult> AnalyzeAsync(
        ResolvedInput input,
        IndexOptions options,
        byte[] inputFingerprint,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var loaded = await workspaceLoader.LoadAsync(input, options, cancellationToken);
        var profile = await profileBuilder.BuildAsync(
            input,
            options,
            loaded.Projects,
            loaded.MetadataReferences,
            cancellationToken);
        var snapshot = new IndexSnapshot
        {
            Profile = profile,
            InputRoot = PathNormalizer.Normalize(input.RootPath),
            InputFingerprint = inputFingerprint,
            RequestHash = requestHash,
            DocumentsExcluded = loaded.DocumentsExcluded,
        };
        snapshot.MetadataReferences.AddRange(loaded.MetadataReferences);
        snapshot.Warnings.AddRange(loaded.Warnings);
        await semanticExtractor.ExtractAsync(loaded.Projects, snapshot, options.Diagnostics, cancellationToken);
        stopwatch.Stop();
        return new AnalysisResult(snapshot, stopwatch.Elapsed);
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
}
