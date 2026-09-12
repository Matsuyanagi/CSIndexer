# CSIndexer

[English](README.md) | 日本語 | [コマンド詳細・使用例（英語）](EXAMPLE.md)

CSIndexer（`csindex`）は、RoslynでC#ソースコードを解析し、再利用可能な
セマンティック索引をSQLiteへ保存するWindows向けコマンドラインツールです。
プロジェクトを検索のたびに開き直すことなく、実行可能シンボル、定義、参照、
呼び出し元、呼び出し先、オーバーライド、正規化済みソース、非同期関与を検索できます。

## 主な機能

- `.sln`、`.slnx`、`.csproj`、またはディレクトリ単位のC#ソースを索引化します。
- 通常メソッド、コンストラクター、デストラクター、演算子、変換、アクセサー、
  ローカル関数、ラムダ、匿名メソッド、初期化子、トップレベルステートメントを扱います。
- 構造化シンボルパス、名前空間、型、メソッド、ファイル、正規化済みソース、
  生成コード種別、実行可能シンボル種別、直接的なasync状態で検索できます。
- 定義と参照、直接の呼び出し元・呼び出し先、オーバーライドや
  インターフェイス実装の分岐を調査できます。
- 上限付きcaller treeと、永続化済みの非同期関与経路を出力します。
- 関数宣言の正規化済みソースと、各物理呼び出し箇所の正規化済み式を表示できます。
- partialの定義側／実装側を含め、論理シンボルと物理宣言を分離して管理します。
- 移動可能な相対パスを保存し、検索時に絶対パスまたは相対パスへ復元します。
- 決定的なtable、JSON、tree、line、Mermaid出力を提供します。
- 索引更新と出力ファイル更新をアトミックに確定します。

## 動作要件

- Windows x64
- ソースからビルドする場合は.NET 10 SDK
- 解析対象solution/projectが必要とするSDK、workload、reference assembly

CLIのtarget frameworkは`net10.0-windows`、既定のruntime identifierは
`win-x64`です。

## ビルドとpublish

solutionをビルドします。

```powershell
dotnet build CsIndex.sln --configuration Release
```

実行ファイルは次の場所に生成されます。

```text
src\CsIndex.Cli\bin\Release\net10.0-windows\win-x64\csindex.exe
```

