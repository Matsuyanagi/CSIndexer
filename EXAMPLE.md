# CSIndexer command guide and examples

[README](README.md) | [Japanese README](README_ja.md)

This guide explains the current schema-version-6 command interface in more
detail. Examples use PowerShell and assume that `csindex.exe` is available as
`csindex` on `PATH`.

For the exact option list accepted by a command, run:

```powershell
csindex <command> --help
csindex <command> --help-verbose
```

`--help-verbose` is equivalent to `--help --verbose`.

## 1. Build an index

### Solution, project, and automatic directory input

Index an explicit solution or project:

```powershell
csindex index C:\Work\Game\Game.sln
csindex index C:\Work\Game\Game.csproj --configuration Release
```

An explicit `.slnx` file is also supported. When a directory is supplied in
`auto` mode, CSIndexer selects the appropriate solution/project route when it
can do so:

```powershell
Set-Location C:\Work\Game
csindex index .
```

Select the MSBuild input explicitly when necessary:

```powershell
csindex index . --mode solution --solution .\Game.sln
csindex index . --mode project
```

The following five forms use the MSBuildWorkspace path and share the same
portable-input rules:

1. A directory for which `auto` selects a solution.
2. An explicit `.sln` or `.slnx`.
3. An explicit `.csproj`.
4. A directory with `--mode project`.
5. A directory with `--solution <path>`.

### Physical directory mode

Force directory mode to enumerate physical `.cs` files under one root without
loading an MSBuild solution/project:

```powershell
csindex index C:\Work\LooseSources --mode directory
```

`obj` path segments are always excluded. `bin` is not automatically excluded.
Use repeatable file globs for project-specific exclusions:

```powershell
csindex index . --mode directory `
  --exclude "**/bin/**" `
  --exclude "**/ThirdParty/**"
```

Directory mode can add preprocessor symbols and references:

```powershell
csindex index . --mode directory `
  --define WINDOWS `
  --define FEATURE_AUDIO `
  --undefine DEBUG `
  --define-file .\defines.txt `
  --reference C:\Libraries\Contracts.dll
```

`--define`, `--undefine`, `--define-file`, `--reference`, and index-mode
`--exclude` are repeatable.

### Analysis profiles

Name a stored profile and select its build dimensions:

```powershell
csindex index Game.sln `
  --configuration Release `
  --framework net10.0-windows `
  --runtime win-x64 `
  --profile-name windows-release
