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

    public async Task<byte[]> BuildAsync(
        ResolvedInput input,
        IndexOptions options,
        CancellationToken cancellationToken)
    {
        var matcher = new GlobMatcher(options.Excludes);
        var files = Directory.EnumerateFiles(input.RootPath, "*", SearchOption.AllDirectories)
            .Where(path => !PathNormalizer.HasPathSegment(input.RootPath, path, "obj"))
            .Where(path => Extensions.Contains(Path.GetExtension(path)) ||
                           ConfigurationFileNames.Contains(Path.GetFileName(path)))
            .Where(path => !matcher.IsMatch(Path.GetRelativePath(input.RootPath, path)))
            .Concat(options.References.Where(File.Exists))
            .Concat(options.DefineFiles.Where(File.Exists))
            .Select(PathNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(input.RootPath, file).Replace('\\', '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()));
            aggregate.AppendData(await HashUtilities.HashFileAsync(file, cancellationToken));
        }

        return aggregate.GetHashAndReset();
    }
}
