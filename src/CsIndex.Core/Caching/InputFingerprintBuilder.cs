using System.Security.Cryptography;
using System.Text;
using CsIndex.Core.Input;
using CsIndex.Core.Model;

namespace CsIndex.Core.Caching;

public sealed class InputFingerprintBuilder
{
    private static readonly HashSet<string> ConfigurationFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".asmdef", ".asmref",
    };

    internal Action? AfterFileFingerprintItem { get; set; }

    public async Task<byte[]> BuildAsync(
        ResolvedInput input,
        IndexOptions options,
        CancellationToken cancellationToken)
    {
        var paths = IndexPathResolver.CreateForIndex(
            Path.Combine(input.RootPath, ".csindex", "index.sqlite"),
            input.RootPath);
        return await BuildAsync(input, options, paths, cancellationToken);
    }

    public async Task<byte[]> BuildAsync(
        ResolvedInput input,
        IndexOptions options,
        IndexPathResolver paths,
        CancellationToken cancellationToken)
    {
        var matcher = new GlobMatcher(options.Excludes);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(input.RootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PathNormalizer.HasPathSegment(input.RootPath, path, "obj") &&
                (Extensions.Contains(Path.GetExtension(path)) ||
                 ConfigurationFileNames.Contains(Path.GetFileName(path))) &&
                !matcher.IsMatch(Path.GetRelativePath(input.RootPath, path)))
            {
                files.Add(PathNormalizer.NormalizeAbsolute(path));
            }
        }

        foreach (var path in options.References.Concat(options.DefineFiles).Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(PathNormalizer.NormalizeAbsolute(path));
        }

        var fingerprintItems = new List<FingerprintItem>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = PathNormalizer.SameVolumeShare(paths.EffectiveBaseDirectory, file)
                ? paths.ToStoredPath(file)
                : $"external:{Path.GetFileName(file)}";
            var contentHash = await HashUtilities.HashFileAsync(file, cancellationToken);
            fingerprintItems.Add(new FingerprintItem(identity, contentHash));
            AfterFileFingerprintItem?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in fingerprintItems
                     .OrderBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => Convert.ToHexString(item.ContentHash), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            aggregate.AppendData(Encoding.UTF8.GetBytes(item.Identity.ToUpperInvariant()));
            aggregate.AppendData(item.ContentHash);
        }

        return aggregate.GetHashAndReset();
    }

    private sealed record FingerprintItem(string Identity, byte[] ContentHash);
}
