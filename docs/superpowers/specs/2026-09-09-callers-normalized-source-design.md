# Callers Normalized Call-Site Source Design

## Status

Approved for implementation on 2026-09-09. This document incorporates the
original `callers` / `callers tree --show-source` request and the subsequent
decisions about avoiding duplicated source text and preserving
`symbol find --include` / `--exclude` behavior.

## Goal

Add `--show-source` to `callers` and `callers tree` so every returned call
site can expose the normalized C# expression that produced that call. Produce
and persist the text at index time. Do not re-read or reparse source files to
obtain normalized call-site text at query time.

Use one normalized text payload per unique indexed document content and store
UTF-16 ranges for declarations and calls. This replaces the current repeated
`symbol_declarations.normalized_source` payload while retaining all existing
source-filter behavior.

## Corrected Current-State Facts

- Schema version 5 already removed normalized-source columns from `symbols`.
  The repeated payload currently lives in
  `symbol_declarations.normalized_source` and
  `symbol_declarations.normalized_source_hash`.
- `calls` already stores `document_id`, original `source_start`, and original
  `source_length`, but no normalized-source range.
- `callers` returns invocation and object-creation calls. This feature does not
  broaden that relation set.
- `callers tree` currently collapses all calls with the same caller and callee
  into one structural edge. The new design retains that structural edge and
  associates every physical call site with it.
- Existing location formatting may read the original file to translate an
  original UTF-16 offset into line and column. The prohibition on query-time
  source reads in this feature concerns obtaining or reconstructing
  `normalizedSource`; redesigning location persistence is out of scope.

## Non-Goals

- No schema migration from version 5 or older.
- No compatibility reader, fallback column, dual-write path, or source-reparse
  fallback.
- No change to call resolution, dispatch expansion, caller scope, graph depth,
  graph node limits, cycle handling, root selection, generated filtering, or
  canonical ordering.
- No new source-normalization dialect and no formatter-based reconstruction of
  call expressions.
- No `--show-source` addition to `references`, `callees`, `overrides`, or
  `async tree`.
- No line/column persistence redesign.

## CLI Contract

### Accepted option scopes

`--show-source` is accepted by exactly these commands in addition to the
existing `symbol find` scope:

```text
csindex callers <selector-or-conditions> [options] --show-source
csindex callers tree <selector-or-conditions> [options] --show-source
```

Every other command keeps its current option scope. `--show-source=<value>` is
invalid because the option is a flag. Normal and verbose help must describe
the two new scopes and the normalized call-expression meaning.

### No-flag compatibility

Without `--show-source`, selection, results, ordering, output fields, and bytes
written by `callers` and `callers tree` remain unchanged.

### Call expression extent

For an invocation, the source extent is the entire
`InvocationExpressionSyntax`. For object creation it is the entire
`BaseObjectCreationExpressionSyntax`. The terminating statement semicolon is
not included. Nested expressions have independent, overlapping normalized
ranges.

Example:

```csharp
B(
    /* 日本語 comment */ A(f),
    A(10 + 20)
);
```

The three persisted call-source slices are:

```text
A(f)
A(10+20)
B(A(f),A(10+20))
```

### `callers` table output

The current call line remains unchanged through its existing final field.
With `--show-source`, append one TAB (`0x09`) followed by the normalized call
source:

```text
  <path>:<line>:<column>  <caller> -> <callee> [<kind>, <status>] [<async-usage>]<TAB><normalized-source>
```

Pass the source through the existing `TableTextSanitizer`, which replaces TAB,
CR, LF, NEL, U+2028, and U+2029 with ASCII space. This is presentation-only;
stored text and JSON are not changed.

### `callers` JSON output

With `--show-source`, every object in `calls` has a final property:

```json
"normalizedSource": "A(10+20)"
```

Without the flag, the property is omitted rather than emitted as `null`.

### `callers tree` result model

The graph keeps one structural `CallerTreeEdge` per caller/callee pair.
Separately retain ordered physical associations:

```csharp
public sealed record CallerTreeCallSite(
    long CallerSymbolId,
    long CalleeSymbolId,
    StoredCall Call);
```

