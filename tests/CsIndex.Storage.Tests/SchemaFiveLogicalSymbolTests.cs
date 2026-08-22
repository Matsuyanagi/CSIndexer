using CsIndex.Core.Model;
using Microsoft.Data.Sqlite;
using System.Reflection;

namespace CsIndex.Storage.Tests;

public sealed class SchemaFiveLogicalSymbolTests
{
    [Fact]
    public void StoredSymbol_SourceBridgeForwardsOnlyFromAttachedPreferredDeclaration()
    {
        var hash = new byte[] { 7, 8, 9 };
        var declaration = new StoredDeclaration(
            Id: 11,
            DeclarationKey: "logical|declaration:src/Game.cs:4:12:1",
            SymbolId: 5,
            DocumentId: 3,
            DocumentPath: "src/Game.cs",
            Role: DeclarationRole.Ordinary,
            SourceStart: 4,
            SourceLength: 12,
            NormalizedSource: "void Run(){}",
            NormalizedSourceHash: hash,
            IsGenerated: false);
        var symbol = CreateStoredSymbol() with { PreferredDeclaration = declaration };

        Assert.Equal(declaration.NormalizedSource, symbol.NormalizedSource);
        Assert.Same(hash, symbol.NormalizedSourceHash);
        Assert.Equal(declaration.SourceLength, symbol.SourceLength);
    }

