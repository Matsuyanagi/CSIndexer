# Implementation Status

## Current Phase

Phase 1（部分再解析を除く実用版） / Phase 2（完了） / Phase 4 async analysis, symbol/source search, and bounded graphs（completed） / Override-aware method search（completed） / CLI query and output contract revision（Tasks 1--9 completed）

## Last Completed Work

- .NET 10 Windows CLI、Roslyn、SQLiteの責務分離されたソリューションを作成
- MSBuildWorkspaceによるProject/Solution入力とAdhocWorkspaceによるDirectory入力を実装
- 型、メソッド、コンストラクター、ローカル関数、ラムダ、呼び出し、参照、継承関係を抽出
- SQLiteスキーマv4、原子的な更新、破損・非対応DB検出、変更なしキャッシュを実装。schema v3は非変更で拒否し、再indexを要求する
- 全検索コマンド、table / JSON出力、生成コードフィルターを実装
- Phase 1/2の自動受け入れテストとCLIプロセス試験を完了
- Roslynで`AsyncRole`と`AsyncUsageKind`を抽出し、Task/ValueTask/UniTask、UniTaskVoid、非同期ストリーム、await/await foreach/await usingを分類
- 解決済み呼び出しの逆辺を使う循環安全な複数始点BFSで、非同期起点へ到達する呼び出し元の最短`AsyncInvolvementDepth`を算出
- 非同期情報をSQLiteへ保存・DB-only復元し、既存CLIのJSON/table出力へ追加
- `symbol list`を追加し、既定でmethodとlambdaを一覧、`--kind method|lambda`と`--async-involved`で絞り込み可能にした
- `--short-names`でtable表示およびJSONの`displayName`だけを短縮し、`fullyQualifiedName`を含むcanonical JSON fieldは不変にした
- ラムダはownerごとに`<lambda#1>`から採番し、`callees`はネストしたラムダdescendantの呼び出しを再帰的に既定で含め、`--exclude-lambda-calls`で直接呼び出しへ限定可能にした
- Added opt-in `--include-overrides` support to `symbol find`, method-query `definition`, `references`, `callers`, and `callees`; the default remains exact method lookup.
- Added branch-scoped interface method bindings, nullable `symbols.type_kind`, inherited real-declaration alias resolution, and descendant-only query-time expansion. Its original schema-v3 decision is superseded by the schema-v4 rebuild requirement.
- Added return type, method kind, normalized executable source, and SHA-256 source hash persistence for methods, constructors, local functions, lambdas, accessors, operators, and conversions.
- Added function-scoped source-order lambda numbering, initializer owners for fields/properties/events, and preserved immediate lambda containment for call ownership.
- Added `async_next_symbol_id` and deterministic reverse-BFS path selection; `async tree` reconstructs and validates one persisted path.
- Added exact/wildcard/component/regex `symbol find`, normalized-source show/search and source predicates, and bounded caller-tree output in text, Mermaid, and JSON.
- Source-definition stable keys now include the owning project key, so same-profile projects with identical assembly/TFM/FQN remain separate while metadata-only symbols stay assembly-scoped.
- Long-running normalization, extraction ordering, async propagation, source filtering, caller traversal, and output ordering observe in-flight cancellation.
- Async propagation and reconstruction share source-backed method/lambda eligibility, validate executable/origin state before truncation, and exclude metadata-only awaitable hops; finite caller-tree boundaries retain internal cycle/cross edges.
- Schema v4 includes profile-prefixed symbol indexes and a partial source-executable index, with PRAGMA and `EXPLAIN QUERY PLAN` regression tests.
- Graph-root ambiguity reports deterministic canonical candidates, duplicate names include document path and ID, and global/command help is snapshot-tested against the accepted grammar.
- Added direct acceptance coverage for nested/all-same-ordinal lambda search, reverse insertion ties, final numeric-ID ordering, duplicate projects, excluded reverse callers, all executable declaration signature kinds, literal variants, corruption, and in-flight cancellation.
- 承認済みのsymbol/source/graph要件を役割別の永続文書へ統合した。`SPEC.md`第33章を正式仕様、`CLI.md`をコマンド契約、`DB_SCHEMA.md`をDB契約、`DECISIONS.md`を判断履歴、`TEST_PLAN.md`を正式な受け入れmatrixとし、独立していた旧要求仕様ファイルを廃止した。
- 実行可能targetを扱うcommandへ共通の`--kind all|method|lambda`と`--async-status all|async|sync`を追加した。filterはdirect `AsyncRole`とtarget/root解決へだけ適用し、`--async-involved`の派生到達性およびgraph/caller/calleeの二次表示とは区別する。
- ラムダtarget grammarをflat queryとgraph rootへ共通化し、method overrideを展開した後にkind/direct-async filterを適用する。delegate `Invoke`、event、callback、reflection、runtime flowからlambda call/reference edgeは生成しない。
- active format optionを`--output-format`へ変更し、`-o` / `--output-file`によるBOMなしUTF-8の同一formatter payload出力を実装した。same-directory temporary file、成功時commit、失敗/cancel時cleanup、DB path同一拒否を含む。
- source tableに既定`single-line`の固定record schemaと`multi-line`互換layoutを追加した。table表示だけでTAB、CRLF、CR、LF、U+0085、U+2028、U+2029をASCII spaceへsanitizeし、DB/hash/search/JSONはlosslessに保持する。
- zero-width array-rank tokenを正規化およびseparator判定から除外し、schema v4を維持したまま`AnalysisCacheVersion = 2`でcache reuseを無効化した。次回index requestは自動再解析されるが、既存DBを直接queryする場合は`index --rebuild`で正規化ソースを更新する。
- Task 8で、上記の正式仕様、CLI契約、decision、acceptance-test mapping、status、limitationを同期した。
- Task 9で、最終diff、format、Release build、全test、全command help、stdout/file等価性をfresh runし、下記の公式検証recordを更新した。

