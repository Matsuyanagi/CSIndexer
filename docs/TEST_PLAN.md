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
- 永続化: schema/request version 4、`async_role`、`async_involvement_depth`、`async_next_symbol_id`、`async_usage_kind`の保存とDB-only復元を確認する。version mismatchでfail-fastし、version 3を含む既存DBのテーブル、行、journal modeを変更しないことを確認する。`schema_info`のない非空の未認識DBも、marker行を保持し、CSIndexer tableを追加せず、journal modeを変更しないことを確認する。`sqliteXmarker`のようにSQLite内部prefixと似ているだけの有効なユーザーobjectも検出対象に含める。
- CLI: symbol/call JSON propertyと、非同期情報があるsymbolだけのtable suffix、callの`[AsyncUsageKind]`を`Console.Out`捕捉で確認する。

Status: 完了。Core、Storage、Integrationの自動テストで上記を検証済み。

## Override-aware method search

- Core extraction: verify nullable `symbols.type_kind` values and
  `interface_method_bindings` for explicit, implicit, inherited, abstract,
  default-interface, partial-type, and repeated-interface-path cases.
- Storage: verify schema/request-hash version 4, the binding table's primary
  key and foreign keys, both binding indexes, transactional persistence, and
  DB-only reconstruction. Verify that a version 3 database is rejected
  without modifying its schema objects, rows, or journal mode.
- Query semantics: verify exact behavior when the option is absent; interface
  expansion rooted at `IPlayable::Play()`; descendant-only concrete expansion
  from `Pianist::Play()` to `ProPianist::Play()`; exclusion of `Game` and
  `Baseball`; exclusion of interface-statically-typed call sites from a
  concrete search; inherited alias resolution from `D1::Play()` to
  `InheritedBase::Play()` and `D2::Play()` without a synthetic `D1` symbol;
  derived-interface scope; branch exclusion; `new` member hiding; and
  malformed-cycle termination.
- CLI: verify table and JSON output for `symbol find`, `definition`,
  `references`, `callers`, and `callees`; verify composition with
  `--short-names`, `--dispatch`, and `--exclude-lambda-calls`; verify the
  exact invalid-argument message for type-only queries; and verify rejection
  by unsupported commands.

## Latest Result

- Command: `rtk dotnet test CsIndex.sln --configuration Release`
- Passed: 222
- Failed: 0
- Skipped: 0
- Warnings: 0
- Date: 2026-08-08

CLIプロセス試験ではDirectoryModeのindex、definition-at、callers、JSON、cache reuseと、MSBuildWorkspaceによる実在`.csproj`解析（4 projects、37 documents、compilation errors 0）を確認済み。

## Symbol, source, and graph expansion acceptance matrix

The following map covers every bullet in section 17 of
`docs/2026-08-08.revised2.md`. Test names are xUnit method names. `Release
solution verification` means the fresh command
`rtk dotnet test CsIndex.sln --configuration Release` recorded in the latest
verification section below.

### 17.1 Lambda search

| ID | Acceptance condition | Automated evidence |
| --- | --- | --- |
| 17.1.1 | Enumerate `::<lambda#1>`. | `SymbolSourceQueryTests.LambdaPatterns_MatchSuffixOwnerSuffixAndFullName`; `CliCommandTests.SymbolFindSupportsLambdaPatternComponentRegexAndSourceFiltering` |
| 17.1.2 | Match an owner-plus-lambda suffix such as `Function()::<lambda#2>`. | `SymbolPatternMatcherTests.LambdaOwnerSuffixPattern_MatchesTheOwningFunction`; `SymbolSourceQueryTests.LambdaPatterns_MatchSuffixOwnerSuffixAndFullName` |
| 17.1.3 | Match a fully qualified lambda name. | `SymbolPatternMatcherTests.FullLambdaPattern_MatchesTheCanonicalDisplayName`; `SymbolSourceQueryTests.LambdaPatterns_MatchSuffixOwnerSuffixAndFullName` |
| 17.1.4 | Find nested lambdas. | `AsyncSemanticExtractorTests.AnalyzeAsync_NumbersLambdasByNearestNonLambdaOwner`; `SymbolSourceQueryTests.LambdaPatterns_MatchSuffixOwnerSuffixAndFullName` |
| 17.1.5 | Do not omit multiple lambdas with the same number under different owners. | `ExecutableSymbolExtractionTests.AnalyzeAsync_NamesLambdasByNearestNonLambdaOwnerWhileKeepingImmediateContainment`; `SymbolSourceQueryTests.LambdaPatterns_MatchSuffixOwnerSuffixAndFullName` |

