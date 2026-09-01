# Implementation Status

## Current Phase

Phase 1（部分再解析を除く実用版） / Phase 2（完了） / Phase 4 async analysis, symbol/source search, and bounded graphs（completed） / Override-aware method search（completed） / Canonical C# symbol paths, typed search, logical declarations, and portable schema-5 index（implementation Tasks 1--13 completed; Task 14 documentation/final closure in progress）

## Last Completed Work

- .NET 10 Windows CLI、Roslyn、SQLiteの責務分離されたソリューションを作成
- MSBuildWorkspaceによるProject/Solution入力とAdhocWorkspaceによるDirectory入力を実装
- 型、メソッド、コンストラクター、ローカル関数、ラムダ、呼び出し、参照、継承関係を抽出
- SQLite schema version 5、analysis-cache version 3、logical symbol / physical declaration分離、portable root anchor、原子的な更新、破損・非対応DBの非変更拒否を実装
- 全検索コマンド、table / JSON出力、生成コードフィルターを実装
- Phase 1/2の自動受け入れテストとCLIプロセス試験を完了
- Roslynで`AsyncRole`と`AsyncUsageKind`を抽出し、Task/ValueTask/UniTask、UniTaskVoid、非同期ストリーム、await/await foreach/await usingを分類
- 解決済み呼び出しの逆辺を使う循環安全な複数始点BFSで、非同期起点へ到達する呼び出し元の最短`AsyncInvolvementDepth`を算出
- 非同期情報をSQLiteへ保存・DB-only復元し、既存CLIのJSON/table出力へ追加
- `symbol list`を追加し、既定でmethodとlambdaを一覧、`--kind method|lambda`と`--async-involved`で絞り込み可能にした
- `--short-names`でtable表示およびJSONの`displayName`だけを短縮し、`fullyQualifiedName`を含むcanonical JSON fieldは不変にした
- ラムダとanonymous methodはimmediate ownerごとの共有source-order ordinalで`<lambda#1>` / `<anonymous-method#2>`として採番し、`callees`はネストしたlambda descendantの呼び出しを再帰的に既定で含め、`--exclude-lambda-calls`で直接呼び出しへ限定可能にした
- Added opt-in `--include-overrides` support to `symbol find`, method-query `definition`, `references`, `callers`, and `callees`; the default remains exact method lookup.
- Added branch-scoped interface method bindings, nullable `symbols.type_kind`, inherited real-declaration alias resolution, and descendant-only query-time expansion. Its original schema-v3 decision is superseded by the schema-v4 rebuild requirement.
- Added return type, method kind, normalized executable source, and SHA-256 source hash persistence for methods, constructors, local functions, lambdas, accessors, operators, and conversions.
- Added immediate-owner source-order lambda/anonymous-method numbering, initializer owners for fields/properties/events, top-level owners, and preserved immediate executable containment for call ownership.
- Added `async_next_symbol_id` and deterministic reverse-BFS path selection; `async tree` reconstructs and validates one persisted path.
- Added structured csharp-suffix/explicit-exact symbol paths, independently typed glob/literal/regex conditions and case categories, normalized-source show/search, and bounded caller-tree output in text, Mermaid, and JSON.
- Source-definition stable keys now include the owning project key, so same-profile projects with identical assembly/TFM/FQN remain separate while metadata-only symbols stay assembly-scoped.
- Long-running normalization, extraction ordering, async propagation, source filtering, caller traversal, and output ordering observe in-flight cancellation.
- Async propagation and reconstruction share source-backed method/lambda eligibility, validate executable/origin state before truncation, and exclude metadata-only awaitable hops; finite caller-tree boundaries retain internal cycle/cross edges.
- Schema version 5 includes semantic path indexes, logical/declaration uniqueness and role constraints, profile-prefixed indexes, and a partial source-executable index, with PRAGMA and query-plan regression tests.
- Graph-root ambiguity reports deterministic canonical candidates, duplicate names include document path and ID, and global/command help is snapshot-tested against the accepted grammar.
- Added direct acceptance coverage for nested/all-same-ordinal lambda search, reverse insertion ties, final numeric-ID ordering, duplicate projects, excluded reverse callers, all executable declaration signature kinds, literal variants, corruption, and in-flight cancellation.
- 承認済みのsymbol/source/graph要件を役割別の永続文書へ統合した。現在は`SPEC.md`第34章を正式仕様、`CLI.md`をコマンド契約、`DB_SCHEMA.md`をschema version 5 DB契約、`DECISIONS.md`を判断履歴、`TEST_PLAN.md`を正式な受け入れmatrixとする。
- 実行可能targetを扱うcommandへ共通の`--kind all|method|lambda`と`--async-status all|async|sync`を追加した。filterはdirect `AsyncRole`とtarget/root解決へだけ適用し、`--async-involved`の派生到達性およびgraph/caller/calleeの二次表示とは区別する。
- ラムダtarget grammarをflat queryとgraph rootへ共通化し、method overrideを展開した後にkind/direct-async filterを適用する。delegate `Invoke`、event、callback、reflection、runtime flowからlambda call/reference edgeは生成しない。
- active format optionを`--output-format`へ変更し、`-o` / `--output-file`によるBOMなしUTF-8の同一formatter payload出力を実装した。same-directory temporary file、成功時commit、失敗/cancel時cleanup、DB path同一拒否を含む。
- source tableに既定`single-line`の固定record schemaと`multi-line`互換layoutを追加した。table表示だけでTAB、CRLF、CR、LF、U+0085、U+2028、U+2029をASCII spaceへsanitizeし、DB/hash/search/JSONはlosslessに保持する。
- zero-width array-rank tokenを正規化およびseparator判定から除外した。現在はschema version 5 / `AnalysisCacheVersion = 3`で旧display path、rooted path、anonymous marker、split partial identityを含むcache reuseを防ぐ。旧DBは暗黙rebuildせず非変更で拒否する。
- Added canonical csharp/explicit parsing and formatting, complete source-callable special segments, semantic identity ordering, typed condition compilation, root-first command orchestration, one-logical/two-declaration partial persistence, portable standard/custom DB relocation, read-only `--base-dir`, and absolute/relative path presentation.
- Added terminal concise/verbose help for global and every recognized command; `--help --verbose` and `--help-verbose` are byte-identical and dependency-free after option-scope validation.
- Task 8で、上記の正式仕様、CLI契約、decision、acceptance-test mapping、status、limitationを同期した。
- Task 9で、最終diff、format、Release build、全test、全command help、stdout/file等価性をfresh runし、下記の公式検証recordを更新した。

