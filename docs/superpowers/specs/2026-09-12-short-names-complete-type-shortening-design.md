# Complete `--short-names` Type Shortening Design

**Date:** 2026-09-12  
**Status:** Approved in conversation  
**Scope:** Query-time symbol presentation only

## 1. Context

The current `--short-names` implementation removes only the namespace of the
symbol owner. It deliberately leaves namespaces in return types, parameter
types, conversion targets, and explicit-interface payloads. For example, it
currently emits:

```text
public static Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax SyntaxRefactorings::ConvertWhileStatementToForStatement(Microsoft.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax,Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax?,Microsoft.CodeAnalysis.SeparatedSyntaxList<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax>)
```

That output is too verbose for the option's intended human-oriented overview.
The option will instead remove namespace qualification from every presented
type while preserving the distinctions that remain useful to a reader, such as
containing-type nesting, generic construction, nullability, and ref kinds.

This design supersedes the owner-only `--short-names` rules in:

- `docs/superpowers/specs/2026-08-16-csharp-symbol-path-design.md` section 16.2;
- `docs/SPEC.md` sections 33.3 and 34.9;
- `docs/CLI.md` and README option descriptions.

All other symbol-path, identity, search, ordering, and portable-index decisions
remain unchanged.

## 2. Goals

When `--short-names` is present:

1. Remove namespaces from the symbol owner and from every type shown in a
   human-facing symbol representation.
2. Apply the same presentation rule to text, table, graph, tree, Mermaid, and
   JSON output.
3. Preserve containing-type paths. `Game.Models.Outer<T>.Inner<U>` becomes
   `Outer<T>.Inner<U>`, not `Inner<U>`.
4. Preserve valid C# type syntax and existing C# aliases such as `int`,
   `string`, and `object`.
5. Leave storage, identity, matching, result cardinality, and result ordering
   unchanged.
6. Require no database schema change and no reindex solely for this option.

## 3. Non-goals

- Do not shorten source paths, assembly names, profile names, stable keys, or
  diagnostic identities.
- Do not change search-selector parsing or cause a short displayed type to
  become the stored/search identity.
- Do not resolve display-name collisions introduced by namespace omission.
  Short names are intentionally lossy presentation.
- Do not add another short-name mode or compatibility switch. The new behavior
  replaces the old owner-only behavior.
- Do not persist a second short representation in SQLite.

## 4. Normative presentation behavior

### 4.1 Owner and callable path

The existing owner behavior remains:

```text
# csharp
Game.Core.Player.Inventory::Load(System.Guid)
Player.Inventory::Load(Guid)

# explicit
Game.Core::Player.Inventory::Load(System.Guid)
**::Player.Inventory::Load(Guid)
```

For explicit style, `**` continues to represent the omitted owner namespace so
the emitted path remains valid as a namespace-wildcard search input.

Every callable segment in a lexical path is shortened, including the parameter
types of an outer method and nested local functions:

```text
Worker::Run(Dictionary<string,Widget>).Validate(Result<Widget?>).<lambda#1>
```

### 4.2 Return and parameter types

Namespace qualification is removed recursively from:

- return types;
- all parameter types in every callable segment;
- generic type arguments;
- tuple element types;
- array and pointer element types;
- nullable underlying types;
- function-pointer parameter and return types;
- custom modifiers or ref-bearing type positions represented by the canonical
  type grammar;
- conversion targets;
- explicit-interface containing-type payloads;
- constructor, operator, accessor, and other bracketed special segments that
  contain types.

Examples:

```text
Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax
=> ForStatementSyntax

System.Collections.Generic.Dictionary<string,Game.Models.Outer<int>.Inner<string?>[]>
=> Dictionary<string,Outer<int>.Inner<string?>[]>

(System.DateTime,Game.Models.Widget?[])
=> (DateTime,Widget?[])

delegate* unmanaged[Cdecl]<System.Int32,Game.Models.Widget,System.Void>
=> delegate* unmanaged[Cdecl]<int,Widget,void>
```

Existing predefined C# aliases remain preferred. Generic parameter names remain
unchanged. Escaped identifiers remain escaped where required by C# syntax.

### 4.3 Namespace/type boundary

Shortening MUST use semantic identity information that records the namespace to
type boundary. It MUST NOT infer that boundary from capitalization and MUST NOT
remove dotted containing-type components.

Given canonical identity and display pairs such as:

```text
identity: Game.Models::Outer<System::Int32>.Inner<System::String>
display:  Game.Models.Outer<int>.Inner<string>
short:    Outer<int>.Inner<string>
```

the namespace components are removed and the complete outer-to-inner type path
is retained. This rule also applies recursively to constructed type arguments.

Simple regular-expression or last-dot truncation is prohibited because it
cannot distinguish namespaces from nested types and is unsafe for balanced C#
type syntax.

### 4.4 Behavior without the option

Output without `--short-names` MUST remain byte-compatible except where an
existing test intentionally normalizes platform line endings or paths. The
default remains fully qualified presentation.

## 5. JSON contract

With `--short-names`, JSON uses shortened presentation in every field that
contains a human-readable symbol or type presentation:

