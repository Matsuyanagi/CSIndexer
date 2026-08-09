# Symbol, Source, and Graph Expansion Design

Date: 2026-08-08

## Authoritative requirements

`docs/2026-08-08.revised2.md` is the authoritative Japanese requirements
document for this implementation. This design maps those approved requirements
onto the existing CSIndexer architecture. When wording differs, the
authoritative requirements win.

## Context

CSIndexer already extracts Roslyn symbols and calls, persists them in SQLite,
and exposes exact symbol, definition, reference, caller, callee, override, and
function-list queries. It already stores accessibility, static state, direct
async roles, async involvement depth, source spans, and immediate lexical
lambda ownership.

The new work extends that foundation in four coupled areas:

1. richer executable-symbol facts and stable lambda naming;
2. normalized source persistence and combined name/source search;
3. one persisted shortest path to an asynchronous origin;
4. bounded recursive caller graphs with Mermaid output.

The changes intentionally break database compatibility. Existing schema
version 3 databases are rejected without modification and must be rebuilt.

## Goals

- Search lambdas by full name, owner suffix, or lambda suffix.
- Number nested lambdas within their nearest non-lambda executable owner and
  give every member initializer a distinct owner display name.
- Persist and display accessibility, static state, return type, executable
  kind, source availability, and normalized source.
- Support exact, wildcard, component, and timeout-bounded regular-expression
  symbol searches.
- Combine symbol-name filters with normalized-source include/exclude filters.
- Provide standalone normalized-source show and search commands.
- Persist one deterministic next hop for the shortest path from each
  async-involved function to an asynchronous origin.
- Display that path as a tree by default, as one line, or as JSON.
- Traverse callers breadth-first with depth and node limits, retain cycle
  edges, exclude metadata/external functions, and emit valid Mermaid.

## Non-goals

- No schema migration from version 3 or older.
- No delegate `Invoke`, event-dispatch, callback-flow, reflection, or runtime
  target inference beyond the existing resolved call facts.
- Lambda lexical ownership is not a call edge.
- No source decompilation for metadata-only symbols.
- No change to the descendant-only semantics of `--include-overrides`.
- No new external dependency.

## Core extraction model

`SymbolData` gains nullable `ReturnTypeKey`, nullable `NormalizedSource`, a
nullable normalized-source hash, and nullable `AsyncNextSymbolKey`. A source
definition remains represented by `SourceDocumentKey` and its span; no
duplicated source-availability boolean is required in memory.

Roslyn `IMethodSymbol.ReturnType` supplies return types for ordinary methods,
local functions, lambdas, operators, conversions, and accessors. Constructors
and static constructors store no return type. Accessibility uses Roslyn
`DeclaredAccessibility` where the C# declaration kind supports an access
modifier. Local functions and lambdas store a not-applicable accessibility so
the formatter never invents a `private` modifier that cannot be written there;
static constructors likewise store not-applicable accessibility. Static lambda
state is taken from its Roslyn method symbol.

A focused source normalizer consumes Roslyn syntax tokens, not regular
expressions. It excludes comments, documentation trivia, directives, and
disabled text, preserves token text for literals and interpolated/raw strings,
and inserts one space only when concatenating adjacent token texts would alter
lexical tokenization. It removes layout line breaks outside literal token text;
embedded newlines in a multiline raw literal remain part of that literal's
exact `Text`. Tokens inside an interpolation expression use ordinary C# token
boundary rules. The normalizer hashes the resulting layout-normalized text
with SHA-256.

Every source definition and source target is scoped by its owning project key
before it enters the stable-key map. This applies to types, executable symbols,
synthetic owners, calls, relations, and interface bindings. Metadata-only
symbols retain assembly/TFM identity and are not duplicated per referencing
project.

Each field, property, or event initializer receives a display name of the
form:

```text
Namespace.Type::<initializer:memberName>
```

Lambda numbering uses the nearest non-lambda executable owner as the counter
scope. Immediate lexical containment remains stored in `ContainingSymbolKey`
so nested-lambda call attribution and descendant queries continue to work.
Consequently, nested lambdas are displayed as consecutive children of the
same method or initializer while calls inside them remain owned by the
immediate lambda symbol.