`CallerTreeResult.CallSites` contains one entry per physical call that
established an included structural edge. It is ordered by structural edge
order and then by document path, original source start, original source
length, and call ID. A single `StoredCall` may be associated with the exact
callee selected during traversal even when candidate resolution, rather than
`callee_definition_id`, caused the match.

`CallerTreeResult.ShowSource` records whether the query requested normalized
source hydration. `CallResult` has the same `ShowSource` meaning.

### `callers tree` text output

Without the flag, output is byte-identical to the current tree output. With
the flag, emit one call-site detail immediately after the caller node that
represents its selected spanning edge:

```text
Target
└─ Caller
   @ path:line:column<TAB>A(f)
   @ path:line:column<TAB>A(10+20)
```

The detail indentation is one level deeper than the caller node. For an edge
under `Additional edges:`, details follow that edge using four leading spaces:

```text
Additional edges:
  Caller -> Callee
    @ path:line:column<TAB>A(f)
```

The source part is passed through `TableTextSanitizer`.

### `callers tree` Mermaid output

Without the flag, Mermaid output is unchanged. With the flag, keep one
structural edge and add one label containing all ordered call sites. Each site
is rendered as `path:line:column source`, sites are separated with `<br/>`,
and the complete label is escaped using the Mermaid label escaping rules:

```text
    n12 -->|"src/C.cs:7:9 A(f)<br/>src/C.cs:8:9 A(10+20)"| n4
```

The escaping routine must additionally escape `|` so source text cannot close
the Mermaid edge label.

### `callers tree` JSON output

Without the flag, each edge remains exactly:

```json
{ "callerSymbolId": 12, "calleeSymbolId": 4 }
```

With the flag, each edge additionally has `callSites`:

```json
{
  "callerSymbolId": 12,
  "calleeSymbolId": 4,
  "callSites": [
    {
      "id": 91,
      "location": {
        "path": "src/C.cs",
        "line": 7,
        "column": 9,
        "offset": 123
      },
      "normalizedSource": "A(f)"
    }
  ]
}
```

JSON source text is the exact stored slice and is not table-sanitized.

## Normalization and Range Model

### One token-emission algorithm

Retain `SourceNormalizer` as the only normalization implementation. Its token
emission rules continue to:

- omit trivia, comments, directives, disabled text, and missing/zero-length
  tokens;
- preserve literal token text, including raw and interpolated strings;
- insert a separator only when concatenation would change tokenization;
- observe cancellation during enumeration and pair re-lexing.

Add a document-normalization API that runs this algorithm once for the
compilation unit and records each emitted token's original span and normalized
start/end.

```csharp
public readonly record struct NormalizedSourceRange(int Start, int Length);

public sealed class NormalizedSourceDocument
{
    public string Text { get; }
    public byte[] Hash { get; }
    public NormalizedSourceRange GetRange(SyntaxNode node);
    public string Slice(NormalizedSourceRange range);
}

public static NormalizedSourceDocument NormalizeDocument(
    SyntaxNode root,
    CancellationToken cancellationToken = default);
```

For a node with emitted tokens, its normalized range starts at its first
emitted token, excluding any separator inserted before that token because of
an outside token, and ends after its last emitted token. Therefore slicing a
node range must equal `SourceNormalizer.Normalize(node).Text` byte-for-byte.
An indexed declaration or call with no emitted token is an index integrity
error; it is never assigned an arbitrary empty range.

Ranges use .NET string indexing: UTF-16 code-unit offsets and lengths. SQLite
`substr` must not be used to materialize a range because its character
counting can disagree for surrogate pairs. Slice and bounds-check in C#.

### Declaration extents

Each physical declaration stores a normalized range that reproduces exactly
the current per-declaration normalized text. Overlapping ranges are expected:
an outer method includes the tokens of nested local functions and lambdas,
while each nested executable also has its own range. This preserves current
source-search behavior.

Special existing extents are retained. In particular:

- a primary-constructor declaration uses the same syntax extent currently
  normalized for that declaration, while calls in a base argument list use
  their own document ranges even when the constructor owns them;
- top-level statements retain the current normalized extent chosen by the
  extractor, independently of the original display location span;
- accessors, initializers, local functions, lambdas, and anonymous methods use
  the exact syntax node currently passed to `SourceNormalizer.Normalize`.

The normalized range is independent from the declaration's original
`source_start` / `source_length`.

## Index Snapshot Model