## Currently Implementing

- なし。

## Next Actions

1. 入力変更時のプロジェクト単位再解析と参照元プロジェクトの無効化を実装
2. Phase 3のUnityアセンブリ復元へ着手

## Build Status

- Command: `rtk dotnet build CsIndex.sln -c Release --no-restore`
- Result: 9 projects; 0 warnings, 0 errors
- Date: 2026-08-13
- Environment note: the sandboxed first attempt stopped before compilation with
  8 `MSB4184` SDK-discovery access errors. The identical approved rerun produced
  the successful result above.

## Test Status

- Command: `rtk dotnet test CsIndex.sln -c Release --no-build --no-restore`
- Passed: 403 across 4 test projects
- Failed: 0
- Skipped: 0
- Warnings: 0
- Date: 2026-08-13
- Additional gates: `rtk dotnet format CsIndex.sln --verify-no-changes --no-restore`
  exited 0; all 14 global/command help probes passed with no active legacy
  `--output` entry; a fresh-index JSON smoke produced byte-identical stdout and
  `--output-file` payloads (48,271 bytes), empty redirected stdout, identical
  diagnostics, valid JSON, and no UTF-8 BOM.

## Known Broken Areas

- なし

## Important Files Changed

- src/CsIndex.Core/Analysis/SemanticExtractor.cs
- src/CsIndex.Core/Analysis/AsyncSymbolClassifier.cs
- src/CsIndex.Core/Analysis/AsyncOperationClassifier.cs
- src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs
- src/CsIndex.Core/Analysis/SourceNormalizer.cs
- src/CsIndex.Core/Caching/RequestHasher.cs
- src/CsIndex.Core/Input/WorkspaceLoader.cs
- src/CsIndex.Storage/SqliteIndex.cs
- src/CsIndex.Storage/Schema/SchemaMigrator.cs
- src/CsIndex.Query/SemanticQueryService.cs
- src/CsIndex.Query/AsyncPathResolver.cs
- src/CsIndex.Query/CallerTreeBuilder.cs
- src/CsIndex.Query/Symbols/SymbolPatternMatcher.cs
- src/CsIndex.Query/Symbols/SourceTextFilter.cs
- src/CsIndex.Cli/Program.cs
- src/CsIndex.Cli/CliArguments.cs
- src/CsIndex.Cli/OutputFormatter.cs
- src/CsIndex.Cli/GraphOutputFormatter.cs
- src/CsIndex.Cli/OutputDestination.cs
- src/CsIndex.Cli/TableTextSanitizer.cs
- src/CsIndex.Cli/SourceLayout.cs
- tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs
- tests/CsIndex.Core.Tests/AsyncInvolvementPropagatorTests.cs
- tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs
- tests/CsIndex.Core.Tests/SourceNormalizerTests.cs
- tests/CsIndex.Core.Tests/RequestHasherTests.cs
- tests/CsIndex.Core.Tests/ProjectScopedSourceSymbolIdentityTests.cs
- tests/CsIndex.Core.Tests/SemanticExtractorCancellationTests.cs
- tests/CsIndex.IntegrationTests/CliCommandTests.cs
- tests/CsIndex.IntegrationTests/OutputFormatterTests.cs
- tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs
- tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs
- tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs
- tests/CsIndex.IntegrationTests/GraphQueryTests.cs
- tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs
- tests/CsIndex.IntegrationTests/ProjectScopedSourceSymbolPersistenceTests.cs
- tests/CsIndex.Query.Tests/CallerTreeBuilderTests.cs
- docs/SPEC.md
- docs/CLI.md
- docs/DB_SCHEMA.md
- docs/DECISIONS.md
- docs/TEST_PLAN.md
- docs/KNOWN_LIMITATIONS.md
- docs/IMPLEMENTATION_STATUS.md

## Database Schema Version

- 4（schema形状は不変。正規化source修正のcache invalidationは`AnalysisCacheVersion = 2`で行う）

## CLI Commands Implemented

- `index`
- `symbol find`
- `async tree`
- `callers tree`
- `source show`
- `source search`
- `symbol list`
- `definition` / `definition --at`
- `references`
- `callers`
- `callees`
- `overrides`
- `conditions`

`index`以外の結果payload commandは`--output-format`と`-o` / `--output-file`を受理する。`--kind`と`--async-status`は実行可能target/rootを持つcommandだけに適用し、`conditions`と`index`は拒否する。

## Pending Decisions

- DEC-0014（複数TFM/Profileにまたがる表示規則）
- DEC-0015（Unity Version Definesの厳密な復元）
