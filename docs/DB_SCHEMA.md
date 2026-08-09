# Database Schema

## Version

- Current schema version: 4
- Request hash schema version: 4
- `schema_info`は必ず1行とし、未知のversionや破損を検出した場合はDBを削除・変更せずエラーにします。
- Version 1, 2, 3, and every other unsupported database version are rejected without modification and must be rebuilt into a version 4 database. No ALTER migration or automatic deletion is performed; the database journal mode is not changed before a mismatch is rejected.
- `schema_info`がない場合、SQLite内部object以外のuser table / index / view / triggerが存在しない空DBだけを新規DBとして初期化します。未認識の非空DBはWAL設定・DDLより前にfail-fastし、既存object、行、journal modeを変更しません。

## Tables

- `analysis_profiles`: 入力モード、構成、TFM、RID、OS/architecture、決定的順序のpreprocessor symbols、profile hash。
- `index_runs`: input root、input fingerprint、request hash、最終更新時刻。変更なしキャッシュの判定に使用します。
- `projects`: profile/run、assembly、project path、target framework、project fingerprint。
- `documents`: 絶対正規化path、content hash、予約済みsemantic hash、生成コード情報。
- `symbols`: project-scoped source stable key、型/メソッド/ラムダ/initializer、表示・検索名、source span、nullable `type_kind INTEGER`（型symbolのRoslyn type kind、非型symbolはNULL）、`method_kind`、`accessibility`、`is_static`、direct async role `async_role INTEGER NOT NULL DEFAULT 0`、async-origin distance `async_involvement_depth INTEGER`、persisted next-hop ID `async_next_symbol_id INTEGER`、`return_type_key TEXT`、`normalized_source TEXT`、and `normalized_source_hash BLOB`。source定義は所属project keyをstable keyへ含め、metadata-only symbolはassembly/TFM identityを共有します。
- `method_parameters`: ordinal、正規化type key、ref kind、optional。
- `calls`: invocation/reference分類、static/virtual/interface/dynamic dispatch、resolution status/reason、呼び出し結果の利用方法`async_usage_kind INTEGER NOT NULL DEFAULT 0`、source span。
- `call_candidates`: 曖昧呼び出しの全候補。
- `symbol_relations`: inherits、implements、overrides、interface implementation、partial関係。
- `interface_method_bindings`: profileごとの実装型、正確なinterface contract method、実際のimplementation methodの対応。interface-rooted queryのbranch contextを保持します。
- `conditional_symbols_used`: Documentごとの条件付きシンボルと出現回数。

`docs/SPEC.md` 19.2の必須indexに加え、`index_runs`のキャッシュ検索indexを持ちます。FTS5は使用していません。

## Version 4 executable-source and async-path storage

The following version 4 columns are part of `symbols` in addition to the
pre-existing identity, location, and async-role fields:

```sql
return_type_key          TEXT,
normalized_source        TEXT,
normalized_source_hash   BLOB,
async_next_symbol_id     INTEGER,

FOREIGN KEY(containing_symbol_id)
  REFERENCES symbols(id) ON DELETE SET NULL,

FOREIGN KEY(async_next_symbol_id)
  REFERENCES symbols(id) ON DELETE SET NULL
```

`normalized_source` is the token-normalized executable syntax and
`normalized_source_hash` is its SHA-256 hash. A source-backed definition is
identified by a non-null `source_document_id`; metadata-only symbols retain no
normalized source. `async_next_symbol_id` is null for async origins (depth
zero) and points to the one selected next symbol for a non-origin. It is not a
set of alternate routes.

Version 4の実行可能シンボルは、メソッド、コンストラクター、ローカル関数、ラムダ、アクセサー、演算子、変換演算子を含みます。`method_kind`、`accessibility`、`is_static`、`return_type_key`、`containing_symbol_id`、`source_document_id`、`source_start`、`source_length`により、宣言kind、適用可能な属性、owner、元ファイル・範囲を復元します。field/property/event initializerは`Namespace.Type::<initializer:memberName>`形式のsource-backed合成ownerとして`symbols`へ保存し、そのIDをラムダの所有関係に使用します。表示名だけをforeign keyの代わりに使用しません。

