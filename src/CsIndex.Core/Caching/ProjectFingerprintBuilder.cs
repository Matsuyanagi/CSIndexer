using System.Security.Cryptography;
using System.Text;
using CsIndex.Core.Input;
using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Caching;

public sealed class ProjectFingerprintBuilder
{
    private readonly Dictionary<string, byte[]> _referenceHashes = new(StringComparer.OrdinalIgnoreCase);

    public async Task<byte[]> BuildAsync(Project project, CancellationToken cancellationToken)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(aggregate, project.Name);
        Append(aggregate, project.AssemblyName ?? string.Empty);
        Append(aggregate, project.FilePath ?? string.Empty);
        Append(aggregate, project.ParseOptions?.ToString() ?? string.Empty);
        Append(aggregate, project.CompilationOptions?.ToString() ?? string.Empty);

        if (project.FilePath is { } projectPath && File.Exists(projectPath))
        {
            aggregate.AppendData(await HashUtilities.HashFileAsync(projectPath, cancellationToken));
        }

        foreach (var reference in project.ProjectReferences.OrderBy(reference => reference.ProjectId.Id))
        {
            Append(aggregate, reference.ProjectId.Id.ToString());
        }

        foreach (var reference in project.MetadataReferences
                     .OfType<PortableExecutableReference>()
                     .OrderBy(reference => reference.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = reference.FilePath;
            Append(aggregate, path ?? reference.Display ?? string.Empty);
            if (path is not null && File.Exists(path))
            {
                if (!_referenceHashes.TryGetValue(path, out var hash))
                {
                    hash = await HashUtilities.HashFileAsync(path, cancellationToken);
                    _referenceHashes[path] = hash;
                }

                aggregate.AppendData(hash);
            }
        }

        foreach (var document in project.Documents.OrderBy(document => document.FilePath ?? document.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(aggregate, document.FilePath ?? document.Name);
            var text = await document.GetTextAsync(cancellationToken);
            aggregate.AppendData(HashUtilities.Sha256(text.ToString()));
        }

        return aggregate.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
