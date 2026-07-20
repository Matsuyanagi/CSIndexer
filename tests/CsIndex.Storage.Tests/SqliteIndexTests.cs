using CsIndex.Core.Caching;
using CsIndex.Core.Model;

namespace CsIndex.Storage.Tests;

public sealed class SqliteIndexTests
{
    [Fact]
    public async Task Save_CreatesSchemaAndCacheEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var snapshot = CreateSnapshot(temporary.Path);
        var index = new SqliteIndex(databasePath);

        await index.SaveAsync(snapshot, cancellationToken);

        Assert.True(await index.IsCacheValidAsync(
            temporary.Path,
            snapshot.InputFingerprint,
            snapshot.RequestHash,
            cancellationToken));
        var profile = await index.CreateQueryRepository().GetProfileAsync(cancellationToken: cancellationToken);
        Assert.Equal("test", profile.Name);
    }

    [Fact]
    public async Task Save_FailureRollsBackPriorIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        var original = CreateSnapshot(temporary.Path);
        await index.SaveAsync(original, cancellationToken);
        var invalid = CreateSnapshot(temporary.Path);
        invalid.Calls.Add(new CallData
        {
            CallerSymbolKey = "missing",
            ReferenceKind = ReferenceKind.Invocation,
            DispatchKind = DispatchKind.Static,
            ResolutionStatus = ResolutionStatus.Unresolved,
            ResolutionReason = ResolutionReason.Unknown,
            DocumentKey = "project|source",
            SourceStart = 0,
            SourceLength = 1,
        });

        await Assert.ThrowsAsync<IndexDatabaseException>(() => index.SaveAsync(invalid, cancellationToken));

        var symbols = await index.CreateQueryRepository().FindSymbolCandidatesAsync(
            (await index.CreateQueryRepository().GetProfileAsync(cancellationToken: cancellationToken)).Id,
            typeSimpleName: "Sample",
            kind: IndexedSymbolKind.Type,
            sourceOnly: true,
            cancellationToken: cancellationToken);
        Assert.Single(symbols);
    }

    [Fact]
    public async Task CorruptDatabase_ProducesExplicitError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "broken.sqlite");
        await File.WriteAllTextAsync(databasePath, "not a sqlite database", cancellationToken);
        var index = new SqliteIndex(databasePath);

        var exception = await Assert.ThrowsAsync<IndexDatabaseException>(() =>
            index.EnsureCreatedAsync(cancellationToken));

        Assert.Contains("corrupt", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IndexSnapshot CreateSnapshot(string root)
    {
        var inputFingerprint = HashUtilities.Sha256("input");
        var requestHash = HashUtilities.Sha256("request");
        var snapshot = new IndexSnapshot
        {
            InputRoot = root,
            InputFingerprint = inputFingerprint,
            RequestHash = requestHash,
            Profile = new AnalysisProfileData
            {
                Name = "test",
                InputMode = InputMode.Directory,
                RuntimeIdentifier = "win-x64",
                OperatingSystem = "Windows",
                Architecture = "x64",
                PreprocessorSymbols = ["WINDOWS"],
                ProfileHash = HashUtilities.Sha256("profile"),
            },
        };
        snapshot.Projects.Add(new ProjectData
        {
            Key = "project",
            Name = "Project",
            AssemblyName = "Project",
            Fingerprint = HashUtilities.Sha256("project"),
        });
        snapshot.Documents.Add(new DocumentData
        {
            Key = "project|source",
            ProjectKey = "project",
            NormalizedPath = Path.Combine(root, "Source.cs"),
            ContentHash = HashUtilities.Sha256("source"),
            IsGenerated = false,
            GenerationKind = GenerationKind.None,
        });
        snapshot.Symbols["sample"] = new SymbolData
        {
            StableKey = "sample",
            ProjectKey = "project",
            Kind = IndexedSymbolKind.Type,
            Name = "Sample",
            NamespaceName = string.Empty,
            TypeSimpleName = "Sample",
            TypeMetadataName = "Sample",
            FullyQualifiedName = "Sample",
            DisplayName = "Sample",
            SourceDocumentKey = "project|source",
            SourceStart = 0,
            SourceLength = 6,
        };
        return snapshot;
    }
}
