# CLI

> **Current contract:** the final section, "Canonical query interface (schema
> version 5)", is authoritative for symbol-path grammar, typed conditions,
> command option scope, portable paths, help, and query output. Earlier query
> examples are retained as implementation history where explicitly marked;
> the build and indexing instructions remain active.

## Build and executable

```powershell
dotnet build CsIndex.sln --configuration Release
src\CsIndex.Cli\bin\Release\net10.0-windows\win-x64\csindex.exe --help
```

実行ファイル名は`csindex.exe`、target frameworkは`net10.0-windows`、既定RIDは`win-x64`です。

## Index

```powershell
csindex index Game.sln
csindex index Game.csproj --configuration Release
csindex index C:\Source --mode directory --define FEATURE_AUDIO
```

オプション:

- `--db <path>`。省略時は入力rootの`.csindex/index.sqlite`。
- `--mode auto|solution|project|directory`
- `--solution <path>`
- `--configuration <name>`
- `--framework <tfm>` / `--target-framework <tfm>`
- `--runtime <rid>`
- `--profile-name <name>`
- `--define`, `--undefine`, `--define-file`, `--reference`, `--exclude`は反復可能。
- `--generated-source physical|all|none`。現在は`physical`のみ実装。
- `--rebuild`, `--verbose`, `--diagnostics`
- `--unity-editor`はPhase 3予約。

`obj` path segmentは大文字小文字を区別せず常時除外され、`bin`や生成コードは既定で含まれます。進捗と警告はstderr、検索結果はstdoutへ出します。

## Queries

> **Superseded where conflicting:** use the schema-version-5 matrix and grammar
> in the final section. This section preserves earlier command examples only.

```powershell
csindex symbol find "Player::Play"
csindex async tree "Game.Player::Play()"
csindex callers tree "Game.Player::Play()"
csindex source show "Game.Player::Play()"
csindex source search --include "PrintVar("
csindex symbol list
csindex symbol list --kind lambda --async-involved --output-format json
csindex definition "Player::Play()"
csindex definition --at "src\Player.cs:120:17"
csindex references "Player::Play(string)"
csindex callers "BaseClass::Run()" --dispatch virtual
csindex callees "Game.Player::Execute()"
csindex callees "Game.Player::Execute()" --exclude-lambda-calls
csindex overrides "BaseClass::Run()"
csindex conditions
```

共通の出力option（commandごとの受理範囲は後述のschema-version-5 matrixに従う）:

- `--db <path>`。省略時はcurrent directoryの`.csindex/index.sqlite`。
- `--profile <name>`
- `--output-format <format>`。通常のquery/source/conditionsは`table|json`、
  `async tree`は`tree|line|json`、`callers tree`は`tree|mermaid|json`。
- `-o <path>` / `--output-file <path>`。結果payloadの出力先を指定する。
- `--exclude-generated` / `--only-generated`
- `--require-single`
- callers固有: `--dispatch static|virtual|all`、`--caller-scope direct|containing|both`

検索構文は`[namespace.]type::method[(parameter-types)]`です。namespace省略は全候補へ展開し、parameter list省略は全overload、`()`は引数なしだけを選びます。C# keyword型は`System.*`へ正規化し、大文字小文字は区別します。

### Override-aware method search

`--include-overrides` is disabled by default. It is accepted only by these
five method-query forms:

```powershell
csindex symbol find "IPlayable::Play()" --include-overrides
csindex definition "IPlayable::Play()" --include-overrides
csindex references "IPlayable::Play()" --include-overrides
csindex callers "IPlayable::Play()" --include-overrides
csindex callees "IPlayable::Play()" --include-overrides
```

The option requires a method query. `--kind all`または`--kind method`は併用
できるが、`--kind lambda`との併用は引数エラーである。type-only query such as
`csindex symbol find "IPlayable" --include-overrides`, and the
`definition --at` form, fail with:

```text
--include-overrides requires a method query.
```

`symbol list`, `overrides`, `conditions`, and `index` reject the option as an
unknown option. `overrides --kind lambda`も明示的な非適用エラーとなる。Without
the option, every command keeps its exact-method lookup behavior.

