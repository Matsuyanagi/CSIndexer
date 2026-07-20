# Database Schema

## Version

- Current schema version: 1
- `schema_info`は必ず1行とし、未知のversionや破損を検出した場合はDBを削除・変更せずエラーにします。

## Tables

- `analysis_profiles`: 入力モード、構成、TFM、RID、OS/architecture、決定的順序のpreprocessor symbols、profile hash。
- `index_runs`: input root、input fingerprint、request hash、最終更新時刻。変更なしキャッシュの判定に使用します。
- `projects`: profile/run、assembly、project path、target framework、project fingerprint。
- `documents`: 絶対正規化path、content hash、予約済みsemantic hash、生成コード情報。
- `symbols`: stable key、型/メソッド/ラムダ/initializer、表示・検索名、source span、将来互換フラグ。
- `method_parameters`: ordinal、正規化type key、ref kind、optional。
- `calls`: invocation/reference分類、static/virtual/interface/dynamic dispatch、resolution status/reason、source span。
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
