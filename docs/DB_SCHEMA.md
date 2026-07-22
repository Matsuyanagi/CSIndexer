# Database Schema

## Version

- Current schema version: 2
- Request hash schema version: 2
- `schema_info`は必ず1行とし、未知のversionや破損を検出した場合はDBを削除・変更せずエラーにします。
- version 1からのALTER migrationは提供しません。非対応versionはfail-fastし、検査前のDBのjournal modeも変更しません。
- `schema_info`がない場合、SQLite内部object以外のuser table / index / view / triggerが存在しない空DBだけを新規DBとして初期化します。未認識の非空DBはWAL設定・DDLより前にfail-fastし、既存object、行、journal modeを変更しません。

## Tables

- `analysis_profiles`: 入力モード、構成、TFM、RID、OS/architecture、決定的順序のpreprocessor symbols、profile hash。
- `index_runs`: input root、input fingerprint、request hash、最終更新時刻。変更なしキャッシュの判定に使用します。
- `projects`: profile/run、assembly、project path、target framework、project fingerprint。
- `documents`: 絶対正規化path、content hash、予約済みsemantic hash、生成コード情報。
- `symbols`: stable key、型/メソッド/ラムダ/initializer、表示・検索名、source span、直接非同期ロール`async_role INTEGER NOT NULL DEFAULT 0`、非同期起点までの最短距離`async_involvement_depth INTEGER`、将来互換フラグ。
- `method_parameters`: ordinal、正規化type key、ref kind、optional。
- `calls`: invocation/reference分類、static/virtual/interface/dynamic dispatch、resolution status/reason、呼び出し結果の利用方法`async_usage_kind INTEGER NOT NULL DEFAULT 0`、source span。
- `call_candidates`: 曖昧呼び出しの全候補。
- `symbol_relations`: inherits、implements、overrides、interface implementation、partial関係。
- `conditional_symbols_used`: Documentごとの条件付きシンボルと出現回数。

`docs/SPEC.md` 19.2の必須indexに加え、`index_runs`のキャッシュ検索indexを持ちます。FTS5は使用していません。

## Update Transaction

1. Roslyn結果を`IndexSnapshot`としてメモリに完成させます。
2. 1つのSQLite transaction内で同一Profileの旧runを削除します。
3. profile/run/project/document/symbol/call/relationをprepared commandで挿入します。
4. `PRAGMA foreign_key_check`を実行します。
5. 成功時だけcommitし、例外・キャンセル時は以前のindexを保持します。

Foreign keyは有効、journal modeはWALです。テストと短命CLIでファイルを確実に解放するためconnection poolingは無効です。

`async_role`と`async_usage_kind`はCore enumの整数値を保存し、`async_involvement_depth`は非同期起点で0、呼び出し元へ1ずつ増加、非関与時はNULLです。`QueryRepository`のすべてのsymbol/call readerがこれらの列を復元するため、RoslynワークスペースなしのDB-only queryでも同じ値を取得できます。

空の新規DBと対応済みversion 2 DBにだけWALを設定します。version不一致時と`schema_info`のない非空DBでは例外を返し、テーブル、行、journal modeを変更しません。connection-localな`PRAGMA foreign_keys=ON`だけはschema検査前に設定します。