`DocumentData` owns:

```text
NormalizedSource          complete normalized document text
NormalizedSourceHash      SHA-256 of that text
```

`SymbolDeclarationData` removes its text/hash fields and adds:

```text
NormalizedStart
NormalizedLength
```

`CallData` adds the same two range fields. `SymbolData.NormalizedSource` and
`SymbolData.NormalizedSourceHash` are removed, along with the compatibility
fallback that treats those transient fields as a source declaration.

The extractor creates the normalized document immediately after obtaining the
syntax root, stores its text/hash in `DocumentData`, and retains its range map
in `DocumentAnalysisState`. Every declaration and every persisted call row is
assigned a range from that map at index time.

## Schema Version 6

Create only the complete version-6 schema. Do not add an upgrade path.

```sql
CREATE TABLE normalized_sources (
    id                      INTEGER PRIMARY KEY,
    normalized_source_hash  BLOB NOT NULL UNIQUE,
    normalized_source       TEXT NOT NULL
);

CREATE TABLE documents (
    ...,
    normalized_source_id INTEGER NOT NULL,
    FOREIGN KEY(normalized_source_id)
      REFERENCES normalized_sources(id)
);

CREATE TABLE symbol_declarations (
    ...,
    normalized_start  INTEGER NOT NULL,
    normalized_length INTEGER NOT NULL,
    -- no normalized_source or normalized_source_hash columns
    ...
);

CREATE TABLE calls (
    ...,
    normalized_start  INTEGER NOT NULL,
    normalized_length INTEGER NOT NULL,
    ...
);
```

Add `ix_documents_normalized_source` on `documents(normalized_source_id)`.

`normalized_sources` is content-addressed by SHA-256 so identical normalized
documents across projects/profiles share a single row. On a hash conflict,
the save path reads the existing text and requires ordinal equality; unequal
text is reported as an integrity failure rather than silently aliasing a hash
collision. After replacing profile data and inserting the new snapshot, delete
only normalized-source rows not referenced by any document. All operations
remain in the existing save transaction, so cancellation or failure restores
the previous valid index.

Snapshot validation requires:

- every document hash equals SHA-256 of its normalized text;
- every declaration/call references a persisted document;
- normalized starts and lengths are nonnegative, lengths are positive, and
  `start + length` is within the referenced normalized document;
- existing original source-location validation remains unchanged.

Schema 5 and older databases are rejected through the existing schema
mismatch error and explicit `csindex index` guidance. `RequestHasher` schema
version becomes 6. `AnalysisCacheVersion` becomes 4 because the extraction
payload and normalization mapping changed.

## Query Hydration

`StoredDeclaration` and `StoredCall` retain their original location fields and
add normalized start/length plus an optional materialized
`NormalizedSource`. The optional string is a query DTO value, not a database
column on each declaration or call. Remove declaration-level
`NormalizedSourceHash` from storage/query models.

Repository queries always read normalized range metadata. They load large
normalized text only when requested:

1. Read declaration/call rows without joining the text payload into every row.
2. If source hydration is required, collect distinct document IDs.
3. Load each referenced `normalized_sources.normalized_source` once.
4. Bounds-check and slice in C# using the row's UTF-16 range.
5. Attach the resulting string to the returned DTO.

Source text is required for:

- a declaration query with any source include/exclude condition;
- `symbol find --show-source`, `source show`, and `source search`;
- `callers --show-source` and `callers tree --show-source`.

It is not loaded for file-only conditions, ordinary graph traversal, or any
no-flag caller output. Existing test observers are updated to observe actual
shared source-row hydration rather than obsolete declaration cell reads.

A malformed persisted range produces a focused `IndexDatabaseException`
identifying the row and document. There is no fallback to the source file and
no SQL substring fallback.

## `symbol find --include` / `--exclude`

This feature must preserve declaration-scoped source matching. Never pass the
complete normalized document to `SourceTextFilter`.

For each candidate physical declaration, hydrate and slice precisely that
declaration's normalized range, then apply the existing predicates:

- every include condition must match that same physical declaration;
- any exclude condition matching that declaration rejects it;
- file and source conditions must pass together on one physical declaration;
- a logical symbol is selected if at least one physical declaration passes;
- a partial definition and implementation remain one logical result;
- include terms cannot be distributed across two partial declarations.