### 17.2 Lambda naming

| ID | Acceptance condition | Automated evidence |
| --- | --- | --- |
| 17.2.1 | Number lambdas independently in different functions. | `AsyncSemanticExtractorTests.AnalyzeAsync_NumbersLambdasByNearestNonLambdaOwner` |
| 17.2.2 | Keep lambda names distinct for different member initializers. | `ExecutableSymbolExtractionTests.AnalyzeAsync_NamesLambdasByNearestNonLambdaOwnerWhileKeepingImmediateContainment` |
| 17.2.3 | Number nested lambdas in source order. | `AsyncSemanticExtractorTests.AnalyzeAsync_NumbersLambdasByNearestNonLambdaOwner`; `ExecutableSymbolExtractionTests.AnalyzeAsync_NamesLambdasByNearestNonLambdaOwnerWhileKeepingImmediateContainment` |
| 17.2.4 | Renumber only later lambdas of the same owner after insertion. | `ExecutableSymbolExtractionTests.AnalyzeAsync_InsertingLambdaRenumbersOnlyLaterLambdasOfSameOwner` |
| 17.2.5 | Distinguish initializers in different files of a partial type. | `ExecutableSymbolExtractionTests.AnalyzeAsync_IndexesInitializersInLaterPartialDocument` |

### 17.3 Function attributes

| ID | Acceptance condition | Automated evidence |
| --- | --- | --- |
| 17.3.1 | Persist accessibility, static state, and return type. | `SqliteIndexTests.Save_CreatesVersionFourSchemaWithExecutableMetadataAndAsyncNextForeignKey`; `SqliteIndexTests.Save_RoundTripsExecutableMetadataAndAsyncNextAcrossAllSymbolReaders` |
| 17.3.2 | Render attributes in text output. | `OutputFormatterTests.WriteSymbolsTableUsesDeclarationOrderingAndShortensReturnAndParameterTypes` |
| 17.3.3 | Expose attributes as independent JSON fields. | `OutputFormatterTests.WriteSymbolsJsonKeepsCanonicalFieldsAndOnlyShowsSourceWhenRequested` |
| 17.3.4 | Apply short-name presentation to return and parameter types. | `OutputFormatterTests.WriteSymbolsTableUsesDeclarationOrderingAndShortensReturnAndParameterTypes` |
| 17.3.5 | Render non-applicable lambda/constructor attributes correctly. | `OutputFormatterTests.WriteSymbolsFormatsConstructorAndLambdaApplicableFieldsAndGatesSource` |

### 17.4 Async shortest path