[Task](https://taskfile.dev/)をインストールしている場合は、次のtaskも使えます。

```powershell
task build
task publish
```

`task publish`は既定で`artifacts/publish/win-x64`に自己完結型のフォルダーを
作成します。同等の直接実行コマンドは次のとおりです。

```powershell
dotnet publish src\CsIndex.Cli\CsIndex.Cli.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts\publish\win-x64
```

以下では`csindex.exe`を`PATH`へ追加し、`csindex`として実行できるものとします。
代わりに実行ファイルのフルパスを指定してもかまいません。

## クイックスタート

solution、project、またはディレクトリから索引を作ります。

```powershell
csindex index Game.sln
# または
csindex index Game.csproj
# または、ソースルートで
csindex index .
```

`.csindex`が置かれたディレクトリから検索するか、`--db`を指定します。

```powershell
csindex symbol find "Game::Player::Run()"
csindex definition "Game::Player::Run()"
csindex callers "Game::Player::Run()" --show-source
csindex callers tree "Game::Player::Run()" --depth 3
csindex source search --include-literal "CancellationToken"
```

各コマンドの詳しい解説と例は[EXAMPLE.md](EXAMPLE.md)を参照してください。

## 索引とデータベース

`--db`を省略すると、索引作成時には選択された入力／storage root配下の
`.csindex/index.sqlite`へ保存します。検索時の既定値はcurrent directory配下の
`.csindex/index.sqlite`です。

現在のDB schemaはversion 6です。古いDBまたは認識できないDBはmigrationや変更を
行わずに拒否します。古いDBをrename／削除するか、新しい`--db`を指定して、
明示的に`csindex index`を実行してください。

projectとdocumentのパスは、1つのstorage rootからの相対パスとしてforward slashで
保存されます。DBディレクトリからrootへの相対anchorも保存するため、DBとソースツリーの
相対配置を保って一緒に移動できます。検索時の`--base-dir`はDBを書き換えず、
パス復元の基準だけを上書きします。

通常の永続化対象ソースは、storage rootと同じWindows driveまたはUNC share上に
ある必要があります。MSBuildが別volume/shareから供給したdocumentでも、生成コードと
確実に判定できるものはRoslyn compilationには残し、warningを出してportable indexから
除外します。

## シンボルパス

CSIndexerは2つのcanonical形式を受け付けます。

```text
csharp:   Game.Core.Player.Inventory::Load(int).Validate()
explicit: Game.Core::Player.Inventory::Load(int).Validate()
```

C#形式はtop-levelの`::`が1個です。名前空間と型の境界候補に対するsuffix検索として
扱うため、結果からコピーした表記や名前空間を省略した表記をそのまま検索できます。
explicit形式はtop-levelの`::`が2個で、名前空間と型の境界を厳密に固定します。

入れ子型は`.`で区切り、ローカル関数やラムダなど実行可能な子要素も`.`で区切ります。
引数リストを省略すると全overloadが対象になり、`()`は引数0個だけを表します。
表示シグネチャでは`int`や`string`などのC# aliasを優先します。

特殊なsource callableには、`[constructor]`、`[operator:+]`、`[get:Name]`、
`<lambda#1>`、`<top-level-statements>`のような曖昧さのないsegmentを使います。
wildcardにはcomponent単位の`*`と`**`を使います。完全な文法と例は
[EXAMPLE.md](EXAMPLE.md)または`csindex --help-verbose`を参照してください。

## コマンド

| コマンド | 用途 |
| --- | --- |
| `csindex index <input>` | セマンティックSQLite索引を作成または更新します。 |
| `csindex symbol find [selector]` | パスまたはtyped conditionで実行可能シンボルを検索します。 |
| `csindex symbol list` | 選択profileの実行可能シンボルを一覧表示します。 |
| `csindex source show <selector>` | 1つの関数宣言の正規化済みソースを表示します。 |
| `csindex source search` | 正規化済みソースとtyped conditionで関数を検索します。 |
| `csindex definition <selector>` | 一致する論理シンボルの物理宣言位置を表示します。 |
| `csindex definition --at <path:line:column>` | ソース位置にある呼び出し先の定義を解決します。 |
| `csindex references <selector>` | 一致するシンボルへの保存済み参照を一覧表示します。 |
| `csindex callers <selector>` | 一致するシンボルの呼び出し箇所と実効callerを一覧表示します。 |
| `csindex callers tree <selector>` | 上限付きの逆方向caller graphを構築します。 |
| `csindex callees <selector>` | 一致するシンボルが行う呼び出しを一覧表示します。 |
| `csindex overrides <selector>` | 保存済みのmethod override関係を一覧表示します。 |
| `csindex async tree <selector>` | 非同期起点までの保存済み経路を1つ辿ります。 |
| `csindex conditions` | 選択profileで検出した条件付きコンパイルシンボルを表示します。 |

各コマンドの詳細と例は[EXAMPLE.md](EXAMPLE.md)にあります。

## オプション一覧

コマンドが受け付けないオプションはエラーになります。簡潔なコマンド別helpは
`csindex <command> --help`、完全な文法と正確な受理オプション一覧は
`csindex <command> --help-verbose`で表示できます。

### indexオプション

| オプション | 意味 |
| --- | --- |
| `--db <path>` | SQLite index path。 |
| `--mode auto\|solution\|project\|directory` | 入力の読み込みmodeを選択します。 |
| `--solution <path>` | directory入力でsolutionを選択します。 |
| `--configuration <name>` | MSBuild configuration。 |
| `--framework <tfm>` | target framework。`--target-framework`のaliasです。 |
| `--target-framework <tfm>` | target framework。`--framework`のaliasです。 |
| `--runtime <rid>` | analysis profileで使うruntime identifier。 |
| `--profile-name <name>` | 保存するanalysis profileに名前を付けます。 |
| `--define <symbol>` | preprocessor symbolを追加します。反復可能です。 |
| `--undefine <symbol>` | preprocessor symbolを削除します。反復可能です。 |
| `--define-file <path>` | ファイルからpreprocessor symbolを読みます。反復可能です。 |
| `--reference <dll>` | metadata referenceを追加します。反復可能です。 |
| `--exclude <glob>` | source pathを除外します。反復可能で、`obj`は常に除外されます。 |
| `--generated-source physical\|all\|none` | 生成ソースmode。現在実装済みなのは`physical`だけです。 |
| `--rebuild` | 互換DBを強制的に再解析します。非互換schemaはmigrationしません。 |
| `--verbose` | 索引作成の進捗を表示します。 |
| `--diagnostics` | compilerとsemantic analysisの詳細診断を表示します。 |
| `--unity-editor <directory>` | 将来のUnity対応向け予約です。現在は未実装です。 |
| `--help` | 簡潔なindex helpを表示します。 |
| `--help-verbose` | 完全なreferenceを表示します。 |

### 共通の検索・表示オプション（`Q`）

| オプション | 意味 |
| --- | --- |
| `--db <path>` | SQLite index path。既定は`.csindex/index.sqlite`です。 |
| `--profile <name>` | profileを選択します。既定は最後に索引化したprofileです。 |
| `--output-format <format>` | コマンドが対応する出力形式を選択します。 |
| `-o <path>`、`--output-file <path>` | stdoutの代わりにファイルへアトミックに出力します。 |
| `--symbol-path-style csharp\|explicit` | 表示用シンボルパス形式を選びます。identityは変えません。 |
| `--short-names` | 表示上のownerと型からnamespaceを省略します。 |
| `--base-dir <path>` | 保存済み相対パスを復元する基準を上書きします。 |
| `--path-style absolute\|relative` | 絶対パス（既定）またはeffective baseからの相対パスで表示します。 |
| `--help` | 簡潔なhelpを表示します。 |
| `--help-verbose` | 完全なreferenceを表示します。 |
| `--verbose` | queryではhelpとの併用時だけ完全なreferenceを表示します。単独指定は無効です。 |

`--short-names`を指定すると、表示上のownerとすべての表示型（戻り値型、引数型、generic引数、conversion target、explicit-interface payloadを含む）からnamespaceを省略します。ネストしたcontaining typeの経路は保持されます。JSONでは`displayName`、`signature`、`fullyQualifiedName`、`parameters`、`returnType`が短縮され、`stableKey`と完全な`namespaceName`は変わりません。

`--short-names`を省略すると、JSONはcanonicalな機械処理向け（machine-oriented）の完全修飾表示になります。JSONのfield構成、既定の出力、結果の順序は変わりません。

通常のコマンドは`table|json`、`async tree`は`tree|line|json`、
`callers tree`は`tree|mermaid|json`を使います。

### typed selection condition（`C`）

| 対象 | Glob（既定構文） | Literal | 正規表現 | 大文字小文字 |
| --- | --- | --- | --- | --- |
| 名前空間 | `--namespace <glob>` | `--namespace-literal <text>` | `--namespace-regex <pattern>` | `--namespace-case strict\|ignore` |
| 型 | `--type <glob>` | `--type-literal <text>` | `--type-regex <pattern>` | `--type-case strict\|ignore` |
| メソッド | `--method <glob>` | `--method-literal <text>` | `--method-regex <pattern>` | `--method-case strict\|ignore` |
| ファイル | `--file <glob>` | `--file-literal <text>` | `--file-regex <pattern>` | `--file-case strict\|ignore` |
| 必須ソース | `--include <glob>` | `--include-literal <text>` | `--include-regex <pattern>` | `--source-case strict\|ignore` |
| 除外ソース | `--exclude <glob>` | `--exclude-literal <text>` | `--exclude-regex <pattern>` | `--source-case strict\|ignore` |

selection groupには次のオプションも含まれます。

| オプション | 意味 |
| --- | --- |
| `--kind all\|method\|lambda` | 直接rootをcallable kindで絞ります。既定は`all`です。 |
| `--async-status all\|async\|sync` | 直接rootを保存済みの直接async roleで絞ります。既定は`all`です。 |

`--kind all`には索引化されたinitializerとtop-level statementも含まれます。
これらは`method`または`lambda`へまとめられません。

case selector以外のconditionは反復可能です。同じnamespace/type/method/file categoryの
複数条件はコマンドライン順のOR、異なるcategoryはANDです。すべてのinclude条件が
一致する必要があり、exclude条件は1つでも一致するとその宣言を除外します。
大文字小文字の既定は対象ごとに`strict`です。

ソース条件は、document全体ではなく1つの物理関数宣言に対応する正規化済み範囲を
部分検索します。このため、call siteのソース文字列をDBへ重複保存せずに、
`symbol find --include`と`--exclude`で特定コードを含む／含まない関数を検索できます。

廃止済みの汎用switch `--regex`と`--ignore-case`はaliasではなく、指定すると
エラーになります。

### コマンド固有オプション

| オプション | 対象コマンド | 意味 |
| --- | --- | --- |
| `--require-single` | `symbol find`、`definition`、`references`、`callers`、`callees`、`overrides` | 論理rootがちょうど1件でなければexit 5にします。 |
| `--include-overrides` | `symbol find`、`definition`、`references`、`callers`、`callees` | 1つの厳密なmethod queryを子孫override/interface実装へ展開します。 |
| `--exclude-generated` | `references`、`callers`、`callees` | 生成コードのrootと該当する返却edgeを除外します。 |
| `--only-generated` | `references`、`callers`、`callees` | 生成コードのrootと該当する返却edgeだけを残します。 |
| `--show-source` | `symbol find`、`callers`、`callers tree` | 正規化済み宣言または物理call site sourceを付加します。 |
| `--source-layout single-line\|multi-line` | `symbol find`、`source show`、`source search` | tableのsource layoutを選びます。`symbol find`では`--show-source`も必要です。 |
| `--async-involved` | `symbol list` | 非同期関与depthが保存されているシンボルだけを残します。 |
| `--at <path:line:column>` | `definition` | 物理位置から呼び出し先を解決します。root conditionとは併用できません。 |
| `--dispatch static\|virtual\|all` | `callers` | 保存済みcallとdispatch候補の表示modeです。既定は`static`です。 |
| `--caller-scope direct\|containing\|both` | `callers` | 直接のcallable owner、包含owner、または両方を表示します。 |
| `--exclude-lambda-calls` | `callees` | 入れ子lambdaが所有する呼び出しを含めません。 |
| `--depth <count>` | `callers tree` | callerの最大深さ。既定は`3`、`0`は無制限です。 |
| `--max-nodes <count>` | `async tree`、`callers tree` | 正のnode上限。既定は`500`です。 |

`--exclude-generated`と`--only-generated`は同時に指定できません。
`--include-overrides`にはwildcardを含まない1つの厳密なmethod selectorが必要です。
conditionだけの検索、lambda、initializer、top-level、`definition --at`では使えません。

### 正確なオプション適用範囲

`Q`は共通の検索・表示option group、`C`は全typed selection conditionと
`--kind`、`--async-status`を表します。

| コマンド | 受け付けるgroupと追加オプション |
| --- | --- |
| global help | `--db + --profile + --output-format + --output-file + --help + --help-verbose + --verbose` |
| `index` | 上記index option tableのみ。query option groupは不可 |
| `symbol find` | `Q + C + --require-single + --include-overrides + --show-source + --source-layout` |
| `symbol list` | `Q + C + --async-involved` |
| `source search` | `Q + C + --source-layout` |
| `source show` | `Q + C + --source-layout` |
| `definition <selector>` | `Q + C + --require-single + --include-overrides` |
| `definition --at` | `Q + --at`。root conditionは不可 |
| `references` | `Q + C + generated filter + --require-single + --include-overrides` |
| `callers` | `Q + C + generated filter + --require-single + --include-overrides + --dispatch + --caller-scope + --show-source` |
| `callees` | `Q + C + generated filter + --require-single + --include-overrides + --exclude-lambda-calls` |
| `overrides` | `Q + C + --require-single`。`--include-overrides`は不可 |
| `async tree` | `Q + C + --max-nodes` |
| `callers tree` | `Q + C + --depth + --max-nodes + --show-source` |
| `conditions` | `--db`、`--profile`、`--output-format`、`--output-file`、`--base-dir`、`--path-style`、help option |

## 出力仕様

- 結果payloadはstdout、diagnostic、progress、warning、summaryはstderrへ出力します。
- `--output-file`は出力先と同じディレクトリのtemporary fileへ書き、描画とflushが
  成功した後だけ出力先を置き換えます。
- single-line tableのソースは、1行1recordを保つためTABや改行separatorをspaceへ
  置き換えます。JSONは保存済みの正規化ソース範囲を正確に保持します。
- `callers --show-source`は、物理call rowごとに正規化済みのinvocationまたは
  object-creation expressionを追加します。
- `callers tree --show-source`は、保持された各物理call siteを構造edgeへ関連付けます。
  JSONは順序付き`callSites`、Mermaidはescape済みの位置とソースをedge labelへ出力します。
- 表示オプションはsemantic identityや結果順序を変更しません。

## 終了コード

| コード | 意味 |
| --- | --- |
| `0` | 成功。正当な0件のlist/search結果も含みます。 |
| `2` | 引数またはqueryが不正です。 |
| `3` | 入力、解析、キャンセル、ソース読み込み、または出力の失敗です。 |
| `4` | SQLiteまたは非互換schemaの失敗です。 |
| `5` | `--require-single`の失敗です。 |

## 主な制限

- Roslynで静的に解決できる事実を記録します。dynamic dispatch、delegate/event/callbackの
  実行、reflection、receiver data flowなど、実行時の挙動は推論しません。
- Source Linkによる取得とmetadata-only symbolのdecompileは行いません。
- 正規化済みソースはtrivia、comment、directive、無効な条件付きテキストを除去し、
  literal tokenのtextは保持します。
- 任意の正規化済みソース部分検索は対象candidateを走査し、FTSは使いません。
- anonymous functionのordinalは1つの索引snapshot内だけで安定し、前方のコード編集で
  後続番号が変わる場合があります。
- `--generated-source all|none`と`--unity-editor`は予約済みで、現在は未実装です。
- queryは1つのanalysis profileを選択します。profile横断検索はありません。

完全な一覧は[既知の制限](docs/KNOWN_LIMITATIONS.md)を参照してください。

## ドキュメント

- [コマンド詳細・使用例（英語）](EXAMPLE.md)
- [CLI契約](docs/CLI.md)
- [仕様書](docs/SPEC.md)
- [DB schema](docs/DB_SCHEMA.md)
- [設計判断](docs/DECISIONS.md)
- [テスト計画](docs/TEST_PLAN.md)
- [実装状況](docs/IMPLEMENTATION_STATUS.md)
- [既知の制限](docs/KNOWN_LIMITATIONS.md)
