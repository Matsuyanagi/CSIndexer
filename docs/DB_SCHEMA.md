# Database Schema

## Version

- Current schema version: 3
- Request hash schema version: 3
- `schema_info`は必ず1行とし、未知のversionや破損を検出した場合はDBを削除・変更せずエラーにします。
- Version 2 databases and every other unsupported version are rejected without modification and must be rebuilt into a version 3 database. No ALTER migration or automatic deletion is performed; the database journal mode is not changed before a mismatch is rejected.
- `schema_info`がない場合、SQLite内部object以外のuser table / index / view / triggerが存在しない空DBだけを新規DBとして初期化します。未認識の非空DBはWAL設定・DDLより前にfail-fastし、既存object、行、journal modeを変更しません。

## Tables

- `analysis_profiles`: 入力モード、構成、TFM、RID、OS/architecture、決定的順序のpreprocessor symbols、profile hash。
- `index_runs`: input root、input fingerprint、request hash、最終更新時刻。変更なしキャッシュの判定に使用します。
- `projects`: profile/run、assembly、project path、target framework、project fingerprint。
- `documents`: 絶対正規化path、content hash、予約済みsemantic hash、生成コード情報。
- `symbols`: stable key、型/メソッド/ラムダ/initializer、表示・検索名、source span、nullable `type_kind INTEGER`（型symbolのRoslyn type kind、非型symbolはNULL）、直接非同期ロール`async_role INTEGER NOT NULL DEFAULT 0`、非同期起点までの最短距離`async_involvement_depth INTEGER`、将来互換フラグ。
- `method_parameters`: ordinal、正規化type key、ref kind、optional。
- `calls`: invocation/reference分類、static/virtual/interface/dynamic dispatch、resolution status/reason、呼び出し結果の利用方法`async_usage_kind INTEGER NOT NULL DEFAULT 0`、source span。
- `call_candidates`: 曖昧呼び出しの全候補。
- `symbol_relations`: inherits、implements、overrides、interface implementation、partial関係。
- `interface_method_bindings`: profileごとの実装型、正確なinterface contract method、実際のimplementation methodの対応。interface-rooted queryのbranch contextを保持します。
- `conditional_symbols_used`: Documentごとの条件付きシンボルと出現回数。

`docs/SPEC.md` 19.2の必須indexに加え、`index_runs`のキャッシュ検索indexを持ちます。FTS5は使用していません。

## Override-aware method-search storage

The version 3 `interface_method_bindings` table and its foreign keys are:

```sql
CREATE TABLE interface_method_bindings (
    analysis_profile_id      INTEGER NOT NULL,
    implementing_type_id     INTEGER NOT NULL,
    interface_method_id      INTEGER NOT NULL,
    implementation_method_id INTEGER NOT NULL,

    PRIMARY KEY (
        analysis_profile_id,
        implementing_type_id,
        interface_method_id,
        implementation_method_id
    ),

    FOREIGN KEY(analysis_profile_id)
      REFERENCES analysis_profiles(id),

    FOREIGN KEY(implementing_type_id)
      REFERENCES symbols(id) ON DELETE CASCADE,

    FOREIGN KEY(interface_method_id)
      REFERENCES symbols(id) ON DELETE CASCADE,

    FOREIGN KEY(implementation_method_id)
      REFERENCES symbols(id) ON DELETE CASCADE
);

CREATE INDEX ix_interface_method_bindings_contract
ON interface_method_bindings(analysis_profile_id, interface_method_id);

CREATE INDEX ix_interface_method_bindings_type
ON interface_method_bindings(analysis_profile_id, implementing_type_id);
```

`symbols.type_kind` is intentionally nullable: it is populated for type
symbols and remains NULL for all other symbol kinds.

## Update Transaction

1. Roslyn結果を`IndexSnapshot`としてメモリに完成させます。
2. 1つのSQLite transaction内で同一Profileの旧runを削除します。
3. profile/run/project/document/symbol/call/relation/interface bindingをprepared commandで挿入します。
4. `PRAGMA foreign_key_check`を実行します。
5. 成功時だけcommitし、例外・キャンセル時は以前のindexを保持します。

Foreign keyは有効、journal modeはWALです。テストと短命CLIでファイルを確実に解放するためconnection poolingは無効です。

`async_role`と`async_usage_kind`はCore enumの整数値を保存し、`async_involvement_depth`は非同期起点で0、呼び出し元へ1ずつ増加、非関与時はNULLです。`QueryRepository`のすべてのsymbol/call readerがこれらの列を復元するため、RoslynワークスペースなしのDB-only queryでも同じ値を取得できます。

空の新規DBと対応済みversion 3 DBにだけWALを設定します。version 2を含むversion不一致時と`schema_info`のない非空DBでは例外を返し、テーブル、行、journal modeを変更しません。connection-localな`PRAGMA foreign_keys=ON`だけはschema検査前に設定します。