## Async shortest-path derivation

The existing reverse multi-source BFS remains the derivation mechanism.
Resolved invocation calls are ordered deterministically by callee, source
document, source start, caller, and stable call identity before adjacency is
built. Origins and both call-edge endpoints are restricted to source-backed
methods/lambdas with normalized source; metadata-only awaitable methods never
become a persisted path node. Async origins are enqueued in deterministic
stable-key order.

For every first or strictly shorter visit to a caller, the propagator records:

- `AsyncInvolvementDepth = callee depth + 1`;
- `AsyncNextSymbolKey = callee stable key`.

An equal-distance visit never replaces the stored next hop. Origins have
depth zero and a null next hop. Query-time path reconstruction follows only
the persisted next-hop IDs and validates that every next depth is exactly one
less. Every node must be a source-backed method/lambda with normalized source;
only a direct async-origin role may terminate at depth zero, and non-origins
must have coherent depth/next state. A fetched hop is validated before a
`--max-nodes` truncation result is returned. A visited-ID set guards corrupt or
cyclic data.

## Storage design

Schema and request-hash versions increase from 3 to 4. The version 4 `symbols`
table adds:

```sql
return_type_key       TEXT,
normalized_source     TEXT,
normalized_source_hash BLOB,
async_next_symbol_id  INTEGER,

FOREIGN KEY(async_next_symbol_id)
  REFERENCES symbols(id)
```

The source document and source span already stored on `symbols` satisfy the
source-location requirement. Source availability is `source_document_id IS
NOT NULL`. The existing `accessibility`, `method_kind`, and `is_static`
columns remain authoritative.

Symbols are inserted first. Containing IDs and async-next IDs are then updated
inside the same save transaction after every stable key has a numeric ID.
The normalized-source hash is inserted with the symbol. Replacement of an
analysis profile remains atomic.

All symbol readers return the new fields. Stable ordering adds the numeric
symbol ID as the final tie-breaker wherever it is currently absent. Version 3
and all other unsupported versions are rejected before WAL or user data is
modified.

Schema v4 also uses profile-prefixed indexes for symbol kind, containing
symbol, async depth/next, exact names, short method components, namespace/type
components, and fully qualified names. A partial
`(analysis_profile_id, kind) WHERE source_document_id IS NOT NULL` index serves
source-backed executable reads. Representative repository predicates are
locked with `EXPLAIN QUERY PLAN` tests that reject a full `symbols` scan.

## Symbol and source search

The existing exact `SymbolQueryParser` remains the resolver for definition,
reference, caller, callee, and override commands. `symbol find` receives a
dedicated search request that can express:

- an optional positional full-name pattern;
- `--namespace`, `--type`, and `--method` component patterns;
- optional `--kind method|lambda`;
- wildcard mode using `*` for zero or more characters;
- `--regex` mode using culture-invariant .NET regular expressions with a
  fixed timeout;
- repeatable source `--include` and `--exclude` filters;
- `--ignore-case` and `--show-source`.

A positional value without `*`, without a `::<lambda#` marker, and without a
matching modifier (`--regex`, `--ignore-case`, components, kind, or source
conditions) preserves existing exact-query semantics. `--show-source` remains
presentation-only and does not disqualify that route. Lambda suffix forms are
matched against `DisplayName` suffixes. A method pattern without a parameter
list matches all overloads; a pattern with parameters matches the complete
display name.

For wildcard and regular-expression searches, matches are evaluated against
canonical stored name components and the canonical `DisplayName`; output-only
short-name conversion is not involved. Multiple name/component filters are
ANDed. Regex compilation or execution failure becomes an invalid-query error.

Name and structured metadata filters run before normalized-source filters.
Source-less symbols are excluded once a source include/exclude filter is
present. Excludes are ORed and evaluated first with short-circuit rejection;
only survivors evaluate all ANDed includes. `--show-source` changes
presentation only.

`source show` resolves source-backed executable symbols and emits their
signature, location, and normalized source. `source search` requires at least
one include or exclude term and scans source-backed executable symbols using
the same filtering semantics. The repository may use SQL filtering or bounded
application evaluation, but it must preserve ordinal case-sensitive matching
by default and profile isolation. Both exact and extended `source show` routes
push source-only scope into their initial repository read.

