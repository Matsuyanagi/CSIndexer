namespace CsIndex.Core.Input;

public enum PathDisplayStyle
{
    Absolute = 1,
    Relative = 2,
}

public sealed class IndexPathResolver
{
    private IndexPathResolver(string databasePath, string indexRootAnchor, string effectiveBaseDirectory)
    {
        DatabasePath = databasePath;
        IndexRootAnchor = indexRootAnchor;
        EffectiveBaseDirectory = effectiveBaseDirectory;
    }

    public string DatabasePath { get; }

    public string IndexRootAnchor { get; }

    public string EffectiveBaseDirectory { get; }

    public static IndexPathResolver CreateForIndex(string databasePath, string storageRoot)
    {
        var normalizedDatabasePath = PathNormalizer.NormalizeAbsolute(databasePath);
        var normalizedStorageRoot = PathNormalizer.NormalizeAbsolute(storageRoot);
        var databaseDirectory = GetDatabaseDirectory(normalizedDatabasePath);
        PathNormalizer.EnsureSameVolumeShare(databaseDirectory, normalizedStorageRoot);
        var indexRootAnchor = PathNormalizer.RelativePath(databaseDirectory, normalizedStorageRoot);
        return new IndexPathResolver(normalizedDatabasePath, indexRootAnchor, normalizedStorageRoot);
    }

    public static IndexPathResolver CreateForQuery(
        string databasePath,
        string storedIndexRootAnchor,
        string? baseDirectory)
    {
        var normalizedDatabasePath = PathNormalizer.NormalizeAbsolute(databasePath);
        var databaseDirectory = GetDatabaseDirectory(normalizedDatabasePath);
        var indexRootAnchor = PathNormalizer.NormalizeRelative(storedIndexRootAnchor);
        var reconstructedBase = PathNormalizer.NormalizeAbsolute(Path.Combine(
            databaseDirectory,
            indexRootAnchor.Replace('/', Path.DirectorySeparatorChar)));
        var effectiveBase = baseDirectory is null
            ? reconstructedBase
            : PathNormalizer.NormalizeAbsolute(baseDirectory);
        return new IndexPathResolver(normalizedDatabasePath, indexRootAnchor, effectiveBase);
    }

    public string ToStoredPath(string absolutePath)
    {
        if (!PathNormalizer.IsFullyQualifiedAbsolute(absolutePath))
        {
            throw new InputResolutionException($"Runtime path must be absolute: {absolutePath}");
        }

        var normalizedAbsolutePath = PathNormalizer.NormalizeAbsolute(absolutePath);
        PathNormalizer.EnsureSameVolumeShare(EffectiveBaseDirectory, normalizedAbsolutePath);
        return PathNormalizer.RelativePath(EffectiveBaseDirectory, normalizedAbsolutePath);
    }

    public string ToAbsolutePath(string storedPath)
    {
        var normalizedStoredPath = PathNormalizer.NormalizeRelative(storedPath);
        var nativeStoredPath = normalizedStoredPath.Replace('/', Path.DirectorySeparatorChar);
        return PathNormalizer.NormalizeAbsolute(Path.Combine(EffectiveBaseDirectory, nativeStoredPath));
    }

    public string ToDisplayPath(string storedPath, PathDisplayStyle style) => style switch
    {
        PathDisplayStyle.Absolute => ToAbsolutePath(storedPath),
        PathDisplayStyle.Relative => PathNormalizer.NormalizeRelative(storedPath),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown path display style."),
    };

    public string NormalizeLocationInputToStoredPath(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        return PathNormalizer.IsRooted(inputPath)
            ? ToStoredPath(inputPath)
            : ToStoredPath(ToAbsolutePath(inputPath));
    }

    private static string GetDatabaseDirectory(string normalizedDatabasePath) =>
        PathNormalizer.NormalizeAbsolute(Path.GetDirectoryName(normalizedDatabasePath)
            ?? throw new InputResolutionException(
                $"Database path has no directory: {normalizedDatabasePath}"));
}
