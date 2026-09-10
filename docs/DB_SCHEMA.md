# SQLite schema

This is the exact active schema for CsIndex. `SchemaMigrator.CurrentVersion` and `RequestHasher.SchemaVersion` are 6; `RequestHasher.AnalysisCacheVersion` is 4.

## Compatibility and creation

- A new, otherwise empty database is placed in WAL mode and the complete schema is created in one transaction.
- An existing database must have exactly one `schema_info` row whose value is 6.
- A database with no `schema_info` but any user table/index/view/trigger is incompatible.
- There is no migration, compatibility fallback, auto-delete, or implicit rebuild. Schema 5 and older query or index attempts leave the database unchanged.
- The recovery message is actionable: delete or rename the old database, or choose a new `--db` path, then run `csindex index` explicitly. `--rebuild` does not authorize incompatible-schema deletion.
- Foreign-key enforcement is enabled for every opened connection. Existing schema compatibility is checked before WAL is enabled.

## Portable path and identity invariants

- `index_runs.input_root` is `.` for a current index.
- `index_runs.index_root_anchor` is the canonical relative path from the database directory to the storage root. The default `.csindex/index.sqlite` layout stores `..`.
- `projects.project_path` and `documents.normalized_path` are canonical forward-slash paths relative to the storage root. Linked source paths may begin with normalized `../` segments.
- Logical and declaration keys use semantic identity and stored relative path data; no persisted path/key field may contain a machine-specific rooted source path.
- The single-root invariant remains for ordinary persisted projects, documents, and linked sources: the storage root, database directory, and persisted source locations must share a Windows drive or UNC server/share. A physical C# document from another volume/share is not a database document when the existing `GeneratedCodeDetector` positively identifies it as generated. It remains in the Roslyn compilation, but contributes no `documents` row, normalized path, declaration, body-derived fact, or query root; it contributes one warning and one `DocumentsExcluded` count. Same-volume generated source remains a normal `documents` row. A non-generated cross-volume/share document is rejected before database mutation.
- This compilation-only disposition changes no schema, adds no migration, and creates no synthetic database row or path.

## Logical symbols and declarations

`symbols` contains logical semantic rows. It deliberately has no legacy `fully_qualified_name`, `display_name`, source-span, or normalized-source columns. Semantic display/identity components are stored separately. The complete normalized document payload lives in `normalized_sources`; declarations and calls retain only ranges into that payload.

The declaration-role encoding is exact:

| Numeric value | Output value |
| --- | --- |
| 1 | `ordinary` |
| 2 | `partial-definition` |
| 3 | `partial-implementation` |

The database check constraint accepts only 1, 2, or 3. A logical partial pair has one `symbols` row and two declaration rows. `preferred_declaration_id` points to the implementation when present, otherwise to the definition or ordinary declaration. A definition-only partial is valid. Calls, candidates, relations, interface bindings, containing/async links, and graph roots all reference logical symbol IDs.

## Exact version-6 DDL

The block below is copied from `SchemaMigrator.CreateVersionSixAsync`. It is normative for every column, default, uniqueness constraint, check constraint, foreign key, delete action, partial index, and index column order.

```sql
CREATE TABLE schema_info (
    version INTEGER NOT NULL
);

INSERT INTO schema_info(version) VALUES (6);

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

CREATE TABLE normalized_sources (
    id                    INTEGER PRIMARY KEY,
    normalized_source_hash BLOB NOT NULL UNIQUE,
    normalized_source     TEXT NOT NULL
);

CREATE TABLE documents (
    id                    INTEGER PRIMARY KEY,
    project_id            INTEGER NOT NULL,
    normalized_path       TEXT NOT NULL,
    content_hash          BLOB NOT NULL,
    semantic_hash         BLOB,
    normalized_source_id  INTEGER NOT NULL,
    is_generated          INTEGER NOT NULL DEFAULT 0,
    generation_kind       INTEGER NOT NULL DEFAULT 0,

    UNIQUE(project_id, normalized_path),

    FOREIGN KEY(project_id)
      REFERENCES projects(id) ON DELETE CASCADE,

    FOREIGN KEY(normalized_source_id)
      REFERENCES normalized_sources(id)
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
    normalized_start      INTEGER NOT NULL,
    normalized_length     INTEGER NOT NULL,
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
    normalized_start      INTEGER NOT NULL,
    normalized_length     INTEGER NOT NULL,
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

CREATE INDEX ix_documents_normalized_source
ON documents(normalized_source_id);

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
```

## Save-time consistency and atomicity

`normalized_sources` is content-addressed by SHA-256. A document's normalized
text and hash are inserted once and identical normalized documents may share one
payload row. `INSERT OR IGNORE` is followed by an ordinal text comparison; a
same-hash, unequal-text row is an integrity failure rather than an alias.

Before replacement, snapshot validation requires canonical relative run/project/document paths, one profile-scoped stable key per logical symbol, valid declaration keys/roles/locations, a valid preferred declaration owned by the same logical symbol, logical call/relation endpoints, and no partial self relation. It also requires every document hash to equal SHA-256 of its normalized text, every declaration and call range to be nonnegative with positive length and to fit within its referenced normalized document, and every referenced document to exist. Invalid snapshots are rejected.

A save writes the run, projects, normalized payloads, documents, logical symbols, parameters, declarations, calls/candidates, relations, interface bindings, and conditional-symbol usage in one transaction. Deferred numeric links such as containing, async-next, callee, and preferred-declaration IDs are resolved against persisted logical/declaration maps. After replacing profile data, only normalized-source rows not referenced by any document are deleted. `PRAGMA foreign_key_check` must succeed before commit. Failure or cancellation, including orphan cleanup failure, rolls back and preserves the previous valid index.

`symbol_declarations.normalized_source` and
`symbol_declarations.normalized_source_hash` were removed in version 6. There
is no declaration-level text/hash compatibility column, compatibility reader,
dual-write path, or source-file fallback. Source ranges use .NET string
indexing: UTF-16 code-unit offsets and lengths. Query hydration and declaration
or call materialization bounds-check and slice in C#; SQLite `substr` is not
used.

## Inspection reference

Useful read-only checks for a fresh database are:

```sql
SELECT version FROM schema_info;
PRAGMA table_info(normalized_sources);
PRAGMA table_info(documents);
PRAGMA table_info(symbols);
PRAGMA table_info(symbol_declarations);
PRAGMA table_info(calls);
PRAGMA foreign_key_list(documents);
PRAGMA foreign_key_list(symbol_declarations);
PRAGMA foreign_key_list(calls);
PRAGMA index_list(normalized_sources);
PRAGMA index_list(documents);
PRAGMA index_list(symbols);
PRAGMA index_list(symbol_declarations);
PRAGMA index_list(calls);
SELECT input_root, index_root_anchor FROM index_runs;
```
