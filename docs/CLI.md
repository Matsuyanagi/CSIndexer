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

共通の出力option（受理するcommandは後述のschema v4節のmatrixに従う）:

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

`symbol find`、`symbol list`、`definition`、`references`、`callers`、`callees`、`overrides`では、`--short-names`によりtable出力とJSONの`displayName`からnamespaceを省略できます。既定は完全修飾表示です。

### `symbol list`

`symbol list`は、現在のprofile内の関数symbolを一覧します。既定では`method`と`lambda`の両方を返し、single-line tableの各recordは署名だけを出力します。該当する非同期解析の注釈は署名fieldに含まれます。

- `--kind all|method|lambda`は結果を指定したkindだけに限定します。既定の`all`は
  kind述語を追加せず、`symbol list`では既存どおりmethodとlambdaの両方を返します。
- `--async-status all|async|sync`は保存済みの直接`AsyncRole`で絞り込みます。
  `async`と`sync`はmethod/lambdaだけを候補にし、型などを`sync`へ混入させません。
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

## シンボル、ソース、グラフコマンド（schema v4）

本節のコマンドは`--db <path>`（既定: current directory配下の`.csindex/index.sqlite`）と`--profile <name>`を受け付けます。`--short-names`は表示専用で、表示名、戻り値型、引数型だけを短縮し、保存済みcanonical値や検索意味を変更しません。

### 共通の実行可能target filter

`--kind all|method|lambda`と`--async-status all|async|sync`は、method/lambdaを候補またはrootとして解決する次のcommandで受理します。

| command | `--kind` / `--async-status`の適用先 |
| --- | --- |
| `symbol find`、`symbol list` | 一致/一覧のsymbol |
| `source show`、`source search` | source-backed実行可能symbol |
| `definition`、`definition --at` | 解決対象とdefinition結果 |
| `references`、`callers` | 検索対象となるcallee target |
| `callees` | 検索対象となるcaller root |
| `async tree`、`callers tree` | 一意に解決するsource-backed executable root |
| `overrides` | method root。`--kind lambda`は非適用エラー |

`index`と`conditions`はfunction targetを持たないため、両filterを未知optionとして拒否します。filterはtarget/rootの解決にだけ適用し、`references`/`callers`が返すcaller、`callees`が返すcallee、tree内の途中nodeやedgeを一律に削除しません。

既定はどちらも`all`です。`--kind all`はkind predicateを追加しないため、exact `symbol find`が従来返していた型などを排除しません。`--async-status async`は`AsyncRole != None`、`sync`は`AsyncRole == None`のmethod/lambdaに限定し、`all`はdirect async predicateを追加しません。未知値は次のusage errorです。

```text
Unknown symbol kind: <value>. Use all, method, or lambda.
Unknown async status: <value>. Use all, async, or sync.
```

ラムダtargetは、各対象commandで次のcanonical grammarを解決します。

```text
::<lambda#1>
Owner()::<lambda#2>
Namespace.Type::Owner()::<lambda#2>
::<lambda#*>
```

複数targetを許すcommandは決定的順序ですべて処理します。`async tree`と`callers tree`は1件のsource-backed executable rootを必要とし、0件または複数件なら候補を含む`Query error`にします。同じcanonical display nameの候補はdocument pathとsymbol IDで区別します。

`--include-overrides`はまず実在するmethod targetをdescendant方向に展開し、その後にkind/direct-async filterを適用します。`--kind all`と`--kind method`はexact method queryで併用でき、`--kind lambda`は拒否されます。ラムダの所有関係はcall edgeではなく、delegate `Invoke`、event、callback、reflection、runtime flowを補ってlambda targetへのcall/referenceを推測しません。

### `symbol find`とsource command

```text
csindex symbol find [<pattern>] [options]
csindex source show <symbol> [options]
csindex source search (--include <text> | --exclude <text>)... [options]
```

`symbol find`の位置引数は最大1つで、省略時は`--namespace`、`--type`、`--method`の少なくとも1つを必要とします。component条件はANDで結合します。非regex modeでは`*`だけがwildcardで、それ以外はliteralです。`--regex`ではすべての名前conditionをculture-invariantな.NET regexとしてtimeout付きで評価し、`--ignore-case`は名前をculture-invariant ignore-case、source conditionをordinal ignore-caseにします。
`--include`/`--exclude`は反復可能で、excludeをORで先に評価してからincludeをANDで評価します。`--show-source`は表示だけを変え、候補をsource-backedへ限定しません。

`symbol find`は`::<lambda#1>`、`Owner()::<lambda#2>`、完全なowner-qualified名、`::<lambda#*>`でlambdaを検索できます。source showは一致するsource-backed executableとoverloadを返し、source searchは位置引数を受け付けず少なくとも1つのinclude/excludeを必要とします。metadata-onlyまたは非実行可能symbolはsource commandから返しません。