    [Fact]
    public void StoredSymbol_DisplayBridgeRejectsMissingSemanticPathInsteadOfUsingLegacyText()
    {
        var symbol = CreateStoredSymbol() with { Path = null };

        var exception = Assert.Throws<InvalidOperationException>(() => symbol.DisplayName);

        Assert.Contains("semantic path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_CreatesSchemaFiveWithoutLegacySymbolPresentationOrSourceColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        await new SqliteIndex(databasePath).SaveAsync(CreateSnapshot(), cancellationToken);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info;";
        Assert.Equal(5L, (long)(await command.ExecuteScalarAsync(cancellationToken))!);

        command.CommandText = "PRAGMA table_info(symbols);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.Contains("path_segment_kind", columns);
        Assert.Contains("type_display_path", columns);
        Assert.Contains("executable_identity_path", columns);
        Assert.Contains("preferred_declaration_id", columns);
        Assert.DoesNotContain("fully_qualified_name", columns);
        Assert.DoesNotContain("display_name", columns);
        Assert.DoesNotContain("normalized_source", columns);
        Assert.DoesNotContain("normalized_source_hash", columns);
        Assert.DoesNotContain("source_document_id", columns);
        Assert.DoesNotContain("source_start", columns);
        Assert.DoesNotContain("source_length", columns);

        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'symbol_declarations';";
        Assert.Equal("symbol_declarations", await command.ExecuteScalarAsync(cancellationToken));

        Assert.Contains("return_type_display", columns);
        Assert.Contains("conversion_type_key", columns);
        Assert.Contains("conversion_type_display", columns);

        command.CommandText = "PRAGMA table_info(index_runs);";
        Assert.Contains("input_root", await ReadNameColumnAsync(command, cancellationToken));
        Assert.Contains("index_root_anchor", await ReadNameColumnAsync(command, cancellationToken));

        command.CommandText = "PRAGMA table_info(method_parameters);";
        var parameterColumns = await ReadNameColumnAsync(command, cancellationToken);
        Assert.Contains("type_key", parameterColumns);
        Assert.Contains("type_display", parameterColumns);

        command.CommandText = "PRAGMA table_info(symbol_declarations);";
        Assert.Equal(
            [
                "id",
                "declaration_key",
                "symbol_id",
                "document_id",
                "declaration_role",
                "source_start",
                "source_length",
                "normalized_source",
                "normalized_source_hash",
                "is_generated",
            ],
            await ReadNameColumnAsync(command, cancellationToken));

        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        var indexes = await ReadStringColumnAsync(command, cancellationToken);
        Assert.Contains("ix_symbols_profile_path_identity", indexes);
        Assert.Contains("ix_symbols_profile_containing", indexes);
        Assert.Contains("ix_symbols_profile_async_next", indexes);
        Assert.Contains("ix_symbols_profile_preferred_declaration", indexes);
        Assert.Contains("ix_symbol_declarations_symbol", indexes);
        Assert.Contains("ix_symbol_declarations_document_location_role", indexes);

        command.CommandText = "PRAGMA foreign_key_list(symbol_declarations);";
        var declarationTargets = await ReadForeignKeyTargetsAsync(command, cancellationToken);
        Assert.Contains(("symbols", "symbol_id", "id", "CASCADE"), declarationTargets);
        Assert.Contains(("documents", "document_id", "id", "CASCADE"), declarationTargets);

        command.CommandText = "PRAGMA foreign_key_list(symbols);";
        Assert.Contains(
            ("symbol_declarations", "preferred_declaration_id", "id", "SET NULL"),
            await ReadForeignKeyTargetsAsync(command, cancellationToken));
    }

    [Fact]
    public async Task Save_RoundTripsLogicalSymbolsPreferredDeclarationsAndLazySource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        await index.SaveAsync(CreateLogicalSnapshot(), cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        Assert.Equal(".", profile.InputRoot);
        Assert.Equal("..", profile.IndexRootAnchor);

        var symbols = await repository.FindLogicalSymbolCandidatesAsync(
            profile.Id,
            cancellationToken: cancellationToken);
        Assert.Equal(8, symbols.Count);
        var byKey = symbols.ToDictionary(symbol => symbol.StableKey, StringComparer.Ordinal);

        var ordinary = byKey["ordinary"];
        Assert.Equal("Game.Player::Run(System.Guid)", ordinary.DisplayName);
        Assert.Equal("System.Threading.Tasks.Task", ordinary.ReturnTypeDisplay);
        Assert.Equal("System::Guid", ordinary.ConversionTypeKey);
        Assert.Equal("System.Guid", ordinary.ConversionTypeDisplay);
        Assert.NotNull(ordinary.PreferredDeclarationId);
        Assert.Equal("src/Game.cs", ordinary.PreferredDocumentPath);
        Assert.Equal(10, ordinary.PreferredSourceStart);
        Assert.Null(ordinary.PreferredDeclaration);
        Assert.Null(ordinary.NormalizedSource);
        Assert.Null(ordinary.NormalizedSourceHash);
        var parameter = Assert.Single(ordinary.Parameters);
        Assert.Equal("System::Guid", parameter.TypeKey);
        Assert.Equal("System.Guid", parameter.TypeDisplay);

        var type = byKey["type"];
        Assert.Equal("Game.Player", type.DisplayName);
        Assert.Null(type.PreferredDeclarationId);
        Assert.Equal(string.Empty, Assert.IsType<SymbolPathData>(type.Path).ExecutableDisplayPath);
        Assert.Null(byKey["metadata"].PreferredDeclarationId);

        var partial = byKey["partial"];
        var partialDeclarations = await repository.GetDeclarationsAsync(
            profile.Id,
            [partial.Id],
            includeSourceText: false,
            cancellationToken);
        Assert.Equal(
            [DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation],
            partialDeclarations.Select(declaration => declaration.Role));
        Assert.All(partialDeclarations, declaration =>
        {
            Assert.Null(declaration.NormalizedSource);
            Assert.Null(declaration.NormalizedSourceHash);
        });

        var loadedPartialDeclarations = await repository.GetDeclarationsAsync(
            profile.Id,
            [partial.Id],
            includeSourceText: true,
            cancellationToken);
        Assert.Equal("partial void Save();", loadedPartialDeclarations[0].NormalizedSource);
        Assert.Equal("partial void Save(){}", loadedPartialDeclarations[1].NormalizedSource);
        Assert.Equal(new byte[] { 20 }, loadedPartialDeclarations[0].NormalizedSourceHash);
        Assert.Equal(new byte[] { 40 }, loadedPartialDeclarations[1].NormalizedSourceHash);

        var preferred = await repository.GetPreferredDeclarationsAsync(
            profile.Id,
            [ordinary.Id, partial.Id, byKey["definition-only"].Id],
            includeSourceText: true,
            cancellationToken);
        var preferredBySymbol = preferred.ToDictionary(declaration => declaration.SymbolId);
        Assert.Equal(DeclarationRole.Ordinary, preferredBySymbol[ordinary.Id].Role);
        Assert.Equal(DeclarationRole.PartialImplementation, preferredBySymbol[partial.Id].Role);
        Assert.Equal(
            DeclarationRole.PartialDefinition,
            preferredBySymbol[byKey["definition-only"].Id].Role);

        Assert.Empty(await repository.GetDeclarationsAsync(
            profile.Id,
            [type.Id, byKey["metadata"].Id],
            includeSourceText: true,
            cancellationToken));

        var projection = (string)typeof(QueryRepository)
            .GetField("SymbolProjection", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.DoesNotContain("normalized_source", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalized_source_hash", projection, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-preferred")]
    [InlineData("cross-owned-preferred")]
    [InlineData("implementation-without-definition")]
    [InlineData("unknown-role")]
    [InlineData("call-declaration-endpoint")]
    [InlineData("candidate-declaration-endpoint")]
    [InlineData("relation-declaration-endpoint")]
    [InlineData("duplicate-logical-key")]
    [InlineData("rooted-input")]
    [InlineData("rooted-anchor")]
    [InlineData("rooted-project")]
    [InlineData("rooted-document")]
    [InlineData("backslash-document")]
    [InlineData("partial-self-relation")]
    [InlineData("missing-relation-endpoint")]
    [InlineData("missing-binding-endpoint")]
    [InlineData("duplicate-conditional-insert")]
    public async Task Save_InvalidSnapshotPreservesPreviouslyCommittedRun(string scenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "index.sqlite");
        var index = new SqliteIndex(databasePath);
        await index.SaveAsync(CreateLogicalSnapshot(), cancellationToken);
        var before = await ReadCommittedStateAsync(databasePath, cancellationToken);

        var invalid = CreateLogicalSnapshot();
        invalid.RequestHash[0] = 99;
        ApplyInvalidMutation(invalid, scenario);

        await Assert.ThrowsAnyAsync<Exception>(() => index.SaveAsync(invalid, cancellationToken));
        Assert.Equal(before, await ReadCommittedStateAsync(databasePath, cancellationToken));
    }

    [Theory]
    [InlineData("missing-containing")]
    [InlineData("missing-async-next")]
    [InlineData("missing-caller")]
    public async Task Save_MissingRequiredLogicalEndpointFailsBeforeDatabaseOpen(string scenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var databasePath = Path.Combine(temporary.Path, "never-created", "index.sqlite");
        var snapshot = CreateLogicalSnapshot();
        ApplyInvalidMutation(snapshot, scenario);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SqliteIndex(databasePath).SaveAsync(snapshot, cancellationToken));

        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task Save_PersistsSourceTokenForNonDeclarationDanglingCallee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temporary = new TempDirectory();
        var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
        var snapshot = CreateLogicalSnapshot();
        snapshot.Calls.Add(CreateCall("ordinary", "compiler-only-target", ["another-compiler-target"]) with
        {
            ReferenceKind = ReferenceKind.ObjectCreation,
            UnresolvedName = "new ImplicitConstructor()",
        });

        await index.SaveAsync(snapshot, cancellationToken);

        var repository = index.CreateQueryRepository();
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        var call = Assert.Single(await repository.GetCallsByCallerAsync(
            profile.Id,
            [(await repository.FindLogicalSymbolCandidatesAsync(
                profile.Id,
                name: "Run",
                cancellationToken: cancellationToken)).Single().Id],
            GeneratedFilter.Include,
            cancellationToken: cancellationToken));
        Assert.Null(call.CalleeSymbolId);
        Assert.Null(call.CalleeDefinitionId);
        Assert.Equal("new ImplicitConstructor()", call.UnresolvedName);
    }