```

Multiple named profiles may coexist. Queries use the most recently indexed
profile unless `--profile` selects another one:

```powershell
csindex symbol list --profile windows-release
```

### Database location and rebuilds

Use a custom database:

```powershell
csindex index Game.sln --db D:\Indexes\Game\index.sqlite
csindex symbol list --db D:\Indexes\Game\index.sqlite
```

Force reanalysis of a compatible index:

```powershell
csindex index Game.sln --rebuild
```

`--rebuild` does not migrate or delete an incompatible database. Schema 5 and
older databases must be renamed/deleted, or replaced by choosing another
`--db` path, before `csindex index` is run again.

### Cross-volume generated documents

An ordinary persisted project, document, or linked source must share the
storage root's Windows drive or UNC server/share. When MSBuild injects a
physical document from another volume/share, CSIndexer keeps it in the Roslyn
compilation only if the existing generated-code detector positively identifies
it as generated. It emits one warning and one excluded-document count, and
does not persist the external path, declaration, source, or body-derived facts.

This rule applies to all five MSBuild input forms above. Forced
`--mode directory` uses a separate physical source-enumerator route and does
not load MSBuild-injected documents.

## 2. Symbol-path syntax

### C# suffix form and explicit form

```text
Game.Core.Player.Inventory::Load(int).Validate()
Game.Core::Player.Inventory::Load(int).Validate()
```

The first path is C# form. Its single top-level `::` leaves the exact
namespace/type boundary open and performs a suffix match across possible
boundaries. This makes displayed C# names convenient to copy back into a
query, but additional namespace-prefixed matches can be returned.

The second path is explicit form:

```text
namespace :: nested-type-path :: executable-path
```

It fixes `Game.Core` as the namespace and `Player.Inventory` as the nested
type path. Use explicit form when the boundary must be exact.

Omit the namespace to search every namespace:

```powershell
csindex symbol find "Player.Inventory::Load()"
```

Select the empty/global namespace explicitly:

```powershell
csindex symbol find "global::Program::<top-level-statements>"
```

`@global` is an ordinary literal identifier; it does not mean the global
namespace.

### Nested executable paths

Local functions and anonymous functions use immediate containment with `.`:

```text
Game.Core::Player::Run().Validate().<lambda#1>
Game.Core::Player::Run().<anonymous-method#2>
```

The old spelling that used `::` between executable children is rejected.

### Wildcards

A whole hierarchy component `*` matches one level. A whole component `**`
matches zero or more levels. An embedded `*` remains inside one component.

```powershell
csindex symbol find "Game.*::Player::Run"
csindex symbol find "Game.**::Player*::Run"
csindex symbol find "**::Player::Run"
```

### Overloads and generic arity

Parameter-list omission includes every overload:

```text
Game::Player::Run          any generic arity and any parameter list
Game::Player::Run()        non-generic, exactly zero parameters
Game::Player::Run(int)     non-generic, exactly one int parameter
```

Generic-list and parameter-list omission are independent:

```text
Find<T>        generic arity 1, any parameter list
Find<T>()      generic arity 1, zero parameters
Find<T>(T)     generic arity 1, exact canonical parameter list
```

Type arity is exact: `Repository`, `Repository<T>`, and `Repository<T,U>` are
different. Use a deliberate type glob such as `Repository*` to span arities.

Constructed invocation notation such as `Find<System.String>` is not accepted.
C# aliases such as `int` and `string` are accepted and preferred in displayed
signatures. Concrete non-alias types must be fully qualified.

### Special callable segments

The canonical special and synthetic segments are:

```text
[constructor]                       [static-constructor]
[destructor]                        [operator:<token>]
[checked-operator:<token>]          [conversion:implicit:<type>]
[conversion:explicit:<type>]        [checked-conversion:explicit:<type>]
[get:<member>]                      [set:<member>]
[init:<member>]                     [add:<member>]
[remove:<member>]                   [explicit:<interface-member>]
<lambda#n>                          <anonymous-method#n>
<initializer:Name>                  <top-level-statements>
```

Examples:

```text
Game::Player::[constructor](int,string)
Game::Player::[static-constructor]()
Game::Player::[destructor]()
Game::Vector::[operator:+](Game.Vector,Game.Vector)
Game::Score::[conversion:implicit:int](Game.Score)
Game::Player::[get:Name]()
Game::Player::[set:Name](string)
Game::Player::<initializer:Health>
global::Program::<top-level-statements>
```

Explicit-interface accessors keep both the accessor tag and fully qualified
interface member:

```text
Game::Player::[get:Game.Contracts.IPlayer.Name]()
Game::Player::[set:Game.Contracts.IPlayer.Name](string)
```

For anonymous queries, the ordinal is a positive integer or `*`:

```powershell
csindex symbol find "Game::Player::Run().<lambda#*>"
```

Run `csindex --help-verbose` for the complete operator-token list and invalid
grammar examples.

## 3. Typed search conditions

The concise namespace/type/method/file/include/exclude options use glob syntax.
Use a `-literal` or `-regex` suffix to select the other matcher explicitly:

```powershell
csindex symbol find `
  --namespace "Game.**" `
  --type "*Service" `
  --method "Load*" `
  --file "src/**" `
  --include-literal "CancellationToken" `
  --exclude-regex "Debug\s*\.\s*Write"
```

Case sensitivity is independent by domain and defaults to `strict`:

```powershell
csindex symbol find `
  --type-literal "player" `
  --type-case ignore `
  --method "get*" `
  --method-case ignore
```

Within one namespace, type, method, or file category, repeated conditions are
ordered OR alternatives. Different categories are ANDed. Every repeated
include condition must match; any matching exclude rejects the declaration.

```powershell
# Namespace is Game.Core OR Game.Editor, and the type ends in Service.
csindex symbol find `
  --namespace-literal "Game.Core" `
  --namespace-literal "Game.Editor" `
  --type "*Service"

# Both source fragments must occur in one physical declaration.
csindex symbol find `
  --include-literal "CancellationToken" `
  --include-literal "await"
```

File and source conditions must match the same physical declaration. A partial
definition and implementation still project to one logical symbol. Source
conditions search the normalized declaration slice, not the complete document,
and are unanchored. Comments, trivia, directives, and inactive conditional
text are absent from normalized source; literal-token text is preserved.