Expansion returns only real declarations and is descendant-only. An interface
query is scoped to that exact contract: `IPlayable::Play()` includes the
interface method and indexed real implementations such as `Pianist::Play()`,
`ProPianist::Play()`, and `Game::Play()`. It does not use a derived-interface
root to include types that implement only the base interface.

A concrete query follows only its own override branch. For example,
`Pianist::Play()` includes `Pianist::Play()` and `ProPianist::Play()`, but not
the sibling `Game::Play()` implementation. It does not expand upward to an
interface or base contract, cross to sibling branches, or infer runtime
targets through receiver-value flow. Consequently, a concrete `references`
or `callers` search excludes a call site statically bound to
`IPlayable::Play()`; search `IPlayable::Play()` to include that call site.

For an inherited alias, `D1::Play()` resolves to its real inherited
declaration, for example `InheritedBase::Play()`, then expands only within
the `D1` descendant branch. Its output can include `D2::Play()` but never a
synthetic `D1::Play()` symbol or an override from another branch. A declared
`new` member remains its own real declaration and is not an override.

`symbol find`、`symbol list`、`definition`、`references`、`callers`、`callees`、`overrides`では、`--short-names`によりtable出力とJSONの`displayName`/`signature`にある所有者namespaceだけを省略できます。戻り値型、引数型、conversion target、explicit-interface payloadは短縮しません。既定は完全修飾表示です。

### `symbol list`

`symbol list`は、現在のprofile内の関数symbolを一覧します。既定では`method`と`lambda`の両方を返し、single-line tableの各recordは署名だけを出力します。該当する非同期解析の注釈は署名fieldに含まれます。

- `--kind all|method|lambda`は結果を指定したkindだけに限定します。既定の`all`は
  kind述語を追加せず、`symbol list`では既存どおりmethodとlambdaの両方を返します。
- `--async-status all|async|sync`は保存済みの直接`AsyncRole`で絞り込みます。
  `async`と`sync`はmethod/lambdaだけを候補にし、型などを`sync`へ混入させません。
- `--async-involved`は`asyncInvolvementDepth`を持つsymbolだけを返します。直接の非同期起点もdepth `0`として含まれます。
- `--short-names`は表示パスの所有者namespaceだけを省略します。たとえば`Alpha.AClass::Play()`は`AClass::Play()`として表示されます。

JSONは`{ "profile": "...", "symbols": [...] }`です。各symbolには`id`、`stableKey`、`kind`、`displayName`、`signature`、`fullyQualifiedName`、`namespaceName`、`typeSimpleName`、`parameters`、`location`、`isGenerated`、`assemblyName`、`accessibility`、`isStatic`、`isAsync`、`asyncRole`、`isAsyncInvolved`、`asyncInvolvementDepth`、`returnType`、`methodKind`、`sourceAvailable`を出力します。`--short-names`を指定しても`fullyQualifiedName`、`stableKey`、`namespaceName`、`parameters`、`returnType`などのcanonical JSON fieldは変更されず、`displayName`と`signature`の所有者namespaceだけが省略されます。

### `callees`とラムダ呼び出し

`callees`は既定で、指定したmethod自身の呼び出しに加えて、その内部にあるラムダとさらにネストしたラムダの呼び出しも再帰的に返します。method本体だけの直接呼び出しに限定するには`--exclude-lambda-calls`を指定します。

```powershell
csindex callees "Alpha.DescendantCallees::Execute()"
csindex callees "Alpha.DescendantCallees::Execute()" --exclude-lambda-calls
```

### 非同期解析情報の出力

既存の検索コマンドの結果へ非同期解析情報を追加します。schema version 5では、これとは別に
`csindex async tree`が永続化された非同期経路を表示します。`--async-status`は直接の
`AsyncRole`を対象とし、`symbol list --async-involved`は派生値
`AsyncInvolvementDepth != null`を対象とします。両方を指定した場合はANDで結合します。
したがって、直接roleを持たず非同期起点へ到達できる関数は
`--async-status sync --async-involved`に一致します。

symbolを含むJSON objectには次のpropertyを出力します。

- `asyncRole`: `AsyncRole` flagsの文字列表現。例: `"DeclaredAsync, ReturnsAwaitable"`
- `isAsyncInvolved`: `asyncInvolvementDepth`がnullでないとき`true`
- `asyncInvolvementDepth`: 非同期起点までの最短呼び出し辺数。起点は`0`、非関与は`null`

