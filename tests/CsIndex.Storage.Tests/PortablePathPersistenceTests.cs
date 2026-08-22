using CsIndex.Core.Model;
using CsIndex.Core.Input;
using Microsoft.Data.Sqlite;

namespace CsIndex.Storage.Tests;

public sealed class PortablePathPersistenceTests
{
    [Fact]
    public void QueryRepository_ExposesNormalizedRuntimeDatabasePath()
    {
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, ".", "index.sqlite"));

        Assert.Equal(index.DatabasePath, index.CreateQueryRepository().DatabasePath);
    }

    [Fact]
    public async Task Save_PersistsRelativeRootsAndForwardSlashDocumentPaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var snapshot = new IndexSnapshot
        {
            Profile = new AnalysisProfileData
            {
                Name = "portable",
                InputMode = InputMode.Directory,
                OperatingSystem = "Windows",
                Architecture = "x64",
                PreprocessorSymbols = [],
                ProfileHash = [1],
            },
            InputRoot = ".",
            IndexRootAnchor = "..",
            InputFingerprint = [2],
            RequestHash = [3],
        };
        snapshot.Projects.Add(new ProjectData
        {
            Key = "project-path:src/Game.csproj",
            Name = "Game",
            ProjectPath = "src/Game.csproj",
            Fingerprint = [4],
        });
        snapshot.Documents.Add(new DocumentData
        {
            Key = "project-path:src/Game.csproj|document:src/Game.cs",
            ProjectKey = "project-path:src/Game.csproj",
            NormalizedPath = "src/Game.cs",
            ContentHash = [5],
            IsGenerated = false,
            GenerationKind = GenerationKind.None,
        });

        await new SqliteIndex(databasePath).SaveAsync(snapshot, cancellationToken);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT input_root, index_root_anchor FROM index_runs;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(".", reader.GetString(0));
            Assert.Equal("..", reader.GetString(1));
        }

        command.CommandText = "SELECT project_path FROM projects;";
        Assert.Equal("src/Game.csproj", await command.ExecuteScalarAsync(cancellationToken));
        command.CommandText = "SELECT normalized_path FROM documents;";
        Assert.Equal("src/Game.cs", await command.ExecuteScalarAsync(cancellationToken));
    }

    [Fact]
    public async Task StandardDatabase_RelocatesAndBaseOverrideDoesNotMutateStoredRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var originalWorkspace = Path.Combine(temporary.Path, "original", "workspace");
        var originalSource = Path.Combine(originalWorkspace, "src", "Game.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(originalSource)!);
        await File.WriteAllTextAsync(originalSource, "class Game {}", cancellationToken);
        var originalDatabase = Path.Combine(originalWorkspace, ".csindex", "index.sqlite");
        var indexPaths = IndexPathResolver.CreateForIndex(originalDatabase, originalWorkspace);
        Assert.Equal("..", indexPaths.IndexRootAnchor);
        await new SqliteIndex(originalDatabase).SaveAsync(
            CreatePortableSnapshot(indexPaths.IndexRootAnchor),
            cancellationToken);

        var relocatedParent = Path.Combine(temporary.Path, "relocated");
        Directory.CreateDirectory(relocatedParent);
        var relocatedWorkspace = Path.Combine(relocatedParent, "workspace");
        Directory.Move(originalWorkspace, relocatedWorkspace);
        var relocatedDatabase = Path.Combine(relocatedWorkspace, ".csindex", "index.sqlite");
        var repository = new SqliteIndex(relocatedDatabase).CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var before = await DumpPortableRowsAsync(relocatedDatabase, cancellationToken);

        var relocatedResolver = IndexPathResolver.CreateForQuery(
            repository.DatabasePath,
            profile.IndexRootAnchor,
            baseDirectory: null);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(relocatedWorkspace, "src", "Game.cs")),
            relocatedResolver.ToAbsolutePath("src/Game.cs"));

        var overrideRoot = Path.Combine(temporary.Path, "override");
        var overrideResolver = IndexPathResolver.CreateForQuery(
            repository.DatabasePath,
            profile.IndexRootAnchor,
            overrideRoot);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(overrideRoot, "src", "Game.cs")),
            overrideResolver.ToAbsolutePath("src/Game.cs"));
        Assert.Equal(before, await DumpPortableRowsAsync(relocatedDatabase, cancellationToken));
    }

    [Fact]
    public async Task CustomDatabase_RelocatesWithDatabaseDirectoryRelativeAnchor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var originalLayout = Path.Combine(temporary.Path, "original-layout");
        var storageRoot = Path.Combine(originalLayout, "source");
        var databasePath = Path.Combine(originalLayout, "indexes", "Game", "index.sqlite");
        Directory.CreateDirectory(storageRoot);
        var paths = IndexPathResolver.CreateForIndex(databasePath, storageRoot);
        Assert.Equal("../../source", paths.IndexRootAnchor);
        await new SqliteIndex(databasePath).SaveAsync(
            CreatePortableSnapshot(paths.IndexRootAnchor),
            cancellationToken);

        var relocatedLayout = Path.Combine(temporary.Path, "relocated-layout");
        Directory.Move(originalLayout, relocatedLayout);
        var relocatedDatabase = Path.Combine(relocatedLayout, "indexes", "Game", "index.sqlite");
        var repository = new SqliteIndex(relocatedDatabase).CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        Assert.Equal("../../source", profile.IndexRootAnchor);
        var resolver = IndexPathResolver.CreateForQuery(
            repository.DatabasePath,
            profile.IndexRootAnchor,
            baseDirectory: null);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(relocatedLayout, "source")),
            resolver.EffectiveBaseDirectory);
    }

    [Fact]
    public async Task Save_PersistsLinkedParentPathAndNoMachineRootInPathsOrKeys()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var snapshot = CreatePortableSnapshot(".", "../Shared/Linked.cs");
        await new SqliteIndex(databasePath).SaveAsync(snapshot, cancellationToken);

        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value
            FROM (
                SELECT input_root AS value FROM index_runs
                UNION ALL SELECT index_root_anchor FROM index_runs
                UNION ALL SELECT project_path FROM projects WHERE project_path IS NOT NULL
                UNION ALL SELECT normalized_path FROM documents
                UNION ALL SELECT stable_key FROM symbols
                UNION ALL SELECT declaration_key FROM symbol_declarations
            );
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(0);
            Assert.False(Path.IsPathRooted(value), value);
            Assert.DoesNotContain('\\', value);
            Assert.DoesNotContain(temporary.Path, value, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("../Shared/Linked.cs", await DumpPortableRowsAsync(databasePath, cancellationToken));
    }

    private static IndexSnapshot CreatePortableSnapshot(
        string indexRootAnchor,
        string documentPath = "src/Game.cs")
    {
        const string projectKey = "project-path:src/Game.csproj";
        var documentKey = $"{projectKey}|document:{documentPath}";
        const string symbolKey = "portable-symbol";
        const string source = "void Run(){}";
        var declarationKey =
            $"{symbolKey}|declaration:{documentPath}:0:{source.Length}:{(int)DeclarationRole.Ordinary}";
        var snapshot = new IndexSnapshot
        {
            Profile = new AnalysisProfileData
            {
                Name = "portable-round-trip",
                InputMode = InputMode.Directory,
                OperatingSystem = "Windows",
                Architecture = "x64",
                PreprocessorSymbols = [],
                ProfileHash = [1],
            },
            InputRoot = ".",
            IndexRootAnchor = indexRootAnchor,
            InputFingerprint = [2],
            RequestHash = [3],
        };
        snapshot.Projects.Add(new ProjectData
        {
            Key = projectKey,
            Name = "Game",
            ProjectPath = "src/Game.csproj",
            Fingerprint = [4],
        });
        snapshot.Documents.Add(new DocumentData
        {
            Key = documentKey,
            ProjectKey = projectKey,
            NormalizedPath = documentPath,
            ContentHash = [5],
            IsGenerated = false,
            GenerationKind = GenerationKind.None,
        });
        snapshot.Symbols.Add(symbolKey, new SymbolData
        {
            StableKey = symbolKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Method,
            Name = "Run",
            NamespaceName = "Game",
            TypeSimpleName = "Player",
            TypeMetadataName = "Player",
            FullyQualifiedName = string.Empty,
            DisplayName = string.Empty,
            Path = new SymbolPathData(
                "Game",
                "Player",
                "Player",
                "Run()",
                "Run()",
                "Run()",
                "Run()",
                CallablePathSegmentKind.Named),
            PreferredDeclarationKey = declarationKey,
            ParameterCount = 0,
        });
        snapshot.Declarations.Add(declarationKey, new SymbolDeclarationData
        {
            Key = declarationKey,
            SymbolKey = symbolKey,
            DocumentKey = documentKey,
            Role = DeclarationRole.Ordinary,
            SourceStart = 0,
            SourceLength = source.Length,
            NormalizedSource = source,
            NormalizedSourceHash = [6],
            IsGenerated = false,
        });
        return snapshot;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<string> DumpPortableRowsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                r.input_root || '|' || r.index_root_anchor || '|' ||
                p.project_path || '|' || d.normalized_path || '|' ||
                s.stable_key || '|' || sd.declaration_key
            FROM index_runs r
            JOIN projects p ON p.index_run_id = r.id
            JOIN documents d ON d.project_id = p.id
            JOIN symbols s ON s.analysis_profile_id = r.analysis_profile_id
            JOIN symbol_declarations sd ON sd.symbol_id = s.id;
            """;
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