    private static IndexSnapshot CreateSnapshot() => new()
    {
        Profile = new AnalysisProfileData
        {
            Name = "schema-five",
            InputMode = InputMode.Directory,
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [1],
        },
        InputRoot = ".",
        IndexRootAnchor = ".",
        InputFingerprint = [2],
        RequestHash = [3],
    };

    private static IndexSnapshot CreateLogicalSnapshot()
    {
        const string projectKey = "project-path:src/Game.csproj";
        const string documentKey = projectKey + "|document:src/Game.cs";
        var snapshot = new IndexSnapshot
        {
            Profile = new AnalysisProfileData
            {
                Name = "logical-round-trip",
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
            Key = projectKey,
            Name = "Game",
            AssemblyName = "Game",
            ProjectPath = "src/Game.csproj",
            Fingerprint = [4],
        });
        snapshot.Documents.Add(new DocumentData
        {
            Key = documentKey,
            ProjectKey = projectKey,
            NormalizedPath = "src/Game.cs",
            ContentHash = [5],
            IsGenerated = false,
            GenerationKind = GenerationKind.None,
        });

        snapshot.Symbols.Add("type", CreateSymbol(
            "type",
            IndexedSymbolKind.Type,
            "Player",
            CreatePath(string.Empty, string.Empty, CallablePathSegmentKind.Named)));
        snapshot.Symbols.Add("metadata", CreateSymbol(
            "metadata",
            IndexedSymbolKind.Method,
            "Metadata",
            CreatePath("Metadata()", "Metadata()", CallablePathSegmentKind.Named)));

        AddOrdinary(snapshot, documentKey, "ordinary", IndexedSymbolKind.Method, "Run", 10,
            "void Run(){}", CreatePath("Run(System.Guid)", "Run(System::Guid)", CallablePathSegmentKind.Named),
            parameters:
            [
                new MethodParameterData
                {
                    Ordinal = 0,
                    Name = "value",
                    TypeKey = "System::Guid",
                    TypeDisplay = "System.Guid",
                    RefKind = 0,
                    IsOptional = false,
                },
            ],
            returnTypeKey: "System.Threading.Tasks::Task",
            returnTypeDisplay: "System.Threading.Tasks.Task",
            conversionTypeKey: "System::Guid",
            conversionTypeDisplay: "System.Guid");
        AddPartial(snapshot, documentKey);
        AddDefinitionOnly(snapshot, documentKey);
        AddOrdinary(snapshot, documentKey, "lambda", IndexedSymbolKind.Lambda, "<lambda#1>", 70,
            "x => x", CreatePath("Run(System.Guid).<lambda#1>", "Run(System::Guid).<lambda#1>", CallablePathSegmentKind.Lambda),
            containingSymbolKey: "ordinary", isGenerated: true);
        AddOrdinary(snapshot, documentKey, "initializer", IndexedSymbolKind.Initializer, "<initializer:Factory>", 80,
            "Factory = Make();", CreatePath("<initializer:Factory>", "<initializer:Factory>", CallablePathSegmentKind.Initializer));
        AddOrdinary(snapshot, documentKey, "top", IndexedSymbolKind.TopLevelStatements, "<top-level-statements>", 100,
            "Run();", CreatePath("<top-level-statements>", "<top-level-statements>", CallablePathSegmentKind.TopLevelStatements));
        return snapshot;
    }