| ID | Acceptance condition | Automated evidence |
| --- | --- | --- |
| 17.4.1 | A self-async root returns one node. | `GraphQueryTests.AsyncPath_RepresentsSelfAsyncAndUnreachableMethods`; `CliCommandTests.AsyncTreeRendersSelfUnreachableAndTruncatedResultsInEachOutputMode` |
| 17.4.2 | Return a shortest path to an async function. | `GraphQueryTests.AsyncPath_FollowsThePersistedShortestPathToAnAsyncOrigin` |
| 17.4.3 | Return one persisted route for equal distances. | `GraphQueryTests.AsyncPath_UsesThePersistedNextHopInsteadOfReselectingAnEqualRoute` |
| 17.4.4 | Do not overwrite a next hop on an equal-distance discovery. | `AsyncInvolvementPropagatorTests.Apply_RecordsOneNextHopAndDoesNotReplaceAnEqualPath` |
| 17.4.5 | Select the same route after reindexing. | `GraphQueryTests.AsyncPath_ReindexPersistsTheSameSelectedEqualRoute` |
| 17.4.6 | Default to tree output. | `CliCommandTests.AsyncTreeRendersSelfUnreachableAndTruncatedResultsInEachOutputMode`; `OutputFormatterTests.GraphOutputFormatterWritesAsyncTreeLineAndJsonWithNoPathAndTruncationStates` |
| 17.4.7 | Emit one-line output with `--output line`. | `OutputFormatterTests.GraphOutputFormatterWritesAsyncTreeLineAndJsonWithNoPathAndTruncationStates` |
| 17.4.8 | Emit structured JSON with `--output json`. | `OutputFormatterTests.GraphOutputFormatterWritesAsyncTreeLineAndJsonWithNoPathAndTruncationStates` |
| 17.4.9 | Report an unreachable async origin. | `GraphQueryTests.AsyncPath_RepresentsSelfAsyncAndUnreachableMethods`; `CliCommandTests.AsyncTreeRendersSelfUnreachableAndTruncatedResultsInEachOutputMode` |
| 17.4.10 | Terminate on a call-graph cycle. | `GraphQueryTests.AsyncPath_TraversesACallCycleThatReachesAnAsyncOrigin`; `AsyncInvolvementPropagatorTests.Apply_OriginsHaveNullNextAndCyclesTerminate` |
| 17.4.11 | Classify Task/ValueTask/UniTask families. | `AsyncSemanticExtractorTests.AnalyzeAsync_ExtractsAsyncRolesAndKeepsNestedOwnersSeparate`; `AsyncSemanticExtractorTests.AnalyzeAsync_ClassifiesGenericTaskAsAwaitable`; `AsyncSemanticExtractorTests.AnalyzeAsync_ClassifiesValueTaskVariantsAsAwaitable` |
| 17.4.12 | Decrease persisted path depth one node at a time. | `GraphQueryTests.AsyncPath_FollowsThePersistedShortestPathToAnAsyncOrigin`; `GraphQueryTests.AsyncPath_ValidatesTheNextHopBeforeReportingTruncation` |
| 17.4.13 | Detect inconsistent next IDs or cycles. | `GraphQueryTests.AsyncPath_RejectsANonOriginWithoutAPersistedNextHop`; `GraphQueryTests.AsyncPath_RejectsCyclicOrOriginNextHops`; `GraphQueryTests.AsyncPath_RejectsANextHopFromAnotherProfile` |
| 17.4.14 | Mark max-node truncation. | `GraphQueryTests.AsyncPath_CountsTheRootAgainstTheNodeLimit`; `CliCommandTests.AsyncTreeRendersSelfUnreachableAndTruncatedResultsInEachOutputMode` |

### 17.5 Caller tree

