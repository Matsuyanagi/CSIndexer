# Override-Aware Method Search Design

Date: 2026-08-05

## Context

CSIndexer already persists direct method relationships for overrides and
explicit or implicit interface implementations. Those relationships are used
by the `overrides` command and by `callers --dispatch`, but ordinary method
queries still resolve only methods declared on the named type.

This feature adds an opt-in search mode that expands a method query toward
derived implementations. It also lets a query such as `D1::Play()` resolve to
the real inherited declaration when `D1` does not declare `Play` itself.

## Goals

- Add a boolean `--include-overrides` option, disabled by default.
- Apply it consistently to `symbol find`, `definition`, `references`,
  `callers`, and `callees`.
- Expand an interface method to its explicit, implicit, inherited, abstract,
  and default-interface implementations.
- Expand a class or abstract-class method to transitive overrides below the
  queried receiver type.
- Resolve an inherited method query to real declarations without creating
  synthetic symbols.
- Keep expansion descendant-only, deterministic, profile-scoped, and safe in
  the presence of malformed cycles.

## Non-goals

- Do not expand a concrete implementation upward to its base class or
  interface contracts.
- Do not include sibling interface implementations when a concrete method is
  queried.
- Do not perform receiver-value flow analysis. A call whose static target is
  `IPlayable::Play()` is not returned by a search rooted at
  `Pianist::Play()`.
- Do not treat `new` method hiding as overriding.
- Do not create or output synthetic inherited symbols such as a stored
  `D1::Play()` symbol.
- Do not change `symbol list`, `conditions`, or the existing `overrides`
  relation-inspection command.
- Do not enumerate all possible metadata-only implementation types outside
  the types analyzed from the selected source profile.

## User-visible semantics

The new option is accepted by these commands:

```text
csindex symbol find <method-query> --include-overrides
csindex definition <method-query> --include-overrides
csindex references <method-query> --include-overrides
csindex callers <method-query> --include-overrides
csindex callees <method-query> --include-overrides
```

The default remains exact method lookup. Supplying the option with a type-only
query is an invalid-argument error.

Given an interface implementation tree, querying `IPlayable::Play()` returns
the interface member and all real implementation methods. Querying
`Pianist::Play()` returns that method and its transitive overrides, but not
other implementations of `IPlayable`.

If `D1` inherits `Base::Play()` without declaring `Play`, querying
`D1::Play()` with the option resolves to `Base::Play()`. Expansion is then
restricted to the D1 descendant branch. Results contain `Base::Play()` and
real overrides below D1, but never a synthetic `D1::Play()` entry or an
override in a sibling branch.

## Search target model

The query layer represents an initial target as a real method ID plus the
receiver type ID that scopes descendant expansion. An internal target may
also carry an interface contract method ID when interface dispatch context is
required.

This branch context is necessary for inherited implementations. For example:

```csharp
class Base { public virtual void Play() {} }
class D1 : Base, IPlayable {}
class Other : Base { public override void Play() {} }
```

`Base::Play()` implements `IPlayable::Play()` for D1, but `Other::Play()` is
not an `IPlayable` implementation. A method-only edge from `Base::Play()` to
`IPlayable::Play()` would lose that distinction.

## Roslyn extraction

For every source type in the selected profile, extraction inspects all of its
interfaces. For each interface method, Roslyn
`FindImplementationForInterfaceMember` determines the method used by that
type. The extractor records a binding containing:

- the implementing type;
- the interface contract method;
- the real implementation method.

Bindings cover explicit implementation, implicit implementation, inherited
class implementation, abstract implementation, derived overrides, and
default interface methods. Partial declarations and repeated interface paths
are deduplicated by stable keys.

If Roslyn cannot determine an implementation because compilation is
incomplete, extraction does not guess. It omits that binding, records a
diagnostic through the existing diagnostics path, and continues indexing.

Existing `Overrides`, `ExplicitlyImplements`, and `ImplicitlyImplements`
symbol relations remain available for relation display and ordinary override
traversal.

## Storage design

Schema version and request-hash schema version increase from 2 to 3. Version 2
databases are rejected without modification and must be rebuilt. No automatic
migration or deletion is performed.

The schema adds:

```sql
CREATE TABLE interface_method_bindings (
    analysis_profile_id     INTEGER NOT NULL,
    implementing_type_id    INTEGER NOT NULL,
    interface_method_id     INTEGER NOT NULL,
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

CREATE INDEX ix_interface_method_bindings_contract
ON interface_method_bindings(analysis_profile_id, interface_method_id);

CREATE INDEX ix_interface_method_bindings_type
ON interface_method_bindings(analysis_profile_id, implementing_type_id);
```

Bindings are inserted in the same transaction as symbols, calls, and symbol
relations. The existing `symbols.accessibility` column supplies accessibility
information for inherited alias resolution; no new accessibility column is
required.