callを含むJSON objectには`asyncUsageKind`を出力します。値は`None`、`Awaited`、`Forwarded`、`Stored`、`Passed`、`Discarded`、`Unobserved`のいずれかです。

`symbol find`のtable出力では、`WriteSymbols`が出力する一致symbol行に限り、symbolのロールが`None`かつdepthがnullの場合を除いて表示名の後へ次の補足を付けます。`definition`の定義位置行や`callers`のeffective caller行には、このsymbol用補足を付けません。

```text
[async: DeclaredAsync, ReturnsAwaitable; depth: 0]
```

call行では既存の`[ReferenceKind, ResolutionStatus]`の後へ`[Awaited]`のような`AsyncUsageKind`を付けます。

## Exit codes

- `0`: success（警告を含む部分解析も原則success）
- `2`: invalid arguments/query
- `3`: fatal input/analysis/cancellation failure
- `4`: SQLite/schema failure
- `5`: `--require-single` failure

## Previous query contract

The former flat matcher/schema-4 command contract is superseded. Its active
source-layout and graph guarantees are incorporated into the canonical
schema-version-5 section below; historical wording remains available in Git
history.

---

## Canonical query interface (schema version 5)

### Quick start

```powershell
csindex index Game.sln
csindex symbol find "Game::Player::Run()"
csindex definition "Game::Player::Run().Local()"
csindex source search --include-literal "CancellationToken"
csindex callers tree "Game::Player::Run()" --depth 3
```

All queries require a schema-version-5 database. If an older database is
opened, CsIndex leaves it unchanged and reports how to rebuild: delete or
rename the old file, or select a new `--db` path, and explicitly run
`csindex index`.

### Symbol-path grammar

```text
csharp:   Game.Core.Player.Inventory::Load(int).Validate()
explicit: Game.Core::Player.Inventory::Load(int).Validate()
```

Csharp form has one top-level `::` separator and performs documented suffix
matching across the possible namespace/type boundary. Explicit form has two
top-level separators and fixes that boundary exactly. `Player::Run()` omits the
namespace and searches all namespaces. `global::Program::<top-level-statements>`
selects the empty/global namespace exactly; `@global` is a literal identifier.

A whole hierarchy `*` component matches one level and a whole `**` component
matches zero or more; an embedded `*` stays within its component. Executable
children use `.` and immediate containment:

```text
Game::Player::Run().Local().<lambda#1>
```

The old `Game::Player::Run()::<lambda#1>` child spelling is rejected. An empty
field/segment, malformed delimiter, or zero/three top-level separators is also
rejected.

Type generic arity is exact: `Repository` is non-generic, `Repository<T>` is
arity 1, and `Repository<T,U>` is arity 2. Use a deliberate type glob such as
`Repository*` to span arities.

Generic-list and parameter-list omission are independent:

```text
Method          any generic arity, any parameters
Method<T>       arity 1, any parameters
Method()        non-generic, zero parameters
Method<T>()     arity 1, zero parameters
Method(int)     non-generic, exact int parameter
Method<T>(T)    arity 1, exact canonical parameter list
```

Bare `Method` is the broad all-arity/all-overload form. Once a parameter list
is present, omitted `<...>` means non-generic. Constructed invocation notation
such as `Method<System.String>` is invalid.
Aliases are accepted; concrete non-alias type output is fully qualified.

Special callable forms are:

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

For anonymous queries, `n` is a positive integer or `*`. The complete operator
token list and copyable example for every form are in `csindex --help-verbose`
and `SPEC.md` section 34.

Explicit-interface accessors keep the accessor tag and use the fully qualified
interface member as its payload:

```text
Game::Player::[get:Game.Contracts.IPlayer.Name]()
Game::Player::[set:Game.Contracts.IPlayer.Name](string)
Game::Player::[add:Game.Contracts.IEvents.Changed](System.EventHandler)
```

### Typed conditions

All rows below are repeatable except the case selector:

