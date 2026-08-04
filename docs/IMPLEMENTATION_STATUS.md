# Implementation Status

## Current Phase

Phase 1（部分再解析を除く実用版） / Phase 2（完了） / Phase 4 非同期関与解析・関数一覧/ラムダ呼び出し出力（完了） / Override-aware method search（completed）

## Last Completed Work

- .NET 10 Windows CLI、Roslyn、SQLiteの責務分離されたソリューションを作成
- MSBuildWorkspaceによるProject/Solution入力とAdhocWorkspaceによるDirectory入力を実装
- 型、メソッド、コンストラクター、ローカル関数、ラムダ、呼び出し、参照、継承関係を抽出
- SQLiteスキーマv3、原子的な更新、破損・非対応DB検出、変更なしキャッシュを実装
- 全検索コマンド、table / JSON出力、生成コードフィルターを実装
- Phase 1/2の自動受け入れテストとCLIプロセス試験を完了
- Roslynで`AsyncRole`と`AsyncUsageKind`を抽出し、Task/ValueTask/UniTask、UniTaskVoid、非同期ストリーム、await/await foreach/await usingを分類
- 解決済み呼び出しの逆辺を使う循環安全な複数始点BFSで、非同期起点へ到達する呼び出し元の最短`AsyncInvolvementDepth`を算出
- 非同期情報をSQLiteへ保存・DB-only復元し、既存CLIのJSON/table出力へ追加
- `symbol list`を追加し、既定でmethodとlambdaを一覧、`--kind method|lambda`と`--async-involved`で絞り込み可能にした
- `--short-names`でtable表示およびJSONの`displayName`だけを短縮し、`fullyQualifiedName`を含むcanonical JSON fieldは不変にした
- ラムダはownerごとに`<lambda#1>`から採番し、`callees`はネストしたラムダdescendantの呼び出しを再帰的に既定で含め、`--exclude-lambda-calls`で直接呼び出しへ限定可能にした
- Added opt-in `--include-overrides` support to `symbol find`, method-query `definition`, `references`, `callers`, and `callees`; the default remains exact method lookup.
- Added branch-scoped interface method bindings, nullable `symbols.type_kind`, inherited real-declaration alias resolution, and descendant-only query-time expansion. Schema and request-hash versions are now 3; version 2 databases are rejected and require rebuilding.

## Currently Implementing

- なし

## Next Actions

1. 入力変更時のプロジェクト単位再解析と参照元プロジェクトの無効化を実装
2. Phase 3のUnityアセンブリ復元へ着手

## Build Status

- Command: `dotnet build CsIndex.sln --configuration Release`
- Result: 成功（警告0、エラー0）
- Date: 2026-08-05

## Test Status

- Command: `dotnet test CsIndex.sln --configuration Release`
- Passed: 125
- Failed: 0
- Skipped: 0
- Date: 2026-08-05

## Known Broken Areas

- なし

## Important Files Changed

- src/CsIndex.Core/Analysis/SemanticExtractor.cs
- src/CsIndex.Core/Analysis/AsyncSymbolClassifier.cs
- src/CsIndex.Core/Analysis/AsyncOperationClassifier.cs
- src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs
- src/CsIndex.Core/Input/WorkspaceLoader.cs
- src/CsIndex.Storage/SqliteIndex.cs
- src/CsIndex.Storage/Schema/SchemaMigrator.cs
- src/CsIndex.Query/SemanticQueryService.cs
- src/CsIndex.Cli/Program.cs
- src/CsIndex.Cli/OutputFormatter.cs
- tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs
- tests/CsIndex.Core.Tests/AsyncInvolvementPropagatorTests.cs
- tests/CsIndex.IntegrationTests/OutputFormatterTests.cs
- tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs

## Database Schema Version

- 3

## CLI Commands Implemented

- `index`
- `symbol find`
- `symbol list`
- `definition` / `definition --at`
- `references`
- `callers`
- `callees`
- `overrides`
- `conditions`

## Pending Decisions

- DEC-0014（複数TFM/Profileにまたがる表示規則）
- DEC-0015（Unity Version Definesの厳密な復元）