## Method resolution and expansion

A shared query-layer `MethodTargetResolver` is used by every affected command.
Its behavior is:

1. Parse the query using the existing symbol query grammar.
2. Find methods declared on every matching receiver type.
3. Without `--include-overrides`, return the exact matches unchanged.
4. With the option, use each exact match as a branch-scoped search seed.
5. If no matching method is declared on a receiver type, recursively walk its
   `Inherits` edges toward base classes.
6. At the nearest base level that declares any accessible method with the
   requested name, stop walking higher to preserve C# name-hiding semantics.
7. Apply the optional parameter signature to methods at that selected level.
8. Exclude inaccessible inherited methods using persisted accessibility and
   assembly information.
9. Expand class and abstract-class seeds through transitive `Overrides`
   relationships, restricted to types in the receiver type's descendant
   branch.
10. For an interface method, read `interface_method_bindings` for that exact
    contract and include the real implementation methods selected for indexed
    implementing types.
11. Deduplicate results by real method ID and order them by display name,
    document path, and source position.

Recursive type and method CTEs use `UNION`, not `UNION ALL`, so visited IDs or
visited `(method, branch)` pairs are not expanded repeatedly. This guarantees
termination for self-cycles and multi-node cycles in malformed persisted
data.

An omitted namespace may select multiple receiver types. Each receiver is
resolved and expanded independently before method IDs are deduplicated.

An exact declaration always takes precedence over inherited alias lookup. A
`new` method therefore resolves to itself, and because it has no `Overrides`
edge, it does not pull in the hidden base method or its override tree.

## Command behavior

- `symbol find` returns the expanded real method symbols in its matched set.
- `definition` returns definitions for the expanded real method symbols.
- `references` searches all existing reference kinds against the expanded
  callee IDs.
- `callers` searches call references against the expanded callee IDs.
  `--dispatch` remains an independent possible-runtime-target presentation
  mode; it does not change the descendant-only direction of
  `--include-overrides`.
- `callees` searches calls made by every expanded method. Existing recursive
  inclusion of calls from descendant lambdas is applied independently to
  each expanded method; `--exclude-lambda-calls` still opts out.

For a concrete query, call sites whose static callee is only a base interface
method remain excluded. Users search the interface contract when they want
those call sites and all implementations.

## Error handling

- A type-only query combined with `--include-overrides` returns the existing
  invalid-arguments exit code with a focused message.
- An unknown receiver type or an inherited method that cannot be resolved
  returns an empty result, consistent with existing query behavior.
- Database and cancellation failures use the existing error and exit-code
  paths.
- Missing bindings caused by compilation errors produce conservative partial
  results rather than speculative relationships.

## Testing strategy

Implementation follows RED-GREEN-REFACTOR with focused tests at each layer.

Core extraction tests cover:

- explicit and implicit interface implementation;
- interface implementation inherited from a base class;
- abstract and default-interface implementations;
- derived-type binding selection;
- partial-type and repeated-path deduplication.

Storage tests cover:

- schema version 3 and the new table and indexes;
- fail-fast, non-mutating rejection of version 2 databases;
- transactional persistence and DB-only reconstruction of bindings;
- cycle-safe recursive type and override traversal;
- exclusion of override branches outside an implementing type.

Query and CLI tests cover:

- interface search returning the interface, Pianist, ProPianist, and Game but
  not Baseball;
- concrete implementation search returning Pianist and ProPianist only;
- exclusion of interface-statically-typed calls from concrete searches;
- inherited alias resolution returning Base and D2 without a synthetic D1;
- exclusion of sibling branches and `new` method hiding;
- unchanged exact behavior when the option is absent;
- all five commands in table and JSON output;
- interactions with `--short-names`, `--dispatch`, and lambda callee options;
- invalid use with type-only queries.

Final verification runs all focused test projects, the complete Release test
suite, a Release build, `git diff --check`, and a clean-status check.

## Documentation updates

Implementation updates `docs/SPEC.md`, `docs/CLI.md`, `docs/DECISIONS.md`,
`docs/DB_SCHEMA.md`, `docs/TEST_PLAN.md`, and
`docs/IMPLEMENTATION_STATUS.md`. `docs/KNOWN_LIMITATIONS.md` is updated only
when an existing limitation becomes inaccurate or a material limitation of
this feature must be recorded.

## Acceptance criteria

- `--include-overrides` is opt-in and accepted by exactly the five specified
  commands.
- Interface-rooted searches include all indexed real implementations and
  transitive derived implementations, with branch context preserved.
- Concrete-rooted searches expand only downward through their own override
  branch.
- Inherited aliases resolve to real declarations and never create synthetic
  output symbols.
- Search expansion is deterministic, profile-scoped, and cycle-safe.
- Schema version 3 persistence and version mismatch behavior are verified.
- Default command behavior remains exact and all existing tests remain green.
