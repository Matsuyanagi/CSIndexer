using System.Text.Json;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace CsIndex.Storage;

public sealed class QueryRepository(string databasePath, SchemaMigrator migrator)
{
    private const string PathIdentityCollation = "CSINDEX_PATH_IDENTITY";

    private readonly string _databasePath = PathNormalizer.Normalize(databasePath);

    internal Action? NormalizedSourceCellReadObserver { get; set; }
    internal Action<TraversalOperation>? TraversalObserver { get; set; }

    public string DatabasePath => _databasePath;

    public async Task<StoredProfile> GetProfileAsync(
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                p.id, p.name, p.input_mode, p.configuration, p.target_framework,
                p.runtime_identifier, p.preprocessor_symbols, r.input_root, r.index_root_anchor
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
            reader.GetString(7),
            reader.GetString(8));
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
            whereClause += "\nAND s.preferred_declaration_id IS NOT NULL";
        }

        command.CommandText = BuildSymbolSelect(whereClause) +
                          "\nORDER BY s.namespace_name, s.type_identity_path, s.executable_identity_path, pdoc.normalized_path, pd.source_start, s.id;";
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$type_name", (object?)typeSimpleName ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (int)kind.Value);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public Task<IReadOnlyList<StoredSymbol>> FindLogicalSymbolCandidatesAsync(
        long profileId,
        string? name = null,
        string? typeSimpleName = null,
        IndexedSymbolKind? kind = null,
        bool sourceOnly = false,
        CancellationToken cancellationToken = default) =>
        FindSymbolCandidatesAsync(profileId, name, typeSimpleName, kind, sourceOnly, cancellationToken);

    public async Task<IReadOnlyList<StoredSymbol>> FindLogicalSymbolCandidatesAsync(
        long profileId,
        LogicalSymbolCandidateHints? hints,
        bool sourceOnly,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var predicates = new List<string>
        {
            "s.analysis_profile_id = $profile_id",
            "s.kind IN ($method_kind, $lambda_kind, $initializer_kind, $top_level_kind)",
        };
        if (hints?.ExactLeafName is not null)
        {
            predicates.Add("s.name = $exact_leaf_name");
            command.Parameters.AddWithValue("$exact_leaf_name", hints.ExactLeafName);
        }

        if (hints?.ExactKind is not null)
        {
            predicates.Add("s.kind = $exact_kind");
            command.Parameters.AddWithValue("$exact_kind", (int)hints.ExactKind.Value);
        }

        if (sourceOnly)
        {
            predicates.Add("s.preferred_declaration_id IS NOT NULL");
        }

        command.CommandText = BuildSymbolSelect(string.Join("\n  AND ", predicates)) +
            "\nORDER BY s.namespace_name, s.type_identity_path, s.executable_identity_path, pdoc.normalized_path, pd.source_start, s.id;";
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$method_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$lambda_kind", (int)IndexedSymbolKind.Lambda);
        command.Parameters.AddWithValue("$initializer_kind", (int)IndexedSymbolKind.Initializer);
        command.Parameters.AddWithValue("$top_level_kind", (int)IndexedSymbolKind.TopLevelStatements);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredSymbol>> GetSymbolsWithContainingAncestorsAsync(
        long profileId,
        IEnumerable<long> candidateSymbolIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateSymbolIds);
        var ids = candidateSymbolIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RECURSIVE chain(id, containing_symbol_id, kind) AS (
                SELECT seed.id, seed.containing_symbol_id, seed.kind
                FROM symbols seed
                JOIN json_each($seed_ids) supplied ON seed.id = CAST(supplied.value AS INTEGER)
                WHERE seed.analysis_profile_id = $profile_id

                UNION

                SELECT parent.id, parent.containing_symbol_id, parent.kind
                FROM symbols parent
                JOIN chain child ON child.containing_symbol_id = parent.id
                WHERE parent.analysis_profile_id = $profile_id
                  AND child.kind <> $type_kind
            )
            SELECT
                {SymbolProjection}
            FROM symbols s
            JOIN chain ON chain.id = s.id
            LEFT JOIN symbol_declarations pd ON pd.id = s.preferred_declaration_id
            LEFT JOIN documents pdoc ON pdoc.id = pd.document_id
            LEFT JOIN projects p ON p.id = s.project_id
            ORDER BY s.namespace_name, s.type_identity_path, s.executable_identity_path,
                     pdoc.normalized_path, pd.source_start, s.id;
            """;
        command.Parameters.AddWithValue("$seed_ids", JsonSerializer.Serialize(ids));
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$type_kind", (int)IndexedSymbolKind.Type);
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

        TraversalObserver?.Invoke(TraversalOperation.SymbolEndpointsById);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, values);
        command.CommandText = BuildSymbolSelect($"""
            s.analysis_profile_id = $profile_id AND s.id IN ({placeholders})
            """) + """
            ORDER BY s.namespace_name, s.type_identity_path, s.executable_identity_path, pdoc.normalized_path, pd.source_start, s.id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadSymbolsAsync(connection, command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredDeclaration>> GetDeclarationsAsync(
        long profileId,
        IEnumerable<long> symbolIds,
        bool includeSourceText,
        CancellationToken cancellationToken = default)
    {
        var ids = symbolIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var sourceProjection = includeSourceText
            ? "d.normalized_source, d.normalized_source_hash"
            : "NULL, NULL";
        command.CommandText = $"""
            SELECT
                d.id, d.declaration_key, d.symbol_id, d.document_id, doc.normalized_path,
                d.declaration_role, d.source_start, d.source_length,
                {sourceProjection}, d.is_generated
            FROM symbol_declarations d
            JOIN symbols s ON s.id = d.symbol_id
            JOIN documents doc ON doc.id = d.document_id
            WHERE s.analysis_profile_id = $profile_id
              AND d.symbol_id IN ({placeholders})
            ORDER BY
                CASE d.declaration_role
                    WHEN 2 THEN 0
                    WHEN 3 THEN 1
                    WHEN 1 THEN 2
                    ELSE 3
                END,
                doc.normalized_path, d.source_start, d.id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadDeclarationsAsync(command, includeSourceText, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredDeclaration>> GetPreferredDeclarationsAsync(
        long profileId,
        IEnumerable<long> symbolIds,
        bool includeSourceText,
        CancellationToken cancellationToken = default)
    {
        var ids = symbolIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var sourceProjection = includeSourceText
            ? "d.normalized_source, d.normalized_source_hash"
            : "NULL, NULL";
        command.CommandText = $"""
            SELECT
                d.id, d.declaration_key, d.symbol_id, d.document_id, doc.normalized_path,
                d.declaration_role, d.source_start, d.source_length,
                {sourceProjection}, d.is_generated
            FROM symbols s
            JOIN symbol_declarations d ON d.id = s.preferred_declaration_id
            JOIN documents doc ON doc.id = d.document_id
            WHERE s.analysis_profile_id = $profile_id
              AND s.id IN ({placeholders})
            ORDER BY s.id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadDeclarationsAsync(command, includeSourceText, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredSymbol>> FindFunctionSymbolsAsync(
        long profileId,
        IndexedSymbolKind? kind,
        bool asyncInvolved,
        CancellationToken cancellationToken = default) =>
        await FindFunctionSymbolsAsync(
            profileId,
            kind,
            AsyncStatusFilter.All,
            asyncInvolved,
            cancellationToken);

    public async Task<IReadOnlyList<StoredSymbol>> FindFunctionSymbolsAsync(
        long profileId,
        IndexedSymbolKind? kind,
        AsyncStatusFilter asyncStatus,
        bool asyncInvolved,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildSymbolSelect("""
            s.analysis_profile_id = $profile_id
              AND s.kind IN ($method_kind, $lambda_kind, $initializer_kind, $top_level_kind)
              AND ($kind IS NULL OR s.kind = $kind)
              AND (
                  $async_status = $all_async_status
                  OR ($async_status = $async_status_async AND s.async_role <> 0)
                  OR ($async_status = $async_status_sync AND s.async_role = 0)
              )
              AND ($async_involved = 0 OR s.async_involvement_depth IS NOT NULL)
            """) + """
            ORDER BY s.namespace_name, s.type_identity_path, s.executable_identity_path,
                     pdoc.normalized_path, pd.source_start, s.id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (int)kind.Value);
        command.Parameters.AddWithValue("$method_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$lambda_kind", (int)IndexedSymbolKind.Lambda);
        command.Parameters.AddWithValue("$initializer_kind", (int)IndexedSymbolKind.Initializer);
        command.Parameters.AddWithValue("$top_level_kind", (int)IndexedSymbolKind.TopLevelStatements);
        command.Parameters.AddWithValue("$async_status", (int)asyncStatus);
        command.Parameters.AddWithValue("$all_async_status", (int)AsyncStatusFilter.All);
        command.Parameters.AddWithValue("$async_status_async", (int)AsyncStatusFilter.Async);
        command.Parameters.AddWithValue("$async_status_sync", (int)AsyncStatusFilter.Sync);
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
              AND s.kind IN ($method_kind, $lambda_kind, $initializer_kind, $top_level_kind)
            """;
        if (sourceOnly)
        {
            whereClause += "\nAND s.preferred_declaration_id IS NOT NULL";
        }

        command.CommandText = BuildSymbolSelect(whereClause) +
                              "\nORDER BY s.namespace_name, s.type_identity_path, s.executable_identity_path, pdoc.normalized_path, pd.source_start, s.id;";
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$method_kind", (int)IndexedSymbolKind.Method);
        command.Parameters.AddWithValue("$lambda_kind", (int)IndexedSymbolKind.Lambda);
        command.Parameters.AddWithValue("$initializer_kind", (int)IndexedSymbolKind.Initializer);
        command.Parameters.AddWithValue("$top_level_kind", (int)IndexedSymbolKind.TopLevelStatements);
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

        TraversalObserver?.Invoke(TraversalOperation.OverrideInterfaceExpansion);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var seedRows = AddMethodSearchSeedParameters(command, values);
        command.CommandText = $"""
            WITH RECURSIVE
            input_seeds(root_method_id, receiver_type_id) AS (
                {seedRows}
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

        TraversalObserver?.Invoke(TraversalOperation.OverrideInterfaceExpansion);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var seedRows = AddInterfaceSearchSeedParameters(command, values);
        command.CommandText = $"""
            WITH RECURSIVE
            input_seeds(interface_method_id, interface_scope_type_id) AS (
                {seedRows}
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

        TraversalObserver?.Invoke(TraversalOperation.CallsByCallee);
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

        TraversalObserver?.Invoke(TraversalOperation.CallsByCaller);
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

        TraversalObserver?.Invoke(TraversalOperation.CallsByCallerIncludingLambdaDescendants);
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

        TraversalObserver?.Invoke(TraversalOperation.RelationsByTarget);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var kindClause = AddRelationKindClause(command, kinds);
        command.CommandText = $"""
            SELECT
                r.source_symbol_id,
                r.target_symbol_id,
                r.relation_kind
            FROM symbol_relations r
            JOIN symbols source ON source.id = r.source_symbol_id
            JOIN symbols target ON target.id = r.target_symbol_id
            WHERE r.analysis_profile_id = $profile_id
              AND r.target_symbol_id IN ({placeholders})
              {kindClause}
            ORDER BY r.source_symbol_id, r.target_symbol_id;
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

        TraversalObserver?.Invoke(TraversalOperation.RelationsBySource);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = AddIdParameters(command, ids);
        var kindClause = AddRelationKindClause(command, kinds);
        command.CommandText = $"""
            SELECT
                r.source_symbol_id,
                r.target_symbol_id,
                r.relation_kind
            FROM symbol_relations r
            JOIN symbols source ON source.id = r.source_symbol_id
            JOIN symbols target ON target.id = r.target_symbol_id
            WHERE r.analysis_profile_id = $profile_id
              AND r.source_symbol_id IN ({placeholders})
              {kindClause}
            ORDER BY r.target_symbol_id, r.source_symbol_id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        return await ReadRelationsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredDocument>> FindDocumentsAsync(
        long profileId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedInput = PathNormalizer.NormalizeRelative(path);
        await using var connection = await OpenAsync(cancellationToken);
        RegisterPathIdentityCollation(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.normalized_path, d.is_generated
            FROM documents d
            JOIN projects p ON p.id = d.project_id
            WHERE p.analysis_profile_id = $profile_id
              AND d.normalized_path = $path COLLATE CSINDEX_PATH_IDENTITY
            ORDER BY d.normalized_path;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$path", normalizedInput);
        var result = new List<StoredDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredDocument(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return result;
    }

    private static void RegisterPathIdentityCollation(SqliteConnection connection)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        connection.CreateCollation(PathIdentityCollation, comparer.Compare);
    }

    public async Task<StoredCall?> FindCallAtAsync(
        long profileId,
        long documentId,
        int position,
        CancellationToken cancellationToken = default)
    {
        TraversalObserver?.Invoke(TraversalOperation.CallAtPosition);
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
        s.type_simple_name, s.type_metadata_name,
        s.path_segment_kind, s.path_segment_display, s.path_segment_identity,
        s.type_display_path, s.type_identity_path,
        s.executable_display_path, s.executable_identity_path,
        s.preferred_declaration_id, s.containing_symbol_id, s.arity, s.parameter_count,
        s.method_kind, s.accessibility, s.type_kind,
        s.is_static, s.is_abstract, s.is_virtual, s.is_override,
        s.async_role, s.async_involvement_depth, s.async_next_symbol_id,
        s.return_type_key, s.return_type_display,
        s.conversion_type_key, s.conversion_type_display,
        pdoc.normalized_path, pd.source_start, pd.source_length, pd.is_generated,
        s.is_generated, p.assembly_name
        """;

    private static string BuildSymbolSelect(string whereClause) => $"""
        SELECT
            {SymbolProjection}
        FROM symbols s
        LEFT JOIN symbol_declarations pd ON pd.id = s.preferred_declaration_id
        LEFT JOIN documents pdoc ON pdoc.id = pd.document_id
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
                var path = new SymbolPathData(
                    reader.GetString(4),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetString(8),
                    reader.GetString(9),
                    (CallablePathSegmentKind)reader.GetInt32(7));
                var preferredPath = reader.IsDBNull(32) ? null : reader.GetString(32);
                int? preferredStart = reader.IsDBNull(33) ? null : reader.GetInt32(33);
                int? preferredLength = reader.IsDBNull(34) ? null : reader.GetInt32(34);
                bool? preferredGenerated = reader.IsDBNull(35) ? null : reader.GetBoolean(35);
                var logicalGenerated = reader.GetBoolean(36);
                rows.Add(new StoredSymbol(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    (IndexedSymbolKind)reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    reader.GetInt32(16),
                    reader.IsDBNull(17) ? null : reader.GetInt32(17),
                    reader.IsDBNull(18) ? null : reader.GetInt32(18),
                    reader.GetBoolean(21),
                    reader.GetBoolean(22),
                    reader.GetBoolean(23),
                    reader.GetBoolean(24),
                    (AsyncRole)reader.GetInt32(25),
                    reader.IsDBNull(26) ? null : reader.GetInt32(26),
                    reader.IsDBNull(27) ? null : reader.GetInt64(27),
                    reader.IsDBNull(28) ? null : reader.GetString(28),
                    null,
                    null,
                    preferredPath,
                    preferredStart,
                    preferredLength,
                    preferredGenerated ?? logicalGenerated,
                    reader.IsDBNull(37) ? null : reader.GetString(37),
                    [],
                    reader.IsDBNull(20) ? null : reader.GetInt32(20),
                    reader.IsDBNull(19) ? null : reader.GetInt32(19)) with
                {
                    Path = path,
                    PreferredDeclarationId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    PreferredDocumentPath = preferredPath,
                    PreferredSourceStart = preferredStart,
                    PreferredIsGenerated = preferredGenerated,
                    ReturnTypeDisplay = reader.IsDBNull(29) ? null : reader.GetString(29),
                    ConversionTypeKey = reader.IsDBNull(30) ? null : reader.GetString(30),
                    ConversionTypeDisplay = reader.IsDBNull(31) ? null : reader.GetString(31),
                });
            }
        }

        if (rows.Count == 0)
        {
            return rows;
        }

        var parametersBySymbolId = rows.ToDictionary(
            row => row.Id,
            _ => new List<StoredParameter>());
        await using var parameterCommand = connection.CreateCommand();
        parameterCommand.CommandText = """
            SELECT method_id, ordinal, name, type_key, ref_kind, is_optional, type_display
            FROM method_parameters
            WHERE method_id IN (
                SELECT CAST(value AS INTEGER)
                FROM json_each($symbol_ids))
            ORDER BY method_id, ordinal;
            """;
        parameterCommand.Parameters.AddWithValue(
            "$symbol_ids",
            JsonSerializer.Serialize(rows.Select(row => row.Id)));
        await using (var reader = await parameterCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var symbolId = reader.GetInt64(0);
                if (!parametersBySymbolId.TryGetValue(symbolId, out var parameters))
                {
                    throw new InvalidOperationException(
                        $"Method parameter data referenced unexpected symbol ID {symbolId}.");
                }

                parameters.Add(new StoredParameter(
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetBoolean(5),
                    reader.GetString(6)));
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows[index] = rows[index] with { Parameters = parametersBySymbolId[rows[index].Id] };
        }

        return rows;
    }

    private async Task<IReadOnlyList<StoredDeclaration>> ReadDeclarationsAsync(
        SqliteCommand command,
        bool includeSourceText,
        CancellationToken cancellationToken)
    {
        var rows = new List<StoredDeclaration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? normalizedSource = null;
            if (includeSourceText && !reader.IsDBNull(8))
            {
                NormalizedSourceCellReadObserver?.Invoke();
                normalizedSource = reader.GetString(8);
            }

            rows.Add(new StoredDeclaration(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                (DeclarationRole)reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                normalizedSource,
                includeSourceText && !reader.IsDBNull(9) ? reader.GetFieldValue<byte[]>(9) : null,
                reader.GetBoolean(10)));
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
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                (ReferenceKind)reader.GetInt32(5),
                (DispatchKind)reader.GetInt32(6),
                (ResolutionStatus)reader.GetInt32(7),
                (ResolutionReason)reader.GetInt32(8),
                (AsyncUsageKind)reader.GetInt32(9),
                reader.GetInt64(10),
                reader.GetString(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetBoolean(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16)));
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
                reader.GetInt64(1),
                (SymbolRelationKind)reader.GetInt32(2)));
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
            caller.id, caller.containing_symbol_id,
            callee.id,
            definition.id,
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
        const string baseParameterName = "$id_list";
        var parameterName = baseParameterName;
        var suffix = 0;
        while (command.Parameters.Contains(parameterName))
        {
            parameterName = $"{baseParameterName}{++suffix}";
        }

        command.Parameters.AddWithValue(parameterName, JsonSerializer.Serialize(ids));
        return $"SELECT CAST(value AS INTEGER) FROM json_each({parameterName})";
    }

    private static string AddMethodSearchSeedParameters(
        SqliteCommand command,
        IReadOnlyList<MethodSearchSeed> seeds)
    {
        return AddSeedPairParameters(
            command,
            seeds.Select(seed => new[] { seed.MethodId, seed.ReceiverTypeId }));
    }

    private static string AddInterfaceSearchSeedParameters(
        SqliteCommand command,
        IReadOnlyList<InterfaceSearchSeed> seeds)
    {
        return AddSeedPairParameters(
            command,
            seeds.Select(seed => new[] { seed.InterfaceMethodId, seed.InterfaceScopeTypeId }));
    }

    private static string AddSeedPairParameters(
        SqliteCommand command,
        IEnumerable<long[]> seedPairs)
    {
        const string baseParameterName = "$seed_pairs";
        var parameterName = baseParameterName;
        var suffix = 0;
        while (command.Parameters.Contains(parameterName))
        {
            parameterName = $"{baseParameterName}{++suffix}";
        }

        command.Parameters.AddWithValue(parameterName, JsonSerializer.Serialize(seedPairs));
        return $"""
            SELECT
                CAST(json_extract(value, '$[0]') AS INTEGER),
                CAST(json_extract(value, '$[1]') AS INTEGER)
            FROM json_each({parameterName})
            """;
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