## 4. `symbol find`

`symbol find` accepts a positional selector, typed selection conditions, or
both. It requires at least one selector/condition, `--kind`, or
`--async-status`.

Find every overload of a method:

```powershell
csindex symbol find "Game::Player::Run"
```

Find exactly the zero-parameter overload:

```powershell
csindex symbol find "Game::Player::Run()"
```

Search by typed conditions without a positional selector:

```powershell
csindex symbol find --type "*Controller" --method "Execute*"
```

Find functions whose normalized declaration contains a pattern:

```powershell
csindex symbol find `
  --include-literal "CancellationToken" `
  --exclude-literal "CancellationToken.None"
```

Include normalized declaration source:

```powershell
csindex symbol find "Game::Player::Run()" --show-source
csindex symbol find "Game::Player::Run()" --show-source --source-layout multi-line
csindex symbol find "Game::Player::Run()" --show-source --output-format json
```

A valid zero-match search succeeds. Add `--require-single` when a script needs
exactly one logical result:

```powershell
csindex symbol find "Game::Player::Run()" --require-single
```

Expand one exact method root to real descendant overrides and interface
implementations:

```powershell
csindex symbol find "Game.Contracts::IPlayable::Play()" --include-overrides
```

`--include-overrides` requires one exact wildcard-free method selector. It is
not a general condition-only or lambda expansion mode.

## 5. `symbol list`

Without filters, `symbol list` uses `--kind all`: it lists methods, lambdas,
initializers, and top-level statements in the selected profile:

```powershell
csindex symbol list
```

List lambdas only, or filter by direct async role:

```powershell
csindex symbol list --kind lambda
csindex symbol list --kind method --async-status async
csindex symbol list --kind method --async-status sync
```

Initializers and top-level statements participate in `all`; they are not
included by `--kind method` or `--kind lambda`.

`--async-involved` is a separate transitive filter. It retains symbols with a
persisted `asyncInvolvementDepth`, including direct async origins at depth 0:

```powershell
csindex symbol list --async-involved
csindex symbol list --async-status sync --async-involved --output-format json
```

The second example finds directly synchronous callables that can reach an
async origin through stored calls.

## 6. `source show`

`source show` requires exactly one source-backed logical executable root and
shows its preferred physical declaration. For a partial method, the
implementation declaration is preferred when available.

```powershell
csindex source show "Game::Player::Run()"
csindex source show "Game::Player::Run().Validate()" --source-layout multi-line
csindex source show "Game::Player::Run().<lambda#1>" --output-format json
```

The text is normalized source, not a byte-for-byte copy of the source file.
Metadata-only symbols do not have source to show.

## 7. `source search`

`source search` has no positional selector. It requires at least one explicit
typed condition, `--kind`, or `--async-status`. A valid zero-match search
succeeds.

```powershell
csindex source search --include-literal "CancellationToken"
csindex source search --include "await*" --source-case ignore
csindex source search --type "*Service" --async-status async
csindex source search --file "src/Gameplay/**" --source-layout multi-line
```

Unlike `symbol find --show-source`, `source search` always returns normalized
source for its matching source-backed executable declarations.

## 8. `definition`

### Selector mode

Show the physical declaration rows for one or more logical symbols:

```powershell
csindex definition "Game::Player::Run()"
csindex definition "Player::Run" --output-format json
```

A partial definition and implementation are one logical candidate, but
`definition` may emit both physical rows with their declaration roles. Add
`--require-single` to require one logical root, not one physical declaration.

```powershell
csindex definition "Game::Player::Run()" --require-single
```

Method roots can opt into descendant expansion:

```powershell
csindex definition "Game.Contracts::IPlayable::Play()" --include-overrides
```

### Position mode

Resolve the call target at a physical source position:

```powershell
csindex definition --at "src\Player.cs:120:17"
csindex definition --at "C:\Work\Game\src\Player.cs:120:17" --output-format json
```

The position form accepts a rooted path or an effective-base-relative path. It
does not accept a positional selector or root selection conditions.

## 9. `references`

List stored source references to matching symbols:

```powershell
csindex references "Game::Player::Run()"
csindex references "Game::Player::Run()" --output-format json
```

Filter roots and returned generated rows:

```powershell
csindex references "Game::Player::Run()" --exclude-generated
csindex references "Generated::Factory::Create()" --only-generated
```

