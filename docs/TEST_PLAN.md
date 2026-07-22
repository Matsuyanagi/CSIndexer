# Test Plan

## Phase 1

- 仕様24章のPhase 1受け入れテスト13項目を自動化する。
- SQLiteスキーマ作成、トランザクション更新、破損DBエラーを検証する。
- CLIのヘルプ、引数エラー、table / JSON出力を検証する。

Status: 完了。Roslyn→SQLite→DB-only queryの統合テストでoverload、namespace省略、定義位置、callers/callees、コメント除外、生成コード、ラムダ、ローカル関数、拡張メソッド、構築generic、constructor、method group、`nameof`、override、条件分岐、cache、破損DBを検証済み。

## Phase 2

- 仕様24章のPhase 2受け入れテスト9項目を自動化する。
- 5,000ファイル列挙は解析を伴わない列挙単体テストとして実施する。

Status: 完了。5,001ファイル列挙、任意階層`obj`除外、`bin`包含、glob除外、WINDOWS/TFM symbols、不足参照の未解決call保存を検証済み。

## Phase 3 / Phase 4

- `TASKS.md` の項目を削除せず、実装着手時に詳細ケースを追加する。

## 非同期解析

- 宣言と戻り値: `DeclaredAsync`と`ReturnsAwaitable`を独立に検証し、`Task` / `Task<T>`、`ValueTask` / `ValueTask<T>`、`UniTask` / `UniTask<T>`、`UniTaskVoid`、`IAsyncEnumerable<T>`、`IUniTaskAsyncEnumerable<T>`の各ロールを確認する。UniTaskはテストソース内の最小互換型を使用し、製品依存を追加しない。
- operation: `await`、`await foreach`、`await using`（statement/declaration）が所有関数へ`ContainsAwait`、`UsesAwaitForEach`、`UsesAwaitUsing`を付けることを確認する。
- 所有者分離: async lambda/local functionを独立した起点depth 0として扱い、ネストしたoperationのロールやdepthが外側メソッドへ漏れないことを確認する。field/property initializer lambda内のローカル変数初期化子をsynthetic initializerと誤認せず、`ContainsAwait`と呼び出し辺をlambda所有にするケースを含める。
- 呼び出し利用方法: `AsyncUsageKind`の`Awaited`、`Forwarded`、`Discarded`、`Stored`、`Passed`、`Unobserved`と、該当なしの`None`を確認する。lambda/local-function所有者境界の外側にある代入・引数文脈を継承しないケース、`await LeafAsync().ConfigureAwait(false)`で内側呼び出しが`Awaited`を優先する競合祖先ケース、同期呼び出しの代入が`None`になるawaitability gateを明示的に検証する。
- 伝播: chain、自己/相互循環、非同期起点へつながらない循環、複数起点/複数経路の最短距離、呼び出し元方向だけの伝播を確認する。非同期起点からのみ呼ばれる同期calleeは非関与のままとする。
- 永続化: schema/request version 2、`async_role`、`async_involvement_depth`、`async_usage_kind`の保存とDB-only復元を確認する。version mismatchでfail-fastし、既存DBのテーブル、行、journal modeを変更しないことを確認する。`schema_info`のない非空の未認識DBも、marker行を保持し、CSIndexer tableを追加せず、journal modeを変更しないことを確認する。`sqliteXmarker`のようにSQLite内部prefixと似ているだけの有効なユーザーobjectも検出対象に含める。
- CLI: symbol/call JSON propertyと、非同期情報があるsymbolだけのtable suffix、callの`[AsyncUsageKind]`を`Console.Out`捕捉で確認する。

Status: 完了。Core、Storage、Integrationの自動テストで上記を検証済み。

## Latest Result

- Command: `dotnet test CsIndex.sln --configuration Release`
- Passed: 55
- Failed: 0
- Skipped: 0
- Date: 2026-07-22

CLIプロセス試験ではDirectoryModeのindex、definition-at、callers、JSON、cache reuseと、MSBuildWorkspaceによる実在`.csproj`解析（4 projects、37 documents、compilation errors 0）を確認済み。
