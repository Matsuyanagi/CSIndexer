# Known Limitations

## Phase 1 / Phase 2

- 変更なしならRoslynを起動せずDBを再利用しますが、入力変更時は現在すべての対象Projectを再解析します。仕様22.3のプロジェクト単位差分更新と参照元Projectの保守的無効化は未実装です。
- キャッシュの事前判定は入力ルート内のソース・構成ファイル、明示参照、define fileをハッシュします。MSBuild評価後にだけ判明する入力ルート外のProjectReferenceや暗黙MetadataReferenceの変更は、`--rebuild`が必要な場合があります。
- 複数TFMの完全な並列インデックスと同一シンボルのProfile/TFM別表示は未実装です。`--framework`で1つを選択できます。
- 呼び出し抽出は通常呼び出し、オブジェクト生成、method group、delegate生成、`nameof`を扱います。Function-symbol and normalized-source extraction now covers accessors, operators, and conversions, but call/relation extraction for property access, event access, and function pointers remains Phase 4.
- 未解決・曖昧呼び出しと候補は保存しますが、高度なデリゲートフロー、`dynamic`の実行時候補、reflectionは追跡しません。
- 検索構文は通常型と通常メソッドを対象とし、ネスト型、ジェネリック型、配列型、nullable型、`ref/out/in`表記は予約済みエラーになります。
- `--generated-source all` / `none` は予約済みで、現在は既定の`physical`だけを受け付けます。
- 同じProfile名の再インデックスは、そのProfileの以前のデータを原子的に置き換えます。複数Profileは別名で保存できますが、`--all-profiles`横断検索はPhase 4です。
- Source Linkと外部シンボルのソース取得は未実装です。外部定義はシンボル名、アセンブリ名、「ソースなし」を返します。

## Override-aware method search

- Interface implementation expansion can enumerate only types analyzed from
  the selected source profile. Metadata-only implementation types outside that
  profile are not enumerated.
- Expansion is intentionally descendant-only. A concrete root does not expand
  upward to base methods or interface contracts, and it does not cross into a
  sibling implementation branch.
- The feature does not perform receiver-value/data-flow or runtime-flow
  analysis. A call statically bound to `IPlayable::Play()` is therefore not
  returned by a concrete-rooted `Pianist::Play()` search; search the interface
  contract to include that call site.
- Incomplete compilation can prevent Roslyn from identifying an interface
  implementation binding. Such a binding is omitted and the affected results
  are conservatively partial rather than inferred.

## 非同期解析

- `ReturnsAwaitable`は既知の`Task` / `ValueTask` / `UniTask`型をRoslynシンボルで照合します。custom awaitableの呼び出しが構造上`await`されている場合は辺を`Awaited`、所有関数を`ContainsAwait`として検出しますが、awaitされずに返却・保存・引数渡し・未観測実行されるcustom awaitableの辺は`None`となり、起点や中継関数を網羅できません。
- `ReturnsAsyncEnumerable`は`IAsyncEnumerable<T>`と`IUniTaskAsyncEnumerable<T>`を対象とします。`IAsyncEnumerator<T>`などenumeratorを直接返すAPIは現在このロールに分類しません。
- `await task;`のtaskが以前の文の呼び出しで生成された場合、所有関数の`ContainsAwait`は記録しますが、データフローを遡って生成元の呼び出し辺を`Awaited`にはしません。
- 非同期関与の伝播辺は解決済みの通常`Invocation`だけです。`dynamic`呼び出し、高度なdelegate flow、method group経由、reflection、runtime dispatch候補は追跡しません。
- 伝播は静的に解決されたcalleeからcallerへの逆辺に限定します。仮想・interface呼び出しの実行時target候補を展開した非同期関与は保存しません。
- `AsyncInvolvementDepth` stores a shortest distance and `async_next_symbol_id` stores one selected next hop. The index does not retain every reachable origin, every equal shortest path, or a separately queryable path-edge history.

## Symbol, source, and graph expansion

- Version 3 and every older/unknown database version must be rebuilt. There is no automatic migration or compatibility reader for those databases.
- array-rank normalization correction後もschema version 4（v4）のままです。`AnalysisCacheVersion = 2`により次回の同一index requestは自動再解析されますが、legacy normalized sourceを持つ既構築v4 DBを直接queryする場合は、更新済みsource/hashを得る前に`index --rebuild`が必要です。
- Normalized-source matching is an arbitrary substring predicate over
  source-backed executable candidates. It can scan candidates because neither
  a B-tree index nor FTS is used for arbitrary substrings.
- Source normalization intentionally omits trivia, comments, directives, and
  inactive conditional text. `source show` and `source search` therefore do
  not expose or match those removed characters. Normalization removes layout
  outside literal tokens but preserves each literal token `Text`, so a
  multiline raw literal can retain embedded newlines.
- `source show`/`source search` are limited to indexed source-backed executable
  symbols: methods, constructors, local functions, lambdas, accessors,
  operators, and conversions. Metadata-only symbols, external decompilation,
  and Source Link retrieval are not provided.
- `--kind` and `--async-status` filter only the resolved executable target/root.
  They intentionally do not remove secondary callers/callees or graph-path
  nodes, so they cannot be used as a display-wide graph pruning feature.
- Lambda queries resolve stored executable targets, but do not create inferred
  call/reference edges. Delegate `Invoke`, event subscription/callback
  execution, reflection, and runtime-flow references to a lambda are not
  indexed; `references` and `callers` can report only stored static facts.
- `async tree` accepts an exact source-backed method root and follows the one
  persisted async next-hop chain. It does not enumerate alternate equal paths
  or dynamically infer another route.
- Awaitable classification recognizes the built-in Task/ValueTask families,
  UniTask families, and async-stream roles recorded by the indexer. An `Async`
  name suffix alone is never sufficient. A user-facing registry for additional
  awaitable types is reserved for future extension and is not currently a CLI
  option.
- `callers tree` follows only resolved static invocation and object-creation
  facts. It does not infer delegate `Invoke` targets, events, callbacks,
  reflection, receiver-value/data flow, or runtime virtual/interface dispatch.
  Lambda ownership is not a caller edge.
- Caller trees exclude metadata-only callers and `System`/`System.*` callers.
  A source-backed external-looking namespace other than `System` remains in
  scope because source definition is the primary filter.
- `single-line`/`multi-line` table output replaces real TAB, CRLF, CR, LF,
  U+0085, U+2028, and U+2029 with ASCII spaces to preserve physical-record
  boundaries. This table representation is not lossless; use JSON or the
  stored normalized source when the original literal control characters matter.
- Output-path comparison normalizes extended Windows drive/UNC spellings before
  rejecting an output path equal to the active DB. Extended UNC comparison is
  covered without network access; live UNC share I/O behavior is not verified.

## Unity / Phase 3

- Unity候補の検出、`.asmdef` / `.asmref`、既定アセンブリ分割、Unity参照DLL探索、Unityバージョンシンボルは未実装です。
- Unityディレクトリを明示的にDirectoryModeで解析することはできますが、現時点では単一仮想Projectになるため、Unity向けの正確な結果としては扱えません。
- 古い特殊フォルダー規則、Version Defines、Define Constraints、platform制約も未実装です。

## Phase 4

- 非物理Source Generator出力、ファイル単位差分、`semantic_hash`、仮想呼び出し候補の精密化、Source Link、caller graph以外のgeneral call-tree views、DOT/YAML/JSONL、daemon、watch、IDE連携は未実装です。
