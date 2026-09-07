using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace CsIndex.Storage.Tests;

public sealed class SqliteIndexTests
{
    private static readonly SymbolPathFormatter PathFormatter = new();

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
            snapshot.InputRoot,
            snapshot.InputFingerprint,
            snapshot.RequestHash,
            cancellationToken));
        var profile = await index.CreateQueryRepository().GetProfileAsync(cancellationToken: cancellationToken);
        Assert.Equal("test", profile.Name);
    }

    [Fact]
    public async Task Save_CreatesVersionFiveSchemaWithSemanticPathsAndAsyncNextForeignKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var index = new SqliteIndex(databasePath);

        await index.SaveAsync(CreateSnapshot(temporary.Path), cancellationToken);

        Assert.Equal(5, SchemaMigrator.CurrentVersion);
        Assert.Equal(5, RequestHasher.SchemaVersion);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA table_info(symbols);";
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1), reader.GetString(2));
            }
        }

        Assert.Equal("TEXT", columns["return_type_key"]);
        Assert.Equal("TEXT", columns["type_identity_path"]);
        Assert.Equal("TEXT", columns["executable_identity_path"]);
        Assert.Equal("INTEGER", columns["async_next_symbol_id"]);
        Assert.DoesNotContain("normalized_source", columns.Keys);
        Assert.DoesNotContain("normalized_source_hash", columns.Keys);

        command.CommandText = "PRAGMA foreign_key_list(symbols);";
        var hasAsyncNextForeignKey = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                hasAsyncNextForeignKey |= reader.GetString(2) == "symbols" &&
                                          reader.GetString(3) == "async_next_symbol_id" &&
                                          reader.GetString(4) == "id" &&
                                          reader.GetString(6) == "SET NULL";
            }
        }

        Assert.True(hasAsyncNextForeignKey);

        var symbolIndexes = await ReadSymbolIndexesAsync(connection, cancellationToken);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_kind",
            ["analysis_profile_id", "kind"],
            isPartial: false);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_containing",
            ["analysis_profile_id", "containing_symbol_id"],
            isPartial: false);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_async_depth",
            ["analysis_profile_id", "async_involvement_depth"],
            isPartial: false);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_async_next",
            ["analysis_profile_id", "async_next_symbol_id"],
            isPartial: false);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_source_executable",
            ["analysis_profile_id", "kind"],
            isPartial: true);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_name",
            ["analysis_profile_id", "name"],
            isPartial: false);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_short_method",
            ["analysis_profile_id", "type_simple_name", "name", "parameter_count"],
            isPartial: false);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_namespace_type_method",
            ["analysis_profile_id", "namespace_name", "type_simple_name", "name", "parameter_count"],
            isPartial: false);
        AssertSymbolIndex(
            symbolIndexes,
            "ix_symbols_profile_path_identity",
            ["analysis_profile_id", "namespace_name", "type_identity_path", "executable_identity_path"],
            isPartial: false);
        Assert.DoesNotContain("ix_symbols_name", symbolIndexes.Keys);
        Assert.DoesNotContain("ix_symbols_short_method", symbolIndexes.Keys);
        Assert.DoesNotContain("ix_symbols_namespace_type_method", symbolIndexes.Keys);
        Assert.DoesNotContain("ix_symbols_fully_qualified", symbolIndexes.Keys);
    }

    [Fact]
    public async Task SymbolsIndexes_UseProfilePrefixedIndexesForRepresentativeRepositoryPredicates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var index = new SqliteIndex(databasePath);

        await index.SaveAsync(CreateIndexPlanSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync("query-plan", cancellationToken);
        var owner = await GetSymbolAsync(
            repository,
            profile.Id,
            "IndexOwner",
            IndexedSymbolKind.Type,
            cancellationToken);
        var asyncTarget = await GetSymbolAsync(
            repository,
            profile.Id,
            "AsyncTarget",
            IndexedSymbolKind.Method,
            cancellationToken,
            "IndexOwner");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var analyze = connection.CreateCommand())
        {
            analyze.CommandText = "ANALYZE;";
            await analyze.ExecuteNonQueryAsync(cancellationToken);
        }

        var sourceExecutablePlan = await ExplainQueryPlanAsync(
            connection,
            """
            SELECT s.id
            FROM symbols AS s
            WHERE s.analysis_profile_id = $profile_id
              AND s.kind IN ($method_kind, $lambda_kind)
              AND s.preferred_declaration_id IS NOT NULL;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$profile_id", profile.Id);
                command.Parameters.AddWithValue("$method_kind", (int)IndexedSymbolKind.Method);
                command.Parameters.AddWithValue("$lambda_kind", (int)IndexedSymbolKind.Lambda);
            },
            cancellationToken);
        AssertPlanUsesSymbolIndex(sourceExecutablePlan, "ix_symbols_profile_source_executable", "s");

        var ownerPlan = await ExplainQueryPlanAsync(
            connection,
            """
            SELECT child.id
            FROM symbols AS child
            WHERE child.analysis_profile_id = $profile_id
              AND child.containing_symbol_id = $containing_symbol_id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$profile_id", profile.Id);
                command.Parameters.AddWithValue("$containing_symbol_id", owner.Id);
            },
            cancellationToken);
        AssertPlanUsesSymbolIndex(ownerPlan, "ix_symbols_profile_containing", "child");

        var asyncDepthPlan = await ExplainQueryPlanAsync(
            connection,
            """
            SELECT s.id
            FROM symbols AS s
            WHERE s.analysis_profile_id = $profile_id
              AND s.async_involvement_depth IS NOT NULL;
            """,
            command => command.Parameters.AddWithValue("$profile_id", profile.Id),
            cancellationToken);
        AssertPlanUsesSymbolIndex(asyncDepthPlan, "ix_symbols_profile_async_depth", "s");

        var componentNamePlan = await ExplainQueryPlanAsync(
            connection,
            """
            SELECT s.id
            FROM symbols AS s
            WHERE s.analysis_profile_id = $profile_id
              AND s.namespace_name = $namespace_name
              AND s.type_simple_name = $type_simple_name
              AND s.name = $name
              AND s.parameter_count = $parameter_count;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$profile_id", profile.Id);
                command.Parameters.AddWithValue("$namespace_name", "Plans");
                command.Parameters.AddWithValue("$type_simple_name", "Component");
                command.Parameters.AddWithValue("$name", "Lookup");
                command.Parameters.AddWithValue("$parameter_count", 2);
            },
            cancellationToken);
        AssertPlanUsesSymbolIndex(
            componentNamePlan,
            "ix_symbols_profile_namespace_type_method",
            "s");

        var asyncNextPlan = await ExplainQueryPlanAsync(
            connection,
            """
            SELECT s.id
            FROM symbols AS s
            WHERE s.analysis_profile_id = $profile_id
              AND s.async_next_symbol_id = $async_next_symbol_id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$profile_id", profile.Id);
                command.Parameters.AddWithValue("$async_next_symbol_id", asyncTarget.Id);
            },
            cancellationToken);
        AssertPlanUsesSymbolIndex(asyncNextPlan, "ix_symbols_profile_async_next", "s");
    }

    [Fact]
    public async Task Save_RoundTripsExecutableMetadataAndAsyncNextAcrossAllSymbolReaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        const string normalizedSource = "async Task<int> Caller(){return 1;}";
        var normalizedSourceHash = HashUtilities.Sha256(normalizedSource);
        var snapshot = CreateSnapshot(temporary.Path);
        snapshot.Symbols["caller"] = snapshot.Symbols["caller"] with
        {
            MethodKind = 0,
            ReturnTypeKey = "System.Threading.Tasks.Task<System.Int32>",
        };
        var callerDeclarationKey = snapshot.Symbols["caller"].PreferredDeclarationKey!;
        snapshot.Declarations[callerDeclarationKey] = snapshot.Declarations[callerDeclarationKey] with
        {
            NormalizedSource = normalizedSource,
            NormalizedSourceHash = normalizedSourceHash,
        };
        snapshot.Symbols["callee"] = snapshot.Symbols["callee"] with
        {
            AsyncInvolvementDepth = 1,
            AsyncNextSymbolKey = "caller",
        };

        await index.SaveAsync(snapshot, cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var caller = Assert.Single(
            await repository.FindSymbolCandidatesAsync(
                profile.Id,
                name: "Caller",
                cancellationToken: cancellationToken),
            symbol => symbol.StableKey == "caller");
        var callee = Assert.Single(
            await repository.FindSymbolCandidatesAsync(
                profile.Id,
                name: "Callee",
                cancellationToken: cancellationToken),
            symbol => symbol.StableKey == "callee");

        AssertExecutableMetadata(caller);
        Assert.Equal(caller.Id, callee.AsyncNextSymbolId);

        var byId = Assert.Single(await repository.GetSymbolsByIdsAsync(
            profile.Id,
            [caller.Id],
            cancellationToken));
        var function = Assert.Single(
            await repository.FindFunctionSymbolsAsync(
                profile.Id,
                kind: IndexedSymbolKind.Method,
                asyncInvolved: false,
                cancellationToken),
            symbol => symbol.Id == caller.Id);

        AssertExecutableMetadata(byId);
        AssertExecutableMetadata(function);
        var declaration = Assert.Single(await repository.GetPreferredDeclarationsAsync(
            profile.Id,
            [caller.Id],
            includeSourceText: true,
            cancellationToken));
        Assert.Equal(normalizedSource, declaration.NormalizedSource);
        Assert.Equal(normalizedSourceHash, declaration.NormalizedSourceHash);
    }

    [Fact]
    public async Task QueryRepository_HandlesLargeIdListsWithoutSQLiteVariableLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));

        await index.SaveAsync(CreateSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var realSymbol = Assert.Single(await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "Caller",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken));
        var ids = Enumerable.Range(1, 32765)
            .Select(static value => -(long)value)
            .Prepend(realSymbol.Id)
            .ToArray();
        Assert.Equal(32766, ids.Length);

        var declarations = await repository.GetDeclarationsAsync(
            profile.Id,
            ids,
            includeSourceText: false,
            cancellationToken);

        var declaration = Assert.Single(declarations);
        Assert.Equal(realSymbol.Id, declaration.SymbolId);

        var symbols = await repository.GetSymbolsByIdsAsync(profile.Id, ids, cancellationToken);

        Assert.Equal(realSymbol.Id, Assert.Single(symbols).Id);
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

        Assert.Equal(5, SchemaMigrator.CurrentVersion);
        Assert.Equal(5, RequestHasher.SchemaVersion);
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

        Assert.Equal(5, SchemaMigrator.CurrentVersion);
        Assert.Equal(5, RequestHasher.SchemaVersion);
        Assert.Equal((int)IndexedTypeKind.Interface, interfaceType.TypeKind);
        Assert.Equal((int)IndexedAccessibility.Public, interfaceType.Accessibility);
        Assert.Equal(5, bindings.Count);
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
        RemoveCallableDeclarations(second);

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
    public async Task FindInheritedMethodCandidatesAsync_ReturnsAccessibleAncestorsAndHonorsAssemblies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var d1 = await GetSymbolAsync(repository, profile.Id, "D1", IndexedSymbolKind.Type, cancellationToken);
        var otherBranch = await GetSymbolAsync(
            repository,
            profile.Id,
            "OtherBranch",
            IndexedSymbolKind.Type,
            cancellationToken);
        var basePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Base");

        var inherited = await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [d1.Id],
            "Play",
            cancellationToken);

        Assert.Contains(inherited, row => row.ReceiverTypeId == d1.Id &&
                                          row.MethodId == basePlay.Id &&
                                          row.Depth == 1);
        Assert.Empty(await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [d1.Id],
            "PrivatePlay",
            cancellationToken));
        Assert.Empty(await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [d1.Id],
            "InternalPlay",
            cancellationToken));
        Assert.Empty(await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [d1.Id],
            "PrivateProtectedPlay",
            cancellationToken));
        Assert.NotEmpty(await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [d1.Id],
            "ProtectedPlay",
            cancellationToken));
        Assert.NotEmpty(await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [d1.Id],
            "ProtectedInternalPlay",
            cancellationToken));
        Assert.NotEmpty(await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [otherBranch.Id],
            "InternalPlay",
            cancellationToken));
        Assert.NotEmpty(await repository.FindInheritedMethodCandidatesAsync(
            profile.Id,
            [otherBranch.Id],
            "PrivateProtectedPlay",
            cancellationToken));
    }

    [Fact]
    public async Task FindInheritedMethodCandidatesAsync_InterfaceReceiverUsesImplementsAndTerminatesCycle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateCyclicOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var loop = await GetSymbolAsync(
            repository,
            profile.Id,
            "ILoop",
            IndexedSymbolKind.Type,
            cancellationToken);
        var basePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "BasePlay",
            IndexedSymbolKind.Method,
            cancellationToken,
            "ILoopBase");

        var inherited = await repository.FindInheritedMethodCandidatesAsync(
                profile.Id,
                [loop.Id],
                "BasePlay",
                cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        var candidate = Assert.Single(inherited);
        Assert.Equal(loop.Id, candidate.ReceiverTypeId);
        Assert.Equal(basePlay.Id, candidate.MethodId);
        Assert.Equal(1, candidate.Depth);
        Assert.Equal(inherited.Count, inherited.Distinct().Count());
    }

    [Fact]
    public async Task ExpandOverrideMethodIdsAsync_RestrictsExpansionToReceiverBranchAndOrdersIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var d1 = await GetSymbolAsync(repository, profile.Id, "D1", IndexedSymbolKind.Type, cancellationToken);
        var basePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Base");
        var d2Play = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "D2");
        var otherPlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "OtherBranch");

        var branchMethods = await repository.ExpandOverrideMethodIdsAsync(
            profile.Id,
            [new MethodSearchSeed(basePlay.Id, d1.Id)],
            cancellationToken);

        Assert.Contains(basePlay.Id, branchMethods);
        Assert.Contains(d2Play.Id, branchMethods);
        Assert.DoesNotContain(otherPlay.Id, branchMethods);
        Assert.Equal(branchMethods.Order(), branchMethods);
        Assert.Equal(branchMethods.Count, branchMethods.Distinct().Count());
    }

    [Fact]
    public async Task ExpandOverrideMethodIdsAsync_HandlesLargeSeedListsWithoutSQLiteVariableLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var d1 = await GetSymbolAsync(repository, profile.Id, "D1", IndexedSymbolKind.Type, cancellationToken);
        var basePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Base");
        var d2Play = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "D2");
        var seeds = Enumerable.Range(1, 16380)
            .Select(static value => new MethodSearchSeed(-value, -100_000L - value))
            .Prepend(new MethodSearchSeed(basePlay.Id, d1.Id))
            .ToArray();
        Assert.Equal(16381, seeds.Length);
        Assert.Equal(seeds.Length, seeds.Distinct().Count());

        var branchMethods = await repository.ExpandOverrideMethodIdsAsync(
            profile.Id,
            seeds,
            cancellationToken);

        Assert.Contains(basePlay.Id, branchMethods);
        Assert.Contains(d2Play.Id, branchMethods);
        Assert.Equal(branchMethods.Count, branchMethods.Distinct().Count());
    }

    [Fact]
    public async Task FindInterfaceImplementationMethodIdsAsync_UsesExactInterfaceScope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var iPlayable = await GetSymbolAsync(
            repository,
            profile.Id,
            "IPlayable",
            IndexedSymbolKind.Type,
            cancellationToken);
        var iAdvancedPlayable = await GetSymbolAsync(
            repository,
            profile.Id,
            "IAdvancedPlayable",
            IndexedSymbolKind.Type,
            cancellationToken);
        var interfacePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "IPlayable");
        var pianistPlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Pianist");
        var proPlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "ProPianist");
        var gamePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Game");
        var basePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Base");
        var d2Play = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "D2");
        var otherPlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "OtherBranch");

        var interfaceMethods = await repository.FindInterfaceImplementationMethodIdsAsync(
            profile.Id,
            [new InterfaceSearchSeed(interfacePlay.Id, iPlayable.Id)],
            cancellationToken);

        Assert.Contains(pianistPlay.Id, interfaceMethods);
        Assert.Contains(proPlay.Id, interfaceMethods);
        Assert.Contains(gamePlay.Id, interfaceMethods);
        Assert.Contains(basePlay.Id, interfaceMethods);
        Assert.Contains(d2Play.Id, interfaceMethods);
        Assert.DoesNotContain(otherPlay.Id, interfaceMethods);
        Assert.Equal(interfaceMethods.Order(), interfaceMethods);
        Assert.Equal(interfaceMethods.Count, interfaceMethods.Distinct().Count());

        var derivedInterfaceMethods = await repository.FindInterfaceImplementationMethodIdsAsync(
            profile.Id,
            [new InterfaceSearchSeed(interfacePlay.Id, iAdvancedPlayable.Id)],
            cancellationToken);

        Assert.Contains(basePlay.Id, derivedInterfaceMethods);
        Assert.Contains(d2Play.Id, derivedInterfaceMethods);
        Assert.DoesNotContain(pianistPlay.Id, derivedInterfaceMethods);
        Assert.DoesNotContain(proPlay.Id, derivedInterfaceMethods);
        Assert.DoesNotContain(gamePlay.Id, derivedInterfaceMethods);
        Assert.DoesNotContain(otherPlay.Id, derivedInterfaceMethods);
    }

    [Fact]
    public async Task FindInterfaceImplementationMethodIdsAsync_HandlesLargeSeedListsWithoutSQLiteVariableLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var interfaceType = await GetSymbolAsync(
            repository,
            profile.Id,
            "IPlayable",
            IndexedSymbolKind.Type,
            cancellationToken);
        var interfacePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "IPlayable");
        var basePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Base");
        var d2Play = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "D2");
        var seeds = Enumerable.Range(1, 16380)
            .Select(static value => new InterfaceSearchSeed(-value, -200_000L - value))
            .Prepend(new InterfaceSearchSeed(interfacePlay.Id, interfaceType.Id))
            .ToArray();
        Assert.Equal(16381, seeds.Length);
        Assert.Equal(seeds.Length, seeds.Distinct().Count());

        var implementationMethods = await repository.FindInterfaceImplementationMethodIdsAsync(
            profile.Id,
            seeds,
            cancellationToken);

        Assert.Contains(basePlay.Id, implementationMethods);
        Assert.Contains(d2Play.Id, implementationMethods);
        Assert.Equal(implementationMethods.Count, implementationMethods.Distinct().Count());
    }

    [Fact]
    public async Task RecursiveGraphQueries_TerminateOnSelfAndMultiNodeCycles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateCyclicOverrideSearchSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var loop = await GetSymbolAsync(repository, profile.Id, "ILoop", IndexedSymbolKind.Type, cancellationToken);
        var cycleA = await GetSymbolAsync(repository, profile.Id, "CycleA", IndexedSymbolKind.Type, cancellationToken);
        var interfacePlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "ILoop");
        var cycleAPlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "CycleA");
        var cycleBPlay = await GetSymbolAsync(
            repository,
            profile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "CycleB");
        var timeout = TimeSpan.FromSeconds(5);

        var inherited = await repository.FindInheritedMethodCandidatesAsync(
                profile.Id,
                [cycleA.Id],
                "Play",
                cancellationToken)
            .WaitAsync(timeout, cancellationToken);
        var overrides = await repository.ExpandOverrideMethodIdsAsync(
                profile.Id,
                [new MethodSearchSeed(cycleAPlay.Id, cycleA.Id)],
                cancellationToken)
            .WaitAsync(timeout, cancellationToken);
        var implementations = await repository.FindInterfaceImplementationMethodIdsAsync(
                profile.Id,
                [new InterfaceSearchSeed(interfacePlay.Id, loop.Id)],
                cancellationToken)
            .WaitAsync(timeout, cancellationToken);

        Assert.Contains(inherited, row => row.MethodId == cycleBPlay.Id);
        Assert.Equal(inherited.Count, inherited.Select(row => row.MethodId).Distinct().Count());
        Assert.Equal(new[] { cycleAPlay.Id, cycleBPlay.Id }.Order(), overrides);
        Assert.Equal(overrides.Count, overrides.Distinct().Count());
        Assert.Equal(new[] { cycleAPlay.Id, cycleBPlay.Id }.Order(), implementations);
        Assert.Equal(implementations.Count, implementations.Distinct().Count());
    }

    [Fact]
    public async Task CycleSafeGraphQueries_RespectAnalysisProfileIsolation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path, "graph"), cancellationToken);
        await index.SaveAsync(CreateSnapshot(temporary.Path, "other"), cancellationToken);

        var repository = index.CreateQueryRepository();
        var graphProfile = await repository.GetProfileAsync("graph", cancellationToken);
        var otherProfile = await repository.GetProfileAsync("other", cancellationToken);
        var d1 = await GetSymbolAsync(
            repository,
            graphProfile.Id,
            "D1",
            IndexedSymbolKind.Type,
            cancellationToken);
        var iPlayable = await GetSymbolAsync(
            repository,
            graphProfile.Id,
            "IPlayable",
            IndexedSymbolKind.Type,
            cancellationToken);
        var basePlay = await GetSymbolAsync(
            repository,
            graphProfile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "Base");
        var interfacePlay = await GetSymbolAsync(
            repository,
            graphProfile.Id,
            "Play",
            IndexedSymbolKind.Method,
            cancellationToken,
            "IPlayable");

        Assert.Empty(await repository.FindInheritedMethodCandidatesAsync(
            otherProfile.Id,
            [d1.Id],
            "Play",
            cancellationToken));
        Assert.Empty(await repository.ExpandOverrideMethodIdsAsync(
            otherProfile.Id,
            [new MethodSearchSeed(basePlay.Id, d1.Id)],
            cancellationToken));
        Assert.Empty(await repository.FindInterfaceImplementationMethodIdsAsync(
            otherProfile.Id,
            [new InterfaceSearchSeed(interfacePlay.Id, iPlayable.Id)],
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
            ["Local", "Nested lambda", "Outer lambda", "Root", "Same", "Same", "Same", "Sync involved", "Unrelated"],
            functions.Select(symbol => symbol.Name).Order());
        Assert.Equal(
            ["same-z-generated", "same-m-source-earlier", "same-a-source-later"],
            functions.Where(symbol => FormatPath(symbol) == "Global::Same").Select(symbol => symbol.StableKey));
        Assert.Equal(
            ["Nested lambda", "Outer lambda"],
            lambdas.Select(symbol => symbol.Name).Order());
        Assert.Equal(
            ["Nested lambda", "Outer lambda", "Root", "Sync involved"],
            asyncInvolved.Select(symbol => symbol.Name).Order());
    }

    [Fact]
    public async Task FindFunctionSymbolsAsync_FiltersDirectAsyncStatusAndComposesAsyncInvolved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateLambdaCallSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);

        var all = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            null,
            AsyncStatusFilter.All,
            false,
            cancellationToken);
        var async = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            null,
            AsyncStatusFilter.Async,
            false,
            cancellationToken);
        var syncInvolved = await repository.FindFunctionSymbolsAsync(
            profile.Id,
            null,
            AsyncStatusFilter.Sync,
            true,
            cancellationToken);

        Assert.Equal([1L, 2L, 3L, 4L, 7L, 6L, 5L, 8L, 9L], all.Select(symbol => symbol.Id));
        Assert.Equal([3L, 4L], async.Select(symbol => symbol.Id));
        Assert.Equal([2L, 8L], syncInvolved.Select(symbol => symbol.Id));
        Assert.Contains(
            syncInvolved,
            symbol => symbol.Kind == IndexedSymbolKind.Method &&
                      symbol.AsyncRole == AsyncRole.None &&
                      symbol.AsyncInvolvementDepth is not null);
    }

    [Fact]
    public async Task GetSymbolsByIdsAsync_OrdersDuplicateDisplayNamesDeterministically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateLambdaCallSnapshot(temporary.Path), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var candidates = (await repository.FindSymbolCandidatesAsync(
                profile.Id,
                name: "Same",
                cancellationToken: cancellationToken))
            .ToDictionary(symbol => symbol.StableKey);

        var symbols = await repository.GetSymbolsByIdsAsync(
            profile.Id,
            [
                candidates["same-a-source-later"].Id,
                candidates["same-z-generated"].Id,
                candidates["same-m-source-earlier"].Id,
                candidates["same-a-source-later"].Id,
            ],
            cancellationToken);

        Assert.Equal(
            ["same-z-generated", "same-m-source-earlier", "same-a-source-later"],
            symbols.Select(symbol => symbol.StableKey));
    }

    [Fact]
    public async Task FindSymbolCandidatesAsync_UsesIdAsTheFinalOrderingTieBreaker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var snapshot = CreateLambdaCallSnapshot(temporary.Path);
        AddFinalTieSymbol(snapshot, "final-z");
        AddFinalTieSymbol(snapshot, "final-a");
        FinalizeSnapshotForSchemaFive(snapshot);
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(snapshot, cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var first = await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "FinalTie",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken);
        var second = await repository.FindSymbolCandidatesAsync(
            profile.Id,
            name: "FinalTie",
            kind: IndexedSymbolKind.Method,
            cancellationToken: cancellationToken);

        Assert.Equal(2, first.Count);
        Assert.All(first, symbol =>
        {
            Assert.Equal(FormatPath(first[0]), FormatPath(symbol));
            Assert.Equal(first[0].DocumentPath, symbol.DocumentPath);
            Assert.Equal(first[0].SourceStart, symbol.SourceStart);
        });
        Assert.Equal(first.Select(symbol => symbol.Id).Order(), first.Select(symbol => symbol.Id));
        Assert.Equal(["final-a", "final-z"], first.Select(symbol => symbol.StableKey));
        Assert.Equal(first.Select(symbol => symbol.Id), second.Select(symbol => symbol.Id));
    }

    private static string FormatPath(StoredSymbol symbol) =>
        PathFormatter.Format(Assert.IsType<SymbolPathData>(symbol.Path), new SymbolPathFormatOptions());

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
        var symbolsById = (await repository.GetSymbolsByIdsAsync(
                profile.Id,
                allCalls.Select(call => call.CallerSymbolId),
                cancellationToken))
            .ToDictionary(symbol => symbol.Id);

        Assert.Equal(
            ["Nested lambda", "Outer lambda", "Root"],
            allCalls.Select(call => symbolsById[call.CallerSymbolId].Name).Order());
        Assert.Equal(
            ["Nested lambda", "Root"],
            invocationCalls.Select(call => symbolsById[call.CallerSymbolId].Name).Order());
        Assert.Equal(
            ["Outer lambda", "Root"],
            nonGeneratedCalls.Select(call => symbolsById[call.CallerSymbolId].Name).Order());
        Assert.Equal(["Nested lambda"], generatedCalls.Select(call => symbolsById[call.CallerSymbolId].Name));
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
            DocumentKey = "project|document:Source.cs",
            SourceStart = 0,
            SourceLength = 1,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => index.SaveAsync(invalid, cancellationToken));

        var symbols = await index.CreateQueryRepository().FindSymbolCandidatesAsync(
            (await index.CreateQueryRepository().GetProfileAsync(cancellationToken: cancellationToken)).Id,
            typeSimpleName: "Sample",
            kind: IndexedSymbolKind.Type,
            sourceOnly: false,
            cancellationToken: cancellationToken);
        Assert.Single(symbols);
    }

    [Fact]
    public async Task Save_MissingAsyncNextSymbolKeyDoesNotResolveAnotherProfileAndRollsBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateSnapshot(temporary.Path, "first"), cancellationToken);
        var other = CreateSnapshot(temporary.Path, "other");
        RemoveCallableDeclarations(other);
        await index.SaveAsync(other, cancellationToken);
        var invalid = CreateSnapshot(temporary.Path, "first");
        invalid.Symbols.Remove("caller");
        invalid.Calls.Clear();
        invalid.Symbols["callee"] = invalid.Symbols["callee"] with
        {
            AsyncInvolvementDepth = 1,
            AsyncNextSymbolKey = "caller",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => index.SaveAsync(invalid, cancellationToken));

        var repository = index.CreateQueryRepository();
        var firstProfile = await repository.GetProfileAsync("first", cancellationToken);
        var otherProfile = await repository.GetProfileAsync("other", cancellationToken);
        Assert.Single(await repository.FindSymbolCandidatesAsync(
            firstProfile.Id,
            name: "Caller",
            cancellationToken: cancellationToken));
        Assert.Single(await repository.FindSymbolCandidatesAsync(
            otherProfile.Id,
            name: "Caller",
            cancellationToken: cancellationToken));
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LegacySchemaVersionsRejectIndexAndQueryWithoutChangingBytes(int version)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, $"legacy-v{version}.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            await command.ExecuteScalarAsync(cancellationToken);
            command.CommandText = $"""
                CREATE TABLE schema_info(version INTEGER NOT NULL);
                INSERT INTO schema_info(version) VALUES ({version});
                CREATE TABLE legacy_sentinel(value TEXT NOT NULL);
                INSERT INTO legacy_sentinel(value) VALUES ('preserve-me');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var before = await File.ReadAllBytesAsync(databasePath, cancellationToken);
        var indexException = await Assert.ThrowsAsync<IndexDatabaseException>(() =>
            new SqliteIndex(databasePath).EnsureCreatedAsync(cancellationToken));
        AssertLegacySchemaGuidance(indexException, version);
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath, cancellationToken));

        var queryException = await Assert.ThrowsAsync<IndexDatabaseException>(() =>
            new SqliteIndex(databasePath).CreateQueryRepository()
                .GetProfileAsync(cancellationToken: cancellationToken));
        AssertLegacySchemaGuidance(queryException, version);
        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath, cancellationToken));

        var readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var verification = new SqliteConnection(readOnlyConnectionString);
        await verification.OpenAsync(cancellationToken);
        await using var verificationCommand = verification.CreateCommand();
        verificationCommand.CommandText = """
            SELECT version,
                   (SELECT value FROM legacy_sentinel LIMIT 1)
            FROM schema_info;
            """;
        await using var reader = await verificationCommand.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(version, reader.GetInt32(0));
        Assert.Equal("preserve-me", reader.GetString(1));
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
    public async Task VersionThreeDatabase_ProducesExplicitErrorWithoutRowObjectOrJournalModeMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "version-three.sqlite");
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
                INSERT INTO schema_info(version) VALUES (3);
                CREATE TABLE version_three_marker(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO version_three_marker(value) VALUES ('preserve-me');
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
            SELECT version,
                   (SELECT COUNT(*) FROM version_three_marker WHERE value = 'preserve-me'),
                   (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'version_three_marker')
            FROM schema_info;
            """;
        await using var reader = await verificationCommand.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var version = reader.GetInt32(0);
        var markerRows = reader.GetInt32(1);
        var markerTables = reader.GetInt32(2);

        Assert.True(
            exception is IndexDatabaseException indexException &&
            indexException.Message.Contains("Unsupported database schema version 3", StringComparison.Ordinal) &&
            version == 3 &&
            markerRows == 1 &&
            markerTables == 1 &&
            string.Equals(journalModeBefore, journalModeAfter, StringComparison.OrdinalIgnoreCase),
            $"exception={exception?.GetType().Name ?? "none"}; version={version}; markerRows={markerRows}; " +
            $"markerTables={markerTables}; journalModeBefore={journalModeBefore}; journalModeAfter={journalModeAfter}");
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

    private static void AssertLegacySchemaGuidance(IndexDatabaseException exception, int version)
    {
        Assert.Contains($"Unsupported database schema version {version}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("database was not modified", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Delete or rename the old database", exception.Message, StringComparison.Ordinal);
        Assert.Contains("choose a new --db path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("run csindex index explicitly", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertExecutableMetadata(StoredSymbol symbol)
    {
        Assert.Equal(0, symbol.MethodKind);
        Assert.Equal("System.Threading.Tasks.Task<System.Int32>", symbol.ReturnTypeKey);
        Assert.Null(symbol.NormalizedSource);
        Assert.Null(symbol.NormalizedSourceHash);
    }

    private sealed record SymbolIndexInfo(string[] Columns, bool IsPartial);

    private static void AssertSymbolIndex(
        IReadOnlyDictionary<string, SymbolIndexInfo> indexes,
        string indexName,
        string[] expectedColumns,
        bool isPartial)
    {
        Assert.True(indexes.ContainsKey(indexName), $"Missing symbols index '{indexName}'.");
        var index = indexes[indexName];
        Assert.Equal(expectedColumns, index.Columns);
        Assert.Equal(isPartial, index.IsPartial);
    }

    private static void AssertPlanUsesSymbolIndex(
        IReadOnlyList<string> plan,
        string indexName,
        string tableAlias)
    {
        Assert.Contains(plan, detail => detail.Contains(indexName, StringComparison.Ordinal));
        Assert.DoesNotContain(
            plan,
            detail => detail.StartsWith($"SCAN {tableAlias}", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<string>> ExplainQueryPlanAsync(
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        configure(command);
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plan.Add(reader.GetString(3));
        }

        return plan;
    }

    private static async Task<IReadOnlyDictionary<string, SymbolIndexInfo>> ReadSymbolIndexesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var partialByName = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list(symbols);";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                partialByName.Add(reader.GetString(1), reader.GetInt32(4) == 1);
            }
        }

        var indexes = new Dictionary<string, SymbolIndexInfo>(StringComparer.Ordinal);
        foreach (var (name, isPartial) in partialByName)
        {
            command.CommandText = $"PRAGMA index_info([{name}]);";
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(2));
            }

            indexes.Add(name, new SymbolIndexInfo(columns.ToArray(), isPartial));
        }

        return indexes;
    }

    private static async Task<StoredSymbol> GetSymbolAsync(
        QueryRepository repository,
        long profileId,
        string name,
        IndexedSymbolKind kind,
        CancellationToken cancellationToken,
        string? typeSimpleName = null)
    {
        return Assert.Single(await repository.FindSymbolCandidatesAsync(
            profileId,
            name,
            typeSimpleName,
            kind,
            cancellationToken: cancellationToken));
    }

    private static IndexSnapshot CreateSnapshot(string root, string profileName = "test")
    {
        var inputFingerprint = HashUtilities.Sha256("input");
        var requestHash = HashUtilities.Sha256("request");
        var snapshot = new IndexSnapshot
        {
            InputRoot = ".",
            IndexRootAnchor = ".",
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
            Key = "project|document:Source.cs",
            ProjectKey = "project",
            NormalizedPath = "Source.cs",
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
            SourceDocumentKey = "project|document:Source.cs",
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
            SourceDocumentKey = "project|document:Source.cs",
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
            SourceDocumentKey = "project|document:Source.cs",
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
            DocumentKey = "project|document:Source.cs",
            SourceStart = 21,
            SourceLength = 6,
        });
        return FinalizeSnapshotForSchemaFive(snapshot);
    }

    private static IndexSnapshot CreateIndexPlanSnapshot(string root)
    {
        var snapshot = CreateSnapshot(root, "query-plan");
        snapshot.Symbols.Clear();
        snapshot.Declarations.Clear();
        snapshot.Calls.Clear();

        snapshot.Symbols["index-owner"] = CreateSymbol(
            "index-owner",
            IndexedSymbolKind.Type,
            "IndexOwner",
            typeSimpleName: "IndexOwner",
            containingSymbolKey: null,
            sourceBacked: true);
        snapshot.Symbols["source-method"] = CreateSymbol(
            "source-method",
            IndexedSymbolKind.Method,
            "SourceMethod",
            typeSimpleName: "IndexOwner",
            sourceBacked: true);
        snapshot.Symbols["source-lambda"] = CreateSymbol(
            "source-lambda",
            IndexedSymbolKind.Lambda,
            "SourceLambda",
            typeSimpleName: "IndexOwner",
            sourceBacked: true);
        snapshot.Symbols["async-depth"] = CreateSymbol(
            "async-depth",
            IndexedSymbolKind.Method,
            "AsyncDepth",
            typeSimpleName: "IndexOwner",
            sourceBacked: true,
            asyncInvolvementDepth: 2);
        snapshot.Symbols["async-next"] = CreateSymbol(
            "async-next",
            IndexedSymbolKind.Method,
            "AsyncNext",
            typeSimpleName: "IndexOwner",
            sourceBacked: true,
            asyncInvolvementDepth: 1,
            asyncNextSymbolKey: "async-target");
        snapshot.Symbols["async-target"] = CreateSymbol(
            "async-target",
            IndexedSymbolKind.Method,
            "AsyncTarget",
            typeSimpleName: "IndexOwner",
            sourceBacked: true,
            asyncInvolvementDepth: 0);
        snapshot.Symbols["component-lookup"] = CreateSymbol(
            "component-lookup",
            IndexedSymbolKind.Method,
            "Lookup",
            typeSimpleName: "Component",
            sourceBacked: true,
            parameterCount: 2);

        for (var i = 0; i < 128; i++)
        {
            var stableKey = $"metadata-{i:D3}";
            snapshot.Symbols[stableKey] = CreateSymbol(
                stableKey,
                IndexedSymbolKind.Method,
                $"Metadata{i:D3}",
                typeSimpleName: "Metadata",
                sourceBacked: false);
        }

        return FinalizeSnapshotForSchemaFive(snapshot);

        static SymbolData CreateSymbol(
            string stableKey,
            IndexedSymbolKind kind,
            string name,
            string typeSimpleName,
            string? containingSymbolKey = "index-owner",
            bool sourceBacked = false,
            int? asyncInvolvementDepth = null,
            string? asyncNextSymbolKey = null,
            int? parameterCount = null)
        {
            return new SymbolData
            {
                StableKey = stableKey,
                ProjectKey = "project",
                Kind = kind,
                Name = name,
                NamespaceName = "Plans",
                TypeSimpleName = typeSimpleName,
                FullyQualifiedName = $"Plans.{typeSimpleName}.{name}",
                DisplayName = $"Plans.{typeSimpleName}.{name}",
                ContainingSymbolKey = containingSymbolKey,
                ParameterCount = parameterCount,
                AsyncInvolvementDepth = asyncInvolvementDepth,
                AsyncNextSymbolKey = asyncNextSymbolKey,
                SourceDocumentKey = sourceBacked ? "project|document:Source.cs" : null,
                SourceStart = sourceBacked ? 0 : null,
                SourceLength = sourceBacked ? 1 : null,
            };
        }
    }

    private static IndexSnapshot CreateOverrideSearchSnapshot(
        string root,
        string profileName = "override",
        bool includeBindingOrderFixture = false)
    {
        var snapshot = CreateSnapshot(root, profileName);
        snapshot.Symbols.Clear();
        snapshot.Declarations.Clear();
        snapshot.Calls.Clear();
        snapshot.Projects.Add(new ProjectData
        {
            Key = "external-project",
            Name = "ExternalProject",
            AssemblyName = "ExternalProject",
            Fingerprint = HashUtilities.Sha256("external-project"),
        });
        snapshot.Documents.Add(new DocumentData
        {
            Key = "external-project|document:ExternalSource.cs",
            ProjectKey = "external-project",
            NormalizedPath = "ExternalSource.cs",
            ContentHash = HashUtilities.Sha256("external-source"),
            IsGenerated = false,
            GenerationKind = GenerationKind.None,
        });

        AddGraphType(snapshot, "i-playable", "IPlayable", IndexedTypeKind.Interface, 0);
        AddGraphMethod(snapshot, "i-playable-play", "IPlayable", "i-playable", "Play", 10);
        AddGraphType(snapshot, "i-advanced-playable", "IAdvancedPlayable", IndexedTypeKind.Interface, 20);
        AddGraphType(snapshot, "pianist", "Pianist", IndexedTypeKind.Class, 30);
        AddGraphMethod(snapshot, "pianist-play", "Pianist", "pianist", "Play", 40, isVirtual: true);
        AddGraphType(snapshot, "pro-pianist", "ProPianist", IndexedTypeKind.Class, 50);
        AddGraphMethod(snapshot, "pro-pianist-play", "ProPianist", "pro-pianist", "Play", 60, isOverride: true);
        AddGraphType(snapshot, "game", "Game", IndexedTypeKind.Class, 70);
        AddGraphMethod(snapshot, "game-play", "Game", "game", "Play", 80);
        AddGraphType(snapshot, "base", "Base", IndexedTypeKind.Class, 90);
        AddGraphMethod(snapshot, "base-play", "Base", "base", "Play", 100, isVirtual: true);
        AddGraphMethod(
            snapshot,
            "base-private-play",
            "Base",
            "base",
            "PrivatePlay",
            110,
            IndexedAccessibility.Private);
        AddGraphMethod(
            snapshot,
            "base-internal-play",
            "Base",
            "base",
            "InternalPlay",
            120,
            IndexedAccessibility.Internal);
        AddGraphMethod(
            snapshot,
            "base-private-protected-play",
            "Base",
            "base",
            "PrivateProtectedPlay",
            130,
            IndexedAccessibility.ProtectedAndInternal);
        AddGraphMethod(
            snapshot,
            "base-protected-play",
            "Base",
            "base",
            "ProtectedPlay",
            140,
            IndexedAccessibility.Protected);
        AddGraphMethod(
            snapshot,
            "base-protected-internal-play",
            "Base",
            "base",
            "ProtectedInternalPlay",
            150,
            IndexedAccessibility.ProtectedOrInternal);
        AddGraphType(snapshot, "d1", "D1", IndexedTypeKind.Class, 160, "external-project");
        AddGraphType(snapshot, "d2", "D2", IndexedTypeKind.Class, 170, "external-project");
        AddGraphMethod(
            snapshot,
            "d2-play",
            "D2",
            "d2",
            "Play",
            180,
            projectKey: "external-project",
            isOverride: true);
        AddGraphType(snapshot, "other-branch", "OtherBranch", IndexedTypeKind.Class, 190);
        AddGraphMethod(
            snapshot,
            "other-branch-play",
            "OtherBranch",
            "other-branch",
            "Play",
            200,
            isOverride: true);

        AddGraphRelation(snapshot, "i-advanced-playable", "i-playable", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "pianist", "i-playable", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "pro-pianist", "pianist", SymbolRelationKind.Inherits);
        AddGraphRelation(snapshot, "game", "i-playable", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "d1", "base", SymbolRelationKind.Inherits);
        AddGraphRelation(snapshot, "d1", "i-playable", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "d1", "i-advanced-playable", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "d2", "d1", SymbolRelationKind.Inherits);
        AddGraphRelation(snapshot, "other-branch", "base", SymbolRelationKind.Inherits);
        AddGraphRelation(snapshot, "pro-pianist-play", "pianist-play", SymbolRelationKind.Overrides);
        AddGraphRelation(snapshot, "d2-play", "base-play", SymbolRelationKind.Overrides);
        AddGraphRelation(snapshot, "other-branch-play", "base-play", SymbolRelationKind.Overrides);

        AddGraphBinding(snapshot, "pianist", "i-playable-play", "pianist-play");
        AddGraphBinding(snapshot, "pro-pianist", "i-playable-play", "pro-pianist-play");
        AddGraphBinding(snapshot, "game", "i-playable-play", "game-play");
        AddGraphBinding(snapshot, "d1", "i-playable-play", "base-play");
        AddGraphBinding(snapshot, "d2", "i-playable-play", "d2-play");
        if (includeBindingOrderFixture)
        {
            AddGraphType(snapshot, "i-left", "ILeft", IndexedTypeKind.Interface, 210);
            AddGraphMethod(snapshot, "i-left-play", "ILeft", "i-left", "Play", 220);
            AddGraphType(snapshot, "i-right", "IRight", IndexedTypeKind.Interface, 230);
            AddGraphMethod(snapshot, "i-right-play", "IRight", "i-right", "Play", 240);
            AddGraphType(snapshot, "dual-player", "DualPlayer", IndexedTypeKind.Class, 250);
            AddGraphMethod(snapshot, "dual-player-play", "DualPlayer", "dual-player", "Play", 260);
            AddGraphBinding(snapshot, "dual-player", "i-right-play", "dual-player-play");
            AddGraphBinding(snapshot, "dual-player", "i-left-play", "dual-player-play");
        }

        return FinalizeSnapshotForSchemaFive(snapshot);
    }

    private static IndexSnapshot CreateCyclicOverrideSearchSnapshot(string root)
    {
        var snapshot = CreateSnapshot(root, "cycle");
        snapshot.Symbols.Clear();
        snapshot.Declarations.Clear();
        snapshot.Calls.Clear();

        AddGraphType(snapshot, "i-loop", "ILoop", IndexedTypeKind.Interface, 0);
        AddGraphMethod(snapshot, "i-loop-play", "ILoop", "i-loop", "Play", 10);
        AddGraphType(snapshot, "i-loop-base", "ILoopBase", IndexedTypeKind.Interface, 20);
        AddGraphMethod(snapshot, "i-loop-base-play", "ILoopBase", "i-loop-base", "BasePlay", 30);
        AddGraphType(snapshot, "cycle-a", "CycleA", IndexedTypeKind.Class, 40);
        AddGraphMethod(snapshot, "cycle-a-play", "CycleA", "cycle-a", "Play", 50, isVirtual: true);
        AddGraphType(snapshot, "cycle-b", "CycleB", IndexedTypeKind.Class, 60);
        AddGraphMethod(snapshot, "cycle-b-play", "CycleB", "cycle-b", "Play", 70, isOverride: true);

        AddGraphRelation(snapshot, "i-loop", "i-loop", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "i-loop", "i-loop-base", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "i-loop-base", "i-loop", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "cycle-a", "i-loop", SymbolRelationKind.Implements);
        AddGraphRelation(snapshot, "cycle-a", "cycle-a", SymbolRelationKind.Inherits);
        AddGraphRelation(snapshot, "cycle-a", "cycle-b", SymbolRelationKind.Inherits);
        AddGraphRelation(snapshot, "cycle-b", "cycle-a", SymbolRelationKind.Inherits);
        AddGraphRelation(snapshot, "cycle-a-play", "cycle-a-play", SymbolRelationKind.Overrides);
        AddGraphRelation(snapshot, "cycle-b-play", "cycle-a-play", SymbolRelationKind.Overrides);
        AddGraphRelation(snapshot, "cycle-a-play", "cycle-b-play", SymbolRelationKind.Overrides);

        AddGraphBinding(snapshot, "cycle-a", "i-loop-play", "cycle-a-play");
        AddGraphBinding(snapshot, "cycle-b", "i-loop-play", "cycle-b-play");
        return FinalizeSnapshotForSchemaFive(snapshot);
    }

    private static void AddGraphType(
        IndexSnapshot snapshot,
        string stableKey,
        string name,
        IndexedTypeKind typeKind,
        int sourceStart,
        string projectKey = "project")
    {
        snapshot.Symbols[stableKey] = new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Type,
            Name = name,
            NamespaceName = string.Empty,
            TypeSimpleName = name,
            TypeMetadataName = name,
            FullyQualifiedName = name,
            DisplayName = name,
            TypeKind = (int)typeKind,
            Accessibility = (int)IndexedAccessibility.Public,
            SourceDocumentKey = $"{projectKey}|document:{(projectKey == "project" ? "Source.cs" : "ExternalSource.cs")}",
            SourceStart = sourceStart,
            SourceLength = name.Length,
        };
    }

    private static void AddGraphMethod(
        IndexSnapshot snapshot,
        string stableKey,
        string typeName,
        string containingTypeKey,
        string methodName,
        int sourceStart,
        IndexedAccessibility accessibility = IndexedAccessibility.Public,
        string projectKey = "project",
        bool isVirtual = false,
        bool isOverride = false)
    {
        snapshot.Symbols[stableKey] = new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = projectKey,
            Kind = IndexedSymbolKind.Method,
            Name = methodName,
            NamespaceName = string.Empty,
            TypeSimpleName = typeName,
            FullyQualifiedName = $"{typeName}.{methodName}()",
            DisplayName = $"{typeName}.{methodName}()",
            ContainingSymbolKey = containingTypeKey,
            ParameterCount = 0,
            Accessibility = (int)accessibility,
            IsVirtual = isVirtual,
            IsOverride = isOverride,
            SourceDocumentKey = $"{projectKey}|document:{(projectKey == "project" ? "Source.cs" : "ExternalSource.cs")}",
            SourceStart = sourceStart,
            SourceLength = methodName.Length,
        };
    }

    private static void AddGraphRelation(
        IndexSnapshot snapshot,
        string sourceSymbolKey,
        string targetSymbolKey,
        SymbolRelationKind relationKind)
    {
        snapshot.Relations.Add(new SymbolRelationData
        {
            SourceSymbolKey = sourceSymbolKey,
            TargetSymbolKey = targetSymbolKey,
            RelationKind = relationKind,
        });
    }

    private static void AddGraphBinding(
        IndexSnapshot snapshot,
        string implementingTypeKey,
        string interfaceMethodKey,
        string implementationMethodKey)
    {
        snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
        {
            ImplementingTypeKey = implementingTypeKey,
            InterfaceMethodKey = interfaceMethodKey,
            ImplementationMethodKey = implementationMethodKey,
        });
    }

    private static void AddFinalTieSymbol(IndexSnapshot snapshot, string stableKey)
    {
        snapshot.Symbols[stableKey] = new SymbolData
        {
            StableKey = stableKey,
            ProjectKey = "project",
            Kind = IndexedSymbolKind.Method,
            Name = "FinalTie",
            NamespaceName = string.Empty,
            FullyQualifiedName = "FinalTie",
            DisplayName = "FinalTie",
            SourceDocumentKey = "project|document:Source.cs",
            SourceStart = 120,
            SourceLength = 5,
        };
    }

    private static IndexSnapshot CreateLambdaCallSnapshot(string root)
    {
        var snapshot = CreateSnapshot(root);
        snapshot.Symbols.Clear();
        snapshot.Declarations.Clear();
        snapshot.Calls.Clear();
        snapshot.Documents.Add(new DocumentData
        {
            Key = "project|document:Generated.cs",
            ProjectKey = "project",
            NormalizedPath = "Generated.cs",
            ContentHash = HashUtilities.Sha256("generated"),
            IsGenerated = true,
            GenerationKind = GenerationKind.FileName,
        });
        AddSymbol(
            "root",
            IndexedSymbolKind.Method,
            "Root",
            null,
            0,
            "project|document:Source.cs",
            0,
            AsyncRole.ReturnsAwaitable);
        AddSymbol("local", IndexedSymbolKind.Method, "Local", "root", null, "project|document:Source.cs", 10);
        AddSymbol(
            "outer",
            IndexedSymbolKind.Lambda,
            "Outer lambda",
            "local",
            1,
            "project|document:Source.cs",
            20,
            AsyncRole.ContainsAwait);
        AddSymbol("nested", IndexedSymbolKind.Lambda, "Nested lambda", "outer", 2, "project|document:Generated.cs", 30);
        AddSymbol("sync-involved", IndexedSymbolKind.Method, "Sync involved", null, 1, "project|document:Source.cs", 35);
        AddSymbol("unrelated", IndexedSymbolKind.Method, "Unrelated", null, null, "project|document:Source.cs", 40);
        AddSymbol("same-z-generated", IndexedSymbolKind.Method, "Same", null, null, "project|document:Generated.cs", 100);
        AddSymbol("same-a-source-later", IndexedSymbolKind.Method, "Same", null, null, "project|document:Source.cs", 110);
        AddSymbol("same-m-source-earlier", IndexedSymbolKind.Method, "Same", null, null, "project|document:Source.cs", 105);

        AddCall("root", ReferenceKind.Invocation, "project|document:Source.cs", 50);
        AddCall("local", ReferenceKind.Invocation, "project|document:Source.cs", 60);
        AddCall("outer", ReferenceKind.MethodGroup, "project|document:Source.cs", 70);
        AddCall("nested", ReferenceKind.Invocation, "project|document:Generated.cs", 80);
        AddCall("unrelated", ReferenceKind.Invocation, "project|document:Source.cs", 90);
        return FinalizeSnapshotForSchemaFive(snapshot);

        void AddSymbol(
            string stableKey,
            IndexedSymbolKind kind,
            string name,
            string? containingSymbolKey,
            int? asyncInvolvementDepth,
            string documentKey,
            int sourceStart,
            AsyncRole asyncRole = AsyncRole.None)
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
                AsyncRole = asyncRole,
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

    private static IndexSnapshot FinalizeSnapshotForSchemaFive(IndexSnapshot snapshot)
    {
        foreach (var pair in snapshot.Symbols.ToArray())
        {
            var symbol = pair.Value;
            var path = symbol.Path ?? CreateStoredTestPath(snapshot, symbol);
            var preferredDeclarationKey = symbol.PreferredDeclarationKey;

            if (symbol.Kind != IndexedSymbolKind.Type &&
                symbol.SourceDocumentKey is { } documentKey &&
                !snapshot.Declarations.Values.Any(value =>
                    value.SymbolKey.Equals(symbol.StableKey, StringComparison.Ordinal)))
            {
                var document = snapshot.Documents.Single(value =>
                    value.Key.Equals(documentKey, StringComparison.Ordinal));
                var sourceStart = symbol.SourceStart ?? 0;
                var sourceLength = symbol.SourceLength ?? Math.Max(1, symbol.Name.Length);
                var normalizedSource = symbol.NormalizedSource ?? symbol.Name;
                var declarationKey =
                    $"{symbol.StableKey}|declaration:{document.NormalizedPath}:{sourceStart}:{sourceLength}:{(int)DeclarationRole.Ordinary}";
                snapshot.Declarations[declarationKey] = new SymbolDeclarationData
                {
                    Key = declarationKey,
                    SymbolKey = symbol.StableKey,
                    DocumentKey = documentKey,
                    Role = DeclarationRole.Ordinary,
                    SourceStart = sourceStart,
                    SourceLength = sourceLength,
                    NormalizedSource = normalizedSource,
                    NormalizedSourceHash = symbol.NormalizedSourceHash ?? HashUtilities.Sha256(normalizedSource),
                    IsGenerated = symbol.IsGenerated,
                };
                preferredDeclarationKey = declarationKey;
            }

            var hasCallableDeclaration = snapshot.Declarations.Values.Any(value =>
                value.SymbolKey.Equals(symbol.StableKey, StringComparison.Ordinal));

            snapshot.Symbols[pair.Key] = symbol with
            {
                Path = path,
                PreferredDeclarationKey = preferredDeclarationKey,
                ProjectKey = symbol.Kind == IndexedSymbolKind.Type || hasCallableDeclaration
                    ? symbol.ProjectKey
                    : null,
                SourceDocumentKey = null,
                SourceStart = null,
                SourceLength = null,
                NormalizedSource = null,
                NormalizedSourceHash = null,
            };
        }

        return snapshot;
    }

    private static void RemoveCallableDeclarations(IndexSnapshot snapshot)
    {
        snapshot.Declarations.Clear();
        foreach (var pair in snapshot.Symbols.ToArray())
        {
            snapshot.Symbols[pair.Key] = pair.Value with
            {
                ProjectKey = pair.Value.Kind == IndexedSymbolKind.Type ? pair.Value.ProjectKey : null,
                PreferredDeclarationKey = null,
            };
        }
    }

    private static SymbolPathData CreateStoredTestPath(IndexSnapshot snapshot, SymbolData symbol)
    {
        var typePath = symbol.TypeSimpleName;
        if (string.IsNullOrWhiteSpace(typePath) &&
            symbol.ContainingSymbolKey is { } containingKey &&
            snapshot.Symbols.TryGetValue(containingKey, out var containingSymbol))
        {
            typePath = containingSymbol.Path?.TypeDisplayPath ??
                       containingSymbol.TypeSimpleName ??
                       containingSymbol.Name;
        }

        typePath = string.IsNullOrWhiteSpace(typePath)
            ? symbol.Kind == IndexedSymbolKind.Type ? symbol.Name : "Global"
            : typePath;
        var executablePath = symbol.Kind == IndexedSymbolKind.Type ? string.Empty : symbol.Name;
        var segmentKind = symbol.Kind switch
        {
            IndexedSymbolKind.Lambda => CallablePathSegmentKind.Lambda,
            IndexedSymbolKind.Initializer => CallablePathSegmentKind.Initializer,
            IndexedSymbolKind.TopLevelStatements => CallablePathSegmentKind.TopLevelStatements,
            _ => CallablePathSegmentKind.Named,
        };

        return new SymbolPathData(
            symbol.NamespaceName,
            typePath,
            typePath,
            executablePath,
            executablePath,
            executablePath,
            executablePath,
            segmentKind);
    }
}
