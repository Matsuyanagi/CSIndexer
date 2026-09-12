# CSIndexer

[Japanese](README_ja.md) | [Command guide and examples](EXAMPLE.md)

CSIndexer (`csindex`) is a Windows command-line tool that uses Roslyn to build
a reusable semantic index of C# source code in SQLite. It can find callable
symbols, definitions, references, callers, callees, overrides, normalized
source, and async involvement without reopening the project for every query.

## Features

- Index `.sln`, `.slnx`, `.csproj`, or directory-based C# source trees.
- Understand methods, constructors, destructors, operators, conversions,
  accessors, local functions, lambdas, anonymous methods, initializers, and
  top-level statements.
- Search by structured symbol path, namespace, type, method, file, normalized
  source, generated-code state, callable kind, and direct async state.
- Find definitions and references, inspect direct callers and callees, and
  follow override and interface-implementation branches.
- Build bounded caller trees and persisted async-involvement paths.
- Show normalized declaration source and the normalized expression for each
  physical call site.
- Keep logical symbols separate from physical declarations, including partial
  definition/implementation pairs.
- Store portable relative paths and reconstruct absolute or relative paths at
  query time.
- Produce deterministic table, JSON, tree, line, and Mermaid output.
- Commit index and output-file updates atomically.

## Requirements

- Windows x64
- .NET 10 SDK when building from source
- The SDKs, workloads, and reference assemblies required by the solutions or
  projects being analyzed

The CLI targets `net10.0-windows` and uses `win-x64` as its default runtime
identifier.

## Build and publish

Build the solution:

```powershell
dotnet build CsIndex.sln --configuration Release
```

The executable is written to:

```text
src\CsIndex.Cli\bin\Release\net10.0-windows\win-x64\csindex.exe
```