The two generated filters are mutually exclusive. Root filtering occurs before
logical cardinality. Root conditions do not remove unrelated secondary rows
after a command starts its relation work.

Search an interface contract and its real implementations:

```powershell
csindex references "Game.Contracts::IPlayable::Play()" --include-overrides
```

Results are based on stored static Roslyn facts. Runtime receiver flow,
reflection, and dynamically discovered targets are not inferred.

## 10. `callers`

List physical calls whose stored callee matches the selected root:

```powershell
csindex callers "Game::Player::Tick()"
```

Attach the exact normalized invocation or object-creation expression for every
physical call row:

```powershell
csindex callers "Game::Player::Tick()" --show-source
csindex callers "Game::Player::Tick()" --show-source --output-format json
```

The source string is a UTF-16 range into the document's one shared normalized
source payload. It is not stored as a duplicate string on every call row.

Choose how lambda/local ownership is presented:

```powershell
csindex callers "Game::Player::Tick()" --caller-scope direct
csindex callers "Game::Player::Tick()" --caller-scope containing
csindex callers "Game::Player::Tick()" --caller-scope both
```

- `direct` reports the immediate callable that owns the call.
- `containing` reports the stored containing callable when one exists.
- `both` reports both distinct views.

Dispatch presentation modes are:

```powershell
csindex callers "Game::BasePlayer::Tick()" --dispatch static
csindex callers "Game::BasePlayer::Tick()" --dispatch virtual
csindex callers "Game.Contracts::IPlayable::Play()" --dispatch all
```

- `static` reports stored calls only.
- `virtual` also reports stored override candidates.
- `all` also permits stored explicit/implicit interface implementation
  candidates.

This is not receiver-value or runtime-flow analysis. Use
`--include-overrides` when the query roots themselves should expand before
calls are selected:

```powershell
csindex callers "Game.Contracts::IPlayable::Play()" --include-overrides
```

## 11. `callees`

List resolved invocation and object-creation calls owned by a selected
callable:

```powershell
csindex callees "Game::Player::Run()"
csindex callees "Game::Player::Run()" --output-format json
```

By default, calls made inside nested lambdas (including further nested lambdas)
are included with the containing method query. Restrict the result to calls
directly owned by the method itself:

```powershell
csindex callees "Game::Player::Run()" --exclude-lambda-calls
```

Generated filters and exact method override expansion are available:

```powershell
csindex callees "Game::Player::Run()" --exclude-generated
csindex callees "Game.Contracts::IPlayable::Play()" --include-overrides
```

## 12. `overrides`

List stored method override relations whose target matches the selected method:

```powershell
csindex overrides "Game::BasePlayer::Tick()"
csindex overrides "Game::BasePlayer::Tick()" --output-format json
```

`overrides` accepts method roots only. It does not accept
`--include-overrides`; that option expands roots for other command families,
whereas `overrides` reports the stored override relation itself.

Interface implementation expansion is available through the five commands
that accept `--include-overrides`, not through `overrides`.

## 13. `async tree`

`async tree` requires exactly one source-backed logical root. It follows the
one deterministic `async_next_symbol_id` chain stored in the index until it
reaches an async origin or a validated boundary.

```powershell
csindex async tree "Game::Player::Run()"
csindex async tree "Game::Player::Run()" --output-format line
csindex async tree "Game::Player::Run()" --output-format json
```

Bound the path:

```powershell
csindex async tree "Game::Player::Run()" --max-nodes 100
```

`--max-nodes` defaults to 500 and must be positive. The index stores one
selected shortest path, not every equal path or every reachable async origin.

## 14. `callers tree`

`callers tree` requires exactly one source-backed logical root and walks
resolved static calls in reverse:

```powershell
csindex callers tree "Game::Player::Tick()"
csindex callers tree "Game::Player::Tick()" --depth 5 --max-nodes 1000
```

`--depth` defaults to 3; `0` means unlimited depth. `--max-nodes` defaults to
500 and must be positive. Traversal is cycle-safe and retains eligible cycle
and cross edges at finite boundaries.

Select another representation:

```powershell
csindex callers tree "Game::Player::Tick()" --output-format mermaid
csindex callers tree "Game::Player::Tick()" --output-format json
```

Attach all retained physical call sites to their structural edge:

```powershell
csindex callers tree "Game::Player::Tick()" --show-source
csindex callers tree "Game::Player::Tick()" --show-source --output-format mermaid
csindex callers tree "Game::Player::Tick()" --show-source --output-format json
```