For example, when one file contains `A(){Marker();}` and `B(){Other();}`, an
include for `Marker` returns `A` only. A document-wide match that also returns
`B` is a correctness failure.

## Query and Graph Data Flow

### Ordinary callers

The CLI passes `showSource` into `SemanticQueryService.FindCallersAsync`.
The service asks `QueryRepository.GetCallsByCalleeAsync` to hydrate normalized
source only when true. Ordering and endpoint hydration remain unchanged. The
returned `CallResult.ShowSource` controls conditional formatting.

### Caller tree

`CallerTreeBuilder` already obtains the physical `StoredCall` rows needed to
discover structural edges. For every call that targets the frontier callee,
record a `CallerTreeCallSite` keyed to that exact structural edge before edge
deduplication. Calls whose caller/callee nodes are excluded by source/system,
depth, or max-node rules do not survive into the result. At a finite depth
boundary, retained cycle/cross edges retain their call sites under the same
eligibility rule.

When `showSource` is false, repository calls do not hydrate text, and
formatters ignore `CallSites`. When true, each physical call carries its
materialized slice. No additional Roslyn work is performed.

## Error and Cancellation Rules

- Invalid or missing normalized ranges fail indexing before database mutation
  or fail query hydration with `IndexDatabaseException` for a corrupt DB.
- Regex errors/timeouts retain their current no-partial-payload behavior.
- Output-file atomicity remains unchanged; all source formatting happens
  inside the existing temporary-output transaction.
- Cancellation is checked during token mapping, range lookup, source-row
  hydration, slicing loops, call-site association, ordering, and formatting.
- No exception path reconstructs source from `UnresolvedName`, original text,
  Roslyn, or a legacy column.

## Required Tests

### Normalization map

- A document slice equals the legacy per-node normalizer for methods,
  accessors, local functions, lambdas, initializers, top-level statements,
  invocations, and object creations.
- Nested invocation ranges independently yield inner and outer expressions.
- ASCII and Japanese comments disappear.
- Ordinary, raw, interpolated, character, and Unicode/surrogate-pair literals
  preserve token text and range correctness.
- Boundary separators outside a node are excluded from its slice.
- Cancellation remains observable.

### Extraction and persistence

- One document payload serves multiple declarations and calls.
- Identical normalized document contents share one `normalized_sources` row.
- Declaration/call ranges are persisted independently from original spans.
- Old declaration text/hash columns and transient symbol text/hash model are
  absent.
- Invalid hashes, unknown documents, zero/out-of-bounds ranges, foreign-key
  failures, collisions, and cancellation preserve the previous database.
- Version 5 is rejected and version 6 is created; no migration exists.

### Search regression

- `symbol find --include` and `--exclude` still operate on declaration slices,
  not full documents.
- Multiple includes must match one declaration; an excluded partial row does
  not rewrite the semantics of another passing row.
- File-only queries do not hydrate normalized text; source conditions do, once
  per distinct shared document payload.

### Caller query and output

- Overloaded `A(float)` / `A(int)` calls return `A(f)` / `A(10+20)`.
- An outer `B(...)` and both nested `A(...)` calls have independent slices;
  the semicolon is absent.
- `callers` table and JSON add source only with the flag.
- Caller tree preserves every physical call site on one structural edge and
  on cycle/cross/additional edges.
- Tree, Mermaid, and JSON render the exact contracts above.
- Table control characters cannot split a physical record; JSON preserves
  exact text; Mermaid escaping remains valid.
- Query-service source hydration succeeds using only DB normalized text. The
  test seam must prove no source-normalization/reparse callback is invoked.
- Existing no-flag snapshots remain byte-identical.

## Completion Criteria

- Schema 6 is the only active schema and old databases are explicitly rejected.
- Normalized document text is persisted once per unique content and referenced
  by documents; declarations and calls store only ranges.
- Existing source filtering is declaration-scoped and regression-tested.
- Both caller commands expose exact normalized call expressions in every
  supported output format when requested.
- Multiple physical call sites are not lost behind one caller-tree edge.
- No normalized-source query path reads/reparses source or falls back to old
  data.
- Focused tests, all four test projects, Release build, formatting validation,
  and `git diff --check` pass with zero warnings/errors.
