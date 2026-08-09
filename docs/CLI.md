# CLI

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

```powershell
csindex symbol find "Player::Play"
csindex async tree "Game.Player::Play()"
csindex callers tree "Game.Player::Play()"
csindex source show "Game.Player::Play()"
csindex source search --include "PrintVar("
csindex symbol list
csindex symbol list --kind lambda --async-involved --output json
csindex definition "Player::Play()"
csindex definition --at "src\Player.cs:120:17"
csindex references "Player::Play(string)"
csindex callers "BaseClass::Run()" --dispatch virtual
csindex callees "Game.Player::Execute()"
csindex callees "Game.Player::Execute()" --exclude-lambda-calls
csindex overrides "BaseClass::Run()"
csindex conditions
```

Legacy flat-query options (where accepted; nested-command availability is
specified exactly in the schema-v4 section below):

- `--db <path>`。省略時はcurrent directoryの`.csindex/index.sqlite`。
- `--profile <name>`
- `--output table|json`
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

The option requires a method query. A type-only query such as
`csindex symbol find "IPlayable" --include-overrides`, and the
`definition --at` form, fail with:

```text
--include-overrides requires a method query.
```

`symbol list`, `overrides`, `conditions`, and `index` reject the option as an
unknown option. Without the option, every command keeps its exact-method
lookup behavior.

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

`symbol find`、`symbol list`、`definition`、`references`、`callers`、`callees`、`overrides`では、`--short-names`によりtable出力とJSONの`displayName`からnamespaceを省略できます。既定は完全修飾表示です。

### `symbol list`

`symbol list`は、現在のprofile内の関数symbolを一覧します。既定では`method`と`lambda`の両方を返し、各table行には定義位置と、該当時は非同期解析の注釈を出力します。

- `--kind method|lambda`は結果を指定したkindだけに限定します。
- `--async-involved`は`asyncInvolvementDepth`を持つsymbolだけを返します。直接の非同期起点もdepth `0`として含まれます。
- `--short-names`は表示名だけを短縮します。たとえば`Alpha.AClass::Play()`は`AClass::Play()`として表示されます。

JSONは`{ "profile": "...", "symbols": [...] }`です。各symbolには`id`、`stableKey`、`kind`、`displayName`、`signature`、`fullyQualifiedName`、`namespaceName`、`typeSimpleName`、`parameters`、`location`、`isGenerated`、`assemblyName`、`accessibility`、`isStatic`、`isAsync`、`asyncRole`、`isAsyncInvolved`、`asyncInvolvementDepth`、`returnType`、`methodKind`、`sourceAvailable`を出力します。`--short-names`を指定しても`fullyQualifiedName`、`stableKey`、`namespaceName`、`parameters`、`returnType`などのcanonical JSON fieldは変更されず、短縮されるのは`displayName`と`signature`だけです。

### `callees`とラムダ呼び出し

`callees`は既定で、指定したmethod自身の呼び出しに加えて、その内部にあるラムダとさらにネストしたラムダの呼び出しも再帰的に返します。method本体だけの直接呼び出しに限定するには`--exclude-lambda-calls`を指定します。

```powershell
csindex callees "Alpha.DescendantCallees::Execute()"
csindex callees "Alpha.DescendantCallees::Execute()" --exclude-lambda-calls
```

### 非同期解析情報の出力

既存の検索コマンドの結果へ非同期解析情報を追加します。schema v4では、これとは別に
`csindex async tree`が永続化された非同期経路を表示します。`symbol list --async-involved`を
除き、既存のフラット検索コマンドには新たな非同期専用フィルターは追加していません。

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

## Symbol, source, and graph commands (schema v4)

All commands in this section accept `--db <path>` (default:
`.csindex/index.sqlite` below the current directory) and `--profile <name>`.
`--short-names` is presentation-only: it shortens displayed names and
signatures, never canonical stored values or matching semantics. Every command
accepts `--help`; command help lists the complete accepted grammar, all output
values, and defaults.

### `symbol find`

```text
csindex symbol find [<pattern>] [options]
```

`<pattern>` is optional only when at least one of `--namespace`, `--type`, or
`--method` is present. It accepts at most one positional value. The supported
options are:

- `--namespace <pattern>`, `--type <pattern>`, and `--method <pattern>`;
  supplied name filters are combined with AND semantics.
- `--kind method|lambda`.
- `--regex`, which makes every supplied name pattern a culture-invariant .NET
  regular expression with a two-second timeout. Matching is case-sensitive by
  default; `--ignore-case` adds culture-invariant .NET regex ignore-case for
  name filters and ordinal ignore-case for source filters. In regex mode `*`
  remains regex syntax; it is not a wildcard option applied in addition to
  regex.
- Without `--regex`, `*` matches zero or more characters and all other
  characters are literals. The existing exact resolver is used only for a
  positional pattern with neither `*` nor `::<lambda#`, and no matching
  modifier (`--regex`, `--ignore-case`, component filters, `--kind`,
  `--include`, or `--exclude`). `--show-source` is presentation-only and does
  not disqualify that exact path. A method pattern without a parameter list
  matches its overloads; a parameter list matches the full signature. Lambda
  suffix forms (`::<lambda#N>`, owner suffixes, and full lambda names) match
  canonical lambda display names.
