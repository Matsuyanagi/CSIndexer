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

共通オプション:

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

JSONは`{ "profile": "...", "symbols": [...] }`です。各symbolには`id`、`stableKey`、`kind`、`displayName`、`fullyQualifiedName`、`namespaceName`、`typeSimpleName`、`parameters`、`location`、`isGenerated`、`assemblyName`、`asyncRole`、`isAsyncInvolved`、`asyncInvolvementDepth`を出力します。`--short-names`を指定しても`fullyQualifiedName`、`stableKey`、`namespaceName`、`parameters`などのcanonical JSON fieldは変更されず、短縮されるのは`displayName`だけです。

### `callees`とラムダ呼び出し

`callees`は既定で、指定したmethod自身の呼び出しに加えて、その内部にあるラムダとさらにネストしたラムダの呼び出しも再帰的に返します。method本体だけの直接呼び出しに限定するには`--exclude-lambda-calls`を指定します。

```powershell
csindex callees "Alpha.DescendantCallees::Execute()"
csindex callees "Alpha.DescendantCallees::Execute()" --exclude-lambda-calls
```

### 非同期解析情報の出力

既存の検索コマンドの結果へ非同期解析情報を追加します。非同期専用の新コマンドやフィルターはありません。

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
