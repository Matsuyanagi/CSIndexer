using System.Text.Json;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace CsIndex.Storage;

public sealed class SqliteIndex(string databasePath)
{
    private readonly string _databasePath = PathNormalizer.Normalize(databasePath);
    private readonly SchemaMigrator _migrator = new();

    public string DatabasePath => _databasePath;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
    }

    public async Task<bool> IsCacheValidAsync(
        string inputRoot,
        byte[] inputFingerprint,
        byte[] requestHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM index_runs
            WHERE input_root = $input_root COLLATE NOCASE
              AND input_fingerprint = $input_fingerprint
              AND request_hash = $request_hash;
            """;
        command.Parameters.AddWithValue("$input_root", inputRoot);
        command.Parameters.Add("$input_fingerprint", SqliteType.Blob).Value = inputFingerprint;
        command.Parameters.Add("$request_hash", SqliteType.Blob).Value = requestHash;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task SaveAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var profileId = await UpsertProfileAsync(connection, transaction, snapshot.Profile, cancellationToken);
            await DeletePriorProfileDataAsync(connection, transaction, profileId, cancellationToken);
            var runId = await InsertRunAsync(connection, transaction, profileId, snapshot, cancellationToken);

            var projectIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in snapshot.Projects)
            {
                projectIds[project.Key] = await InsertProjectAsync(
                    connection,
                    transaction,
                    runId,
                    profileId,
                    project,
                    cancellationToken);
            }

            var documentIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var document in snapshot.Documents)
            {
                documentIds[document.Key] = await InsertDocumentAsync(
                    connection,
                    transaction,
                    projectIds[document.ProjectKey],
                    document,
                    cancellationToken);
            }

            var symbolIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var symbol in snapshot.Symbols.Values.OrderBy(symbol => symbol.StableKey, StringComparer.Ordinal))
            {
                symbolIds[symbol.StableKey] = await InsertSymbolAsync(
                    connection,
                    transaction,
                    profileId,
                    symbol,
                    projectIds,
                    documentIds,
                    cancellationToken);
            }

            await UpdateContainingSymbolsAsync(
                connection,
                transaction,
                snapshot.Symbols.Values,
                symbolIds,
                cancellationToken);
            await UpdateAsyncNextSymbolsAsync(
                connection,
                transaction,
                snapshot.Symbols.Values,
                symbolIds,
                cancellationToken);
            await InsertParametersAsync(
                connection,
                transaction,
                snapshot.Symbols.Values,
                symbolIds,
                cancellationToken);
            var declarationIds = await InsertDeclarationsAsync(
                connection,
                transaction,
                snapshot.Declarations.Values,
                symbolIds,
                documentIds,
                cancellationToken);
            await UpdatePreferredDeclarationsAsync(
                connection,
                transaction,
                snapshot.Symbols.Values,
                symbolIds,
                declarationIds,
                cancellationToken);
            await VerifyPreferredOwnershipAsync(connection, transaction, cancellationToken);
            await VerifyIntegrityAsync(connection, transaction, cancellationToken);
            await InsertCallsAsync(
                connection,
                transaction,
                profileId,
                snapshot.Calls,
                symbolIds,
                documentIds,
                cancellationToken);
            await InsertRelationsAsync(
                connection,
                transaction,
                profileId,
                snapshot.Relations,
                symbolIds,
                cancellationToken);
            await InsertInterfaceMethodBindingsAsync(
                connection,
                transaction,
                profileId,
                snapshot.InterfaceMethodBindings,
                symbolIds,
                cancellationToken);
            await InsertConditionalSymbolsAsync(
                connection,
                transaction,
                profileId,
                snapshot.ConditionalSymbols,
                documentIds,
                cancellationToken);
            await VerifyIntegrityAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or KeyNotFoundException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new IndexDatabaseException($"Failed to update SQLite index: {exception.Message}", exception);
        }
    }

    public QueryRepository CreateQueryRepository() => new(_databasePath, _migrator);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection? connection = null;
        try
        {
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await _migrator.EnsureMigratedAsync(connection, cancellationToken);
            return connection;
        }
        catch (IndexDatabaseException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw new IndexDatabaseException($"Could not open SQLite index '{_databasePath}': {exception.Message}", exception);
        }
    }

    private static async Task<long> UpsertProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalysisProfileData profile,
        CancellationToken cancellationToken)
    {
        await using (var insert = CreateCommand(connection, transaction, """
            INSERT INTO analysis_profiles(
                name, input_mode, configuration, target_framework, runtime_identifier,
                operating_system, architecture, preprocessor_symbols, profile_hash)
            VALUES(
                $name, $input_mode, $configuration, $target_framework, $runtime_identifier,
                $operating_system, $architecture, $preprocessor_symbols, $profile_hash)
            ON CONFLICT(profile_hash) DO UPDATE SET
                name = excluded.name,
                preprocessor_symbols = excluded.preprocessor_symbols;
            """))
        {
            insert.Parameters.AddWithValue("$name", profile.Name);
            insert.Parameters.AddWithValue("$input_mode", (int)profile.InputMode);
            insert.Parameters.AddWithValue("$configuration", (object?)profile.Configuration ?? DBNull.Value);
            insert.Parameters.AddWithValue("$target_framework", (object?)profile.TargetFramework ?? DBNull.Value);
            insert.Parameters.AddWithValue("$runtime_identifier", (object?)profile.RuntimeIdentifier ?? DBNull.Value);
            insert.Parameters.AddWithValue("$operating_system", profile.OperatingSystem);
            insert.Parameters.AddWithValue("$architecture", profile.Architecture);
            insert.Parameters.AddWithValue("$preprocessor_symbols", JsonSerializer.Serialize(profile.PreprocessorSymbols));
            insert.Parameters.Add("$profile_hash", SqliteType.Blob).Value = profile.ProfileHash;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = CreateCommand(
            connection,
            transaction,
            "SELECT id FROM analysis_profiles WHERE profile_hash = $profile_hash;");
        select.Parameters.Add("$profile_hash", SqliteType.Blob).Value = profile.ProfileHash;
        return Convert.ToInt64(await select.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task DeletePriorProfileDataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
                 {
            "DELETE FROM calls WHERE analysis_profile_id = $profile_id;",
                     "DELETE FROM call_candidates WHERE call_id NOT IN (SELECT id FROM calls);",
                     "DELETE FROM symbol_relations WHERE analysis_profile_id = $profile_id;",
                     "DELETE FROM interface_method_bindings WHERE analysis_profile_id = $profile_id;",
                     "DELETE FROM conditional_symbols_used WHERE analysis_profile_id = $profile_id;",
                     "DELETE FROM index_runs WHERE analysis_profile_id = $profile_id;",
                     "DELETE FROM symbols WHERE analysis_profile_id = $profile_id;",
                 })
        {
            await using var command = CreateCommand(connection, transaction, sql);
            command.Parameters.AddWithValue("$profile_id", profileId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<long> InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IndexSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO index_runs(
                analysis_profile_id, input_root, index_root_anchor, input_fingerprint, request_hash, indexed_at_utc)
            VALUES($profile_id, $input_root, $index_root_anchor, $input_fingerprint, $request_hash, $indexed_at);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$input_root", snapshot.InputRoot);
        command.Parameters.AddWithValue("$index_root_anchor", snapshot.IndexRootAnchor);
        command.Parameters.Add("$input_fingerprint", SqliteType.Blob).Value = snapshot.InputFingerprint;
        command.Parameters.Add("$request_hash", SqliteType.Blob).Value = snapshot.RequestHash;
        command.Parameters.AddWithValue("$indexed_at", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        long profileId,
        ProjectData project,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO projects(
                index_run_id, analysis_profile_id, name, assembly_name, project_path,
                target_framework, project_fingerprint)
            VALUES(
                $run_id, $profile_id, $name, $assembly_name, $project_path,
                $target_framework, $fingerprint);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$assembly_name", (object?)project.AssemblyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$project_path", (object?)project.ProjectPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$target_framework", (object?)project.TargetFramework ?? DBNull.Value);
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = project.Fingerprint;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        DocumentData document,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO documents(
                project_id, normalized_path, content_hash, semantic_hash, is_generated, generation_kind)
            VALUES(
                $project_id, $path, $content_hash, $semantic_hash, $is_generated, $generation_kind);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$project_id", projectId);
        command.Parameters.AddWithValue("$path", document.NormalizedPath);
        command.Parameters.Add("$content_hash", SqliteType.Blob).Value = document.ContentHash;
        command.Parameters.Add("$semantic_hash", SqliteType.Blob).Value =
            (object?)document.SemanticHash ?? DBNull.Value;
        command.Parameters.AddWithValue("$is_generated", document.IsGenerated);
        command.Parameters.AddWithValue("$generation_kind", (int)document.GenerationKind);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertSymbolAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        SymbolData symbol,
        IReadOnlyDictionary<string, long> projectIds,
        IReadOnlyDictionary<string, long> documentIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO symbols(
                analysis_profile_id, project_id, stable_key, kind, name, namespace_name,
                type_simple_name, type_metadata_name,
                path_segment_kind, path_segment_display, path_segment_identity,
                type_display_path, type_identity_path, executable_display_path, executable_identity_path,
                preferred_declaration_id, containing_symbol_id, arity, parameter_count, method_kind, accessibility,
                type_kind,
                is_static, is_abstract, is_virtual, is_override, async_role,
                async_involvement_depth, return_type_key, return_type_display,
                conversion_type_key, conversion_type_display, is_generated)
            VALUES(
                $profile_id, $project_id, $stable_key, $kind, $name, $namespace_name,
                $type_simple_name, $type_metadata_name,
                $path_segment_kind, $path_segment_display, $path_segment_identity,
                $type_display_path, $type_identity_path, $executable_display_path, $executable_identity_path,
                NULL, NULL, $arity, $parameter_count, $method_kind, $accessibility,
                $type_kind,
                $is_static, $is_abstract, $is_virtual, $is_override, $async_role,
                $async_involvement_depth, $return_type_key, $return_type_display,
                $conversion_type_key, $conversion_type_display, $is_generated);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$project_id", symbol.ProjectKey is not null && projectIds.TryGetValue(symbol.ProjectKey, out var projectId)
            ? projectId
            : DBNull.Value);
        command.Parameters.AddWithValue("$stable_key", symbol.StableKey);
        command.Parameters.AddWithValue("$kind", (int)symbol.Kind);
        command.Parameters.AddWithValue("$name", symbol.Name);
        command.Parameters.AddWithValue("$namespace_name", symbol.NamespaceName);
        command.Parameters.AddWithValue("$type_simple_name", (object?)symbol.TypeSimpleName ?? DBNull.Value);
        command.Parameters.AddWithValue("$type_metadata_name", (object?)symbol.TypeMetadataName ?? DBNull.Value);
        var path = symbol.Path ?? throw new InvalidOperationException(
            $"Logical symbol '{symbol.StableKey}' is missing semantic path data.");
        command.Parameters.AddWithValue("$path_segment_kind", (int)path.SegmentKind);
        command.Parameters.AddWithValue("$path_segment_display", path.SegmentDisplay);
        command.Parameters.AddWithValue("$path_segment_identity", path.SegmentIdentity);
        command.Parameters.AddWithValue("$type_display_path", path.TypeDisplayPath);
        command.Parameters.AddWithValue("$type_identity_path", path.TypeIdentityPath);
        command.Parameters.AddWithValue("$executable_display_path", path.ExecutableDisplayPath);
        command.Parameters.AddWithValue("$executable_identity_path", path.ExecutableIdentityPath);
        command.Parameters.AddWithValue("$arity", symbol.Arity);
        command.Parameters.AddWithValue("$parameter_count", (object?)symbol.ParameterCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$method_kind", (object?)symbol.MethodKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$accessibility", (object?)symbol.Accessibility ?? DBNull.Value);
        command.Parameters.AddWithValue("$type_kind", (object?)symbol.TypeKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_static", symbol.IsStatic);
        command.Parameters.AddWithValue("$is_abstract", symbol.IsAbstract);
        command.Parameters.AddWithValue("$is_virtual", symbol.IsVirtual);
        command.Parameters.AddWithValue("$is_override", symbol.IsOverride);
        command.Parameters.AddWithValue("$async_role", (int)symbol.AsyncRole);
        command.Parameters.AddWithValue(
            "$async_involvement_depth",
            (object?)symbol.AsyncInvolvementDepth ?? DBNull.Value);
        command.Parameters.AddWithValue("$return_type_key", (object?)symbol.ReturnTypeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$return_type_display", (object?)symbol.ReturnTypeDisplay ?? DBNull.Value);
        command.Parameters.AddWithValue("$conversion_type_key", (object?)symbol.ConversionTypeKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$conversion_type_display", (object?)symbol.ConversionTypeDisplay ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_generated", symbol.IsGenerated);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateContainingSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SymbolData> symbols,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            UPDATE symbols SET containing_symbol_id = $containing_id WHERE id = $id;
            """);
        var containingParameter = command.Parameters.Add("$containing_id", SqliteType.Integer);
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var symbol in symbols.Where(symbol => symbol.ContainingSymbolKey is not null))
        {
            if (!symbolIds.TryGetValue(symbol.ContainingSymbolKey!, out var containingId))
            {
                continue;
            }

            containingParameter.Value = containingId;
            idParameter.Value = symbolIds[symbol.StableKey];
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdateAsyncNextSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SymbolData> symbols,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            UPDATE symbols SET async_next_symbol_id = $next_id WHERE id = $id;
            """);
        var nextParameter = command.Parameters.Add("$next_id", SqliteType.Integer);
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var symbol in symbols.Where(symbol => symbol.AsyncNextSymbolKey is not null))
        {
            if (!symbolIds.TryGetValue(symbol.AsyncNextSymbolKey!, out var nextId))
            {
                throw new InvalidOperationException(
                    $"Async next symbol key '{symbol.AsyncNextSymbolKey}' for '{symbol.StableKey}' is missing from the current snapshot.");
            }

            nextParameter.Value = nextId;
            idParameter.Value = symbolIds[symbol.StableKey];
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertParametersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SymbolData> symbols,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO method_parameters(method_id, ordinal, name, type_key, type_display, ref_kind, is_optional)
            VALUES($method_id, $ordinal, $name, $type_key, $type_display, $ref_kind, $is_optional);
            """);
        foreach (var symbol in symbols)
        {
            foreach (var parameter in symbol.Parameters)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$method_id", symbolIds[symbol.StableKey]);
                command.Parameters.AddWithValue("$ordinal", parameter.Ordinal);
                command.Parameters.AddWithValue("$name", (object?)parameter.Name ?? DBNull.Value);
                command.Parameters.AddWithValue("$type_key", parameter.TypeKey);
                command.Parameters.AddWithValue("$type_display", parameter.TypeDisplay);
                command.Parameters.AddWithValue("$ref_kind", parameter.RefKind);
                command.Parameters.AddWithValue("$is_optional", parameter.IsOptional);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task<Dictionary<string, long>> InsertDeclarationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SymbolDeclarationData> declarations,
        IReadOnlyDictionary<string, long> symbolIds,
        IReadOnlyDictionary<string, long> documentIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO symbol_declarations(
                declaration_key, symbol_id, document_id, declaration_role,
                source_start, source_length, normalized_source, normalized_source_hash, is_generated)
            VALUES(
                $declaration_key, $symbol_id, $document_id, $declaration_role,
                $source_start, $source_length, $normalized_source, $normalized_source_hash, $is_generated);
            SELECT last_insert_rowid();
            """);
        var declarationIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var declaration in declarations.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (!symbolIds.TryGetValue(declaration.SymbolKey, out var symbolId))
            {
                throw new InvalidOperationException(
                    $"Declaration '{declaration.Key}' references unknown logical symbol '{declaration.SymbolKey}'.");
            }

            if (!documentIds.TryGetValue(declaration.DocumentKey, out var documentId))
            {
                throw new InvalidOperationException(
                    $"Declaration '{declaration.Key}' references unknown document '{declaration.DocumentKey}'.");
            }

            command.Parameters.Clear();
            command.Parameters.AddWithValue("$declaration_key", declaration.Key);
            command.Parameters.AddWithValue("$symbol_id", symbolId);
            command.Parameters.AddWithValue("$document_id", documentId);
            command.Parameters.AddWithValue("$declaration_role", (int)declaration.Role);
            command.Parameters.AddWithValue("$source_start", declaration.SourceStart);
            command.Parameters.AddWithValue("$source_length", declaration.SourceLength);
            command.Parameters.AddWithValue("$normalized_source", declaration.NormalizedSource);
            command.Parameters.Add("$normalized_source_hash", SqliteType.Blob).Value = declaration.NormalizedSourceHash;
            command.Parameters.AddWithValue("$is_generated", declaration.IsGenerated);
            declarationIds[declaration.Key] = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        return declarationIds;
    }

    private static async Task UpdatePreferredDeclarationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SymbolData> symbols,
        IReadOnlyDictionary<string, long> symbolIds,
        IReadOnlyDictionary<string, long> declarationIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            UPDATE symbols
            SET preferred_declaration_id = $declaration_id
            WHERE id = $symbol_id;
            """);
        foreach (var symbol in symbols.Where(value => value.PreferredDeclarationKey is not null))
        {
            if (!symbolIds.TryGetValue(symbol.StableKey, out var symbolId) ||
                !declarationIds.TryGetValue(symbol.PreferredDeclarationKey!, out var declarationId))
            {
                throw new InvalidOperationException(
                    $"Preferred declaration '{symbol.PreferredDeclarationKey}' for '{symbol.StableKey}' is missing.");
            }

            command.Parameters.Clear();
            command.Parameters.AddWithValue("$declaration_id", declarationId);
            command.Parameters.AddWithValue("$symbol_id", symbolId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VerifyPreferredOwnershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT COUNT(*)
            FROM symbols s
            JOIN symbol_declarations d ON d.id = s.preferred_declaration_id
            WHERE d.symbol_id <> s.id;
            """);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
        {
            throw new InvalidOperationException("A preferred declaration belongs to a different logical symbol.");
        }
    }

    private static async Task InsertCallsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IEnumerable<CallData> calls,
        IReadOnlyDictionary<string, long> symbolIds,
        IReadOnlyDictionary<string, long> documentIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO calls(
                analysis_profile_id, caller_symbol_id, callee_symbol_id, callee_definition_id,
                reference_kind, dispatch_kind, resolution_status, resolution_reason, async_usage_kind,
                document_id, source_start, source_length, unresolved_name, receiver_type_key)
            VALUES(
                $profile_id, $caller_id, $callee_id, $definition_id,
                $reference_kind, $dispatch_kind, $resolution_status, $resolution_reason, $async_usage_kind,
                $document_id, $source_start, $source_length, $unresolved_name, $receiver_type_key);
            SELECT last_insert_rowid();
            """);
        await using var candidateCommand = CreateCommand(connection, transaction, """
            INSERT OR IGNORE INTO call_candidates(call_id, candidate_symbol_id)
            VALUES($call_id, $candidate_id);
            """);
        foreach (var call in calls)
        {
            var mappedCalleeId = call.CalleeSymbolKey is not null &&
                                 symbolIds.TryGetValue(call.CalleeSymbolKey, out var calleeId)
                ? calleeId
                : (long?)null;
            var mappedDefinitionId = call.CalleeDefinitionKey is not null &&
                                     symbolIds.TryGetValue(call.CalleeDefinitionKey, out var definitionId)
                ? definitionId
                : (long?)null;
            var unresolvedName = mappedCalleeId is null &&
                                 mappedDefinitionId is null &&
                                 !string.IsNullOrEmpty(call.UnresolvedName)
                ? call.UnresolvedName
                : null;

            command.Parameters.Clear();
            command.Parameters.AddWithValue("$profile_id", profileId);
            command.Parameters.AddWithValue("$caller_id", symbolIds[call.CallerSymbolKey]);
            command.Parameters.AddWithValue("$callee_id", (object?)mappedCalleeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$definition_id", (object?)mappedDefinitionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$reference_kind", (int)call.ReferenceKind);
            command.Parameters.AddWithValue("$dispatch_kind", (int)call.DispatchKind);
            command.Parameters.AddWithValue("$resolution_status", (int)call.ResolutionStatus);
            command.Parameters.AddWithValue("$resolution_reason", (int)call.ResolutionReason);
            command.Parameters.AddWithValue("$async_usage_kind", (int)call.AsyncUsageKind);
            command.Parameters.AddWithValue("$document_id", documentIds[call.DocumentKey]);
            command.Parameters.AddWithValue("$source_start", call.SourceStart);
            command.Parameters.AddWithValue("$source_length", call.SourceLength);
            command.Parameters.AddWithValue("$unresolved_name", (object?)unresolvedName ?? DBNull.Value);
            command.Parameters.AddWithValue("$receiver_type_key", (object?)call.ReceiverTypeKey ?? DBNull.Value);
            var callId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            foreach (var candidateKey in call.CandidateSymbolKeys)
            {
                if (!symbolIds.TryGetValue(candidateKey, out var candidateId))
                {
                    continue;
                }

                candidateCommand.Parameters.Clear();
                candidateCommand.Parameters.AddWithValue("$call_id", callId);
                candidateCommand.Parameters.AddWithValue("$candidate_id", candidateId);
                await candidateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task InsertRelationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IEnumerable<SymbolRelationData> relations,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT OR IGNORE INTO symbol_relations(
                analysis_profile_id, source_symbol_id, target_symbol_id, relation_kind)
            VALUES($profile_id, $source_id, $target_id, $kind);
            """);
        foreach (var relation in relations)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$profile_id", profileId);
            command.Parameters.AddWithValue("$source_id", symbolIds[relation.SourceSymbolKey]);
            command.Parameters.AddWithValue("$target_id", symbolIds[relation.TargetSymbolKey]);
            command.Parameters.AddWithValue("$kind", (int)relation.RelationKind);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertInterfaceMethodBindingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IEnumerable<InterfaceMethodBindingData> bindings,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT OR IGNORE INTO interface_method_bindings(
                analysis_profile_id, implementing_type_id,
                interface_method_id, implementation_method_id)
            VALUES($profile_id, $type_id, $interface_id, $implementation_id);
            """);
        foreach (var binding in bindings)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$profile_id", profileId);
            command.Parameters.AddWithValue("$type_id", symbolIds[binding.ImplementingTypeKey]);
            command.Parameters.AddWithValue("$interface_id", symbolIds[binding.InterfaceMethodKey]);
            command.Parameters.AddWithValue("$implementation_id", symbolIds[binding.ImplementationMethodKey]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertConditionalSymbolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long profileId,
        IEnumerable<ConditionalSymbolData> symbols,
        IReadOnlyDictionary<string, long> documentIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO conditional_symbols_used(
                analysis_profile_id, document_id, symbol_name, occurrence_count)
            VALUES($profile_id, $document_id, $symbol_name, $occurrence_count);
            """);
        foreach (var symbol in symbols)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$profile_id", profileId);
            command.Parameters.AddWithValue("$document_id", documentIds[symbol.DocumentKey]);
            command.Parameters.AddWithValue("$symbol_name", symbol.SymbolName);
            command.Parameters.AddWithValue("$occurrence_count", symbol.OccurrenceCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "PRAGMA foreign_key_check;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Foreign-key integrity check failed for table '{reader.GetString(0)}'.");
        }
    }

    private static void ValidateSnapshot(IndexSnapshot snapshot)
    {
        ValidateRelativePath(snapshot.InputRoot, nameof(snapshot.InputRoot));
        ValidateRelativePath(snapshot.IndexRootAnchor, nameof(snapshot.IndexRootAnchor));

        var projectKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in snapshot.Projects)
        {
            if (!projectKeys.Add(project.Key))
            {
                throw new InvalidOperationException($"Duplicate project key '{project.Key}'.");
            }

            if (project.ProjectPath is not null)
            {
                ValidateRelativePath(project.ProjectPath, $"project '{project.Key}' path");
                if (!project.Key.Equals($"project-path:{project.ProjectPath}", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Project key '{project.Key}' does not match stored project path '{project.ProjectPath}'.");
                }
            }
        }

        var documentsByKey = new Dictionary<string, DocumentData>(StringComparer.Ordinal);
        var documentPaths = new HashSet<(string Project, string Path)>();
        foreach (var document in snapshot.Documents)
        {
            ValidateRelativePath(document.NormalizedPath, $"document '{document.Key}' path");
            if (!documentsByKey.TryAdd(document.Key, document) ||
                !documentPaths.Add((document.ProjectKey, document.NormalizedPath)))
            {
                throw new InvalidOperationException($"Duplicate document key or path '{document.Key}'.");
            }

            if (!projectKeys.Contains(document.ProjectKey))
            {
                throw new InvalidOperationException(
                    $"Document '{document.Key}' references unknown project '{document.ProjectKey}'.");
            }

            var expectedKey = $"{document.ProjectKey}|document:{document.NormalizedPath}";
            if (!document.Key.Equals(expectedKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Document key '{document.Key}' does not match stored path '{document.NormalizedPath}'.");
            }
        }

        var symbolKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Symbols)
        {
            if (!pair.Key.Equals(pair.Value.StableKey, StringComparison.Ordinal) ||
                !symbolKeys.Add(pair.Value.StableKey))
            {
                throw new InvalidOperationException($"Logical symbol dictionary key mismatch or duplicate '{pair.Key}'.");
            }
        }

        var declarationKeys = new HashSet<string>(StringComparer.Ordinal);
        var declarationsBySymbol = new Dictionary<string, List<SymbolDeclarationData>>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Declarations)
        {
            var declaration = pair.Value;
            if (!pair.Key.Equals(declaration.Key, StringComparison.Ordinal) ||
                !declarationKeys.Add(declaration.Key))
            {
                throw new InvalidOperationException($"Declaration dictionary key mismatch or duplicate '{pair.Key}'.");
            }

            if (!symbolKeys.Contains(declaration.SymbolKey) ||
                !documentsByKey.ContainsKey(declaration.DocumentKey))
            {
                throw new InvalidOperationException($"Declaration '{declaration.Key}' references an unknown owner.");
            }

            if (!Enum.IsDefined(declaration.Role))
            {
                throw new InvalidOperationException($"Declaration '{declaration.Key}' has an unknown role.");
            }

            var document = documentsByKey[declaration.DocumentKey];
            var expectedKey = $"{declaration.SymbolKey}|declaration:{document.NormalizedPath}:{declaration.SourceStart}:{declaration.SourceLength}:{(int)declaration.Role}";
            if (!declaration.Key.Equals(expectedKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Declaration key '{declaration.Key}' does not match its stored span and role.");
            }

            if (!declarationsBySymbol.TryGetValue(declaration.SymbolKey, out var declarations))
            {
                declarations = [];
                declarationsBySymbol.Add(declaration.SymbolKey, declarations);
            }

            declarations.Add(declaration);
        }

        if (symbolKeys.Overlaps(declarationKeys))
        {
            throw new InvalidOperationException("Logical symbol and declaration key domains must be disjoint.");
        }

        foreach (var symbol in snapshot.Symbols.Values)
        {
            if (symbol.Path is null)
            {
                throw new InvalidOperationException($"Logical symbol '{symbol.StableKey}' is missing semantic path data.");
            }

            if (symbol.SourceDocumentKey is not null ||
                symbol.SourceStart is not null ||
                symbol.SourceLength is not null ||
                symbol.NormalizedSource is not null ||
                symbol.NormalizedSourceHash is not null)
            {
                throw new InvalidOperationException(
                    $"Logical symbol '{symbol.StableKey}' contains source payload that belongs to a declaration.");
            }

            if (symbol.ContainingSymbolKey is not null)
            {
                ValidateRequiredLogicalKey(
                    symbol.ContainingSymbolKey,
                    symbolKeys,
                    declarationKeys,
                    "containing symbol");
            }

            if (symbol.AsyncNextSymbolKey is not null)
            {
                ValidateRequiredLogicalKey(
                    symbol.AsyncNextSymbolKey,
                    symbolKeys,
                    declarationKeys,
                    "async-next symbol");
            }

            IReadOnlyList<SymbolDeclarationData> declarations =
                declarationsBySymbol.TryGetValue(symbol.StableKey, out var groupedDeclarations)
                    ? groupedDeclarations
                    : [];
            if (symbol.Kind == IndexedSymbolKind.Type)
            {
                if (declarations.Count > 0 || symbol.PreferredDeclarationKey is not null)
                {
                    throw new InvalidOperationException(
                        $"Type symbol '{symbol.StableKey}' cannot have a callable declaration or preferred declaration.");
                }

                continue;
            }

            if (symbol.ProjectKey is null)
            {
                if (declarations.Count > 0 || symbol.PreferredDeclarationKey is not null)
                {
                    throw new InvalidOperationException(
                        $"Metadata symbol '{symbol.StableKey}' cannot have a source declaration or preferred declaration.");
                }

                continue;
            }

            if (declarations.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Source symbol '{symbol.StableKey}' must have a callable declaration and preferred declaration.");
            }

            SymbolDeclarationData? ordinary = null;
            SymbolDeclarationData? definition = null;
            SymbolDeclarationData? implementation = null;
            var hasDuplicateRole = false;
            foreach (var declaration in declarations)
            {
                switch (declaration.Role)
                {
                    case DeclarationRole.Ordinary when ordinary is null:
                        ordinary = declaration;
                        break;
                    case DeclarationRole.PartialDefinition when definition is null:
                        definition = declaration;
                        break;
                    case DeclarationRole.PartialImplementation when implementation is null:
                        implementation = declaration;
                        break;
                    default:
                        hasDuplicateRole = true;
                        break;
                }
            }

            if (hasDuplicateRole ||
                (ordinary is not null && (definition is not null || implementation is not null)) ||
                (implementation is not null && definition is null))
            {
                throw new InvalidOperationException($"Declaration roles for '{symbol.StableKey}' do not form a valid ordinary/partial set.");
            }

            var expectedPreferred = implementation ?? definition ?? ordinary!;
            if (!string.Equals(symbol.PreferredDeclarationKey, expectedPreferred.Key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Preferred declaration for '{symbol.StableKey}' is invalid.");
            }
        }

        foreach (var call in snapshot.Calls)
        {
            ValidateRequiredLogicalKey(call.CallerSymbolKey, symbolKeys, declarationKeys, "caller");
            ValidateLogicalOrUnresolvedKey(call.CalleeSymbolKey, symbolKeys, declarationKeys, "callee");
            ValidateLogicalOrUnresolvedKey(call.CalleeDefinitionKey, symbolKeys, declarationKeys, "callee definition");
            foreach (var candidate in call.CandidateSymbolKeys)
            {
                ValidateLogicalOrUnresolvedKey(candidate, symbolKeys, declarationKeys, "candidate");
            }

            if (!documentsByKey.ContainsKey(call.DocumentKey))
            {
                throw new InvalidOperationException($"Call references unknown document '{call.DocumentKey}'.");
            }
        }

        foreach (var relation in snapshot.Relations)
        {
            ValidateRequiredLogicalKey(relation.SourceSymbolKey, symbolKeys, declarationKeys, "relation source");
            ValidateRequiredLogicalKey(relation.TargetSymbolKey, symbolKeys, declarationKeys, "relation target");
            if (relation.SourceSymbolKey.Equals(relation.TargetSymbolKey, StringComparison.Ordinal) &&
                relation.RelationKind is SymbolRelationKind.PartialDefinition or SymbolRelationKind.PartialImplementation)
            {
                throw new InvalidOperationException("A partial logical callable cannot have a self relation.");
            }
        }

        foreach (var binding in snapshot.InterfaceMethodBindings)
        {
            ValidateRequiredLogicalKey(binding.ImplementingTypeKey, symbolKeys, declarationKeys, "binding type");
            ValidateRequiredLogicalKey(binding.InterfaceMethodKey, symbolKeys, declarationKeys, "binding interface method");
            ValidateRequiredLogicalKey(binding.ImplementationMethodKey, symbolKeys, declarationKeys, "binding implementation method");
        }
    }

    private static void ValidateLogicalOrUnresolvedKey(
        string? key,
        IReadOnlySet<string> symbolKeys,
        IReadOnlySet<string> declarationKeys,
        string role)
    {
        if (key is null)
        {
            return;
        }

        if (declarationKeys.Contains(key))
        {
            throw new InvalidOperationException($"{role} endpoint '{key}' is a physical declaration key.");
        }

        // Task 12 owns final dangling-call normalization. Non-declaration
        // unresolved compiler targets may be staged as SQL NULL.
    }

    private static void ValidateRequiredLogicalKey(
        string key,
        IReadOnlySet<string> symbolKeys,
        IReadOnlySet<string> declarationKeys,
        string role)
    {
        if (declarationKeys.Contains(key))
        {
            throw new InvalidOperationException($"{role} endpoint '{key}' is a physical declaration key.");
        }

        if (!symbolKeys.Contains(key))
        {
            throw new InvalidOperationException($"{role} endpoint '{key}' is missing from logical symbols.");
        }
    }

    private static void ValidateRelativePath(string path, string field)
    {
        string normalized;
        try
        {
            normalized = PathNormalizer.NormalizeRelative(path);
        }
        catch (Exception exception) when (exception is ArgumentException or InputResolutionException)
        {
            throw new InvalidOperationException(
                $"{field} must be a canonical non-rooted forward-slash path: '{path}'.",
                exception);
        }

        if (!path.Equals(normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{field} must be a canonical non-rooted forward-slash path: '{path}'.");
        }
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