    private static SymbolData CreateSymbol(
        string key,
        IndexedSymbolKind kind,
        string name,
        SymbolPathData path,
        string? preferredDeclarationKey = null,
        string? containingSymbolKey = "type",
        IReadOnlyList<MethodParameterData>? parameters = null,
        string? returnTypeKey = null,
        string? returnTypeDisplay = null,
        string? conversionTypeKey = null,
        string? conversionTypeDisplay = null,
        bool isGenerated = false) => new()
        {
            StableKey = key,
            ProjectKey = "project-path:src/Game.csproj",
            Kind = kind,
            Name = name,
            NamespaceName = "Game",
            TypeSimpleName = "Player",
            TypeMetadataName = "Player",
            FullyQualifiedName = string.Empty,
            DisplayName = string.Empty,
            Path = path,
            PreferredDeclarationKey = preferredDeclarationKey,
            ContainingSymbolKey = kind == IndexedSymbolKind.Type ? null : containingSymbolKey,
            ParameterCount = parameters?.Count,
            MethodKind = kind == IndexedSymbolKind.Method ? 0 : null,
            ReturnTypeKey = returnTypeKey,
            ReturnTypeDisplay = returnTypeDisplay,
            ConversionTypeKey = conversionTypeKey,
            ConversionTypeDisplay = conversionTypeDisplay,
            Parameters = parameters ?? [],
            IsGenerated = isGenerated,
        };