| ID | Acceptance condition | Automated evidence |
| --- | --- | --- |
| 17.5.1 | Find direct and indirect callers to a specified depth. | `GraphQueryTests.CallerTree_UsesBreadthFirstDepthBoundsAndTreatsZeroAsUnlimited` |
| 17.5.2 | Treat `--depth 0` as unlimited depth. | `GraphQueryTests.CallerTree_UsesBreadthFirstDepthBoundsAndTreatsZeroAsUnlimited` |
| 17.5.3 | Never exceed the maximum node count. | `GraphQueryTests.CallerTree_CountsTheRootAgainstMaxNodesAndMarksTruncation`; `GraphQueryTests.CallerTree_SortsAllCallersAtTheSameDepthBeforeApplyingTheNodeLimit` |
| 17.5.4 | Terminate while retaining cyclic call relationships. | `GraphQueryTests.CallerTree_RetainsCycleEdgesWithUniqueNodesAndEdges` |
| 17.5.5 | Exclude external libraries and `System.*`. | `GraphQueryTests.CallerTree_SortsNodesAndExcludesExternalMetadataSymbols` |
| 17.5.6 | Attribute lambda calls to the lambda itself. | `GraphQueryTests.CallerTree_KeepsLambdaCallersWithoutSynthesizingOwnershipEdges`; `AsyncSemanticExtractorTests.AnalyzeAsync_AssignsNestedLambdaCallsToTheirNearestLambdaOwner` |
| 17.5.7 | Do not turn lambda ownership into a function-call edge. | `GraphQueryTests.CallerTree_KeepsLambdaCallersWithoutSynthesizingOwnershipEdges` |
| 17.5.8 | Emit valid Mermaid flowchart output. | `OutputFormatterTests.GraphOutputFormatterWritesCallerTreeMermaidAndJsonWithEscapedUniqueEdges`; `CliCommandTests.CallerTreeRendersTextMermaidJsonDepthCyclesLambdasAndShortNames` |

### 17.6 Name search

| ID | Acceptance condition | Automated evidence |
| --- | --- | --- |
| 17.6.1 | Perform exact search. | `SymbolSourceQueryTests.ExactSearch_PreservesTheExistingResolverResults` |
| 17.6.2 | Perform `*` wildcard search. | `SymbolPatternMatcherTests.WildcardPattern_MatchesParameterlessMethodDisplay`; `SymbolSourceQueryTests.PatternSearch_MatchesWildcardComponentAndRegexRequests` |
| 17.6.3 | Filter namespace/type/method components individually. | `SymbolPatternMatcherTests.ComponentPatterns_AreCombinedWithAndSemantics`; `SymbolSourceQueryTests.PatternSearch_MatchesWildcardComponentAndRegexRequests` |
| 17.6.4 | Perform regular-expression search. | `SymbolPatternMatcherTests.RegexPattern_MatchesTheAnchoredRequirementExample`; `SymbolSourceQueryTests.PatternSearch_MatchesWildcardComponentAndRegexRequests` |
| 17.6.5 | Safely report invalid/timeout regular expressions. | `SymbolPatternMatcherTests.InvalidRegex_ReportsTheAffectedPattern`; `SymbolPatternMatcherTests.RegexTimeout_IsReportedInsteadOfRunningUnbounded`; `CliCommandTests.SymbolFindAndSourceCommandsRejectInvalidSearchInput` |
| 17.6.6 | Enumerate omitted-parameter overloads and filter a supplied signature. | `SymbolPatternMatcherTests.PatternWithoutParameterList_MatchesEveryOverload`; `SymbolPatternMatcherTests.PatternWithParameterList_MatchesOnlyTheCompleteSignature` |
| 17.6.7 | Keep same-display-name symbols independent by project/symbol ID. | `SqliteIndexTests.GetSymbolsByIdsAsync_OrdersDuplicateDisplayNamesDeterministically`; Release solution verification |

### 17.7 Source storage and search

