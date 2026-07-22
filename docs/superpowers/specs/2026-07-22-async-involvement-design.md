# 非同期関与情報の設計

## 目的

Roslynの意味解析結果と呼び出しグラフを使用し、各関数について次を区別して保存・検索可能にする。

- C#の宣言上の非同期関数か
- await可能な値または非同期ストリームを返すか
- 本文で非同期構文を使用するか
- 非同期関数へ直接または間接的に到達する呼び出し元か

関数名の `Async` 接尾辞は判定材料に使用しない。呼び出し元方向の伝播は循環呼び出しを含むグラフでも必ず停止させる。

## 対象

対象シンボルは、既存インデックスが関数の所有者として扱う次の種類とする。

- メソッド、コンストラクター、アクセサーとして表現される `IMethodSymbol`
- ローカル関数
- ラムダ、匿名関数
- トップレベルステートメント
- フィールドまたはプロパティ初期化子

型シンボルには非同期関与距離を付けない。メタデータ由来のメソッドは非同期呼び出しの起点になり得るため、判定可能な戻り値情報を保存する。

## 非同期ロール

シンボルに `AsyncRole` ビットフラグを保存する。

| ロール | 判定 |
|---|---|
| `DeclaredAsync` | `IMethodSymbol.IsAsync` |
| `ReturnsAwaitable` | 戻り値が既知のTask系またはtask-like型 |
| `ContainsAwait` | 所有関数内に `IAwaitOperation` がある |
| `AsyncIterator` | `IMethodSymbol.IsAsync && IMethodSymbol.IsIterator` |
| `ReturnsAsyncEnumerable` | 戻り値が既知の非同期ストリーム型 |
| `AsyncVoid` | `IsAsync` かつ戻り値が `void` |
| `UniTaskVoid` | 戻り値が `Cysharp.Threading.Tasks.UniTaskVoid` |
| `UsesAwaitForEach` | `IForEachLoopOperation.IsAsynchronous` |
| `UsesAwaitUsing` | `IUsingOperation` または `IUsingDeclarationOperation` の `IsAsynchronous` |

既知のawaitable型は、`Compilation.GetTypeByMetadataName` と `SymbolEqualityComparer.Default` で `OriginalDefinition` を比較する。

- `System.Threading.Tasks.Task`
- `System.Threading.Tasks.Task<T>`
- `System.Threading.Tasks.ValueTask`
- `System.Threading.Tasks.ValueTask<T>`
- `Cysharp.Threading.Tasks.UniTask`
- `Cysharp.Threading.Tasks.UniTask<T>`

既知の非同期ストリーム型は次とする。

- `System.Collections.Generic.IAsyncEnumerable<T>`
- `Cysharp.Threading.Tasks.IUniTaskAsyncEnumerable<T>`

`UniTaskVoid` は完了をawaitできないため `ReturnsAwaitable` には含めず、fire-and-forgetを表す独立ロールとする。

カスタムawaitableは型名だけから完全には判定しない。実際にawaitされる利用箇所はRoslynが構築した `IAwaitOperation` を正とし、所有関数へ `ContainsAwait` を付ける。これによりUnity固有のawaitableなども利用側では検出できる。

## 所有関数の分離

メソッド全体の構文を単純に走査すると、ネストしたラムダやローカル関数の `await` が外側の関数へ混入する。各非同期操作は既存の `DocumentAnalysisState.FindOwner` で所有シンボルを決定し、その所有者だけへロールを付与する。フィールド、event field、プロパティの実際の初期化句だけをsynthetic initializer所有者として登録し、ラムダ本体内のローカル変数初期化句、引数の既定値などは登録しない。

ラムダは `IAnonymousFunctionOperation.Symbol`、ローカル関数は宣言シンボルから `IsAsync` と戻り値を取得する。トップレベルステートメントと初期化子は宣言シンボルを持たないため、本文操作から得られるロールだけを付与する。

## 呼び出し辺の非同期利用方法