If [Task](https://taskfile.dev/) is installed, the repository also provides:

```powershell
task build
task publish
```

`task publish` creates a self-contained folder at
`artifacts/publish/win-x64` by default. The equivalent direct command is:

```powershell
dotnet publish src\CsIndex.Cli\CsIndex.Cli.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts\publish\win-x64
```

The remaining examples assume that `csindex.exe` is available as `csindex` on
`PATH`. You can use the full executable path instead.

## Quick start

Create an index from a solution, project, or directory:

```powershell
csindex index Game.sln
# or
csindex index Game.csproj
# or, from the source root
csindex index .
```

Run queries from the directory containing `.csindex`, or pass `--db`:

```powershell
csindex symbol find "Game::Player::Run()"
csindex definition "Game::Player::Run()"
csindex callers "Game::Player::Run()" --show-source
csindex callers tree "Game::Player::Run()" --depth 3
csindex source search --include-literal "CancellationToken"
```

See [EXAMPLE.md](EXAMPLE.md) for a detailed guide to every command.

## Index and database

When `--db` is omitted, indexing writes `.csindex/index.sqlite` under the
selected input/storage root. Queries default to `.csindex/index.sqlite` under
the current directory.

The current database schema is version 6. Older or unrecognized databases are
rejected without migration or modification. Rename or delete the old database,
or select a new `--db` path, and run `csindex index` explicitly.

Stored project and document paths use forward slashes and are relative to one
storage root. The database also stores a relative anchor from its directory to
that root, so moving the database and source tree together preserves their
relationship. Query-time `--base-dir` can override path reconstruction without
rewriting the database.

Ordinary persisted source must be on the same Windows drive or UNC share as
the storage root. A positively identified generated document supplied by
MSBuild from another volume/share remains available to Roslyn compilation but
is excluded from the portable index with a warning.

## Symbol paths

CSIndexer accepts two canonical forms:

```text
csharp:   Game.Core.Player.Inventory::Load(int).Validate()
explicit: Game.Core::Player.Inventory::Load(int).Validate()
```

The C# form has one top-level `::` separator. It treats the namespace/type
boundary as a suffix search, so a copied result remains searchable even when
the namespace is omitted. The explicit form has two top-level separators and
fixes the namespace/type boundary exactly.

Nested types use `.`, and executable children such as local functions and
lambdas also use `.`. Omitting a parameter list includes overloads; `()` means
exactly zero parameters. C# aliases such as `int` and `string` are preferred
in displayed signatures.

Special source callables use unambiguous segments such as `[constructor]`,
`[operator:+]`, `[get:Name]`, `<lambda#1>`, and
`<top-level-statements>`. Wildcards use whole-component `*` and `**` forms.
The complete grammar and examples are in [EXAMPLE.md](EXAMPLE.md) and
`csindex --help-verbose`.

## Commands

| Command | Purpose |
| --- | --- |
| `csindex index <input>` | Build or update a semantic SQLite index. |
| `csindex symbol find [selector]` | Search callable symbols by path and/or typed conditions. |
| `csindex symbol list` | List callable symbols in the selected profile. |
| `csindex source show <selector>` | Show one callable's normalized declaration source. |
| `csindex source search` | Find callables using normalized-source and typed conditions. |
| `csindex definition <selector>` | Show physical declaration locations for matching logical symbols. |
| `csindex definition --at <path:line:column>` | Resolve the definition targeted at a source position. |
| `csindex references <selector>` | List stored references to matching symbols. |
| `csindex callers <selector>` | List call sites and effective callers of matching symbols. |
| `csindex callers tree <selector>` | Build a bounded reverse caller graph. |
| `csindex callees <selector>` | List calls made by matching symbols. |
| `csindex overrides <selector>` | List stored method override relationships. |
| `csindex async tree <selector>` | Follow one persisted path to an async origin. |
| `csindex conditions` | List conditional-compilation symbols observed by the selected profile. |

Detailed behavior and examples for every row are in
[EXAMPLE.md](EXAMPLE.md).

## Option reference

An option that is not accepted by a command is an error. Run
`csindex <command> --help` for concise command-specific help, or
`csindex <command> --help-verbose` for the full grammar and exact accepted
option list.

### Index options

| Option | Meaning |
| --- | --- |
| `--db <path>` | SQLite index path. |
| `--mode auto\|solution\|project\|directory` | Select input loading mode. |
| `--solution <path>` | Select a solution for a directory input. |
| `--configuration <name>` | MSBuild configuration. |
| `--framework <tfm>` | Target framework; alias of `--target-framework`. |
| `--target-framework <tfm>` | Target framework; alias of `--framework`. |
| `--runtime <rid>` | Runtime identifier used by the analysis profile. |
| `--profile-name <name>` | Name the stored analysis profile. |
| `--define <symbol>` | Add a preprocessor symbol; repeatable. |
| `--undefine <symbol>` | Remove a preprocessor symbol; repeatable. |
| `--define-file <path>` | Read preprocessor symbols from a file; repeatable. |
| `--reference <dll>` | Add a metadata reference; repeatable. |
| `--exclude <glob>` | Exclude source paths; repeatable. `obj` is always excluded. |
| `--generated-source physical\|all\|none` | Generated-source mode. Only `physical` is currently implemented. |
| `--rebuild` | Force reanalysis of a compatible index. It does not migrate an incompatible schema. |
| `--verbose` | Show indexing progress. |
| `--diagnostics` | Show detailed compiler and semantic diagnostics. |
| `--unity-editor <directory>` | Reserved for future Unity support; not implemented. |
| `--help` | Show concise index help. |
| `--help-verbose` | Show the full reference. |

### Common query and presentation options (`Q`)

| Option | Meaning |
| --- | --- |
| `--db <path>` | SQLite index path; default `.csindex/index.sqlite`. |
| `--profile <name>` | Select a profile; defaults to the most recently indexed profile. |
| `--output-format <format>` | Select a command-supported output format. |
| `-o <path>`, `--output-file <path>` | Write the payload atomically to a file instead of stdout. |
| `--symbol-path-style csharp\|explicit` | Choose displayed symbol-path notation; presentation only. |
| `--short-names` | Omit namespaces from displayed owners and types. |
| `--base-dir <path>` | Override the base used to reconstruct stored relative paths. |
| `--path-style absolute\|relative` | Display absolute paths (default) or effective-base-relative paths. |
| `--help` | Show concise help. |
| `--help-verbose` | Show the full reference. |
| `--verbose` | With query help, show the full reference; otherwise invalid. |

With --short-names, namespaces are omitted from displayed owners and every displayed type, including return types, parameters, generic arguments, conversion targets, and explicit-interface payloads. Nested containing types remain visible. In JSON, displayName, signature, fullyQualifiedName, parameters, and returnType shorten; stableKey and the complete namespaceName do not change.

Omit --short-names when consumers require canonical machine-oriented JSON. Without the option, JSON remains fully qualified, and the default output, field presence, and ordering remain unchanged.

Standard commands use `table|json`. `async tree` uses `tree|line|json`, and
`callers tree` uses `tree|mermaid|json`.

### Typed selection conditions (`C`)

| Domain | Glob (default syntax) | Literal | Regular expression | Case mode |
| --- | --- | --- | --- | --- |
| Namespace | `--namespace <glob>` | `--namespace-literal <text>` | `--namespace-regex <pattern>` | `--namespace-case strict\|ignore` |
| Type | `--type <glob>` | `--type-literal <text>` | `--type-regex <pattern>` | `--type-case strict\|ignore` |
| Method | `--method <glob>` | `--method-literal <text>` | `--method-regex <pattern>` | `--method-case strict\|ignore` |
| File | `--file <glob>` | `--file-literal <text>` | `--file-regex <pattern>` | `--file-case strict\|ignore` |
| Required source | `--include <glob>` | `--include-literal <text>` | `--include-regex <pattern>` | `--source-case strict\|ignore` |
| Excluded source | `--exclude <glob>` | `--exclude-literal <text>` | `--exclude-regex <pattern>` | `--source-case strict\|ignore` |

The selection group also contains:

| Option | Meaning |
| --- | --- |
| `--kind all\|method\|lambda` | Filter direct roots by callable kind; default `all`. |
| `--async-status all\|async\|sync` | Filter direct roots by stored direct async role; default `all`. |

`--kind all` also includes indexed initializers and top-level statements;
those categories are not folded into `method` or `lambda`.

Conditions are repeatable except for case selectors. Alternatives within the
same namespace/type/method/file category are ORed in command-line order;
different categories are ANDed. Every include condition must match, and any
exclude condition rejects the declaration. Case matching defaults independently
to `strict`.

Source matching is unanchored over the normalized range of one physical
callable declaration, not over the complete document. This lets
`symbol find --include` and `--exclude` find functions containing or omitting
particular code without duplicating call-site source in the database.

The removed generic switches `--regex` and `--ignore-case` are not aliases and
are rejected.

### Command-specific options

| Option | Commands | Meaning |
| --- | --- | --- |
| `--require-single` | `symbol find`, `definition`, `references`, `callers`, `callees`, `overrides` | Exit 5 unless exactly one logical root remains. |
| `--include-overrides` | `symbol find`, `definition`, `references`, `callers`, `callees` | Expand one exact method query to descendant overrides/interface implementations. |
| `--exclude-generated` | `references`, `callers`, `callees` | Exclude generated roots and applicable returned edges. |
| `--only-generated` | `references`, `callers`, `callees` | Keep only generated roots and applicable returned edges. |
| `--show-source` | `symbol find`, `callers`, `callers tree` | Include normalized declaration or physical call-site source. |
| `--source-layout single-line\|multi-line` | `symbol find`, `source show`, `source search` | Select table source layout. `symbol find` also requires `--show-source`. |
| `--async-involved` | `symbol list` | Keep symbols with a persisted async-involvement depth. |
| `--at <path:line:column>` | `definition` | Resolve a call target by physical position; cannot be combined with root conditions. |
| `--dispatch static\|virtual\|all` | `callers` | Select stored-call output and possible dispatch targets; default `static`. |
| `--caller-scope direct\|containing\|both` | `callers` | Display direct callable owners, containing callable owners, or both. |
| `--exclude-lambda-calls` | `callees` | Do not include calls owned by nested lambdas. |
| `--depth <count>` | `callers tree` | Maximum caller depth; default `3`, `0` means unlimited. |
| `--max-nodes <count>` | `async tree`, `callers tree` | Positive node limit; default `500`. |

`--exclude-generated` and `--only-generated` are mutually exclusive.
`--include-overrides` requires one exact, wildcard-free method selector and is
not accepted for condition-only, lambda, initializer, top-level, or
`definition --at` searches.

### Exact option scope

`Q` means the common query/presentation group and `C` means all typed selection
conditions plus `--kind` and `--async-status`.

| Command | Accepted groups and additions |
| --- | --- |
| Global help | `--db + --profile + --output-format + --output-file + --help + --help-verbose + --verbose` |
| `index` | The index-option table above; no query option groups |
| `symbol find` | `Q + C + --require-single + --include-overrides + --show-source + --source-layout` |
| `symbol list` | `Q + C + --async-involved` |
| `source search` | `Q + C + --source-layout` |
| `source show` | `Q + C + --source-layout` |
| `definition <selector>` | `Q + C + --require-single + --include-overrides` |
| `definition --at` | `Q + --at`; no root conditions |
| `references` | `Q + C + generated filters + --require-single + --include-overrides` |
| `callers` | `Q + C + generated filters + --require-single + --include-overrides + --dispatch + --caller-scope + --show-source` |
| `callees` | `Q + C + generated filters + --require-single + --include-overrides + --exclude-lambda-calls` |
| `overrides` | `Q + C + --require-single`; no `--include-overrides` |
| `async tree` | `Q + C + --max-nodes` |
| `callers tree` | `Q + C + --depth + --max-nodes + --show-source` |
| `conditions` | `--db`, `--profile`, `--output-format`, `--output-file`, `--base-dir`, `--path-style`, and help options |

## Output behavior

- Result payloads go to stdout; diagnostics, progress, warnings, and summaries
  go to stderr.
- `--output-file` writes to a same-directory temporary file and replaces the
  destination only after successful rendering and flushing.
- Single-line table source replaces tabs and line-separator characters with
  spaces to keep one physical record per line. JSON preserves the exact stored
  normalized-source slice.
- `callers --show-source` adds the normalized invocation or object-creation
  expression for every physical call row.
- `callers tree --show-source` attaches every retained physical call site to
  its structural edge. JSON emits ordered `callSites`; Mermaid embeds escaped
  locations and source in edge labels.
- Presentation options do not change semantic identity or result ordering.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success, including a valid empty list/search result. |
| `2` | Invalid arguments or query. |
| `3` | Input, analysis, cancellation, source-read, or output failure. |
| `4` | SQLite or incompatible-schema failure. |
| `5` | `--require-single` failure. |

## Important limitations

- Analysis records static Roslyn facts. It does not infer dynamic dispatch,
  delegate/event/callback execution, reflection, receiver data flow, or other
  runtime behavior.
- Source Link retrieval and decompilation of metadata-only symbols are not
  provided.
- Normalized source omits trivia, comments, directives, and inactive
  conditional text, while preserving literal-token text.
- Arbitrary normalized-source substring searches scan eligible candidates;
  they do not use FTS.
- Anonymous-function ordinals are stable only within one indexed snapshot and
  may change after edits earlier in the containing callable.
- `--generated-source all|none` and `--unity-editor` are reserved and not
  implemented in this build.
- Queries select one analysis profile; cross-profile search is not provided.

See [Known Limitations](docs/KNOWN_LIMITATIONS.md) for the full list.

## Documentation

- [Command guide and examples](EXAMPLE.md)
- [CLI contract](docs/CLI.md)
- [Specification](docs/SPEC.md)
- [Database schema](docs/DB_SCHEMA.md)
- [Design decisions](docs/DECISIONS.md)
- [Test plan](docs/TEST_PLAN.md)
- [Implementation status](docs/IMPLEMENTATION_STATUS.md)
- [Known limitations](docs/KNOWN_LIMITATIONS.md)