| ID | Acceptance condition | Automated evidence |
| --- | --- | --- |
| 17.7.1 | Remove comments. | `SourceNormalizerTests.Normalize_RemovesTriviaWithoutJoiningTokensOrChangingLiterals` |
| 17.7.2 | Preserve comment markers inside string literals. | `SourceNormalizerTests.Normalize_RemovesTriviaWithoutJoiningTokensOrChangingLiterals` |
| 17.7.3 | Normalize source to one line outside literal-token text; multiline raw literal token text preserves embedded newlines. | `SourceNormalizerTests.Normalize_RemovesTriviaWithoutJoiningTokensOrChangingLiterals`; `SourceNormalizerTests.Normalize_ExcludesDirectivesAndDisabledTextAndPreservesRawStrings` |
| 17.7.4 | Keep `var a` from becoming `vara`. | `SourceNormalizerTests.Normalize_RemovesTriviaWithoutJoiningTokensOrChangingLiterals` |
| 17.7.5 | Preserve token boundaries after comment removal. | `SourceNormalizerTests.Normalize_RemovesTriviaWithoutJoiningTokensOrChangingLiterals` |
| 17.7.6 | Show normalized function and lambda source. | `SymbolSourceQueryTests.ShowSource_ReturnsSourceBackedExecutableOverloadsAndRequestsPresentation`; `CliCommandTests.SourceShowRendersNormalizedLambdaSourceInTableAndJson` |
| 17.7.7 | Require source-search include or exclude conditions. | `SymbolSourceQueryTests.SearchSource_RejectsAnUnboundedQuery`; `CliCommandTests.SourceSearchRequiresAtLeastOneIncludeOrExcludeCondition` |
| 17.7.8 | Permit name-only `symbol find`. | `SymbolSourceQueryTests.ExactSearch_PreservesTheExistingResolverResults`; `CliCommandTests.SymbolFindSupportsLambdaPatternComponentRegexAndSourceFiltering` |
| 17.7.9 | Combine name search with source conditions. | `SymbolSourceQueryTests.NameAndSourceFilters_ExcludeBeforeRequiringAllIncludes`; `CliCommandTests.SymbolFindSupportsLambdaPatternComponentRegexAndSourceFiltering` |
| 17.7.10 | Show normalized source with `symbol find --show-source`. | `CliCommandTests.SymbolFindSupportsLambdaPatternComponentRegexAndSourceFiltering`; supplementary formatter evidence: `OutputFormatterTests.WriteSymbolsJsonKeepsCanonicalFieldsAndOnlyShowsSourceWhenRequested` |
| 17.7.11 | OR multiple excludes. | `SourceTextFilterTests.Excludes_RejectWhenAnyTermMatches`; `SymbolSourceQueryTests.NameAndSourceFilters_ExcludeBeforeRequiringAllIncludes` |
| 17.7.12 | Short-circuit includes after an exclude match. | `SourceTextFilterTests.ExcludeMatch_ShortCircuitsBeforeAnyIncludePredicate` |
| 17.7.13 | Evaluate includes only after excludes survive. | `SourceTextFilterTests.ExcludeMatch_ShortCircuitsBeforeAnyIncludePredicate`; `SymbolSourceQueryTests.NameAndSourceFilters_ExcludeBeforeRequiringAllIncludes` |
| 17.7.14 | Require every supplied include term (AND semantics). | `SourceTextFilterTests.Includes_RequireEveryTerm` |
| 17.7.15 | Include a source when no include terms are supplied and excludes do not match. | `SourceTextFilterTests.ExcludeOnly_FilterAcceptsSourceWithoutExcludedTerms` |
| 17.7.16 | Use include-only search when no excludes are supplied. | `SourceTextFilterTests.IncludeOnly_FilterAcceptsMatchingSource` |
| 17.7.17 | Support case-sensitive and case-insensitive source matching. | `SourceTextFilterTests.IgnoreCaseComparison_ControlsSourceTermMatching` |

### Fresh verification record

Task 7 runs the four Release test projects, the full Release solution test,
the Release build, `rtk git diff --check`, and status/hygiene commands after
the documentation changes. The exact commands and fresh results are recorded
in `task-7-report.md`. Fresh 2026-08-08 evidence is:

- `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --configuration Release`:
  46 passed, 0 warnings.
- `rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --configuration Release`:
  24 passed, 0 warnings.
- `rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj --configuration Release`:
  27 passed, 0 warnings.
- `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --configuration Release`:
  129 passed, 0 warnings.
- `rtk dotnet test CsIndex.sln --configuration Release`: 226 passed, 0
  warnings.
- `rtk dotnet build CsIndex.sln --configuration Release`: 9 projects, 0
  warnings, 0 errors.
- `rtk dotnet format CsIndex.sln --verify-no-changes --no-restore`: exit 0.
- `rtk git diff --check`: exit 0.
