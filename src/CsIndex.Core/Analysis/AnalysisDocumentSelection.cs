using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Analysis;

internal sealed record CompilationOnlyGeneratedDocument(
    ProjectId ProjectId,
    DocumentId DocumentId,
    string Name,
    GenerationKind GenerationKind,
    ImmutableArray<byte> ContentHash);

internal sealed class AnalysisDocumentSelection
{
    private readonly FrozenSet<DocumentId> _indexableDocumentIds;
    private readonly FrozenSet<DocumentId> _compilationOnlyDocumentIds;

    private AnalysisDocumentSelection(
        FrozenSet<DocumentId> indexableDocumentIds,
        FrozenSet<DocumentId> compilationOnlyDocumentIds,
        ImmutableArray<CompilationOnlyGeneratedDocument> compilationOnlyDocuments,
        ImmutableArray<string> warnings)
    {
        _indexableDocumentIds = indexableDocumentIds;
        _compilationOnlyDocumentIds = compilationOnlyDocumentIds;
        CompilationOnlyDocuments = compilationOnlyDocuments;
        Warnings = warnings;
    }

    internal ImmutableArray<CompilationOnlyGeneratedDocument> CompilationOnlyDocuments { get; }

    internal ImmutableArray<string> Warnings { get; }

    internal int CompilationOnlyCount => CompilationOnlyDocuments.Length;

    internal bool IsIndexable(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _indexableDocumentIds.Contains(document.Id);
    }

    internal bool IsCompilationOnly(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _compilationOnlyDocumentIds.Contains(document.Id);
    }

    internal ImmutableArray<CompilationOnlyGeneratedDocument> GetCompilationOnly(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return CompilationOnlyDocuments
            .Where(document => document.ProjectId == project.Id)
            .ToImmutableArray();
    }

    internal static async Task<AnalysisDocumentSelection> CreateAsync(
        string storageRoot,
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken,
        Action? afterDocumentClassificationItem = null)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var indexableDocumentIds = new List<DocumentId>();
        var compilationOnlyDocumentIds = new List<DocumentId>();
        var compilationOnlyDocuments = new List<CompilationOnlyGeneratedDocument>();
        var warnings = new List<string>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.FilePath is not { } documentPath ||
                    PathNormalizer.SameVolumeShare(storageRoot, documentPath))
                {
                    indexableDocumentIds.Add(document.Id);
                    afterDocumentClassificationItem?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var generated = GeneratedCodeDetector.Detect(documentPath, text);
                if (!generated.IsGenerated)
                {
                    PathNormalizer.EnsureSameVolumeShare(storageRoot, documentPath);
                    throw new UnreachableException();
                }

                compilationOnlyDocumentIds.Add(document.Id);
                compilationOnlyDocuments.Add(new CompilationOnlyGeneratedDocument(
                    project.Id,
                    document.Id,
                    document.Name,
                    generated.Kind,
                    HashUtilities.Sha256(text.ToString()).ToImmutableArray()));
                warnings.Add(
                    $"Generated document was kept in the compilation but excluded from the portable index because it is on another volume/share: {PathNormalizer.NormalizeAbsolute(documentPath)}");
                afterDocumentClassificationItem?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return new AnalysisDocumentSelection(
            indexableDocumentIds.ToFrozenSet(),
            compilationOnlyDocumentIds.ToFrozenSet(),
            compilationOnlyDocuments.ToImmutableArray(),
            warnings.ToImmutableArray());
    }
}