- `displayName`;
- `signature`;
- `fullyQualifiedName` (the field name is retained for schema stability even
  though its value is shortened under this explicit presentation option);
- every value in `parameters`;
- `returnType`;
- any equivalent conversion-target, interface-payload, node-label, endpoint,
  or nested symbol/type presentation field exposed by a command.

The following fields never change under `--short-names`:

- numeric IDs and ID references;
- `stableKey`;
- `namespaceName`, which continues to contain the complete namespace requested
  by the user;
- `assemblyName`, profile data, paths, offsets, declaration roles, flags, and
  other non-presentation data.

Consumers requiring canonical machine-readable type strings must omit
`--short-names`. JSON shape and property presence do not change merely because
the option is present.

## 6. Output-surface consistency

One shared shortening implementation MUST serve all formatters. At minimum the
following surfaces must agree:

- `symbol find` and `symbol list`;
- `definition`, `references`, `callers`, `callees`, and `overrides`;
- caller and async trees;
- graph text, JSON, and Mermaid labels;
- source-command headers or records that contain formatted symbols;
- ambiguity lists and other query diagnostics that honor symbol-presentation
  options;
- output-file and stdout variants.

No command may retain owner-only shortening after this change.

## 7. Architecture

### 7.1 Presentation-time transformation

Shortening occurs after a query has selected and ordered its semantic results
and immediately before formatting the payload. Stored canonical values are
read-only inputs.

The formatter will use paired canonical identity and display values to produce
a short display type. The identity supplies the semantic namespace/type
boundary; the display supplies C# aliases, concrete generic parameter names,
nullable syntax, and other presentation choices.

`SymbolSignatureFormatter` and `SymbolPathFormatter` remain the presentation
entry points. A single structural type-shortening component may be added in the
Core symbol-presentation layer and reused for:

1. standalone return and JSON type fields; and
2. type-bearing parts of executable display paths.

The implementation may factor parsing helpers out of existing canonical type
logic, but it MUST NOT duplicate a second incompatible type grammar.

### 7.2 Stored data and schema

No SQLite columns or model persistence fields are added. Existing identity and
display pairs contain the information needed for this transformation. Schema
version remains unchanged, and an existing compatible database can immediately
use the new presentation behavior.

### 7.3 Ordering and search

Sorting continues to use semantic identity and stored location data. It never
uses the shortened output. Two distinct symbols that render to the same short
text remain two distinct, deterministically ordered results.

Search input, case modes, wildcard/regex behavior, overload selection, root
cardinality, traversal, and `--include`/`--exclude` source matching are
unchanged.

## 8. Error handling

Canonical identity/display pairs written by the supported schema are expected
to be structurally valid. A structural mismatch must produce a deterministic,
actionable formatting error rather than silently deleting arbitrary dotted
segments or returning a misleading partially shortened value.

Unsupported text that is not documented as a canonical type position remains
unchanged. Cancellation behavior and atomic output-file semantics remain
unchanged.

## 9. Help and documentation

Normal and verbose help must describe the complete behavior, for example:

```text
--short-names  Omit namespaces from displayed owners and types
```

Verbose help must include at least one return/parameter example and state that
`stableKey` and `namespaceName` are retained in JSON. Update README.md,
README_ja.md, EXAMPLE.md, docs/CLI.md, docs/SPEC.md, and any implementation
status or decision records that still claim owner-only shortening.

## 10. Testing strategy

Follow RED-GREEN-REFACTOR.

### 10.1 Structural unit tests

Cover at least:

- predefined aliases and ordinary qualified named types;
- nested and constructed nested types;
- recursively nested generic arguments;
- nullable value/reference types, arrays, pointers, and tuples;
- function pointers and ref kinds;
- escaped and non-ASCII identifiers;
- conversion and explicit-interface payload types;
- parent method, local-function, and lambda paths;
- malformed identity/display pair diagnostics;
- unchanged output when `ShortNames` is false.

### 10.2 Formatter and CLI integration tests

Cover at least:

- the reported Roslyn-style method signature in table output;
- JSON shortening of `displayName`, `signature`, `fullyQualifiedName`,
  `parameters`, and `returnType`;
- JSON preservation of `stableKey` and complete `namespaceName`;
- text/tree/graph/Mermaid consistency;
- csharp and explicit symbol-path styles;
- stdout and output-file equivalence;
- no schema migration or reindex requirement.

Existing owner-only assertions must be replaced, not weakened. Existing tests
for canonical identity, matching, ordering, and default fully qualified output
must continue to pass.

## 11. Acceptance criteria

The change is complete when all of the following are true:

1. The reported Roslynator command emits `ForStatementSyntax`,
   `WhileStatementSyntax`, `VariableDeclarationSyntax?`, and
   `SeparatedSyntaxList<ExpressionSyntax>` under `--short-names`.
2. No namespace prefix remains in any documented human-readable type position.
3. Nested type paths remain intact.
4. JSON presentation/type fields shorten while `stableKey` and
   `namespaceName` remain exact.
5. Output without the option is unchanged.
6. All symbol-bearing output families share the same semantics.
7. Existing compatible databases work without rebuilding.
8. Focused tests, the complete test suite, build, formatting verification, and
   diff checks pass with no unaccounted failures.
