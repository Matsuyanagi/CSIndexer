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
csindex definition "Player::Play()"
csindex definition --at "src\Player.cs:120:17"
csindex references "Player::Play(string)"
csindex callers "BaseClass::Run()" --dispatch virtual
csindex callees "Game.Player::Execute()"
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

### 非同期解析情報の出力

既存の検索コマンドの結果へ非同期解析情報を追加します。非同期専用の新コマンドやフィルターはありません。

symbolを含むJSON objectには次のpropertyを出力します。

- `asyncRole`: `AsyncRole` flagsの文字列表現。例: `"DeclaredAsync, ReturnsAwaitable"`
- `isAsyncInvolved`: `asyncInvolvementDepth`がnullでないとき`true`
- `asyncInvolvementDepth`: 非同期起点までの最短呼び出し辺数。起点は`0`、非関与は`null`

callを含むJSON objectには`asyncUsageKind`を出力します。値は`None`、`Awaited`、`Forwarded`、`Stored`、`Passed`、`Discarded`、`Unobserved`のいずれかです。

table出力では、symbolのロールが`None`かつdepthがnullの場合を除き、表示名の後へ次の補足を付けます。

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