    private static SymbolPathData CreatePath(
        string executableDisplay,
        string executableIdentity,
        CallablePathSegmentKind segmentKind) => new(
        "Game",
        "Player",
        "Player",
        executableDisplay,
        executableIdentity,
        executableDisplay.Contains('.', StringComparison.Ordinal)
            ? executableDisplay[(executableDisplay.LastIndexOf('.') + 1)..]
            : executableDisplay,
        executableIdentity.Contains('.', StringComparison.Ordinal)
            ? executableIdentity[(executableIdentity.LastIndexOf('.') + 1)..]
            : executableIdentity,
        segmentKind);

    private static void AddOrdinary(
        IndexSnapshot snapshot,
        string documentKey,
        string key,
        IndexedSymbolKind kind,
        string name,
        int start,
        string source,
        SymbolPathData path,
        IReadOnlyList<MethodParameterData>? parameters = null,
        string? returnTypeKey = null,
        string? returnTypeDisplay = null,
        string? conversionTypeKey = null,
        string? conversionTypeDisplay = null,
        string? containingSymbolKey = "type",
        bool isGenerated = false)
    {
        var declarationKey = DeclarationKey(key, start, source.Length, DeclarationRole.Ordinary);
        snapshot.Symbols.Add(key, CreateSymbol(
            key,
            kind,
            name,
            path,
            declarationKey,
            containingSymbolKey,
            parameters,
            returnTypeKey,
            returnTypeDisplay,
            conversionTypeKey,
            conversionTypeDisplay,
            isGenerated));
        snapshot.Declarations.Add(declarationKey, CreateDeclaration(
            declarationKey,
            key,
            documentKey,
            DeclarationRole.Ordinary,
            start,
            source,
            isGenerated));
    }

    private static void AddPartial(IndexSnapshot snapshot, string documentKey)
    {
        const string definitionSource = "partial void Save();";
        const string implementationSource = "partial void Save(){}";
        var definitionKey = DeclarationKey("partial", 20, definitionSource.Length, DeclarationRole.PartialDefinition);
        var implementationKey = DeclarationKey("partial", 40, implementationSource.Length, DeclarationRole.PartialImplementation);
        snapshot.Symbols.Add("partial", CreateSymbol(
            "partial",
            IndexedSymbolKind.Method,
            "Save",
            CreatePath("Save()", "Save()", CallablePathSegmentKind.Named),
            implementationKey));
        snapshot.Declarations.Add(definitionKey, CreateDeclaration(
            definitionKey, "partial", documentKey, DeclarationRole.PartialDefinition, 20, definitionSource));
        snapshot.Declarations.Add(implementationKey, CreateDeclaration(
            implementationKey, "partial", documentKey, DeclarationRole.PartialImplementation, 40, implementationSource));
    }

    private static void AddDefinitionOnly(IndexSnapshot snapshot, string documentKey)
    {
        const string source = "partial void Validate();";
        var declarationKey = DeclarationKey("definition-only", 60, source.Length, DeclarationRole.PartialDefinition);
        snapshot.Symbols.Add("definition-only", CreateSymbol(
            "definition-only",
            IndexedSymbolKind.Method,
            "Validate",
            CreatePath("Validate()", "Validate()", CallablePathSegmentKind.Named),
            declarationKey));
        snapshot.Declarations.Add(declarationKey, CreateDeclaration(
            declarationKey,
            "definition-only",
            documentKey,
            DeclarationRole.PartialDefinition,
            60,
            source));
    }

