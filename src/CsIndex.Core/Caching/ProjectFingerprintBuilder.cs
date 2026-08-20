using System.Security.Cryptography;
using System.Text;
using CsIndex.Core.Analysis;
using CsIndex.Core.Input;
using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Caching;

public sealed class ProjectFingerprintBuilder
{
    private readonly Dictionary<string, byte[]> _referenceHashes = new(StringComparer.OrdinalIgnoreCase);

    public async Task<byte[]> BuildAsync(Project project, CancellationToken cancellationToken)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendProjectHeader(aggregate, project, project.FilePath is null ? null : Path.GetFileName(project.FilePath));
        await AppendProjectFileContentAsync(aggregate, project, cancellationToken);

        foreach (var reference in project.ProjectReferences
                     .OrderBy(reference => GetPortableProjectReferenceName(project, reference), StringComparer.Ordinal)
                     .ThenBy(reference => string.Join(',', reference.Aliases), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(aggregate, GetPortableProjectReferenceName(project, reference));
            Append(aggregate, string.Join(',', reference.Aliases.Order(StringComparer.Ordinal)));
            Append(aggregate, reference.EmbedInteropTypes.ToString());
        }

        await AppendMetadataReferencesAsync(aggregate, project, cancellationToken);

        foreach (var document in project.Documents.OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(aggregate, document.Name);
            var text = await document.GetTextAsync(cancellationToken);
            aggregate.AppendData(HashUtilities.Sha256(text.ToString()));
        }

        return aggregate.GetHashAndReset();
    }

    internal async Task<byte[]> BuildAsync(
        Project project,
        string? storedProjectPath,
        AnalysisPathMappings paths,
        CancellationToken cancellationToken)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendProjectHeader(aggregate, project, storedProjectPath);
        await AppendProjectFileContentAsync(aggregate, project, cancellationToken);

        foreach (var reference in project.ProjectReferences
                     .OrderBy(reference => paths.GetProjectReferenceIdentity(project, reference), StringComparer.Ordinal)
                     .ThenBy(reference => string.Join(',', reference.Aliases), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(aggregate, paths.GetProjectReferenceIdentity(project, reference));
            Append(aggregate, string.Join(',', reference.Aliases.Order(StringComparer.Ordinal)));
            Append(aggregate, reference.EmbedInteropTypes.ToString());
        }

        await AppendMetadataReferencesAsync(aggregate, project, cancellationToken);

        foreach (var document in project.Documents
                     .Where(document => document.FilePath is not null)
                     .OrderBy(paths.GetDocumentPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(aggregate, paths.GetDocumentPath(document));
            var text = await document.GetTextAsync(cancellationToken);
            aggregate.AppendData(HashUtilities.Sha256(text.ToString()));
        }

        return aggregate.GetHashAndReset();
    }

    private async Task AppendMetadataReferencesAsync(
        IncrementalHash aggregate,
        Project project,
        CancellationToken cancellationToken)
    {
        var references = new List<MetadataFingerprint>();
        foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = reference.FilePath;
            var name = Path.GetFileName(path ?? reference.Display) ?? string.Empty;
            byte[] contentHash = [];
            if (path is not null && File.Exists(path))
            {
                if (!_referenceHashes.TryGetValue(path, out var hash))
                {
                    hash = await HashUtilities.HashFileAsync(path, cancellationToken);
                    _referenceHashes[path] = hash;
                }

                contentHash = hash;
            }

            references.Add(new MetadataFingerprint(
                name,
                string.Join(',', reference.Properties.Aliases.Order(StringComparer.Ordinal)),
                reference.Properties.EmbedInteropTypes,
                contentHash));
        }

        foreach (var reference in references
                     .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(reference => reference.Aliases, StringComparer.Ordinal)
                     .ThenBy(reference => Convert.ToHexString(reference.ContentHash), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(aggregate, reference.Name);
            Append(aggregate, reference.Aliases);
            Append(aggregate, reference.EmbedInteropTypes.ToString());
            aggregate.AppendData(reference.ContentHash);
        }
    }

    private static void AppendProjectHeader(IncrementalHash aggregate, Project project, string? projectPath)
    {
        Append(aggregate, project.Name);
        Append(aggregate, project.AssemblyName ?? string.Empty);
        Append(aggregate, projectPath ?? string.Empty);
        Append(aggregate, project.ParseOptions?.ToString() ?? string.Empty);
        Append(aggregate, project.CompilationOptions?.ToString() ?? string.Empty);
    }

    private static async Task AppendProjectFileContentAsync(
        IncrementalHash aggregate,
        Project project,
        CancellationToken cancellationToken)
    {
        if (project.FilePath is { } projectPath && File.Exists(projectPath))
        {
            aggregate.AppendData(await HashUtilities.HashFileAsync(projectPath, cancellationToken));
        }
    }

    private static string GetPortableProjectReferenceName(Project project, ProjectReference reference) =>
        $"project-name:{project.Solution.GetProject(reference.ProjectId)?.Name ?? "unresolved"}";

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private sealed record MetadataFingerprint(
        string Name,
        string Aliases,
        bool EmbedInteropTypes,
        byte[] ContentHash);
}