解決済みの通常呼び出しに `AsyncUsageKind` を保存する。

| 値 | 例 |
|---|---|
| `None` | 非awaitable呼び出し、または分類不能 |
| `Awaited` | `await LoadAsync()` |
| `Forwarded` | `return LoadAsync()`、式本体による返却 |
| `Stored` | `var task = LoadAsync()`、フィールドやプロパティへの代入 |
| `Passed` | `WhenAll(LoadAsync())` |
| `Discarded` | `_ = LoadAsync()` |
| `Unobserved` | `LoadAsync();` |

分類は構文名ではなく `IInvocationOperation` の親操作をたどって決定する。変換、括弧、条件アクセス、`ConfigureAwait`など同じ所有関数内で結果の意味を変えない中間操作や呼び出し連鎖は越えて走査する。`IAnonymousFunctionOperation`または`ILocalFunctionOperation`へ達したら走査を停止し、外側所有者の代入、引数、return文脈を内側の呼び出しへ適用しない。複数条件に一致する場合は、同じ所有者内で `Awaited`、`Forwarded`、`Discarded`、`Stored`、`Passed`、`Unobserved` の順で優先する。

呼び出し先が既知awaitableを返さない場合でも、呼び出し式が `IAwaitOperation` の被演算子に含まれるなら `Awaited` とする。`Awaited`以外の分類は、戻り値が既知の`Task` / `ValueTask` / `UniTask`型とシンボル同値な呼び出しだけに適用し、通常の同期呼び出しや未知型は`None`とする。`await task;` のように生成元の呼び出しとawaitが別文の場合、本文の非同期関与は検出するが、データフロー解析なしに特定の呼び出し辺へ `Awaited` を遡及させない。

## 非同期関与と伝播方向

`AsyncInvolvementDepth` をnullable整数としてシンボルへ保存する。

- `0`: 非同期起点
- `1`: 非同期起点を直接呼び出す
- `2` 以上: 指定された辺数で非同期起点へ到達する
- `null`: 非同期起点へ到達しない

非同期起点は、次のロールを1つ以上持つシンボルとする。

- `DeclaredAsync`
- `ReturnsAwaitable`
- `ContainsAwait`
- `AsyncIterator`
- `ReturnsAsyncEnumerable`
- `AsyncVoid`
- `UniTaskVoid`
- `UsesAwaitForEach`
- `UsesAwaitUsing`

伝播は「非同期関数へ到達する呼び出し元方向」だけとする。非同期関数から呼ばれる同期関数を非同期関与とはしない。

## 循環を停止する伝播アルゴリズム

全ドキュメントの抽出後に、解決済みの `ReferenceKind.Invocation` から `callee_definition_key -> caller_symbol_key` の逆隣接リストを作る。メソッドグループ、`nameof`、オブジェクト生成、未解決呼び出し、動的呼び出しは伝播辺に含めない。

全非同期起点を距離0でキューへ入れ、複数始点の幅優先探索を行う。

```text
distance[seed] = 0
queue.Enqueue(seed)

while queue is not empty:
    callee = queue.Dequeue()
    for caller in reverseCalls[callee]:
        candidate = distance[callee] + 1
        if caller is未訪問 or candidate < distance[caller]:
            distance[caller] = candidate
            queue.Enqueue(caller)
```

幅優先探索では最初に確定する距離が最短になる。同距離またはより長い経路では再投入しない。したがって自己再帰、相互再帰、複数の循環があっても有限個のシンボルを処理した時点で停止する。

例:

```text
A -> B -> C(async)
^    |
|____|
```

結果は `C=0`、`B=1`、`A=2` となる。非同期起点へつながらない循環には距離を付けない。

## データモデルと永続化

Coreモデルへ次を追加する。

- `SymbolData.AsyncRole`
- `SymbolData.AsyncInvolvementDepth`
- `CallData.AsyncUsageKind`

SQLiteの `symbols` へ次を追加する。

- `async_role INTEGER NOT NULL DEFAULT 0`
- `async_involvement_depth INTEGER`