Text tree output writes one `@ path:line:column` record per site followed by
sanitized source. Mermaid puts ordered, escaped sites in edge labels. JSON
adds ordered `callSites` objects containing `id`, `location`, and exact
`normalizedSource`.

The tree does not infer delegate `Invoke`, events/callbacks, reflection,
receiver data flow, or runtime virtual/interface dispatch. Root conditions
select the starting root; they do not prune stored descendants.

## 15. `conditions`

List conditional-compilation symbols observed while indexing the selected
profile:

```powershell
csindex conditions
csindex conditions --profile windows-release
csindex conditions --output-format json
```

This command accepts no positional selector or root conditions.

## 16. Portable path display

The default output reconstructs absolute paths from the database's stored
root anchor:

```powershell
csindex definition "Game::Player::Run()" --path-style absolute
```

Display paths relative to the effective base:

```powershell
csindex definition "Game::Player::Run()" --path-style relative
```

Override only the query-time reconstruction base after moving an existing
database/source tree:

```powershell
csindex definition "Game::Player::Run()" `
  --db D:\Moved\Game\.csindex\index.sqlite `
  --base-dir D:\Moved\Game
```

`--base-dir` never rewrites the database. Presentation path choices do not
change symbol identity or canonical ordering.

## 17. Symbol-name display

Choose the displayed symbol-path style:

```powershell
csindex symbol find "Game::Player::Run()" --symbol-path-style csharp
csindex symbol find "Game::Player::Run()" --symbol-path-style explicit
```

Shorten namespaces in displayed owners and every displayed type:

```powershell
csindex symbol list --short-names
```

For example, a Roslyn-style signature is fully qualified by default:

```text
public static Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax SyntaxRefactorings::ConvertWhileStatementToForStatement(Microsoft.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax,Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax?,Microsoft.CodeAnalysis.SeparatedSyntaxList<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax>)
```

With `--short-names`, the displayed owner and every displayed type are
shortened:

```text
public static ForStatementSyntax SyntaxRefactorings::ConvertWhileStatementToForStatement(WhileStatementSyntax,VariableDeclarationSyntax?,SeparatedSyntaxList<ExpressionSyntax>)
```

The same rule applies to generic arguments, conversion targets, and
explicit-interface payloads. Nested containing types remain visible; for
example, `Game.Models.Outer<T>.Inner<U>` becomes `Outer<T>.Inner<U>` rather
than losing `Outer<T>`.

In JSON, `displayName`, `signature`, `fullyQualifiedName`, `parameters`, and
`returnType` use the shortened presentation. `stableKey` and the complete
`namespaceName` remain unchanged. Omit `--short-names` when consumers need
canonical machine-oriented JSON; the default output and ordering are
unchanged.

## 18. Output files and scripting

Write the same formatter payload that would have gone to stdout:

```powershell
csindex symbol list --output-format json --output-file .\symbols.json
csindex callers tree "Game::Player::Tick()" -o .\callers.mmd --output-format mermaid
```

The file is rendered to an owned same-directory temporary file, flushed, and
committed atomically. Query, regex, path, formatting, cancellation, replace,
or commit failures preserve an existing destination and do not expose partial
payload.

For lossless normalized-source text, use JSON. Single-line table output
replaces tabs and line-separator characters with ASCII spaces so every result
remains one physical line.

Exit codes for scripts are:

```text
0  success, including valid empty list/search results
2  invalid arguments or query
3  input, analysis, cancellation, source, or output failure
4  SQLite or incompatible-schema failure
5  --require-single failure
```

## 19. Diagnostics and help

Show indexing progress and detailed compiler/semantic diagnostics:

```powershell
csindex index Game.sln --verbose
csindex index Game.sln --diagnostics
```

Query `--verbose` is help-only:

```powershell
csindex callers --help --verbose
csindex callers --help-verbose
```

Help validates tokenization and the command's allowed-option set, then exits
without opening the database, source files, or output destination. Unknown
commands and options remain errors.

## Further reference

- [README and complete option index](README.md)
- [Canonical CLI contract](docs/CLI.md)
- [Full specification](docs/SPEC.md)
- [Database schema](docs/DB_SCHEMA.md)
- [Known limitations](docs/KNOWN_LIMITATIONS.md)