- Repeatable `--include <text>` and `--exclude <text>`. Excludes are ORed and
  evaluated before ANDed includes. A source condition limits candidates to
  source-backed executable symbols. `--show-source` only controls
  presentation; it does not add a filter.
- `--output table|json` (default `table`), `--require-single`, and
  `--short-names`.

`--include-overrides` remains available only for its legacy exact method-query
mode. It cannot be combined with component, kind, regex, case, or source
search options; `--show-source` is allowed. An invalid request reports one of
the following command errors (with the standard `Argument error:` prefix and
usage hint):

```text
symbol find accepts at most one positional pattern.
symbol find requires a pattern or at least one --namespace, --type, or --method condition.
Unknown symbol kind: <value>. Use method or lambda.
--include-overrides cannot be combined with component, kind, regex, case, or source search options.
```

Invalid regular expressions and regex timeouts use the `Query error:` path and
identify the affected pattern. Unknown or unsupported options use
`Unknown option(s): ...`.

Table output starts with `Query matched <count> symbol(s):`, uses C#-like
declaration ordering (`accessibility static async return-type name`), and
prints `source: <normalized-source>` only when `--show-source` is set. JSON is
`{ "profile": "...", "matched": [...] }`; each symbol object has the fields
listed for `symbol list`, plus `normalizedSource` only when source presentation
was requested.

Local functions, lambdas, and static constructors do not display an
accessibility modifier. Constructors do not display a return type; accessors,
operators, and conversions display only the fields applicable to their
declaration kind.

### Source commands

```text
csindex source show <symbol> [--output table|json] [--short-names]
csindex source search (--include <text> | --exclude <text>)...
    [--ignore-case] [--output table|json] [--short-names]
```

`source show` returns all source-backed executable matches (including matching
overloads) and always presents their normalized source. Metadata-only symbols
and non-executable matches are not returned. `source search` accepts no
positionals and requires at least one include or exclude term. Its error text
is exactly:

```text
source search does not accept positional arguments.
source search requires at least one include or exclude condition.
```

Both commands use table output by default. Their JSON shape is the same as
`symbol find` and contains `normalizedSource`; table rows include a signature,
location, and an indented `source:` line. Source matching is ordinal and
case-sensitive by default, or ordinal case-insensitive with `--ignore-case`.
Normalized source removes layout outside literal-token text while preserving
each literal token's `Text`; a multiline raw literal can therefore retain
embedded newlines in the presented source.

### Async shortest path

```text
csindex async tree <symbol> [--output tree|line|json] [--max-nodes 500]
    [--short-names]
```

The root must resolve to exactly one source-backed method through the exact
query parser. `tree` is the default output; `line` uses exactly ` -> ` between
path nodes; `json` emits `profile`, `found`, `truncated`, `root`, and `nodes`.
An async origin prints as `async <name>`. If no origin is reachable, tree and
line output are exactly `No reachable asynchronous function: <root>` and JSON
has `found: false` with an empty `nodes` array. The root counts toward the
positive `--max-nodes` limit (default `500`); a cut path appends
`<truncated>` in tree/line and has `truncated: true` in JSON.

### Caller tree

```text
csindex callers tree <symbol> [--depth 3] [--max-nodes 500]
    [--output tree|mermaid|json] [--short-names]
```

The root has depth zero. `--depth 0` removes the depth bound; otherwise the
default is `3`. The positive `--max-nodes` default is `500` and includes the
root. `tree` is the default and can include an `Additional edges:` section for
non-spanning/cycle edges. `mermaid` emits `flowchart TD`, `n<symbol-id>` node
IDs, escaped displayed-name labels (canonical by default and shortened by
`--short-names`), caller-to-callee arrows, and `%% truncated` when cut. JSON
emits `profile`, `truncated`, a `root` symbol object, `nodes` with `depth`, and
`edges` with caller/callee symbol IDs.

Caller traversal is breadth-first, profile-scoped, and ordered by display
name, source path, source offset, and ID. It uses resolved invocation and
object-creation edges, includes only source-backed methods/lambdas, and
excludes `System` and `System.*` symbols. It does not invent a call edge from
a lambda owner and does not infer delegate `Invoke`, event, callback, or
runtime dispatch execution.

Graph validation errors include:

```text
This command requires exactly one symbol query.
Depth must be an integer.
Depth cannot be negative.
Maximum node count must be an integer.
Maximum node count must be positive.
Graph queries require an exact source-backed method query.
No source-backed method matches graph query: <query>
Graph query is ambiguous for '<query>'. Candidates: <canonical candidates>
Unknown async tree output: <value>. Use tree, line, json.
Unknown callers tree output: <value>. Use tree, mermaid, json.
```

Candidate order is deterministic. If two candidates have the same canonical
display name, each is disambiguated as
`<display-name> [document: <path>; symbol ID: <id>]`.

Corrupt persisted async-path data is a database error rather than a silently
reselected path; the message begins `Async path integrity failure:`.