    private static SymbolDeclarationData CreateDeclaration(
        string declarationKey,
        string symbolKey,
        string documentKey,
        DeclarationRole role,
        int start,
        string source,
        bool isGenerated = false) => new()
        {
            Key = declarationKey,
            SymbolKey = symbolKey,
            DocumentKey = documentKey,
            Role = role,
            SourceStart = start,
            SourceLength = source.Length,
            NormalizedSource = source,
            NormalizedSourceHash = [(byte)start],
            IsGenerated = isGenerated,
        };

    private static string DeclarationKey(string symbolKey, int start, int length, DeclarationRole role) =>
        $"{symbolKey}|declaration:src/Game.cs:{start}:{length}:{(int)role}";

    private static void ApplyInvalidMutation(IndexSnapshot snapshot, string scenario)
    {
        var ordinaryDeclaration = snapshot.Symbols["ordinary"].PreferredDeclarationKey!;
        var partialImplementation = snapshot.Symbols["partial"].PreferredDeclarationKey!;
        switch (scenario)
        {
            case "missing-preferred":
                snapshot.Symbols["ordinary"] = snapshot.Symbols["ordinary"] with
                {
                    PreferredDeclarationKey = "missing",
                };
                break;
            case "cross-owned-preferred":
                snapshot.Symbols["ordinary"] = snapshot.Symbols["ordinary"] with
                {
                    PreferredDeclarationKey = partialImplementation,
                };
                break;
            case "implementation-without-definition":
                snapshot.Declarations.Remove(snapshot.Declarations.Values.Single(
                    declaration => declaration.SymbolKey == "partial" &&
                                   declaration.Role == DeclarationRole.PartialDefinition).Key);
                break;
            case "unknown-role":
                {
                    var original = snapshot.Declarations.Values.Single(
                        declaration => declaration.SymbolKey == "partial" &&
                                       declaration.Role == DeclarationRole.PartialDefinition);
                    snapshot.Declarations[original.Key] = original with { Role = (DeclarationRole)99 };
                    break;
                }
            case "call-declaration-endpoint":
                snapshot.Calls.Add(CreateCall("ordinary", ordinaryDeclaration, []));
                break;
            case "candidate-declaration-endpoint":
                snapshot.Calls.Add(CreateCall("ordinary", "partial", [ordinaryDeclaration]));
                break;
            case "relation-declaration-endpoint":
                snapshot.Relations.Add(new SymbolRelationData
                {
                    SourceSymbolKey = "ordinary",
                    TargetSymbolKey = ordinaryDeclaration,
                    RelationKind = SymbolRelationKind.Overrides,
                });
                break;
            case "duplicate-logical-key":
                snapshot.Symbols.Add("alias", snapshot.Symbols["ordinary"]);
                break;
            case "rooted-input":
                snapshot.InputRoot = "C:/root";
                break;
            case "rooted-anchor":
                snapshot.IndexRootAnchor = "C:/root";
                break;
            case "rooted-project":
                snapshot.Projects[0] = snapshot.Projects[0] with { ProjectPath = "C:/root/Game.csproj" };
                break;
            case "rooted-document":
                snapshot.Documents[0] = snapshot.Documents[0] with { NormalizedPath = "C:/root/Game.cs" };
                break;
            case "backslash-document":
                snapshot.Documents[0] = snapshot.Documents[0] with { NormalizedPath = "src\\Game.cs" };
                break;
            case "partial-self-relation":
                snapshot.Relations.Add(new SymbolRelationData
                {
                    SourceSymbolKey = "partial",
                    TargetSymbolKey = "partial",
                    RelationKind = SymbolRelationKind.PartialDefinition,
                });
                break;
            case "missing-relation-endpoint":
                snapshot.Relations.Add(new SymbolRelationData
                {
                    SourceSymbolKey = "ordinary",
                    TargetSymbolKey = "missing",
                    RelationKind = SymbolRelationKind.Overrides,
                });
                break;
            case "missing-binding-endpoint":
                snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
                {
                    ImplementingTypeKey = "type",
                    InterfaceMethodKey = "missing",
                    ImplementationMethodKey = "ordinary",
                });
                break;
            case "duplicate-conditional-insert":
                snapshot.ConditionalSymbols.Add(new ConditionalSymbolData
                {
                    DocumentKey = "project-path:src/Game.csproj|document:src/Game.cs",
                    SymbolName = "DEBUG",
                    OccurrenceCount = 1,
                });
                snapshot.ConditionalSymbols.Add(new ConditionalSymbolData
                {
                    DocumentKey = "project-path:src/Game.csproj|document:src/Game.cs",
                    SymbolName = "DEBUG",
                    OccurrenceCount = 2,
                });
                break;
            case "missing-containing":
                snapshot.Symbols["lambda"] = snapshot.Symbols["lambda"] with
                {
                    ContainingSymbolKey = "missing",
                };
                break;
            case "missing-async-next":
                snapshot.Symbols["ordinary"] = snapshot.Symbols["ordinary"] with
                {
                    AsyncNextSymbolKey = "missing",
                };
                break;
            case "missing-caller":
                snapshot.Calls.Add(CreateCall("missing", "partial", []));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static CallData CreateCall(
        string callerKey,
        string? calleeKey,
        IReadOnlyList<string> candidates) => new()
        {
            CallerSymbolKey = callerKey,
            CalleeSymbolKey = calleeKey,
            CalleeDefinitionKey = calleeKey,
            ReferenceKind = ReferenceKind.Invocation,
            DispatchKind = DispatchKind.Static,
            ResolutionStatus = calleeKey is null ? ResolutionStatus.Unresolved : ResolutionStatus.Resolved,
            ResolutionReason = calleeKey is null ? ResolutionReason.Unknown : ResolutionReason.None,
            DocumentKey = "project-path:src/Game.csproj|document:src/Game.cs",
            SourceStart = 120,
            SourceLength = 3,
            UnresolvedName = calleeKey,
            CandidateSymbolKeys = candidates,
        };

    private static async Task<string> ReadCommittedStateAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                hex(r.request_hash) || '|' ||
                (SELECT COUNT(*) FROM symbols) || '|' ||
                (SELECT COUNT(*) FROM symbol_declarations) || '|' ||
                (SELECT COUNT(*) FROM calls) || '|' ||
                (SELECT COUNT(*) FROM conditional_symbols_used)
            FROM index_runs r;
            """;
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<IReadOnlyList<string>> ReadNameColumnAsync(
        SqliteCommand command,
        CancellationToken cancellationToken) =>
        await ReadStringColumnAsync(command, cancellationToken, 1);

    private static async Task<IReadOnlyList<string>> ReadStringColumnAsync(
        SqliteCommand command,
        CancellationToken cancellationToken,
        int ordinal = 0)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(ordinal));
        }

        return values;
    }

    private static async Task<IReadOnlyList<(string Table, string From, string To, string OnDelete)>>
        ReadForeignKeyTargetsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var values = new List<(string, string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add((reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(6)));
        }

        return values;
    }

    private static StoredSymbol CreateStoredSymbol() => new StoredSymbol(
        Id: 5,
        StableKey: "logical",
        Kind: IndexedSymbolKind.Method,
        Name: "Run",
        NamespaceName: "Game",
        TypeSimpleName: "Player",
        TypeMetadataName: "Player",
        FullyQualifiedName: string.Empty,
        DisplayName: string.Empty,
        ContainingSymbolId: null,
        Arity: 0,
        ParameterCount: 0,
        MethodKind: 0,
        IsStatic: false,
        IsAbstract: false,
        IsVirtual: false,
        IsOverride: false,
        AsyncRole: AsyncRole.None,
        AsyncInvolvementDepth: null,
        AsyncNextSymbolId: null,
        ReturnTypeKey: null,
        NormalizedSource: null,
        NormalizedSourceHash: null,
        DocumentPath: "src/Game.cs",
        SourceStart: 4,
        SourceLength: null,
        IsGenerated: false,
        AssemblyName: "Game",
        Parameters: [],
        TypeKind: null,
        Accessibility: null) with
    {
        Path = new SymbolPathData(
            "Game",
            "Player",
            "Player",
            "Run()",
            "Run()",
            "Run()",
            "Run()",
            CallablePathSegmentKind.Named),
    };
}
