using CsIndex.Core.Input;

namespace CsIndex.Core.Tests;

public sealed class IndexPathResolverTests
{
    [Fact]
    public void CreateForIndex_UsesStandardAnchorAndRelativeStoredPath()
    {
        var resolver = IndexPathResolver.CreateForIndex(
            @"D:\Work\Game\.csindex\index.sqlite",
            @"D:\Work\Game");

        Assert.Equal(@"D:\Work\Game\.csindex\index.sqlite", resolver.DatabasePath);
        Assert.Equal("..", resolver.IndexRootAnchor);
        Assert.Equal(@"D:\Work\Game", resolver.EffectiveBaseDirectory);
        Assert.Equal("src/play.cs", resolver.ToStoredPath(@"D:\Work\Game\src\play.cs"));
    }

    [Fact]
    public void CreateForIndex_UsesDatabaseDirectoryRelativeCustomAnchor()
    {
        var resolver = IndexPathResolver.CreateForIndex(
            @"D:\Indexes\Game\index.sqlite",
            @"D:\Work\Game");

        Assert.Equal("../../Work/Game", resolver.IndexRootAnchor);
        Assert.Equal("src/play.cs", resolver.ToStoredPath(@"D:\Work\Game\src\play.cs"));
    }

    [Theory]
    [InlineData(@"C:\index.sqlite", @"C:\\\", @"C:\", @"C:\src\play.cs")]
    [InlineData(
        @"\\server\share\index.sqlite",
        @"\\server\share\\\",
        @"\\server\share",
        @"\\server\share\src\play.cs")]
    public void RootTrailingSeparators_AreNormalizedWithoutChangingRootIdentity(
        string databasePath,
        string storageRoot,
        string expectedBaseDirectory,
        string sourcePath)
    {
        var resolver = IndexPathResolver.CreateForIndex(databasePath, storageRoot);

        Assert.Equal(expectedBaseDirectory, resolver.EffectiveBaseDirectory);
        Assert.Equal(".", resolver.IndexRootAnchor);
        Assert.Equal("src/play.cs", resolver.ToStoredPath(sourcePath));
    }

    [Fact]
    public void TemporaryPath_RoundTripsAbsoluteAndRelativeRepresentations()
    {
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, ".csindex", "index.sqlite");
        var absoluteFile = Path.Combine(temporary.Path, "src", "Play.cs");
        var resolver = IndexPathResolver.CreateForIndex(databasePath, temporary.Path);

        var stored = resolver.ToStoredPath(absoluteFile);

        Assert.Equal("src/Play.cs", stored);
        Assert.Equal(Path.GetFullPath(absoluteFile), resolver.ToAbsolutePath(stored));
        Assert.Equal(stored, resolver.ToDisplayPath(stored, PathDisplayStyle.Relative));
        Assert.Equal(Path.GetFullPath(absoluteFile), resolver.ToDisplayPath(stored, PathDisplayStyle.Absolute));
    }

    [Fact]
    public void LeadingParentPath_RoundTripsWithoutContainmentRejection()
    {
        using var temporary = new TempDirectory();
        var resolver = IndexPathResolver.CreateForIndex(
            Path.Combine(temporary.Path, ".csindex", "index.sqlite"),
            temporary.Path);
        var absoluteLinkedFile = Path.Combine(Directory.GetParent(temporary.Path)!.FullName, "Shared", "Generated", "Bindings.cs");

        var stored = resolver.ToStoredPath(absoluteLinkedFile);

        Assert.Equal("../Shared/Generated/Bindings.cs", stored);
        Assert.Equal(Path.GetFullPath(absoluteLinkedFile), resolver.ToAbsolutePath(stored));
    }

    [Fact]
    public void Conversion_NormalizesSeparatorsLexicalComponentsAndPreservesCasing()
    {
        var resolver = IndexPathResolver.CreateForIndex(
            @"C:\Work\Game\.csindex\index.sqlite",
            @"C:\Work\Game");

        Assert.Equal(
            "Src/PLAY.cs",
            resolver.ToStoredPath(@"C:\Work\Game\Src\.\Generated\..\PLAY.cs"));
        Assert.Equal(
            @"C:\Work\Game\Src\PLAY.cs",
            resolver.ToAbsolutePath(@"Src\.\Generated\..\PLAY.cs"));
    }

    [Fact]
    public void Query_DefaultAndOverrideReconstructFromTheSameStoredAnchor()
    {
        var databasePath = @"D:\Indexes\Game\index.sqlite";
        var query = IndexPathResolver.CreateForQuery(databasePath, "../../Work/Game", null);
        var overrideBase = @"E:\Moved\Game";
        var relocated = IndexPathResolver.CreateForQuery(databasePath, "../../Work/Game", overrideBase);

        Assert.Equal("../../Work/Game", query.IndexRootAnchor);
        Assert.Equal(@"D:\Work\Game", query.EffectiveBaseDirectory);
        Assert.Equal(@"D:\Work\Game\src\play.cs", query.ToAbsolutePath("src/play.cs"));
        Assert.Equal("../../Work/Game", relocated.IndexRootAnchor);
        Assert.Equal(Path.GetFullPath(overrideBase), relocated.EffectiveBaseDirectory);
        Assert.Equal(@"E:\Moved\Game\src\play.cs", relocated.ToAbsolutePath("src/play.cs"));
        Assert.Equal("src/play.cs", relocated.ToStoredPath(@"E:\Moved\Game\src\play.cs"));
        Assert.Equal("src/play.cs", relocated.ToDisplayPath("src/play.cs", PathDisplayStyle.Relative));
    }

    [Fact]
    public void Query_AllowsNonexistentOverrideBaseDirectory()
    {
        var overrideBase = Path.Combine(Path.GetTempPath(), "csindex-query-base-that-does-not-exist", Guid.NewGuid().ToString("N"));

        var resolver = IndexPathResolver.CreateForQuery(
            Path.Combine(Path.GetTempPath(), "csindex-db", "index.sqlite"),
            "..",
            overrideBase);

        Assert.Equal(Path.GetFullPath(overrideBase), resolver.EffectiveBaseDirectory);
        Assert.Equal(Path.Combine(resolver.EffectiveBaseDirectory, "src", "play.cs"), resolver.ToAbsolutePath("src/play.cs"));
    }

    [Fact]
    public void LocationInput_AbsoluteAndEffectiveBaseRelativeSpellingsMatch()
    {
        using var temporary = new TempDirectory();
        var resolver = IndexPathResolver.CreateForIndex(
            Path.Combine(temporary.Path, ".csindex", "index.sqlite"),
            temporary.Path);
        var absolutePath = Path.Combine(temporary.Path, "src", "play.cs");

        Assert.Equal("src/play.cs", resolver.NormalizeLocationInputToStoredPath(absolutePath));
        Assert.Equal("src/play.cs", resolver.NormalizeLocationInputToStoredPath(@"src\play.cs"));
    }

    [Fact]
    public void CreateForIndex_RejectsDifferentDriveWithBothPathsAndRule()
    {
        var exception = Assert.Throws<InputResolutionException>(() => IndexPathResolver.CreateForIndex(
            @"D:\Indexes\Game\index.sqlite",
            @"E:\Work\Game"));

        Assert.Contains(@"D:\Indexes\Game", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"E:\Work\Game", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same-volume/share", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateForIndex_RejectsDifferentUncServerWithBothPathsAndRule()
    {
        var exception = Assert.Throws<InputResolutionException>(() => IndexPathResolver.CreateForIndex(
            @"\\server-one\share\Indexes\index.sqlite",
            @"\\server-two\share\Work"));

        Assert.Contains(@"\\server-one\share\Indexes", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\\server-two\share\Work", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same-volume/share", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateForIndex_RejectsDifferentUncSharesOnTheSameServer()
    {
        var exception = Assert.Throws<InputResolutionException>(() => IndexPathResolver.CreateForIndex(
            @"\\server\share-one\Indexes\index.sqlite",
            @"\\server\share-two\Work"));

        Assert.Contains(@"\\server\share-one\Indexes", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\\server\share-two\Work", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same-volume/share", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedDriveDeviceAliasesCompareAsTheSameVolume()
    {
        var regular = IndexPathResolver.CreateForIndex(
            @"C:\Indexes\Game\index.sqlite",
            @"C:\Work\Game");
        var extended = IndexPathResolver.CreateForIndex(
            @"\\?\C:\Indexes\Game\index.sqlite",
            @"\\.\C:\Work\Game");

        Assert.Equal("../../Work/Game", regular.IndexRootAnchor);
        Assert.Equal("../../Work/Game", extended.IndexRootAnchor);
        Assert.Equal("src/play.cs", extended.ToStoredPath(@"\\?\C:\Work\Game\src\play.cs"));
    }

    [Fact]
    public void AcceptedUncDeviceAliasesCompareAsTheSameShare()
    {
        var resolver = IndexPathResolver.CreateForIndex(
            @"\\?\UNC\server\share\Indexes\index.sqlite",
            @"\\.\UNC\server\share\Work");

        Assert.Equal("../Work", resolver.IndexRootAnchor);
        Assert.Equal("src/play.cs", resolver.ToStoredPath(@"\\server\share\Work\src\play.cs"));
    }

    [Fact]
    public void UnsupportedDeviceNamespaceIsRejected()
    {
        var exception = Assert.Throws<InputResolutionException>(() => IndexPathResolver.CreateForIndex(
            @"\\.\PIPE\csindex\index.sqlite",
            @"C:\Work\Game"));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootedStoredPathAndAbsoluteAnchorAreRejected()
    {
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, ".csindex", "index.sqlite");

        Assert.Throws<InputResolutionException>(() => IndexPathResolver.CreateForQuery(
            databasePath,
            Path.Combine(temporary.Path, "absolute-root"),
            null));
        Assert.Throws<InputResolutionException>(() => IndexPathResolver.CreateForQuery(
            databasePath,
            "../root",
            null).ToAbsolutePath(@"C:\absolute\path.cs"));
    }

    [Fact]
    public void ToStoredPath_RejectsRelativeInput()
    {
        var resolver = IndexPathResolver.CreateForIndex(
            @"C:\Work\Game\.csindex\index.sqlite",
            @"C:\Work\Game");

        Assert.Throws<InputResolutionException>(() =>
        {
            _ = resolver.ToStoredPath(@"src\play.cs");
        });
    }

    [Fact]
    public void InvalidInputsAndDisplayStylesAreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => IndexPathResolver.CreateForIndex(" ", @"C:\Work"));
        Assert.ThrowsAny<ArgumentException>(() => IndexPathResolver.CreateForQuery(@"C:\db\index.sqlite", " ", null));

        var resolver = IndexPathResolver.CreateForIndex(
            @"C:\Work\Game\.csindex\index.sqlite",
            @"C:\Work\Game");
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.ToDisplayPath("src/play.cs", (PathDisplayStyle)99));
        Assert.Throws<InputResolutionException>(() =>
        {
            _ = resolver.NormalizeLocationInputToStoredPath(@"E:\Other\play.cs");
        });
    }
}