| Domain | Glob | Literal | Regex | Case selector |
| --- | --- | --- | --- | --- |
| namespace | `--namespace` | `--namespace-literal` | `--namespace-regex` | `--namespace-case strict|ignore` |
| type | `--type` | `--type-literal` | `--type-regex` | `--type-case strict|ignore` |
| method | `--method` | `--method-literal` | `--method-regex` | `--method-case strict|ignore` |
| file | `--file` | `--file-literal` | `--file-regex` | `--file-case strict|ignore` |
| include source | `--include` | `--include-literal` | `--include-regex` | `--source-case strict|ignore` |
| exclude source | `--exclude` | `--exclude-literal` | `--exclude-regex` | `--source-case strict|ignore` |

Case defaults independently to `strict`. Same-domain namespace/type/method/file
conditions are ordered OR alternatives evaluated in CLI order; different
domains AND. Every include must match, while any exclude rejects. File/source
conditions must pass on one physical declaration; partial rows then project to
one logical result. Source matching is unanchored over normalized source.
Invalid regex or timeout emits no partial payload.

The removed bare `--regex` and `--ignore-case` switches are always errors.

### Common query options

The common presentation/path group `Q` is:

```text
--db <path>
--profile <name>
--output-format <command-specific-value>
-o <path> | --output-file <path>
--symbol-path-style csharp|explicit
--short-names
--base-dir <path>
--path-style absolute|relative
--help
--help-verbose
--verbose                     valid on queries only with help
```

The selection group `C` contains every typed condition and case option above,
plus:

```text
--kind all|method|lambda
--async-status all|async|sync
```

`--kind` and `--async-status` describe the direct root. Initializers and
top-level statements participate in `all`. `symbol list --async-involved` is a
separate transitive filter.

### Exact accepted-option matrix

An option not present in a row is rejected for that command.

| Scope | Accepted options |
| --- | --- |
| global help | `db`, `profile`, `output-format`, `output-file`, `help`, `help-verbose`, `verbose` |
| `index` | `db`, `mode`, `solution`, `configuration`, `framework`, `target-framework`, `runtime`, `profile-name`, `define`, `undefine`, `define-file`, `reference`, `unity-editor`, `exclude`, `generated-source`, `rebuild`, `verbose`, `diagnostics`, `help`, `help-verbose` |
| `symbol find` | `Q + C + require-single + include-overrides + show-source + source-layout` |
| `symbol list` | `Q + C + async-involved` |
| `source search` | `Q + C + source-layout` |
| `source show` | `Q + C + source-layout` |
| `definition <selector>` | `Q + C + require-single + include-overrides` |
| `definition --at` | `Q + at` only; no root conditions |
| `references` | `Q + C + exclude-generated + only-generated + require-single + include-overrides` |
| `callers` | `Q + C + exclude-generated + only-generated + require-single + include-overrides + dispatch + caller-scope` |
| `callees` | `Q + C + exclude-generated + only-generated + require-single + include-overrides + exclude-lambda-calls` |
| `overrides` | `Q + C + require-single`; no `include-overrides` |
| `async tree` | `Q + C + max-nodes` |
| `callers tree` | `Q + C + depth + max-nodes` |
| `conditions` | `db`, `profile`, `output-format`, `output-file`, `help`, `help-verbose`, `verbose`, `base-dir`, `path-style` |

The symbols in the table are option names without their leading `--`. `Q` and
`C` expand exactly to the groups above; verbose help prints a fully expanded,
sorted `Accepted options:` list for machine comparison.

### Command behavior

| Command | Root/selection rule | Output formats |
| --- | --- | --- |
| `symbol find [selector]` | selector or explicit condition/kind/async required; zero matches succeeds; optional `--require-single` | `table`, `json` |
| `symbol list` | no selector; optional conditions; zero matches succeeds | `table`, `json` |
| `source search` | no selector; explicit condition/kind/async required; zero matches succeeds | `table`, `json` |
| `source show <selector>` | exactly one source-backed logical root | `table`, `json` |
| `definition <selector>` | one or more roots; optional `--require-single` | `table`, `json` |
| `definition --at <path:line:column>` | physical position mode; no selector conditions | `table`, `json` |
| `references <selector>` | one or more roots; optional `--require-single` | `table`, `json` |
| `callers <selector>` | one or more roots; optional `--require-single` | `table`, `json` |
| `callees <selector>` | one or more roots; optional `--require-single` | `table`, `json` |
| `overrides <selector>` | method roots only; optional `--require-single` | `table`, `json` |
| `async tree <selector>` | exactly one source-backed logical root | `tree`, `line`, `json` |
| `callers tree <selector>` | exactly one source-backed logical root | `tree`, `mermaid`, `json` |
| `conditions` | no selector/root conditions | `table`, `json` |

