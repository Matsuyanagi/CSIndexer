using CsIndex.Core.Caching;
using Microsoft.Data.Sqlite;

namespace CsIndex.Storage.Schema;

public sealed class SchemaMigrator
{
    public const int CurrentVersion = RequestHasher.SchemaVersion;

    private const string IncompatibleDatabaseGuidance =
        "The database was not modified. Delete or rename the old database or choose a new --db path, " +
        "then run csindex index explicitly.";

    public async Task EnsureMigratedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);

            await using var existsCommand = connection.CreateCommand();
            existsCommand.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_info';";
            var exists = Convert.ToInt64(await existsCommand.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
            {
                await using var userSchemaCommand = connection.CreateCommand();
                userSchemaCommand.CommandText = """
                    SELECT COUNT(*)
                    FROM sqlite_master
                    WHERE type IN ('table', 'index', 'view', 'trigger')
                      AND name NOT GLOB 'sqlite_*';
                    """;
                var hasUserSchema = Convert.ToInt64(
                    await userSchemaCommand.ExecuteScalarAsync(cancellationToken)) > 0;
                if (hasUserSchema)
                {
                    throw UnsupportedSchema("schema_info is missing");
                }

                await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
                await CreateVersionFiveAsync(connection, cancellationToken);
                return;
            }

            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT version FROM schema_info;";
            var versions = new List<long>();
            await using (var reader = await versionCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    versions.Add(reader.GetInt64(0));
                }
            }

            if (versions.Count != 1)
            {
                throw UnsupportedSchema("schema_info is corrupt: exactly one row is required");
            }

            if (versions[0] != CurrentVersion)
            {
                throw UnsupportedSchema(versions[0]);
            }

            await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        }
        catch (IndexDatabaseException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new IndexDatabaseException(
                $"The SQLite index is unreadable or corrupt: {exception.Message}",
                exception);
        }
    }

    private static IndexDatabaseException UnsupportedSchema(string details) =>
        new($"Unsupported database schema version ({details}). {IncompatibleDatabaseGuidance}");

    private static IndexDatabaseException UnsupportedSchema(long version) =>
        new($"Unsupported database schema version {version}; this build supports version {CurrentVersion}. " +
            IncompatibleDatabaseGuidance);

    private static async Task CreateVersionFiveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE schema_info (
                version INTEGER NOT NULL
            );

            INSERT INTO schema_info(version) VALUES (5);

            CREATE TABLE analysis_profiles (
                id                    INTEGER PRIMARY KEY,
                name                  TEXT NOT NULL,
                input_mode            INTEGER NOT NULL,
                configuration         TEXT,
                target_framework      TEXT,
                runtime_identifier    TEXT,
                operating_system      TEXT,
                architecture          TEXT,
                preprocessor_symbols  TEXT NOT NULL,
                profile_hash          BLOB NOT NULL UNIQUE
            );

            CREATE TABLE index_runs (
                id                    INTEGER PRIMARY KEY,
                analysis_profile_id   INTEGER NOT NULL,
                input_root            TEXT NOT NULL,
                index_root_anchor     TEXT NOT NULL,
                input_fingerprint     BLOB NOT NULL,
                request_hash          BLOB NOT NULL,
                indexed_at_utc        TEXT NOT NULL,

                FOREIGN KEY(analysis_profile_id)
                  REFERENCES analysis_profiles(id)
            );

            CREATE TABLE projects (
                id                    INTEGER PRIMARY KEY,
                index_run_id          INTEGER NOT NULL,
                analysis_profile_id   INTEGER NOT NULL,
                name                  TEXT NOT NULL,
                assembly_name         TEXT,
                project_path          TEXT,
                target_framework      TEXT,
                project_fingerprint   BLOB NOT NULL,

                FOREIGN KEY(index_run_id)
                  REFERENCES index_runs(id) ON DELETE CASCADE,

                FOREIGN KEY(analysis_profile_id)
                  REFERENCES analysis_profiles(id)
            );

            CREATE TABLE documents (
                id                    INTEGER PRIMARY KEY,
                project_id            INTEGER NOT NULL,
                normalized_path       TEXT NOT NULL,
                content_hash          BLOB NOT NULL,
                semantic_hash         BLOB,
                is_generated          INTEGER NOT NULL DEFAULT 0,
                generation_kind       INTEGER NOT NULL DEFAULT 0,

                UNIQUE(project_id, normalized_path),

                FOREIGN KEY(project_id)
                  REFERENCES projects(id) ON DELETE CASCADE
            );

            CREATE TABLE symbols (
                id                    INTEGER PRIMARY KEY,
                analysis_profile_id   INTEGER NOT NULL,
                project_id            INTEGER,
                stable_key            TEXT NOT NULL,
                kind                  INTEGER NOT NULL,
                name                  TEXT NOT NULL,
                namespace_name        TEXT NOT NULL DEFAULT '',
                type_simple_name      TEXT,
                type_metadata_name    TEXT,
                path_segment_kind     INTEGER NOT NULL,
                path_segment_display  TEXT NOT NULL,
                path_segment_identity TEXT NOT NULL,
                type_display_path     TEXT NOT NULL,
                type_identity_path    TEXT NOT NULL,
                executable_display_path TEXT NOT NULL,
                executable_identity_path TEXT NOT NULL,
                preferred_declaration_id INTEGER,
                containing_symbol_id  INTEGER,
                arity                 INTEGER NOT NULL DEFAULT 0,
                parameter_count      INTEGER,
                method_kind           INTEGER,
                accessibility         INTEGER,
                type_kind             INTEGER,
                is_static             INTEGER NOT NULL DEFAULT 0,
                is_abstract           INTEGER NOT NULL DEFAULT 0,
                is_virtual            INTEGER NOT NULL DEFAULT 0,
                is_override           INTEGER NOT NULL DEFAULT 0,
                async_role            INTEGER NOT NULL DEFAULT 0,
                async_involvement_depth INTEGER,
                return_type_key       TEXT,
                return_type_display   TEXT,
                conversion_type_key   TEXT,
                conversion_type_display TEXT,
                async_next_symbol_id  INTEGER,
                is_generated          INTEGER NOT NULL DEFAULT 0,

                UNIQUE(analysis_profile_id, stable_key),

                FOREIGN KEY(analysis_profile_id)
                  REFERENCES analysis_profiles(id),

                FOREIGN KEY(project_id)
                  REFERENCES projects(id) ON DELETE CASCADE,

                FOREIGN KEY(containing_symbol_id)
                  REFERENCES symbols(id) ON DELETE SET NULL,

                FOREIGN KEY(async_next_symbol_id)
                  REFERENCES symbols(id) ON DELETE SET NULL,

                FOREIGN KEY(preferred_declaration_id)
                  REFERENCES symbol_declarations(id) ON DELETE SET NULL
            );

            CREATE TABLE method_parameters (
                method_id       INTEGER NOT NULL,
                ordinal          INTEGER NOT NULL,
                name             TEXT,
                type_key         TEXT NOT NULL,
                type_display     TEXT NOT NULL,
                ref_kind         INTEGER NOT NULL,
                is_optional      INTEGER NOT NULL DEFAULT 0,

                PRIMARY KEY(method_id, ordinal),

                FOREIGN KEY(method_id)
                  REFERENCES symbols(id) ON DELETE CASCADE
            );

            CREATE TABLE symbol_declarations (
                id                     INTEGER PRIMARY KEY,
                declaration_key       TEXT NOT NULL UNIQUE,
                symbol_id             INTEGER NOT NULL,
                document_id           INTEGER NOT NULL,
                declaration_role      INTEGER NOT NULL CHECK (declaration_role IN (1, 2, 3)),
                source_start           INTEGER NOT NULL,
                source_length         INTEGER NOT NULL,
                normalized_source     TEXT NOT NULL,
                normalized_source_hash BLOB NOT NULL,
                is_generated          INTEGER NOT NULL,

                UNIQUE(symbol_id, document_id, source_start, source_length, declaration_role),

                FOREIGN KEY(symbol_id)
                  REFERENCES symbols(id) ON DELETE CASCADE,

                FOREIGN KEY(document_id)
                  REFERENCES documents(id) ON DELETE CASCADE
            );

            CREATE TABLE calls (
                id                      INTEGER PRIMARY KEY,
                analysis_profile_id     INTEGER NOT NULL,
                caller_symbol_id        INTEGER NOT NULL,
                callee_symbol_id       INTEGER,
                callee_definition_id   INTEGER,
                reference_kind         INTEGER NOT NULL,
                dispatch_kind          INTEGER NOT NULL,
                resolution_status      INTEGER NOT NULL,
                resolution_reason      INTEGER NOT NULL,
                async_usage_kind       INTEGER NOT NULL DEFAULT 0,
                document_id            INTEGER NOT NULL,
                source_start           INTEGER NOT NULL,
                source_length          INTEGER NOT NULL,
                unresolved_name        TEXT,
                receiver_type_key      TEXT,

                FOREIGN KEY(analysis_profile_id)
                  REFERENCES analysis_profiles(id),

                FOREIGN KEY(caller_symbol_id)
                  REFERENCES symbols(id) ON DELETE CASCADE,

                FOREIGN KEY(callee_symbol_id)
                  REFERENCES symbols(id) ON DELETE SET NULL,

                FOREIGN KEY(callee_definition_id)
                  REFERENCES symbols(id) ON DELETE SET NULL,

                FOREIGN KEY(document_id)
                  REFERENCES documents(id) ON DELETE CASCADE
            );

            CREATE TABLE call_candidates (
                call_id             INTEGER NOT NULL,
                candidate_symbol_id INTEGER NOT NULL,

                PRIMARY KEY(call_id, candidate_symbol_id),

                FOREIGN KEY(call_id)
                  REFERENCES calls(id) ON DELETE CASCADE,

                FOREIGN KEY(candidate_symbol_id)
                  REFERENCES symbols(id) ON DELETE CASCADE
            );

            CREATE TABLE symbol_relations (
                analysis_profile_id INTEGER NOT NULL,
                source_symbol_id    INTEGER NOT NULL,
                target_symbol_id    INTEGER NOT NULL,
                relation_kind       INTEGER NOT NULL,

                PRIMARY KEY(
                    analysis_profile_id,
                    source_symbol_id,
                    target_symbol_id,
                    relation_kind
                ),

                FOREIGN KEY(source_symbol_id)
                  REFERENCES symbols(id) ON DELETE CASCADE,

                FOREIGN KEY(target_symbol_id)
                  REFERENCES symbols(id) ON DELETE CASCADE
            );

            CREATE TABLE interface_method_bindings (
                analysis_profile_id      INTEGER NOT NULL,
                implementing_type_id     INTEGER NOT NULL,
                interface_method_id      INTEGER NOT NULL,
                implementation_method_id INTEGER NOT NULL,

                PRIMARY KEY (
                    analysis_profile_id,
                    implementing_type_id,
                    interface_method_id,
                    implementation_method_id
                ),

                FOREIGN KEY(analysis_profile_id)
                  REFERENCES analysis_profiles(id),

                FOREIGN KEY(implementing_type_id)
                  REFERENCES symbols(id) ON DELETE CASCADE,

                FOREIGN KEY(interface_method_id)
                  REFERENCES symbols(id) ON DELETE CASCADE,

                FOREIGN KEY(implementation_method_id)
                  REFERENCES symbols(id) ON DELETE CASCADE
            );

            CREATE TABLE conditional_symbols_used (
                analysis_profile_id INTEGER NOT NULL,
                document_id         INTEGER NOT NULL,
                symbol_name         TEXT NOT NULL,
                occurrence_count    INTEGER NOT NULL,

                PRIMARY KEY(
                    analysis_profile_id,
                    document_id,
                    symbol_name
                ),

                FOREIGN KEY(document_id)
                  REFERENCES documents(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_index_runs_cache
            ON index_runs(input_root, request_hash, input_fingerprint);

            CREATE INDEX ix_symbols_profile_kind
            ON symbols(analysis_profile_id, kind);

            CREATE INDEX ix_symbols_profile_containing
            ON symbols(analysis_profile_id, containing_symbol_id);

            CREATE INDEX ix_symbols_profile_async_depth
            ON symbols(analysis_profile_id, async_involvement_depth);

            CREATE INDEX ix_symbols_profile_async_next
            ON symbols(analysis_profile_id, async_next_symbol_id);

            CREATE INDEX ix_symbols_profile_preferred_declaration
            ON symbols(analysis_profile_id, preferred_declaration_id);

            CREATE INDEX ix_symbols_profile_name
            ON symbols(analysis_profile_id, name);

            CREATE INDEX ix_symbols_profile_short_method
            ON symbols(analysis_profile_id, type_simple_name, name, parameter_count);

            CREATE INDEX ix_symbols_profile_namespace_type_method
            ON symbols(analysis_profile_id, namespace_name, type_simple_name, name, parameter_count);

            CREATE INDEX ix_symbols_profile_path_identity
            ON symbols(analysis_profile_id, namespace_name, type_identity_path, executable_identity_path);

            CREATE INDEX ix_symbols_profile_source_executable
            ON symbols(analysis_profile_id, kind)
            WHERE preferred_declaration_id IS NOT NULL;

            CREATE INDEX ix_symbol_declarations_symbol
            ON symbol_declarations(symbol_id);

            CREATE INDEX ix_symbol_declarations_document_location_role
            ON symbol_declarations(document_id, source_start, source_length, declaration_role);

            CREATE INDEX ix_calls_callee
            ON calls(callee_definition_id);

            CREATE INDEX ix_calls_caller
            ON calls(caller_symbol_id);

            CREATE INDEX ix_calls_location
            ON calls(document_id, source_start);

            CREATE INDEX ix_relations_target
            ON symbol_relations(target_symbol_id, relation_kind);

            CREATE INDEX ix_interface_method_bindings_contract
            ON interface_method_bindings(analysis_profile_id, interface_method_id);

            CREATE INDEX ix_interface_method_bindings_type
            ON interface_method_bindings(analysis_profile_id, implementing_type_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
