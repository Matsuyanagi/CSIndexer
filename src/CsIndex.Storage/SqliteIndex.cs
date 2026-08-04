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
        command.Parameters.AddWithValue("$input_root", PathNormalizer.Normalize(inputRoot));
        command.Parameters.Add("$input_fingerprint", SqliteType.Blob).Value = inputFingerprint;
        command.Parameters.Add("$request_hash", SqliteType.Blob).Value = requestHash;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task SaveAsync(IndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
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
            await InsertParametersAsync(
                connection,
                transaction,
                snapshot.Symbols.Values,
                symbolIds,
                cancellationToken);
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
                     "DELETE FROM symbol_relations WHERE analysis_profile_id = $profile_id;",
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
                analysis_profile_id, input_root, input_fingerprint, request_hash, indexed_at_utc)
            VALUES($profile_id, $input_root, $input_fingerprint, $request_hash, $indexed_at);
            SELECT last_insert_rowid();
            """);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$input_root", PathNormalizer.Normalize(snapshot.InputRoot));
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
                type_simple_name, type_metadata_name, fully_qualified_name, display_name,
                containing_symbol_id, arity, parameter_count, method_kind, accessibility,
                type_kind,
                is_static, is_abstract, is_virtual, is_override, async_role,
                async_involvement_depth, source_document_id, source_start, source_length, is_generated)
            VALUES(
                $profile_id, $project_id, $stable_key, $kind, $name, $namespace_name,
                $type_simple_name, $type_metadata_name, $fully_qualified_name, $display_name,
                NULL, $arity, $parameter_count, $method_kind, $accessibility,
                $type_kind,
                $is_static, $is_abstract, $is_virtual, $is_override, $async_role,
                $async_involvement_depth, $source_document_id, $source_start, $source_length, $is_generated);
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
        command.Parameters.AddWithValue("$fully_qualified_name", symbol.FullyQualifiedName);
        command.Parameters.AddWithValue("$display_name", symbol.DisplayName);
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
        command.Parameters.AddWithValue("$source_document_id", symbol.SourceDocumentKey is not null &&
                                                               documentIds.TryGetValue(symbol.SourceDocumentKey, out var documentId)
            ? documentId
            : DBNull.Value);
        command.Parameters.AddWithValue("$source_start", (object?)symbol.SourceStart ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_length", (object?)symbol.SourceLength ?? DBNull.Value);
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

    private static async Task InsertParametersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SymbolData> symbols,
        IReadOnlyDictionary<string, long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO method_parameters(method_id, ordinal, name, type_key, ref_kind, is_optional)
            VALUES($method_id, $ordinal, $name, $type_key, $ref_kind, $is_optional);
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
                command.Parameters.AddWithValue("$ref_kind", parameter.RefKind);
                command.Parameters.AddWithValue("$is_optional", parameter.IsOptional);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
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
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$profile_id", profileId);
            command.Parameters.AddWithValue("$caller_id", symbolIds[call.CallerSymbolKey]);
            command.Parameters.AddWithValue("$callee_id", call.CalleeSymbolKey is not null &&
                                                          symbolIds.TryGetValue(call.CalleeSymbolKey, out var calleeId)
                ? calleeId
                : DBNull.Value);
            command.Parameters.AddWithValue("$definition_id", call.CalleeDefinitionKey is not null &&
                                                              symbolIds.TryGetValue(call.CalleeDefinitionKey, out var definitionId)
                ? definitionId
                : DBNull.Value);
            command.Parameters.AddWithValue("$reference_kind", (int)call.ReferenceKind);
            command.Parameters.AddWithValue("$dispatch_kind", (int)call.DispatchKind);
            command.Parameters.AddWithValue("$resolution_status", (int)call.ResolutionStatus);
            command.Parameters.AddWithValue("$resolution_reason", (int)call.ResolutionReason);
            command.Parameters.AddWithValue("$async_usage_kind", (int)call.AsyncUsageKind);
            command.Parameters.AddWithValue("$document_id", documentIds[call.DocumentKey]);
            command.Parameters.AddWithValue("$source_start", call.SourceStart);
            command.Parameters.AddWithValue("$source_length", call.SourceLength);
            command.Parameters.AddWithValue("$unresolved_name", (object?)call.UnresolvedName ?? DBNull.Value);
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
