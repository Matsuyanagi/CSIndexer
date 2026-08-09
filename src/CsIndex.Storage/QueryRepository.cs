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
        var whereClause = """
            s.analysis_profile_id = $profile_id
              AND ($name IS NULL OR s.name = $name)
              AND ($type_name IS NULL OR s.type_simple_name = $type_name)
              AND ($kind IS NULL OR s.kind = $kind)
            """;
        if (sourceOnly)
        {
            whereClause += "\nAND s.source_document_id IS NOT NULL";
        }

        command.CommandText = BuildSymbolSelect(whereClause) +
                          "\nORDER BY s.display_name, d.normalized_path, s.source_start, s.id;";
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$type_name", (object?)typeSimpleName ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (int)kind.Value);
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
        command.CommandText = BuildSymbolSelect($"""
            s.analysis_profile_id = $profile_id AND s.id IN ({placeholders})
            """) + """
            ORDER BY s.display_name, d.normalized_path, s.source_start, s.id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredSymbol>> FindFunctionSymbolsAsync(
        long profileId,
        IndexedSymbolKind? kind,
        bool asyncInvolved,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildSymbolSelect("""
            s.analysis_profile_id = $profile_id
              AND (
                  ($kind IS NOT NULL AND s.kind = $kind)
                  OR ($kind IS NULL AND s.kind IN ($method_kind, $lambda_kind))
              )
              AND ($async_involved = 0 OR s.async_involvement_depth IS NOT NULL)
            """) + """
            ORDER BY s.display_name, d.normalized_path, s.source_start, s.id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (int)kind.Value);
        command.Parameters.AddWithValue("$method_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$lambda_kind", (int)IndexedSymbolKind.Lambda);
        command.Parameters.AddWithValue("$async_involved", asyncInvolved);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredSymbol>> FindExecutableSymbolsAsync(
        long profileId,
        bool sourceOnly,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var whereClause = """
            s.analysis_profile_id = $profile_id
              AND s.kind IN ($method_kind, $lambda_kind)
            """;
        if (sourceOnly)
        {
            whereClause += "\nAND s.source_document_id IS NOT NULL";
        }

        command.CommandText = BuildSymbolSelect(whereClause) +
                          "\nORDER BY s.display_name, d.normalized_path, s.source_start, s.id;";
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$method_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$lambda_kind", (int)IndexedSymbolKind.Lambda);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredInterfaceMethodBinding>> GetInterfaceMethodBindingsAsync(
        long profileId,
        IEnumerable<long> interfaceMethodIds,
        CancellationToken cancellationToken = default)
    {
        var ids = interfaceMethodIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        command.CommandText = $"""
            SELECT implementing_type_id, interface_method_id, implementation_method_id
            FROM interface_method_bindings
            WHERE analysis_profile_id = $profile_id
              AND interface_method_id IN ({placeholders})
            ORDER BY implementing_type_id, implementation_method_id, interface_method_id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var result = new List<StoredInterfaceMethodBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredInterfaceMethodBinding(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }

        return result;
    }

    public async Task<IReadOnlyList<StoredInheritedMethodCandidate>> FindInheritedMethodCandidatesAsync(
        long profileId,
        IEnumerable<long> receiverTypeIds,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        var ids = receiverTypeIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        command.CommandText = $"""
            WITH RECURSIVE ancestors(
                receiver_type_id,
                current_type_id,
                receiver_type_kind,
                depth,
                path) AS (
                SELECT
                    receiver.id,
                    receiver.id,
                    receiver.type_kind,
                    0,
                    ',' || CAST(receiver.id AS TEXT) || ','
                FROM symbols receiver
                WHERE receiver.analysis_profile_id = $profile_id
                  AND receiver.kind = $type_symbol_kind
                  AND receiver.id IN ({placeholders})

                UNION ALL

                SELECT
                    parent.receiver_type_id,
                    relation.target_symbol_id,
                    parent.receiver_type_kind,
                    parent.depth + 1,
                    parent.path || CAST(relation.target_symbol_id AS TEXT) || ','
                FROM ancestors parent
                JOIN symbol_relations relation
                  ON relation.source_symbol_id = parent.current_type_id
                JOIN symbols ancestor
                  ON ancestor.id = relation.target_symbol_id
                 AND ancestor.analysis_profile_id = $profile_id
                 AND ancestor.kind = $type_symbol_kind
                WHERE relation.analysis_profile_id = $profile_id
                  AND (
                      (parent.receiver_type_kind IN ($class_type_kind, $struct_type_kind)
                       AND relation.relation_kind = $inherits_relation_kind)
                      OR
                      (parent.receiver_type_kind = $interface_type_kind
                       AND relation.relation_kind = $implements_relation_kind)
                  )
                  AND instr(
                      parent.path,
                      ',' || CAST(relation.target_symbol_id AS TEXT) || ',') = 0
            )
            SELECT
                ancestor.receiver_type_id,
                method.id,
                MIN(ancestor.depth)
            FROM ancestors ancestor
            JOIN symbols receiver
              ON receiver.id = ancestor.receiver_type_id
             AND receiver.analysis_profile_id = $profile_id
            JOIN symbols method
              ON method.containing_symbol_id = ancestor.current_type_id
             AND method.analysis_profile_id = $profile_id
             AND method.kind = $method_symbol_kind
            LEFT JOIN projects receiver_project ON receiver_project.id = receiver.project_id
            LEFT JOIN projects method_project ON method_project.id = method.project_id
            WHERE ancestor.depth > 0
              AND method.name = $method_name
              AND (
                  method.accessibility IN (
                      $public_accessibility,
                      $protected_accessibility,
                      $protected_internal_accessibility)
                  OR (
                      method.accessibility IN (
                          $internal_accessibility,
                          $private_protected_accessibility)
                      AND method_project.assembly_name = receiver_project.assembly_name
                  )
              )
            GROUP BY ancestor.receiver_type_id, method.id
            ORDER BY ancestor.receiver_type_id, MIN(ancestor.depth), method.id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$method_name", methodName);
        command.Parameters.AddWithValue("$type_symbol_kind", (int)IndexedSymbolKind.Type);
        command.Parameters.AddWithValue("$method_symbol_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$class_type_kind", (int)IndexedTypeKind.Class);
        command.Parameters.AddWithValue("$struct_type_kind", (int)IndexedTypeKind.Struct);
        command.Parameters.AddWithValue("$interface_type_kind", (int)IndexedTypeKind.Interface);
        command.Parameters.AddWithValue("$inherits_relation_kind", (int)SymbolRelationKind.Inherits);
        command.Parameters.AddWithValue("$implements_relation_kind", (int)SymbolRelationKind.Implements);
        command.Parameters.AddWithValue("$public_accessibility", (int)IndexedAccessibility.Public);
        command.Parameters.AddWithValue("$protected_accessibility", (int)IndexedAccessibility.Protected);
        command.Parameters.AddWithValue(
            "$protected_internal_accessibility",
            (int)IndexedAccessibility.ProtectedOrInternal);
        command.Parameters.AddWithValue("$internal_accessibility", (int)IndexedAccessibility.Internal);
        command.Parameters.AddWithValue(
            "$private_protected_accessibility",
            (int)IndexedAccessibility.ProtectedAndInternal);

        var result = new List<StoredInheritedMethodCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredInheritedMethodCandidate(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2)));
        }

        return result;
    }

    public async Task<IReadOnlyList<long>> ExpandOverrideMethodIdsAsync(
        long profileId,
        IEnumerable<MethodSearchSeed> seeds,
        CancellationToken cancellationToken = default)
    {
        var values = seeds.Distinct().ToArray();
        if (values.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var seedRows = AddMethodSearchSeedParameters(command, values);
        command.CommandText = $"""
            WITH RECURSIVE
            input_seeds(root_method_id, receiver_type_id) AS (
                VALUES {seedRows}
            ),
            seeds(root_method_id, receiver_type_id) AS (
                SELECT input.root_method_id, input.receiver_type_id
                FROM input_seeds input
                JOIN symbols root_method
                  ON root_method.id = input.root_method_id
                 AND root_method.analysis_profile_id = $profile_id
                 AND root_method.kind = $method_symbol_kind
                JOIN symbols receiver
                  ON receiver.id = input.receiver_type_id
                 AND receiver.analysis_profile_id = $profile_id
                 AND receiver.kind = $type_symbol_kind
            ),
            receiver_branch(root_method_id, receiver_type_id, type_id) AS (
                SELECT seed.root_method_id, seed.receiver_type_id, seed.receiver_type_id
                FROM seeds seed

                UNION

                SELECT branch.root_method_id, branch.receiver_type_id, relation.source_symbol_id
                FROM receiver_branch branch
                JOIN symbol_relations relation
                  ON relation.target_symbol_id = branch.type_id
                JOIN symbols descendant
                  ON descendant.id = relation.source_symbol_id
                 AND descendant.analysis_profile_id = $profile_id
                 AND descendant.kind = $type_symbol_kind
                WHERE relation.analysis_profile_id = $profile_id
                  AND relation.relation_kind = $inherits_relation_kind
            ),
            override_methods(root_method_id, receiver_type_id, method_id) AS (
                SELECT seed.root_method_id, seed.receiver_type_id, seed.root_method_id
                FROM seeds seed

                UNION

                SELECT methods.root_method_id, methods.receiver_type_id, relation.source_symbol_id
                FROM override_methods methods
                JOIN symbol_relations relation
                  ON relation.target_symbol_id = methods.method_id
                JOIN symbols overriding_method
                  ON overriding_method.id = relation.source_symbol_id
                 AND overriding_method.analysis_profile_id = $profile_id
                 AND overriding_method.kind = $method_symbol_kind
                WHERE relation.analysis_profile_id = $profile_id
                  AND relation.relation_kind = $overrides_relation_kind
            )
            SELECT DISTINCT methods.method_id
            FROM override_methods methods
            JOIN symbols method
              ON method.id = methods.method_id
             AND method.analysis_profile_id = $profile_id
             AND method.kind = $method_symbol_kind
            WHERE methods.method_id = methods.root_method_id
               OR EXISTS (
                   SELECT 1
                   FROM receiver_branch branch
                   WHERE branch.root_method_id = methods.root_method_id
                     AND branch.receiver_type_id = methods.receiver_type_id
                     AND branch.type_id = method.containing_symbol_id
               )
            ORDER BY methods.method_id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$method_symbol_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$type_symbol_kind", (int)IndexedSymbolKind.Type);
        command.Parameters.AddWithValue("$inherits_relation_kind", (int)SymbolRelationKind.Inherits);
        command.Parameters.AddWithValue("$overrides_relation_kind", (int)SymbolRelationKind.Overrides);
        return await ReadIdsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> FindInterfaceImplementationMethodIdsAsync(
        long profileId,
        IEnumerable<InterfaceSearchSeed> seeds,
        CancellationToken cancellationToken = default)
    {
        var values = seeds.Distinct().ToArray();
        if (values.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var seedRows = AddInterfaceSearchSeedParameters(command, values);
        command.CommandText = $"""
            WITH RECURSIVE
            input_seeds(interface_method_id, interface_scope_type_id) AS (
                VALUES {seedRows}
            ),
            seeds(interface_method_id, interface_scope_type_id) AS (
                SELECT input.interface_method_id, input.interface_scope_type_id
                FROM input_seeds input
                JOIN symbols interface_method
                  ON interface_method.id = input.interface_method_id
                 AND interface_method.analysis_profile_id = $profile_id
                 AND interface_method.kind = $method_symbol_kind
                JOIN symbols interface_scope
                  ON interface_scope.id = input.interface_scope_type_id
                 AND interface_scope.analysis_profile_id = $profile_id
                 AND interface_scope.kind = $type_symbol_kind
                 AND interface_scope.type_kind = $interface_type_kind
            ),
            assignable_types(interface_method_id, interface_scope_type_id, type_id) AS (
                SELECT
                    seed.interface_method_id,
                    seed.interface_scope_type_id,
                    seed.interface_scope_type_id
                FROM seeds seed

                UNION

                SELECT
                    assignable.interface_method_id,
                    assignable.interface_scope_type_id,
                    relation.source_symbol_id
                FROM assignable_types assignable
                JOIN symbol_relations relation
                  ON relation.target_symbol_id = assignable.type_id
                JOIN symbols implementing_type
                  ON implementing_type.id = relation.source_symbol_id
                 AND implementing_type.analysis_profile_id = $profile_id
                 AND implementing_type.kind = $type_symbol_kind
                WHERE relation.analysis_profile_id = $profile_id
                  AND relation.relation_kind IN (
                      $implements_relation_kind,
                      $inherits_relation_kind)
            )
            SELECT DISTINCT binding.implementation_method_id
            FROM assignable_types assignable
            JOIN interface_method_bindings binding
              ON binding.analysis_profile_id = $profile_id
             AND binding.interface_method_id = assignable.interface_method_id
             AND binding.implementing_type_id = assignable.type_id
            JOIN symbols implementation_method
              ON implementation_method.id = binding.implementation_method_id
             AND implementation_method.analysis_profile_id = $profile_id
             AND implementation_method.kind = $method_symbol_kind
            ORDER BY binding.implementation_method_id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$method_symbol_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$type_symbol_kind", (int)IndexedSymbolKind.Type);
        command.Parameters.AddWithValue("$interface_type_kind", (int)IndexedTypeKind.Interface);
        command.Parameters.AddWithValue("$implements_relation_kind", (int)SymbolRelationKind.Implements);
        command.Parameters.AddWithValue("$inherits_relation_kind", (int)SymbolRelationKind.Inherits);
        return await ReadIdsAsync(command, cancellationToken);
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

    public async Task<IReadOnlyList<StoredCall>> GetCallsByCallerIncludingLambdaDescendantsAsync(
        long profileId,
        IEnumerable<long> rootIds,
        GeneratedFilter generatedFilter,
        IReadOnlySet<ReferenceKind>? referenceKinds = null,
        CancellationToken cancellationToken = default)
    {
        var ids = rootIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var referenceClause = AddReferenceKindClause(command, referenceKinds);
        command.CommandText = $"""
            WITH RECURSIVE descendants(id) AS (
                SELECT s.id
                FROM symbols s
                WHERE s.analysis_profile_id = $profile_id AND s.id IN ({placeholders})
                UNION
                SELECT child.id
                FROM symbols child
                JOIN descendants parent ON parent.id = child.containing_symbol_id
                WHERE child.analysis_profile_id = $profile_id
            )
            {BuildCallSelect($"""
                c.analysis_profile_id = $profile_id
                AND (
                    c.caller_symbol_id IN ({placeholders})
                    OR EXISTS (
                        SELECT 1
                        FROM descendants descendant
                        JOIN symbols caller ON caller.id = descendant.id
                        WHERE descendant.id = c.caller_symbol_id
                          AND caller.kind = $lambda_kind
                    )
                )
                {referenceClause}
                AND ($generated_filter = 0
                     OR ($generated_filter = 1 AND d.is_generated = 0)
                     OR ($generated_filter = 2 AND d.is_generated = 1))
                """)}
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$lambda_kind", (int)IndexedSymbolKind.Lambda);
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

    private const string SymbolProjection = """
        s.id, s.stable_key, s.kind, s.name, s.namespace_name,
        s.type_simple_name, s.type_metadata_name, s.fully_qualified_name,
        s.display_name, s.containing_symbol_id, s.arity, s.parameter_count, s.method_kind,
        s.is_static, s.is_abstract, s.is_virtual, s.is_override,
        s.async_role, s.async_involvement_depth, s.async_next_symbol_id,
        s.return_type_key, s.normalized_source, s.normalized_source_hash,
        d.normalized_path, s.source_start, s.source_length, s.is_generated,
        p.assembly_name, s.type_kind, s.accessibility
        """;

    private static string BuildSymbolSelect(string whereClause) => $"""
        SELECT
            {SymbolProjection}
        FROM symbols s
        LEFT JOIN documents d ON d.id = s.source_document_id
        LEFT JOIN projects p ON p.id = s.project_id
        WHERE {whereClause}
        """;

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
                    reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    reader.GetBoolean(13),
                    reader.GetBoolean(14),
                    reader.GetBoolean(15),
                    reader.GetBoolean(16),
                    (AsyncRole)reader.GetInt32(17),
                    reader.IsDBNull(18) ? null : reader.GetInt32(18),
                    reader.IsDBNull(19) ? null : reader.GetInt64(19),
                    reader.IsDBNull(20) ? null : reader.GetString(20),
                    reader.IsDBNull(21) ? null : reader.GetString(21),
                    reader.IsDBNull(22) ? null : reader.GetFieldValue<byte[]>(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23),
                    reader.IsDBNull(24) ? null : reader.GetInt32(24),
                    reader.IsDBNull(25) ? null : reader.GetInt32(25),
                    reader.GetBoolean(26),
                    reader.IsDBNull(27) ? null : reader.GetString(27),
                    [],
                    reader.IsDBNull(28) ? null : reader.GetInt32(28),
                    reader.IsDBNull(29) ? null : reader.GetInt32(29)));
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

    private static async Task<IReadOnlyList<long>> ReadIdsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt64(0));
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

    private static string AddMethodSearchSeedParameters(
        SqliteCommand command,
        IReadOnlyList<MethodSearchSeed> seeds)
    {
        var rows = new string[seeds.Count];
        for (var index = 0; index < seeds.Count; index++)
        {
            var methodName = $"$seed_method_id{index}";
            var receiverName = $"$seed_receiver_type_id{index}";
            command.Parameters.AddWithValue(methodName, seeds[index].MethodId);
            command.Parameters.AddWithValue(receiverName, seeds[index].ReceiverTypeId);
            rows[index] = $"({methodName}, {receiverName})";
        }

        return string.Join(',', rows);
    }

    private static string AddInterfaceSearchSeedParameters(
        SqliteCommand command,
        IReadOnlyList<InterfaceSearchSeed> seeds)
    {
        var rows = new string[seeds.Count];
        for (var index = 0; index < seeds.Count; index++)
        {
            var methodName = $"$seed_interface_method_id{index}";
            var scopeName = $"$seed_interface_scope_type_id{index}";
            command.Parameters.AddWithValue(methodName, seeds[index].InterfaceMethodId);
            command.Parameters.AddWithValue(scopeName, seeds[index].InterfaceScopeTypeId);
            rows[index] = $"({methodName}, {scopeName})";
        }

        return string.Join(',', rows);
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