`normalized_source`はRoslynのactive tokenから生成し、コメント、documentation trivia、directive、inactive branch、literal外のlayoutを除きます。literal tokenの`Text`はそのまま保持し、隣接tokenの再字句解析結果が変わる場合だけ1空白を補います。したがって複数行raw literalの内部改行は保存される場合があります。任意substring検索にはB-tree/FTS indexを設けず、source-backed executable候補へ絞った後に評価します。

Version 4 defines the following `symbols` indexes:

```sql
CREATE INDEX ix_symbols_profile_kind
ON symbols(analysis_profile_id, kind);

CREATE INDEX ix_symbols_profile_containing
ON symbols(analysis_profile_id, containing_symbol_id);

CREATE INDEX ix_symbols_profile_async_depth
ON symbols(analysis_profile_id, async_involvement_depth);

CREATE INDEX ix_symbols_profile_name
ON symbols(analysis_profile_id, name);

CREATE INDEX ix_symbols_profile_short_method
ON symbols(analysis_profile_id, type_simple_name, name, parameter_count);

CREATE INDEX ix_symbols_profile_namespace_type_method
ON symbols(analysis_profile_id, namespace_name, type_simple_name, name, parameter_count);

CREATE INDEX ix_symbols_profile_fully_qualified
ON symbols(analysis_profile_id, fully_qualified_name);

CREATE INDEX ix_symbols_location
ON symbols(source_document_id, source_start);

CREATE INDEX ix_symbols_profile_async_next
ON symbols(analysis_profile_id, async_next_symbol_id);

CREATE INDEX ix_symbols_profile_source_executable
ON symbols(analysis_profile_id, kind)
WHERE source_document_id IS NOT NULL;
```

The other indexes remain: `ix_index_runs_cache`, `ix_calls_callee`,
`ix_calls_caller`, `ix_calls_location`, `ix_relations_target`,
`ix_interface_method_bindings_contract`, and
`ix_interface_method_bindings_type`. The previous non-profile-prefixed symbol
name/component indexes are not created by schema v4. Source-only repository
queries use a direct `source_document_id IS NOT NULL` predicate so SQLite can
use the partial executable index. PRAGMA tests lock the exact columns and
partial flag; representative `EXPLAIN QUERY PLAN` tests require the intended
index and reject a full `symbols` scan. Arbitrary substring matching against
`normalized_source` deliberately has no B-tree index or FTS table.

## Override-aware method-search storage

The version 4 `interface_method_bindings` table and its foreign keys are:

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
3. profile/run/project/document/symbol rowsをprepared commandで挿入します。
4. 全symbolのnumeric IDが確定してから、同じtransaction内で
   `containing_symbol_id`と`async_next_symbol_id`をstable keyから更新します。
5. parameter/call/relation/interface-binding/conditional-symbol rowsを挿入します。
6. `PRAGMA foreign_key_check`を実行します。
7. 成功時だけcommitし、例外・キャンセル時は以前のindexを保持します。

Foreign keyは有効、journal modeはWALです。テストと短命CLIでファイルを確実に解放するためconnection poolingは無効です。

`async_role`と`async_usage_kind`はCore enumの整数値を保存します。
`async_involvement_depth` is zero at an async origin, increases by one toward
callers, and is NULL for a non-involved symbol. `async_next_symbol_id` records
the one deterministic next hop toward that origin. Every symbol/call reader in
`QueryRepository` reconstructs these fields, return type, method kind, and
normalized source for DB-only queries; Roslyn is not loaded while querying.
Query-time reconstruction rejects a depth-zero row without a direct async
origin role, non-executable or source-less hops, missing normalized source,
incoherent depth/next state, cross-profile hops, and cycles.
Index-time propagation uses the same source-backed method/lambda eligibility
for origins and both call endpoints, so normal metadata awaitable calls cannot
create a chain that query-time reconstruction would reject.

空の新規DBと対応済みversion 4 DBにだけWALを設定します。version 3を含むversion不一致時と`schema_info`のない非空DBでは例外を返し、テーブル、行、journal modeを変更しません。connection-localな`PRAGMA foreign_keys=ON`だけはschema検査前に設定します。
