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

## シンボル、ソース、グラフコマンド（schema v4）

本節のコマンドは`--db <path>`（既定: current directory配下の`.csindex/index.sqlite`）と`--profile <name>`を受け付けます。`--short-names`は表示専用で、表示名、戻り値型、引数型だけを短縮し、保存済みcanonical値や検索意味を変更しません。各コマンドの`--help`は、受理する完全な構文、output値、既定値を表示します。

### `symbol find`

```text
csindex symbol find [<pattern>] [options]
```

`<pattern>`は位置引数を最大1つ受け付けます。省略する場合は、`--namespace`、`--type`、`--method`の少なくとも1つが必要です。

- `--namespace <pattern>`、`--type <pattern>`、`--method <pattern>`はANDで結合します。
- `--kind method|lambda`.
- `--regex`は、すべての名前patternをculture-invariantな.NET正規表現として2秒のtimeout付きで評価します。既定はcase-sensitiveです。`--ignore-case`指定時、名前条件はculture-invariant ignore-case、ソース条件はordinal ignore-caseになります。regex modeの`*`は正規表現の一部であり、wildcardとして重ねて解釈しません。
- `--regex`がなければ`*`だけが0文字以上に一致し、それ以外はliteralです。例: `*.Gamer::Play`、`Tokyo.*::Play`、`Tokyo.Gamer::P*l*y`。
- 既存のexact resolverを使うのは、位置引数があり、`*`と`::<lambda#`を含まず、`--regex`、`--ignore-case`、component条件、`--kind`、`--include`、`--exclude`を持たない場合だけです。`--show-source`は表示専用なのでexact pathを妨げません。引数リストを省略したmethod patternはoverloadを列挙し、引数リストを指定したpatternは完全signatureを照合します。
- ラムダは`::<lambda#1>`、`Function()::<lambda#2>`、完全表示名、`::<lambda#*>`で検索できます。suffix、owner suffix、完全名のいずれもcanonical lambda display nameへ照合します。
- `--include <text>`と`--exclude <text>`は複数回指定できます。excludeはORで先に短絡評価し、それを通過した候補にincludeをANDで評価します。ソース条件がある場合はsource-backed実行可能シンボルだけが候補です。`--show-source`は表示だけを変更し、filterを追加しません。
- `--output table|json` (default `table`), `--require-single`, and
  `--short-names`.

`--include-overrides`はlegacy exact method-query modeだけで使用できます。component、kind、regex、case、source検索optionとは併用できませんが、`--show-source`は併用できます。不正な指定は`Argument error:` prefixとusage hintを伴って、次のエラーを返します。

```text
symbol find accepts at most one positional pattern.
symbol find requires a pattern or at least one --namespace, --type, or --method condition.
Unknown symbol kind: <value>. Use method or lambda.
--include-overrides cannot be combined with component, kind, regex, case, or source search options.
```

無効な正規表現とtimeoutは、対象patternを示す`Query error:`になります。未知または非対応optionは`Unknown option(s): ...`になります。

table出力は`Query matched <count> symbol(s):`で始まり、`accessibility static async return-type name(parameters)`の順でC#宣言に近い署名を表示します。`source: <normalized-source>`は`--show-source`指定時だけ出力します。JSONは`{ "profile": "...", "matched": [...] }`で、各symbol objectは`symbol list`と同じcanonical fieldを持ち、ソース表示を要求した場合だけ`normalizedSource`を追加します。

ローカル関数、ラムダ、static constructorはaccessibilityを表示しません。コンストラクターは戻り値を表示せず、アクセサー、演算子、変換演算子も宣言kindに適用できるfieldだけを表示します。

### Source commands

```text
csindex source show <symbol> [--output table|json] [--short-names]
csindex source search (--include <text> | --exclude <text>)...
    [--ignore-case] [--output table|json] [--short-names]
```

`source show`は一致するすべてのsource-backed実行可能シンボルとoverloadを返し、常に正規化ソースを表示します。対象はメソッド、コンストラクター、ローカル関数、ラムダ、アクセサー、演算子、変換演算子です。metadata-onlyまたは非実行可能symbolは返しません。`source search`は位置引数を受け付けず、少なくとも1つのincludeまたはexcludeを必須とします。

```text
source search does not accept positional arguments.
source search requires at least one include or exclude condition.
```

両コマンドの既定出力はtableです。JSON shapeは`symbol find`と同じで`normalizedSource`を含みます。tableは署名、位置、indentした`source:`行を表示します。ソース照合は既定でordinal case-sensitive、`--ignore-case`指定時はordinal ignore-caseです。正規化ではliteral token外のlayout、コメント、directive、inactive branchを除きますが、各literal tokenの`Text`は保持するため、複数行raw literalの内部改行は表示結果に残り得ます。

### Async shortest path

```text
csindex async tree <symbol> [--output tree|line|json] [--max-nodes 500]
    [--short-names]
```

rootはexact query parserによってsource-backed method 1件へ解決される必要があります。既定出力は`tree`です。`line`はnode間を厳密に` -> `で接続し、`json`は`profile`、`found`、`truncated`、`root`、`nodes`を出力します。非同期起点は`async <name>`と表示します。到達可能な起点がなければ、tree/lineは`No reachable asynchronous function: <root>`、JSONは`found: false`と空の`nodes`を返します。正の`--max-nodes`は既定500でrootを含み、打ち切り時はtree/lineへ`<truncated>`、JSONへ`truncated: true`を出力します。

表示経路はindex時に決定した1つの最短next-hop chainであり、query時に別経路を再選択しません。同距離の候補が複数あっても最初に決定的順序で記録した1経路だけを返します。root自身が非同期起点なら1nodeです。宣言`async`、Task/ValueTask/UniTask系、非同期streamなどのRoslyn direct roleで起点を判定し、名前の`Async` suffixだけでは判定しません。

### Caller tree

```text
csindex callers tree <symbol> [--depth 3] [--max-nodes 500]
    [--output tree|mermaid|json] [--short-names]
```

rootのdepthは0です。`--depth 0`は深度制限なし、それ以外の既定は3です。正の`--max-nodes`は既定500でrootを含みます。既定の`tree`はspanning treeを表示し、non-spanning/cycle edgeがあれば`Additional edges:`を追加します。`mermaid`は`flowchart TD`、`n<symbol-id>`のnode ID、escape済み表示名label（既定canonical、`--short-names`で短縮）、callerからcalleeへの矢印、打ち切り時の`%% truncated`を出力します。JSONは`profile`、`truncated`、`root` symbol、`depth`付き`nodes`、caller/callee symbol IDを持つ`edges`を出力します。

caller探索はprofile内のBFSで、同じdepthではdisplay name、source path、source offset、ID順です。解決済みinvocation/object-creation edgeを使用し、source-backed method/lambdaだけを含め、metadata-only・外部libraryと`System`/`System.*`を除外します。cycleでもnodeを重複させず、両端が含まれるedgeを保持します。ラムダownerからcall edgeを合成せず、delegate `Invoke`、event、callback、reflection、runtime dispatchの実行を推論しません。

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

候補順は決定的です。2件以上の候補が同じcanonical display nameなら、`<display-name> [document: <path>; symbol ID: <id>]`として区別します。

保存済みasync pathが破損している場合は、黙って別経路を選ばずdatabase errorにし、messageは`Async path integrity failure:`で始めます。profile不存在、非対応schema、破損DBも明示的なerrorとし、終了コードは本書の「Exit codes」に従います。
