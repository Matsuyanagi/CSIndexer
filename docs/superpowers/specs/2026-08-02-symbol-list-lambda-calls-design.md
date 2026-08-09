# Symbol List, Lambda Call Inclusion, and Short Names Design

Date: 2026-08-02
Status: Accepted with lambda-numbering revision

Current authority: `docs/SPEC.md` sections 16 and 33.2. DEC-0020 supersedes
the original nested-lambda counter reset described by the first version of
this design; the corrected rule is recorded below.

## Goal

Extend the CLI so users can inspect function names, see calls written inside
lambda expressions (including nested lambdas), and optionally render names
without namespace prefixes while preserving exact indexed identities.

## Decisions

### Namespace-shortened presentation

`--short-names` is a presentation-only option. The default output remains the
current fully qualified representation. Index keys, stored `display_name`,
query matching, and `fullyQualifiedName` JSON values remain exact and are not
changed by this option.

The option is accepted by `symbol find`, `symbol list`, `definition`,
`references`, `callers`, `callees`, and `overrides`. Human-readable symbol,
caller, callee, and relation names are shortened. The canonical
`fullyQualifiedName` field in JSON remains unchanged. A type-name shortener
handles namespace-qualified types inside generic arguments, arrays, and
nullable suffixes without requiring a Roslyn compilation at query time.

### Lambda ownership and numbering

Every `AnonymousFunctionExpressionSyntax` is indexed as an `IndexedSymbolKind.Lambda`.
The synthetic name is `<lambda#N>`, where `N` is counted from one in source
order for the nearest non-lambda executable owner. Nested lambdas share that
display counter instead of restarting it. Their stored containing-symbol ID
still points to the immediate lexical lambda, so calls remain attributed to
the lambda in which they are written. `Invoke()` or delegate execution sites
are not used to change ownership or call classification.

### Calls inside lambdas

Invocation and object-creation facts are assigned to the nearest syntax owner,
including a lambda owner. `callees` includes calls from the selected function
and all descendant lambda owners by default. Descendant traversal follows the
stored containing-symbol hierarchy, including intermediate local functions, so
deeply nested lambdas are included. `--exclude-lambda-calls` limits `callees`
to calls whose caller is one of the selected symbols, preserving the previous
behavior. Calls from local functions that are not lambda descendants are not
added by this option.

### Function listing

`csindex symbol list` lists source and metadata function symbols. The default
kind set is `method` plus `lambda`; `--kind method` and `--kind lambda` select a
single kind. Unsupported values are rejected as CLI usage errors.

`--async-involved` filters to symbols whose persisted
`async_involvement_depth` is not null. The filter therefore includes async
roots at depth zero and callers reached by async propagation.

Table output lists one symbol per line with its location and async annotation
when applicable. JSON output is shaped as `{ profile, symbols: [...] }` and
reuses the existing symbol fields and async fields. `--short-names` changes
human-facing `displayName` values but does not alter canonical identity fields.

## Architecture

1. Add a query-repository method for listing symbols with kind and async-depth
   predicates.
2. Add `SemanticQueryService.ListSymbolsAsync` and a descendant-lambda-aware
   callee query. Keep SQL/profile filtering inside the repository.
3. Extend CLI command dispatch and option validation with `symbol list`,
   `--short-names`, and `--exclude-lambda-calls` for `callees`.
4. Centralize presentation-only shortening in the CLI formatter. Do not change
   Roslyn extraction or persisted symbol names except where tests expose a
   numbering/ownership regression.

## Error handling

- `symbol list --kind` accepts only `method` or `lambda` and reports an
  `Argument error` for any other value.
- Existing database/profile errors and output-format validation remain
  unchanged.
- Empty result sets are successful and produce an empty `symbols` array in
  JSON.

## Testing

- Unit tests cover namespace/type shortening, generic parameter formatting,
  lambda display numbering per nearest non-lambda executable owner, and nested
  lambda ownership.
- Query tests cover method/lambda listing, async-depth filtering, and recursive
  descendant lambda call retrieval.
- CLI/output tests cover `--short-names`, both `symbol list` formats, and
  `--exclude-lambda-calls`.
- The complete solution test suite and build are run before completion.