SQLiteの `calls` へ次を追加する。

- `async_usage_kind INTEGER NOT NULL DEFAULT 0`

スキーマバージョンとリクエストハッシュのスキーマ番号を2へ上げる。現行方針どおり、別バージョンの既存DBを自動削除・暗黙変換せず、明示的な再構築を要求する。

`schema_info`がないDBは、SQLite内部object以外のuser table / index / view / triggerが存在しない場合だけ新規DBとして初期化する。未認識の非空DBはWAL設定とDDLより前に拒否し、既存schema、行、journal modeを変更しない。

Storageの `StoredSymbol` と `StoredCall` に対応フィールドを追加し、全SELECT、INSERT、reader ordinalを同期させる。

## 検索と表示

既存のシンボル検索結果へ次を公開する。

- `asyncRole`
- `isAsyncInvolved` (`AsyncInvolvementDepth != null`)
- `asyncInvolvementDepth`

既存の呼び出し検索結果へ `asyncUsageKind` を公開する。JSON出力は列挙値名で出力し、table出力は非同期ロール、関与の有無、距離を短い補足として表示する。新しいCLIコマンドは追加せず、既存の `find`、`definition`、`callers`、`callees`、`references` の結果を拡張する。

## エラー処理と限界

- コンパイルエラーでoperationを取得できない箇所は既存の未解決呼び出し処理を維持し、推測で非同期辺を作らない。
- `dynamic`、reflection、関数ポインター、高度なデリゲートデータフローは伝播対象外とする。
- インターフェースまたは仮想呼び出しはRoslynが静的に解決した定義への辺で伝播する。実行時候補すべてへの伝播は行わない。
- 戻り値だけでは判定できない任意のカスタムawaitableは、実際の `await` 利用側では検出するが、未awaitの中継関数を網羅できない場合がある。
- `AsyncInvolvementDepth` は最短距離のみを保存し、到達可能な全非同期起点や全経路は保存しない。

これらは `docs/KNOWN_LIMITATIONS.md` に追記する。

## テスト方針

テストは実装前に追加し、期待した理由で失敗することを確認する。

1. `async Task`、`async ValueTask<T>`、`Task`中継関数のロールを検証する。
2. `await`、`await foreach`、`await using` のoperationロールを検証する。
3. テスト内に最小の `Cysharp.Threading.Tasks` 互換型を宣言し、`UniTask`、`UniTask<T>`、`UniTaskVoid`、`IUniTaskAsyncEnumerable<T>` を外部パッケージなしで検証する。
4. ラムダとローカル関数の非同期ロールが外側へ混入しないことを検証する。
5. `Awaited`、`Forwarded`、`Stored`、`Passed`、`Discarded`、`Unobserved` を検証する。
6. `A -> B -> C(async)` が距離2、1、0になることを検証する。
7. 自己再帰、相互再帰、非同期起点へつながらない循環で停止し、最短距離が保存されることをタイムアウト付きテストで検証する。
8. SQLite保存後にプロセスメモリ上のsnapshotへ依存せず、ロール、距離、利用方法がDBから復元されることを検証する。
9. JSON/table出力に追加情報が含まれることを検証する。
10. 全既存テストとReleaseビルドを実行する。

## ドキュメント更新

実装時に次を更新する。

- `docs/SPEC.md`: 非同期判定、伝播方向、循環停止、DB列を正式仕様として追加
- `docs/DECISIONS.md`: 直接ロールと派生距離を分離する判断、インデックス時BFSを採用した理由
- `docs/DB_SCHEMA.md`: symbols/callsの追加列とスキーマv2
- `docs/CLI.md`: 既存出力へ追加される非同期情報
- `docs/TEST_PLAN.md`: 非同期および循環グラフの検証内容
- `docs/IMPLEMENTATION_STATUS.md`: 実装済み範囲
- `docs/KNOWN_LIMITATIONS.md`: カスタムawaitable、dynamic、delegate flow、仮想dispatchの限界