`symbol find`のexact queryでは、`--kind all`と`--async-status all`はpredicateを追加しないため、従来のexact type-query behaviorを維持します。`--async-status async|sync`を明示した場合は、exact候補に保存済み`AsyncRole`のdirect filterを適用します。

### 出力形式と結果ファイル

`--output-format`は形式を選ぶoptionであり、既定は通常commandでは`table`、`async tree`と`callers tree`では`tree`です。受理する値はcommandごとに次のとおりです。

| command | format |
| --- | --- |
| `symbol find`、`symbol list`、`source show`、`source search`、`definition`、`references`、`callers`、`callees`、`overrides`、`conditions` | `table|json` |
| `async tree` | `tree|line|json` |
| `callers tree` | `tree|mermaid|json` |

`-o <path>`と`--output-file <path>`は同義で、上表の結果payloadを持つすべてのcommandで使えます。`index`は結果payloadを持たないため受理しません。短縮形として認識するのは完全一致する`-o`だけであり、`-opath`と`-o=<path>`は位置引数です。file extensionからformatを推測せず、同じformatterがstdoutまたはfileへ同じpayloadを出力します。

出力先を指定しない場合はpayloadをstdoutへ書きます。指定した場合は成功時のstdoutを空にし、diagnostic、warning、progress、argument/query/database/output errorはstderrのままにします。fileはBOMなしUTF-8で、relative pathはprocess current directoryから絶対化します。

global helpと各commandの`--help`は通常どおりstdoutへ表示し、`--output-file`を併記しても結果fileを開いたりredirectしたりしません。

file出力はlazyに出力先と同じdirectoryのtemporary fileを開き、formatterの完了・flush・cancellation checkに成功した場合だけ既存fileを置換または新規fileへcommitします。query/format/database/write/cancel失敗時は既存fileをtruncateせず、所有するtemporary fileをcleanupします。parent directoryは自動作成しません。正規化比較で出力先が使用中SQLite DB pathと同一ならusage error、存在しないparentまたはI/O failureなら`Output error:`で始まるanalysis failureです。空値、値なし、または`-o`/`--output-file`の重複はusage errorです。

旧名`--output`はbreaking changeとして受理しません。指定すると`Unknown option(s): --output`のusage error（exit code 2）になります。

### source table layout

`--source-layout single-line|multi-line`の既定は`single-line`です。`symbol find`では`--show-source`と組み合わせる場合だけ、`source show`と`source search`では常に受理します。JSONとの併用、およびソースを表示しないcommandでの指定はusage errorです。

未知値は次のusage errorです。

```text
Unknown source layout: <value>. Use single-line or multi-line.
```

single-line tableのstdoutにはrecordだけを出します。summaryはstderrへ出し、0件ならstdoutは空です。field separatorはTABで、同一実行中のfield数と順序は固定です。

| command | 1 record |
| --- | --- |
| `symbol find` | `<signature><TAB><path>:<line>:<column>`（metadata-onlyは空location） |
| `symbol find --show-source` | `<signature><TAB><path>:<line>:<column><TAB><normalized-source>`（metadata-onlyは空location/source） |
| `source show`、`source search` | `<signature><TAB><path>:<line>:<column><TAB><normalized-source>` |
| `symbol list` | `<signature>` |

`multi-line`は互換layoutとしてheading、symbol行、`    source: <normalized-source>`行をstdoutへ維持します。single-lineの各field、およびmulti-lineのsignature/sourceは、実TAB、CRLF（1個のspace）、CR、LF、U+0085、U+2028、U+2029をASCII spaceへ表示時だけ置換します。これにより各recordとsource行は1物理行になります。DBの`normalized_source`とhash、source search、JSONの`normalizedSource`はlosslessな保存値を維持します。

### graph command補足

`async tree`と`callers tree`のrootはexact source-backed executable queryです。前者は保存済みの1本のasync next-hop chainを`tree|line|json`で、後者はprofile内のbounded static caller graphを`tree|mermaid|json`で出力します。root filterは適用しますが、途中nodeをfilterしてpath/edgeを切断しません。delegate `Invoke`、event、callback、reflection、runtime dispatch、およびlambda ownership edgeは推論しません。

```text
csindex async tree <symbol> [--max-nodes <count>] [--output-format tree|line|json]
csindex callers tree <symbol> [--depth <count>] [--max-nodes <count>]
    [--output-format tree|mermaid|json]
```

`async tree`の`--max-nodes`は正の値で既定500、`callers tree`の`--depth`は既定3（`0`は無制限）、`--max-nodes`は正の値で既定500です。両commandのambiguous rootは決定的順の候補を示す`Query error`になります。保存済みasync pathの整合性違反は、別経路を推測せずdatabase errorにします。