`--source-layout single-line|multi-line` is available only on the rows that
list it. `symbol find --source-layout` also requires `--show-source`.

`--include-overrides` is limited to the five rows that list it and requires one
exact wildcard-free method selector. It rejects condition-only, lambda,
initializer, top-level, or wildcard roots. Root conditions, direct kind/async,
and generated-root filters run before cardinality. `--require-single` returns
exit 5 unless exactly one logical root remains. Override expansion then occurs
before command work. Root filters never remove secondary relation/graph rows.

`--exclude-generated` and `--only-generated` are mutually exclusive. Their root
effect is applied before cardinality; the relevant command's returned-edge
generated filtering still applies.

### Source layout and graph bounds

`single-line` source table output uses fixed physical records: `symbol find`
has signature/location fields; `symbol find --show-source`, `source show`, and
`source search` add normalized source; `symbol list` has only the signature.
TAB, CRLF, CR, LF, U+0085, U+2028, and U+2029 are replaced with ASCII space at
table-render time so each record stays on one physical line. `multi-line`
retains headings and an indented source line. This presentation conversion does
not alter stored normalized source/hash, matching, or JSON.

`async tree --max-nodes` defaults to 500 and must be positive. It follows the
one persisted, validated next-hop chain and marks truncation in every format.
`callers tree --depth` defaults to 3 (`0` is unlimited), while `--max-nodes`
defaults to 500 and must be positive. Caller traversal is cycle-safe, retains
eligible cycle/cross edges at a finite boundary, and represents one shared
node/edge set in tree, Mermaid, and JSON. Neither graph infers delegate
`Invoke`, event/callback, reflection, receiver data flow, or runtime-dispatch
execution. Root conditions never prune stored path/caller descendants.

### Logical partial declarations

A partial definition and implementation are one query candidate. Their physical
declaration roles are exactly:

```text
partial-definition
partial-implementation
```

Ordinary declarations use `ordinary`. The implementation is preferred when it
exists. `definition` can emit both role rows; ordinary symbol/relation/graph
output emits the logical symbol once and uses the preferred location. File and
source conditions may select one physical row without splitting logical
cardinality.

### Portable paths and presentation

The database stores forward-slash paths relative to the indexed storage root
and an anchor from the database directory to that root. It never stores a
machine-specific rooted source path. `--base-dir` changes query-time
reconstruction only and never rewrites the database.

```text
--path-style absolute     reconstruct rooted locations (default)
--path-style relative     emit paths relative to the effective base
--symbol-path-style csharp
--symbol-path-style explicit
--short-names             omit only the displayed owner namespace
```

`definition --at` accepts rooted or effective-base-relative input. A relocated
database/source layout works when their relative relationship is preserved or
`--base-dir` names the new root. A missing or unreadable reconstructed source is
reported only by a command that must read it.

Presentation options do not change identity or canonical ordering.

### Help

Normal help is concise. Full reference help is byte-identical for these two
spellings at global scope and every recognized command:

```text
--help --verbose
--help-verbose
```

Help validates tokenization and the command's allowed-option set, then exits
before required positional validation, database/source opening, or output-file
creation. Unknown commands/options remain errors. `index --verbose` without
help enables progress; query `--verbose` is help-only.

### Output and failures

Payload goes to stdout unless `--output-file` is supplied. A file payload is
rendered to an owned same-directory temporary file, flushed, cancellation
checked, and committed atomically. Any query, regex, path, formatting, flush,
cancellation, replace, or commit failure preserves an existing destination and
does not expose a partial committed payload.

```text
0  success (including valid empty list/search results)
2  invalid arguments or query
3  input, analysis, cancellation, source, or output failure
4  SQLite or incompatible-schema failure
5  --require-single failure
```
