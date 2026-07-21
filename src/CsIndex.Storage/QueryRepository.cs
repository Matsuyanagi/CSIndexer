using System.Text.Json;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace CsIndex.Storage;

public sealed class QueryRepository(string databasePath, SchemaMigrator migrator)
{
    private readonly string _databasePath = PathNormalizer.Normalize(databasePath);

    public async Task<StoredProfile> GetProfileAsync(
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                p.id, p.name, p.input_mode, p.configuration, p.target_framework,
                p.runtime_identifier, p.preprocessor_symbols, r.input_root
            FROM analysis_profiles p
            JOIN index_runs r ON r.analysis_profile_id = p.id
            WHERE ($profile_name IS NULL OR p.name = $profile_name)
            ORDER BY r.indexed_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$profile_name", (object?)profileName ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new IndexDatabaseException(profileName is null
                ? "The database does not contain an index run."
                : $"Analysis profile was not found: {profileName}");
        }

        return new StoredProfile(
            reader.GetInt64(0),
            reader.GetString(1),
            (InputMode)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
            reader.GetString(7));
    }

    public async Task<IReadOnlyList<StoredSymbol>> FindSymbolCandidatesAsync(
        long profileId,
        string? name = null,
        string? typeSimpleName = null,
        IndexedSymbolKind? kind = null,
        bool sourceOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.id, s.stable_key, s.kind, s.name, s.namespace_name,
                s.type_simple_name, s.type_metadata_name, s.fully_qualified_name,
                s.display_name, s.containing_symbol_id, s.arity, s.parameter_count,
                s.is_static, s.is_abstract, s.is_virtual, s.is_override,
                s.async_role, s.async_involvement_depth,
                d.normalized_path, s.source_start, s.source_length, s.is_generated,
                p.assembly_name
            FROM symbols s
            LEFT JOIN documents d ON d.id = s.source_document_id
            LEFT JOIN projects p ON p.id = s.project_id
            WHERE s.analysis_profile_id = $profile_id
              AND ($name IS NULL OR s.name = $name)
              AND ($type_name IS NULL OR s.type_simple_name = $type_name)
              AND ($kind IS NULL OR s.kind = $kind)
              AND ($source_only = 0 OR s.source_document_id IS NOT NULL)
            ORDER BY s.display_name, d.normalized_path, s.source_start;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$type_name", (object?)typeSimpleName ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (int)kind.Value);
        command.Parameters.AddWithValue("$source_only", sourceOnly);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredSymbol>> GetSymbolsByIdsAsync(
        long profileId,
        IEnumerable<long> ids,
        CancellationToken cancellationToken = default)
    {
        var values = ids.Distinct().ToArray();
        if (values.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, values);
        command.CommandText = $"""
            SELECT
                s.id, s.stable_key, s.kind, s.name, s.namespace_name,
                s.type_simple_name, s.type_metadata_name, s.fully_qualified_name,
                s.display_name, s.containing_symbol_id, s.arity, s.parameter_count,
                s.is_static, s.is_abstract, s.is_virtual, s.is_override,
                s.async_role, s.async_involvement_depth,
                d.normalized_path, s.source_start, s.source_length, s.is_generated,
                p.assembly_name
            FROM symbols s
            LEFT JOIN documents d ON d.id = s.source_document_id
            LEFT JOIN projects p ON p.id = s.project_id
            WHERE s.analysis_profile_id = $profile_id AND s.id IN ({placeholders})
            ORDER BY s.display_name;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredCall>> GetCallsByCalleeAsync(
        long profileId,
        IEnumerable<long> definitionIds,
        GeneratedFilter generatedFilter,
        IReadOnlySet<ReferenceKind>? referenceKinds = null,
        CancellationToken cancellationToken = default)
    {
        var ids = definitionIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var referenceClause = AddReferenceKindClause(command, referenceKinds);
        command.CommandText = BuildCallSelect($"""
            c.analysis_profile_id = $profile_id
            AND (
                c.callee_definition_id IN ({placeholders})
                OR EXISTS (
                    SELECT 1 FROM call_candidates cc
                    WHERE cc.call_id = c.id AND cc.candidate_symbol_id IN ({placeholders})
                )
            )
            {referenceClause}
            AND ($generated_filter = 0
                 OR ($generated_filter = 1 AND d.is_generated = 0)
                 OR ($generated_filter = 2 AND d.is_generated = 1))
            """);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$generated_filter", (int)generatedFilter);
        return await ReadCallsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredCall>> GetCallsByCallerAsync(
        long profileId,
        IEnumerable<long> callerIds,
        GeneratedFilter generatedFilter,
        IReadOnlySet<ReferenceKind>? referenceKinds = null,
        CancellationToken cancellationToken = default)
    {
        var ids = callerIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var referenceClause = AddReferenceKindClause(command, referenceKinds);
        command.CommandText = BuildCallSelect($"""
            c.analysis_profile_id = $profile_id
            AND c.caller_symbol_id IN ({placeholders})
            {referenceClause}
            AND ($generated_filter = 0
                 OR ($generated_filter = 1 AND d.is_generated = 0)
                 OR ($generated_filter = 2 AND d.is_generated = 1))
            """);
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$generated_filter", (int)generatedFilter);
        return await ReadCallsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredRelation>> GetRelationsByTargetAsync(
        long profileId,
        IEnumerable<long> targetIds,
        IReadOnlySet<SymbolRelationKind>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        var ids = targetIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var kindClause = AddRelationKindClause(command, kinds);
        command.CommandText = $"""
            SELECT
                r.source_symbol_id, source.display_name,
                r.target_symbol_id, target.display_name, r.relation_kind
            FROM symbol_relations r
            JOIN symbols source ON source.id = r.source_symbol_id
            JOIN symbols target ON target.id = r.target_symbol_id
            WHERE r.analysis_profile_id = $profile_id
              AND r.target_symbol_id IN ({placeholders})
              {kindClause}
            ORDER BY source.display_name;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadRelationsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredRelation>> GetRelationsBySourceAsync(
        long profileId,
        IEnumerable<long> sourceIds,
        IReadOnlySet<SymbolRelationKind>? kinds = null,
        CancellationToken cancellationToken = default)
    {
        var ids = sourceIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var kindClause = AddRelationKindClause(command, kinds);
        command.CommandText = $"""
            SELECT
                r.source_symbol_id, source.display_name,
                r.target_symbol_id, target.display_name, r.relation_kind
            FROM symbol_relations r
            JOIN symbols source ON source.id = r.source_symbol_id
            JOIN symbols target ON target.id = r.target_symbol_id
            WHERE r.analysis_profile_id = $profile_id
              AND r.source_symbol_id IN ({placeholders})
              {kindClause}
            ORDER BY target.display_name;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadRelationsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredDocument>> FindDocumentsAsync(
        long profileId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedInput = path.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(normalizedInput) ? PathNormalizer.Normalize(normalizedInput) : null;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.normalized_path, d.is_generated
            FROM documents d
            JOIN projects p ON p.id = d.project_id
            WHERE p.analysis_profile_id = $profile_id
              AND ($full_path IS NOT NULL AND d.normalized_path = $full_path COLLATE NOCASE
                   OR d.normalized_path LIKE $suffix ESCAPE '\' COLLATE NOCASE)
            ORDER BY d.normalized_path;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$full_path", (object?)fullPath ?? DBNull.Value);
        var escaped = normalizedInput.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        command.Parameters.AddWithValue("$suffix", $"%{Path.DirectorySeparatorChar}{escaped}");
        var result = new List<StoredDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredDocument(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return result;
    }

    public async Task<StoredCall?> FindCallAtAsync(
        long profileId,
        long documentId,
        int position,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildCallSelect("""
            c.analysis_profile_id = $profile_id
            AND c.document_id = $document_id
            AND c.source_start <= $position
            AND c.source_start + c.source_length >= $position
            """) + " ORDER BY c.source_length ASC LIMIT 1;";
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$document_id", documentId);
        command.Parameters.AddWithValue("$position", position);
        return (await ReadCallsAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ConditionalSummary>> GetConditionalSymbolsAsync(
        StoredProfile profile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_name, COUNT(DISTINCT document_id), SUM(occurrence_count)
            FROM conditional_symbols_used
            WHERE analysis_profile_id = $profile_id
            GROUP BY symbol_name
            ORDER BY symbol_name;
            """;
        command.Parameters.AddWithValue("$profile_id", profile.Id);
        var active = profile.PreprocessorSymbols.ToHashSet(StringComparer.Ordinal);
        var result = new List<ConditionalSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            result.Add(new ConditionalSummary(
                name,
                reader.GetInt32(1),
                reader.GetInt32(2),
                active.Contains(name)));
        }

        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection? connection = null;
        try
        {
            if (!File.Exists(_databasePath))
            {
                throw new IndexDatabaseException($"Index database does not exist: {_databasePath}");
            }

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await migrator.EnsureMigratedAsync(connection, cancellationToken);
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
        catch (SqliteException exception)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            throw new IndexDatabaseException(
                $"The SQLite index is unreadable or corrupt: {exception.Message}",
                exception);
        }
    }

    private static async Task<IReadOnlyList<StoredSymbol>> ReadSymbolsAsync(
        SqliteConnection connection,
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<StoredSymbol>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new StoredSymbol(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    (IndexedSymbolKind)reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.GetBoolean(12),
                    reader.GetBoolean(13),
                    reader.GetBoolean(14),
                    reader.GetBoolean(15),
                    (AsyncRole)reader.GetInt32(16),
                    reader.IsDBNull(17) ? null : reader.GetInt32(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? null : reader.GetInt32(19),
                    reader.IsDBNull(20) ? null : reader.GetInt32(20),
                    reader.GetBoolean(21),
                    reader.IsDBNull(22) ? null : reader.GetString(22),
                    []));
            }
        }

        await using var parameterCommand = connection.CreateCommand();
        parameterCommand.CommandText = """
            SELECT ordinal, name, type_key, ref_kind, is_optional
            FROM method_parameters
            WHERE method_id = $method_id
            ORDER BY ordinal;
            """;
        var methodId = parameterCommand.Parameters.Add("$method_id", SqliteType.Integer);
        for (var index = 0; index < rows.Count; index++)
        {
            var parameters = new List<StoredParameter>();
            methodId.Value = rows[index].Id;
            await using var reader = await parameterCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                parameters.Add(new StoredParameter(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetBoolean(4)));
            }

            rows[index] = rows[index] with { Parameters = parameters };
        }

        return rows;
    }

    private static async Task<IReadOnlyList<StoredCall>> ReadCallsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<StoredCall>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredCall(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                (ReferenceKind)reader.GetInt32(8),
                (DispatchKind)reader.GetInt32(9),
                (ResolutionStatus)reader.GetInt32(10),
                (ResolutionReason)reader.GetInt32(11),
                (AsyncUsageKind)reader.GetInt32(12),
                reader.GetInt64(13),
                reader.GetString(14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetBoolean(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<StoredRelation>> ReadRelationsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<StoredRelation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredRelation(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                (SymbolRelationKind)reader.GetInt32(4)));
        }

        return result;
    }

    private static string BuildCallSelect(string whereClause) => $"""
        SELECT
            c.id,
            caller.id, caller.display_name, caller.containing_symbol_id,
            callee.id, callee.display_name,
            definition.id, definition.display_name,
            c.reference_kind, c.dispatch_kind, c.resolution_status, c.resolution_reason,
            c.async_usage_kind,
            d.id, d.normalized_path, c.source_start, c.source_length, d.is_generated,
            c.unresolved_name, c.receiver_type_key
        FROM calls c
        JOIN symbols caller ON caller.id = c.caller_symbol_id
        LEFT JOIN symbols callee ON callee.id = c.callee_symbol_id
        LEFT JOIN symbols definition ON definition.id = c.callee_definition_id
        JOIN documents d ON d.id = c.document_id
        WHERE {whereClause}
        """;

    private static string AddIdParameters(SqliteCommand command, IReadOnlyList<long> ids)
    {
        var names = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
        {
            names[index] = $"$id{index}";
            command.Parameters.AddWithValue(names[index], ids[index]);
        }

        return string.Join(',', names);
    }

    private static string AddReferenceKindClause(
        SqliteCommand command,
        IReadOnlySet<ReferenceKind>? kinds)
    {
        if (kinds is null || kinds.Count == 0)
        {
            return string.Empty;
        }

        var names = kinds.Select((kind, index) =>
        {
            var name = $"$reference_kind{index}";
            command.Parameters.AddWithValue(name, (int)kind);
            return name;
        });
        return $"AND c.reference_kind IN ({string.Join(',', names)})";
    }

    private static string AddRelationKindClause(
        SqliteCommand command,
        IReadOnlySet<SymbolRelationKind>? kinds)
    {
        if (kinds is null || kinds.Count == 0)
        {
            return string.Empty;
        }

        var names = kinds.Select((kind, index) =>
        {
            var name = $"$relation_kind{index}";
            command.Parameters.AddWithValue(name, (int)kind);
            return name;
        });
        return $"AND r.relation_kind IN ({string.Join(',', names)})";
    }
}
