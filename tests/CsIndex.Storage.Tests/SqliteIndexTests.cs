using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Storage.Schema;
using Microsoft.Data.Sqlite;

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
    public async Task Save_RestoresAsyncAnalysisFromDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var snapshot = CreateSnapshot(temporary.Path);
        var index = new SqliteIndex(databasePath);

        await index.SaveAsync(snapshot, cancellationToken);

        var repository = new SqliteIndex(databasePath).CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var caller = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "Caller",
            cancellationToken: cancellationToken));
        var call = Assert.Single(await repository.GetCallsByCallerAsync(
            profile.Id,
            [caller.Id],
            GeneratedFilter.Include,
            cancellationToken: cancellationToken));

        Assert.Equal(3, SchemaMigrator.CurrentVersion);
        Assert.Equal(3, RequestHasher.SchemaVersion);
        Assert.Equal(AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable, caller.AsyncRole);
        Assert.Equal(0, caller.AsyncInvolvementDepth);
        Assert.Equal(AsyncUsageKind.Awaited, call.AsyncUsageKind);
    }

    [Fact]
    public async Task Save_PersistsTypeKindAndInterfaceMethodBindings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));

        await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var interfaceType = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "IPlayable",
            kind: IndexedSymbolKind.Type,
            cancellationToken: cancellationToken));
        var contract = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "Play",
            typeSimpleName: "IPlayable",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken));
        var bindings = await repository.GetInterfaceMethodBindingsAsync(
            profile.Id,
            [contract.Id],
            cancellationToken);

        Assert.Equal(3, SchemaMigrator.CurrentVersion);
        Assert.Equal(3, RequestHasher.SchemaVersion);
        Assert.Equal((int)IndexedTypeKind.Interface, interfaceType.TypeKind);
        Assert.Equal((int)IndexedAccessibility.Public, interfaceType.Accessibility);
        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding => Assert.Equal(contract.Id, binding.InterfaceMethodId));
    }

    [Fact]
    public async Task GetInterfaceMethodBindingsAsync_OrdersSharedImplementationByInterfaceMethodId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));

        await index.SaveAsync(
            CreateOverrideSearchSnapshot(temporary.Path, includeBindingOrderFixture: true),
            cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var leftContract = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "Play",
            typeSimpleName: "ILeft",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken));
        var rightContract = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "Play",
            typeSimpleName: "IRight",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken));
        var implementingType = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "DualPlayer",
            kind: IndexedSymbolKind.Type,
            cancellationToken: cancellationToken));
        var implementation = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "Play",
            typeSimpleName: "DualPlayer",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken));

        var bindings = await repository.GetInterfaceMethodBindingsAsync(
            profile.Id,
            [rightContract.Id, leftContract.Id],
            cancellationToken);

        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding =>
        {
            Assert.Equal(implementingType.Id, binding.ImplementingTypeId);
            Assert.Equal(implementation.Id, binding.ImplementationMethodId);
        });
        Assert.Equal(
            new[] { leftContract.Id, rightContract.Id }.Order().ToArray(),
            bindings.Select(binding => binding.InterfaceMethodId));
    }

    [Fact]
    public async Task Save_ReplacementRemovesPriorInterfaceMethodBindingsWithoutAffectingOtherProfile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        var first = CreateOverrideSearchSnapshot(temporary.Path, "first");
        var second = CreateOverrideSearchSnapshot(temporary.Path, "second");

        await index.SaveAsync(first, cancellationToken);
        await index.SaveAsync(second, cancellationToken);
        first.InterfaceMethodBindings.Clear();
        await index.SaveAsync(first, cancellationToken);

        var repository = index.CreateQueryRepository();
        var firstProfile = await repository.GetProfileAsync("first", cancellationToken);
        var secondProfile = await repository.GetProfileAsync("second", cancellationToken);
        var firstContract = Assert.Single(await repository.FindSymbolCandidatesAsync(
            firstProfile.Id,
            name: "Play",
            typeSimpleName: "IPlayable",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken));
        var secondContract = Assert.Single(await repository.FindSymbolCandidatesAsync(
            secondProfile.Id,
            name: "Play",
            typeSimpleName: "IPlayable",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken));

        Assert.Empty(await repository.GetInterfaceMethodBindingsAsync(
            firstProfile.Id,
            [firstContract.Id],
            cancellationToken));
        Assert.NotEmpty(await repository.GetInterfaceMethodBindingsAsync(
            secondProfile.Id,
            [secondContract.Id],
            cancellationToken));
    }

    [Fact]
    public async Task FindFunctionSymbolsAsync_ReturnsFunctionKindsAndAsyncInvolvedSubset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var index = new SqliteIndex(databasePath);
        await index.SaveAsync(CreateLambdaCallSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);

        var functions = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            kind: null,
            asyncInvolved: false,
            cancellationToken);
        var lambdas = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            IndexedSymbolKind.Lambda,
            asyncInvolved: false,
            cancellationToken);
        var asyncInvolved = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            kind: null,
            asyncInvolved: true,
            cancellationToken);

        Assert.Equal(
            ["Local", "Nested lambda", "Outer lambda", "Root", "Same", "Same", "Same", "Unrelated"],
            functions.Select(symbol => symbol.Name).Order());
        Assert.Equal(
            ["same-generated", "same-source-earlier", "same-source-later"],
            functions.Where(symbol => symbol.DisplayName == "Same").Select(symbol => symbol.StableKey));
        Assert.Equal(
            ["Nested lambda", "Outer lambda"],
            lambdas.Select(symbol => symbol.Name).Order());
        Assert.Equal(
            ["Nested lambda", "Outer lambda", "Root"],
            asyncInvolved.Select(symbol => symbol.Name).Order());
    }

    [Fact]
    public async Task GetCallsByCallerIncludingLambdaDescendantsAsync_ReturnsRootsAndNestedLambdaCallers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var index = new SqliteIndex(databasePath);
        await index.SaveAsync(CreateLambdaCallSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var root = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "Root",
            cancellationToken: cancellationToken));

        var allCalls = await repository.GetCallsByCallerIncludingLambdaDescendantsAsync(
            profile.Id,
            [root.Id],
            GeneratedFilter.Include,
            referenceKinds: null,
            cancellationToken);
        var invocationCalls = await repository.GetCallsByCallerIncludingLambdaDescendantsAsync(
            profile.Id,
            [root.Id],
            GeneratedFilter.Include,
            new HashSet<ReferenceKind> { ReferenceKind.Invocation },
            cancellationToken);
        var nonGeneratedCalls = await repository.GetCallsByCallerIncludingLambdaDescendantsAsync(
            profile.Id,
            [root.Id],
            GeneratedFilter.Exclude,
            referenceKinds: null,
            cancellationToken);
        var generatedCalls = await repository.GetCallsByCallerIncludingLambdaDescendantsAsync(
            profile.Id,
            [root.Id],
            GeneratedFilter.Only,
            referenceKinds: null,
            cancellationToken);

        Assert.Equal(
            ["Nested lambda", "Outer lambda", "Root"],
            allCalls.Select(call => call.CallerDisplayName).Order());
        Assert.Equal(
            ["Nested lambda", "Root"],
            invocationCalls.Select(call => call.CallerDisplayName).Order());
        Assert.Equal(
            ["Outer lambda", "Root"],
            nonGeneratedCalls.Select(call => call.CallerDisplayName).Order());
        Assert.Equal(["Nested lambda"], generatedCalls.Select(call => call.CallerDisplayName));
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

    [Fact]
    public async Task VersionOneDatabase_ProducesExplicitErrorWithoutModification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "version-one.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        string journalModeBefore;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            journalModeBefore = Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
            command.CommandText = """
                CREATE TABLE schema_info(version INTEGER NOT NULL);
                INSERT INTO schema_info(version) VALUES (1);
                CREATE TABLE version_one_marker(id INTEGER PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var exception = await Assert.ThrowsAsync<IndexDatabaseException>(() =>
            new SqliteIndex(databasePath).EnsureCreatedAsync(cancellationToken));

        Assert.Contains("Unsupported database schema version 1", exception.Message, StringComparison.Ordinal);
        await using var verificationConnection = new SqliteConnection(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        await using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "PRAGMA journal_mode;";
        var journalModeAfter = Assert.IsType<string>(
            await verificationCommand.ExecuteScalarAsync(cancellationToken));
        Assert.Equal(journalModeBefore, journalModeAfter);
        verificationCommand.CommandText = """
            SELECT version,
                   (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'version_one_marker')
            FROM schema_info;
            """;
        await using var reader = await verificationCommand.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task VersionTwoDatabase_ProducesExplicitErrorWithoutModification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "version-two.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        string journalModeBefore;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            journalModeBefore = Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
            command.CommandText = """
                CREATE TABLE schema_info(version INTEGER NOT NULL);
                INSERT INTO schema_info(version) VALUES (2);
                CREATE TABLE version_two_marker(id INTEGER PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var exception = await Assert.ThrowsAsync<IndexDatabaseException>(() =>
            new SqliteIndex(databasePath).EnsureCreatedAsync(cancellationToken));

        Assert.Contains("Unsupported database schema version 2", exception.Message, StringComparison.Ordinal);
        await using var verificationConnection = new SqliteConnection(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        await using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "PRAGMA journal_mode;";
        var journalModeAfter = Assert.IsType<string>(
            await verificationCommand.ExecuteScalarAsync(cancellationToken));
        Assert.Equal(journalModeBefore, journalModeAfter);
        verificationCommand.CommandText = """
            SELECT version,
                   (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'version_two_marker')
            FROM schema_info;
            """;
        await using var reader = await verificationCommand.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task UnrecognizedNonEmptyDatabase_ProducesExplicitErrorWithoutModification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "unrecognized.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        string journalModeBefore;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            journalModeBefore = Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
            command.CommandText = """
                CREATE TABLE marker(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO marker(value) VALUES ('preserve-me');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var exception = await Record.ExceptionAsync(() =>
            new SqliteIndex(databasePath).EnsureCreatedAsync(cancellationToken));

        await using var verificationConnection = new SqliteConnection(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        await using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "PRAGMA journal_mode;";
        var journalModeAfter = Assert.IsType<string>(
            await verificationCommand.ExecuteScalarAsync(cancellationToken));
        verificationCommand.CommandText = """
            SELECT (SELECT COUNT(*) FROM marker WHERE value = 'preserve-me'),
                   (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_info'),
                   (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'symbols');
            """;
        await using var reader = await verificationCommand.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var markerRows = reader.GetInt32(0);
        var schemaInfoTables = reader.GetInt32(1);
        var symbolsTables = reader.GetInt32(2);

        Assert.True(
            exception is IndexDatabaseException &&
            markerRows == 1 &&
            schemaInfoTables == 0 &&
            symbolsTables == 0 &&
            string.Equals(journalModeBefore, journalModeAfter, StringComparison.OrdinalIgnoreCase),
            $"exception={exception?.GetType().Name ?? "none"}; markerRows={markerRows}; " +
            $"schemaInfoTables={schemaInfoTables}; symbolsTables={symbolsTables}; " +
            $"journalModeBefore={journalModeBefore}; journalModeAfter={journalModeAfter}");
    }

    [Fact]
    public async Task UnrecognizedNonEmptyDatabase_WithSqliteLikeUserTableName_IsNotModified()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "sqlite-like-name.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        string journalModeBefore;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            journalModeBefore = Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
            command.CommandText = """
                CREATE TABLE sqliteXmarker(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO sqliteXmarker(value) VALUES ('preserve-me');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var exception = await Record.ExceptionAsync(() =>
            new SqliteIndex(databasePath).EnsureCreatedAsync(cancellationToken));

        await using var verificationConnection = new SqliteConnection(connectionString);
        await verificationConnection.OpenAsync(cancellationToken);
        await using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "PRAGMA journal_mode;";
        var journalModeAfter = Assert.IsType<string>(
            await verificationCommand.ExecuteScalarAsync(cancellationToken));
        verificationCommand.CommandText = """
            SELECT (SELECT COUNT(*) FROM sqliteXmarker WHERE value = 'preserve-me'),
                   (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_info');
            """;
        await using var reader = await verificationCommand.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var markerRows = reader.GetInt32(0);
        var schemaInfoTables = reader.GetInt32(1);

        Assert.True(
            exception is IndexDatabaseException &&
            markerRows == 1 &&
            schemaInfoTables == 0 &&
            string.Equals(journalModeBefore, journalModeAfter, StringComparison.OrdinalIgnoreCase),
            $"exception={exception?.GetType().Name ?? "none"}; markerRows={markerRows}; " +
            $"schemaInfoTables={schemaInfoTables}; journalModeBefore={journalModeBefore}; " +
            $"journalModeAfter={journalModeAfter}");
    }

    private static IndexSnapshot CreateSnapshot(string root, string profileName = "test")
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
                Name = profileName,
                InputMode = InputMode.Directory,
                RuntimeIdentifier = "win-x64",
                OperatingSystem = "Windows",
                Architecture = "x64",
                PreprocessorSymbols = ["WINDOWS"],
                ProfileHash = HashUtilities.Sha256($"profile-{profileName}"),
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
        snapshot.Symbols["caller"] = new SymbolData
        {
            StableKey = "caller",
            ProjectKey = "project",
            Kind = IndexedSymbolKind.Method,
            Name = "Caller",
            NamespaceName = string.Empty,
            FullyQualifiedName = "Sample.Caller()",
            DisplayName = "Sample.Caller()",
            ContainingSymbolKey = "sample",
            ParameterCount = 0,
            AsyncRole = AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable,
            AsyncInvolvementDepth = 0,
            SourceDocumentKey = "project|source",
            SourceStart = 7,
            SourceLength = 6,
        };
        snapshot.Symbols["callee"] = new SymbolData
        {
            StableKey = "callee",
            ProjectKey = "project",
            Kind = IndexedSymbolKind.Method,
            Name = "Callee",
            NamespaceName = string.Empty,
            FullyQualifiedName = "Sample.Callee()",
            DisplayName = "Sample.Callee()",
            ContainingSymbolKey = "sample",
            ParameterCount = 0,
            SourceDocumentKey = "project|source",
            SourceStart = 14,
            SourceLength = 6,
        };
        snapshot.Calls.Add(new CallData
        {
            CallerSymbolKey = "caller",
            CalleeSymbolKey = "callee",
            CalleeDefinitionKey = "callee",
            ReferenceKind = ReferenceKind.Invocation,
            DispatchKind = DispatchKind.Static,
            ResolutionStatus = ResolutionStatus.Resolved,
            ResolutionReason = ResolutionReason.None,
            AsyncUsageKind = AsyncUsageKind.Awaited,
            DocumentKey = "project|source",
            SourceStart = 21,
            SourceLength = 6,
        });
        return snapshot;
    }

    private static IndexSnapshot CreateOverrideSearchSnapshot(
        string root,
        string profileName = "override",
        bool includeBindingOrderFixture = false)
    {
        var snapshot = CreateSnapshot(root, profileName);
        snapshot.Symbols.Clear();
        snapshot.Calls.Clear();

        AddType("i-playable", "IPlayable", IndexedTypeKind.Interface, 0);
        AddMethod("i-playable-play", "IPlayable", "i-playable", 10);
        AddType("pianist", "Pianist", IndexedTypeKind.Class, 20);
        AddMethod("pianist-play", "Pianist", "pianist", 30);
        AddType("game", "Game", IndexedTypeKind.Class, 40);
        AddMethod("game-play", "Game", "game", 50);
        snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
        {
            ImplementingTypeKey = "pianist",
            InterfaceMethodKey = "i-playable-play",
            ImplementationMethodKey = "pianist-play",
        });
        snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
        {
            ImplementingTypeKey = "game",
            InterfaceMethodKey = "i-playable-play",
            ImplementationMethodKey = "game-play",
        });
        if (includeBindingOrderFixture)
        {
            AddType("i-left", "ILeft", IndexedTypeKind.Interface, 60);
            AddMethod("i-left-play", "ILeft", "i-left", 70);
            AddType("i-right", "IRight", IndexedTypeKind.Interface, 80);
            AddMethod("i-right-play", "IRight", "i-right", 90);
            AddType("dual-player", "DualPlayer", IndexedTypeKind.Class, 100);
            AddMethod("dual-player-play", "DualPlayer", "dual-player", 110);
            snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
            {
                ImplementingTypeKey = "dual-player",
                InterfaceMethodKey = "i-right-play",
                ImplementationMethodKey = "dual-player-play",
            });
            snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
            {
                ImplementingTypeKey = "dual-player",
                InterfaceMethodKey = "i-left-play",
                ImplementationMethodKey = "dual-player-play",
            });
        }
        return snapshot;

        void AddType(string stableKey, string name, IndexedTypeKind typeKind, int sourceStart)
        {
            snapshot.Symbols[stableKey] = new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = "project",
                Kind = IndexedSymbolKind.Type,
                Name = name,
                NamespaceName = string.Empty,
                TypeSimpleName = name,
                TypeMetadataName = name,
                FullyQualifiedName = name,
                DisplayName = name,
                TypeKind = (int)typeKind,
                Accessibility = (int)IndexedAccessibility.Public,
                SourceDocumentKey = "project|source",
                SourceStart = sourceStart,
                SourceLength = name.Length,
            };
        }

        void AddMethod(string stableKey, string typeName, string containingTypeKey, int sourceStart)
        {
            snapshot.Symbols[stableKey] = new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = "project",
                Kind = IndexedSymbolKind.Method,
                Name = "Play",
                NamespaceName = string.Empty,
                TypeSimpleName = typeName,
                FullyQualifiedName = $"{typeName}.Play()",
                DisplayName = $"{typeName}.Play()",
                ContainingSymbolKey = containingTypeKey,
                ParameterCount = 0,
                Accessibility = (int)IndexedAccessibility.Public,
                SourceDocumentKey = "project|source",
                SourceStart = sourceStart,
                SourceLength = 4,
            };
        }
    }

    private static IndexSnapshot CreateLambdaCallSnapshot(string root)
    {
        var snapshot = CreateSnapshot(root);
        snapshot.Symbols.Clear();
        snapshot.Calls.Clear();
        snapshot.Documents.Add(new DocumentData
        {
            Key = "project|generated",
            ProjectKey = "project",
            NormalizedPath = Path.Combine(root, "Generated.cs"),
            ContentHash = HashUtilities.Sha256("generated"),
            IsGenerated = true,
            GenerationKind = GenerationKind.FileName,
        });
        AddSymbol("root", IndexedSymbolKind.Method, "Root", null, 0, "project|source", 0);
        AddSymbol("local", IndexedSymbolKind.Method, "Local", "root", null, "project|source", 10);
        AddSymbol("outer", IndexedSymbolKind.Lambda, "Outer lambda", "local", 1, "project|source", 20);
        AddSymbol("nested", IndexedSymbolKind.Lambda, "Nested lambda", "outer", 2, "project|generated", 30);
        AddSymbol("unrelated", IndexedSymbolKind.Method, "Unrelated", null, null, "project|source", 40);
        AddSymbol("same-generated", IndexedSymbolKind.Method, "Same", null, null, "project|generated", 100);
        AddSymbol("same-source-later", IndexedSymbolKind.Method, "Same", null, null, "project|source", 110);
        AddSymbol("same-source-earlier", IndexedSymbolKind.Method, "Same", null, null, "project|source", 105);

        AddCall("root", ReferenceKind.Invocation, "project|source", 50);
        AddCall("local", ReferenceKind.Invocation, "project|source", 60);
        AddCall("outer", ReferenceKind.MethodGroup, "project|source", 70);
        AddCall("nested", ReferenceKind.Invocation, "project|generated", 80);
        AddCall("unrelated", ReferenceKind.Invocation, "project|source", 90);
        return snapshot;

        void AddSymbol(
            string stableKey,
            IndexedSymbolKind kind,
            string name,
            string? containingSymbolKey,
            int? asyncInvolvementDepth,
            string documentKey,
            int sourceStart)
        {
            snapshot.Symbols[stableKey] = new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = "project",
                Kind = kind,
                Name = name,
                NamespaceName = string.Empty,
                FullyQualifiedName = name,
                DisplayName = name,
                ContainingSymbolKey = containingSymbolKey,
                AsyncInvolvementDepth = asyncInvolvementDepth,
                SourceDocumentKey = documentKey,
                SourceStart = sourceStart,
                SourceLength = 5,
            };
        }

        void AddCall(string callerSymbolKey, ReferenceKind referenceKind, string documentKey, int sourceStart)
        {
            snapshot.Calls.Add(new CallData
            {
                CallerSymbolKey = callerSymbolKey,
                ReferenceKind = referenceKind,
                DispatchKind = DispatchKind.Static,
                ResolutionStatus = ResolutionStatus.Unresolved,
                ResolutionReason = ResolutionReason.Unknown,
                DocumentKey = documentKey,
                SourceStart = sourceStart,
                SourceLength = 1,
                UnresolvedName = "Target",
            });
        }
    }
}