## Query results and presentation

Executable symbol JSON exposes accessibility, static state, async state,
return type, method kind, source availability, and normalized source when
requested. Table/text output uses a dedicated signature formatter so return
and parameter types obey `--short-names` consistently without mutating stored
canonical fields.

Async path output supports:

```text
--output tree   # default
--output line
--output json
```

Line output uses the exact separator ` -> `. Tree and line outputs represent
the same single persisted path. JSON includes path nodes, `found`, and
`truncated` state.

Caller-tree output is a graph result with unique nodes and unique directed
caller-to-callee edges. The service performs breadth-first expansion from the
resolved root. `--depth 0` removes the depth bound but never the node bound.
New nodes stop at `--max-nodes`; edges between already included nodes are
retained so recursive and mutual-recursive cycles remain visible. At a finite
depth boundary the service still reads reverse calls and retains only edges
whose two endpoints are already in the result; it never admits a deeper node.

Traversal includes only symbols with indexed source definitions and excludes
the `System` namespace and its descendants. Calls written inside a lambda are
edges from that lambda; the containing method is not synthesized as a caller.
Mermaid uses symbol-ID-based node IDs and escaped labels.

## CLI integration

The CLI adds these command routes:

```text
csindex async tree <symbol>
csindex callers tree <symbol>
csindex source show <symbol>
csindex source search --include <text> | --exclude <text>
```

`symbol find` accepts the search and source options above. Repeatable options
continue to use the existing parser. Command-specific validation rejects
unknown output modes, negative depth, non-positive maximum node counts,
missing source-search conditions, conflicting regex/wildcard use, and
ambiguous roots for graph commands.

Existing exit-code categories remain unchanged.

## Error handling

- Missing profiles and unsupported schemas use existing storage exceptions.
- Invalid patterns, regex failures, invalid numeric limits, and ambiguous
  graph roots use existing invalid-argument/query paths.
- A missing graph root names the query. An ambiguous graph root lists every
  canonical candidate in repository order; duplicate display names add their
  document path and symbol ID.
- No reachable async origin is a successful empty-path result.
- Corrupt async next-hop data produces a focused database/query failure rather
  than looping or silently selecting a different route.
- Cancellation is checked during extraction, token/lambda/call ordering,
  filtering, BFS traversal, collection materialization, and output ordering.

## Testing strategy

Implementation follows RED-GREEN-REFACTOR at each layer.

Core tests cover return types, token-safe normalization, comments and literal
preservation, field/property/event initializer names, function-scoped nested
lambda numbering, immediate call ownership, deterministic async ties, and
async-next cycle safety.

Storage tests cover schema version 4, non-mutating version 3 rejection,
round-trip persistence of all new symbol fields, async-next foreign keys,
profile isolation, stable result ordering, and transaction rollback.

Query tests cover exact compatibility, lambda suffix lookup, overload
behavior, wildcard/component/regex matching, regex timeout/error handling,
exclude-first source filtering, source-less exclusion, source show/search,
async path validation, caller-tree bounds, cycles, and external filtering.

CLI integration tests cover every new command and option in table/tree, line,
JSON, and Mermaid output, including short names, invalid arguments,
truncation, no-path behavior, and combined name/source search.

Final verification runs all focused test projects, the complete Release test
suite, a Release build, formatting/diff checks, and a tracked-clean status
check.

## Documentation

Implementation updates `docs/SPEC.md`, `docs/CLI.md`, `docs/DB_SCHEMA.md`,
`docs/DECISIONS.md`, `docs/TEST_PLAN.md`, `docs/IMPLEMENTATION_STATUS.md`, and
`docs/KNOWN_LIMITATIONS.md` where applicable. The Japanese authoritative
requirements document is amended only to record the approved normalization
clarification: layout newlines outside literal-token text are removed, while
newlines contained in literal-token text remain unchanged.

## Acceptance

The implementation is complete only when every acceptance condition in
`docs/2026-08-08.revised2.md` is represented by a focused automated test or an
explicitly documented non-automated verification, all existing behavior not
superseded by that document remains green, and schema version 4 can reconstruct
every new query result without loading a Roslyn workspace.