## Currently Implementing

- Task 14: the focused help/document synchronization slice is complete; the
  primary-owned full-solution gates, schema/path probes, reviews, and branch
  closure remain pending.

## Next Actions

1. Primary agentがTask 14のfull-solution final gates、schema/path probes、independent reviews、branch completionを実施
2. 入力変更時のプロジェクト単位再解析と参照元プロジェクトの無効化を実装
3. Phase 3のUnityアセンブリ復元へ着手

## Build Status

Task 14 pre-review full-solution verification:

- Command: `rtk dotnet build CsIndex.sln -c Release --no-restore`
- Result: 9 projects; 0 warnings, 0 errors
- Date: 2026-09-01
- Environment note: the sandboxed first attempt stopped before compilation with
  8 `MSB4184` SDK-discovery access errors. The identical approved rerun produced
  the successful result above.

## Test Status

Task 14 pre-review full-solution verification:

- Command: `rtk dotnet test CsIndex.sln -c Release --no-build --no-restore`
- Passed: 1,176 across 4 test projects
- Failed: 0
- Skipped: 0
- Warnings: 0
- Date: 2026-09-01
- Additional gates: `rtk dotnet format CsIndex.sln --verify-no-changes --no-restore`
  exited 0; `rtk git diff --check` exited 0.

## Task 14 Focused Verification (2026-09-01)

- `VerboseHelpTests`: exit 0; 13 passed, 0 failed, 0 skipped, 0 warnings.
- Combined `CliSymbolPathOptionMatrixTests | VerboseHelpTests | CliCommandTests`:
  exit 0; 196 passed, 0 failed, 0 skipped, 0 warnings.
- CLI Release build: exit 0; 4 projects, 0 errors, 0 warnings.
- `rtk git diff --check`: exit 0; no whitespace errors.
- The documented version-5 DDL is ordinally identical to the SQL raw string in
  `SchemaMigrator.CreateVersionFiveAsync` after raw-string indentation and
  newline normalization (9,981 characters each).
- The built CLI returned exit 0 and empty stderr for normal help and all three
  orderings/spellings of verbose help in global plus 14 recognized command
  scopes. The three verbose outputs were byte-identical in every scope.
- Fresh standard/custom indexes created through the built CLI reported schema
  version 5, anchors `..` and `../workspace`, relative project/document paths,
  zero rooted persisted path/key values, and zero foreign-key violations. The
  partial probe persisted one logical callable, two role rows, and selected the
  implementation declaration as preferred.
- Fifteen positive executable probes covered csharp suffix breadth, explicit
  exactness and copy/re-query, namespace omission/global namespace, local and
  lambda containment, a special accessor, mixed typed/source conditions,
  root-only callee filtering, partial declaration/source projection,
  standard/custom relocation, relative output, and `--base-dir` override.
- Eleven negative probes covered legacy grammar/options, malformed delimiters,
  invalid regex/case/style, forbidden option scope, ambiguous graph root,
  `--require-single`, schema 4, empty stdout, and output/database sentinel
  preservation. Dedicated PP08 and OH09 cross-volume/UNC and atomic-failure
  acceptance tests both passed separately with zero warnings.

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
- src/CsIndex.Query/Symbols/SymbolPathParser.cs
- src/CsIndex.Query/Symbols/SymbolPathResolver.cs
- src/CsIndex.Query/Symbols/TypedConditionCompiler.cs
- src/CsIndex.Query/Symbols/StructuralGlobMatcher.cs
- src/CsIndex.Query/Symbols/SymbolCanonicalComparer.cs
- src/CsIndex.Core/Symbols/SymbolSignatureCanonicalizer.cs
- src/CsIndex.Core/Symbols/SymbolPathFormatter.cs
- src/CsIndex.Core/Input/IndexPathResolver.cs
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

- 5（`AnalysisCacheVersion = 3`）。旧versionはmigration/auto-delete/implicit rebuildを行わず、delete/renameまたは新しい`--db`を選択して明示的に`csindex index`するよう案内する。

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
