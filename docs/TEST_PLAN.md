# Test Plan

The current canonical acceptance matrix is the final section of this file and
maps `SPEC.md` section 34 / approved-design section 22 to current tests. Earlier
phase matrices are retained as historical regression coverage and are not the
source of current grammar, schema, or option names.

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
- 永続化: schema/request version 5、analysis-cache version 3、`async_role`、`async_involvement_depth`、`async_next_symbol_id`、`async_usage_kind`の保存とDB-only復元を確認する。version mismatchでfail-fastし、version 4以前を含む既存DBのテーブル、行、journal modeを変更しないことを確認する。`schema_info`のない非空の未認識DBも、marker行を保持し、CsIndex tableを追加せず、journal modeを変更しないことを確認する。
- CLI: symbol/call JSON propertyと、非同期情報があるsymbolだけのtable suffix、callの`[AsyncUsageKind]`を`Console.Out`捕捉で確認する。

Status: 完了。Core、Storage、Integrationの自動テストで上記を検証済み。

## Override-aware method search

- Core extraction: verify nullable `symbols.type_kind` values and
  `interface_method_bindings` for explicit, implicit, inherited, abstract,
  default-interface, partial-type, and repeated-interface-path cases.
- Storage: verify schema/request-hash version 5, the binding table's primary
  key and foreign keys, both binding indexes, transactional persistence, and
  DB-only reconstruction. Verify that a version 4 database is rejected
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

## Previous symbol/source/graph matrix

The obsolete flat-matcher and schema-4 mapping has been retired from the
active plan. Its historical test names and verification totals remain
available in Git history. Current behavior is mapped below.

---

## Current schema-5 canonical acceptance matrix

Every row below is active. A passing aggregate run does not substitute for the
focused suites named in the row.

| Design row | Required observable behavior | Direct automated evidence |
| --- | --- | --- |
| 22.1 parser and formatter | csharp/explicit equivalence, balanced top-level fields, `.` executable children, every special form, malformed rejection, csharp/explicit short/full round-trip | `SymbolPathParserTests`; `SymbolPathFormatterTests`; `SymbolPathOutputAcceptanceTests`; `CSharpSymbolPathAcceptanceTests` |
| 22.2 resolution and containment | csharp suffix versus explicit exact, namespace omission/global, `*`/`**`, immediate local/anonymous containment, copied result styles, deterministic duplicates | `SymbolPathResolverTests`; `RootSelectionOrchestrationTests`; `CSharpSymbolPathAcceptanceTests` |
| 22.3 signature identity | type arity, callable three-state arity/overload omission, alias/framework equality, fully qualified non-alias types, placeholder ordinal, ref modes, nullability, arrays/pointers/tuples/function pointers, conversion target | `SymbolSignatureCanonicalizerTests`; `SymbolPathParserTests`; `SymbolPathResolverTests`; `CSharpSymbolPathAcceptanceTests` |
| 22.4 callable and partial identity | every included/excluded source callable, special/synthetic markers, immediate-owner anonymous ordinals, one partial logical row/two role rows/preferred implementation, logical call/relation endpoints | `CallablePathExtractionTests`; `ExecutableSymbolExtractionTests`; `LogicalDeclarationExtractionTests`; `SchemaFiveLogicalSymbolTests`; `RootSelectionOrchestrationTests` |
| 22.5 typed conditions | mixed glob/literal/regex, independent case categories, hierarchy/file/method/source semantics, bounded regex failure, OR/AND composition, one-declaration scope and partial projection | `TypedConditionCompilerTests`; `StructuralGlobMatcherTests`; `SourceTextFilterTests`; `TypedSearchAcceptanceTests` |
| 22.6 command matrix and traversal | every allowed/forbidden option, exact selection minimums, definition-at isolation, filters before logical cardinality, root-only traversal semantics, exact override expansion, initializer/top-level eligibility | `CliSymbolPathOptionMatrixTests`; `RootSelectionOrchestrationTests`; `CliCommandTests`; `TypedSearchAcceptanceTests` |
| 22.7 portable paths and schema | default/custom anchor, zero rooted persisted paths, linked `../`, same-volume/share preflight, relocation/base override, path styles/at-location, non-mutating schema-4 rejection, actionable rebuild guidance | `IndexPathResolverTests`; `PortablePathPersistenceTests`; `PortablePathOutputTests`; `SchemaFiveLogicalSymbolTests`; `PortableIndexAcceptanceTests` |
| 22.8 ordering, help, output safety | semantic order invariant across presentation/base/case/format, parent-first trees and role order, all normal/verbose help scopes, identical verbose spellings, terminal dependency-free help, atomic file/cancellation/failure behavior | `SymbolCanonicalComparerTests`; `VerboseHelpTests`; `SymbolPathOutputAcceptanceTests`; `OutputFormatterTests`; `PortableIndexAcceptanceTests`; `CliCommandTests` |

### Active negative contract

- Legacy executable-child syntax, zero/three fields, malformed delimiters,
  constructed generic invocation notation, invalid anonymous ordinals, and
  unqualified non-alias parameter types must be rejected by parser/acceptance
  tests.
- The removed bare matcher switches, invalid regex/case/style, duplicate case
  options, and command-inapplicable options must fail before payload commit.
- Partial definition/implementation must never become two logical candidates,
  call/relation endpoints, or a self relation.
- Root filters must never prune traversal descendants, and no traversal may
  start before cardinality succeeds.
- Persisted project/document/declaration/key data must contain no rooted source
  path. Cross-drive/share preflight and incompatible schema must preserve the
  prior database.
- Output formatting, cancellation, flush, replace, database, and path failures
  must preserve an existing destination and remove owned temporary files.

### Task 14 focused verification record

Fresh results on 2026-09-01:

- `rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~VerboseHelpTests"`: exit 0; 13 passed, 0 failed, 0 skipped, 0 warnings.
- `rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CliSymbolPathOptionMatrixTests|FullyQualifiedName~VerboseHelpTests|FullyQualifiedName~CliCommandTests"`: exit 0; 196 passed, 0 failed, 0 skipped, 0 warnings.
- `rtk dotnet build src\CsIndex.Cli\CsIndex.Cli.csproj -c Release --no-restore`: exit 0; 4 projects, 0 errors, 0 warnings.
- `rtk git diff --check`: exit 0; no whitespace errors.
- `rtk dotnet format CsIndex.sln --verify-no-changes --no-restore`: exit 0.
- `rtk dotnet build CsIndex.sln -c Release --no-restore`: the sandboxed attempt
  stopped only on the known Microsoft SDK discovery access denial; the
  identical approved run exited 0 with 9 projects, 0 errors, and 0 warnings.
- `rtk dotnet test CsIndex.sln -c Release --no-build --no-restore`: exit 0;
  1,176 passed across 4 projects, 0 failed, 0 skipped, 0 warnings.

`VerboseHelpTests` enumerates global, `index`, and every recognized command for
both verbose spellings and verifies byte-identical output. The normal `--help`
matrix is enumerated by `CliSymbolPathOptionMatrixTests`; together they verify
concise normal help and complete terminal help coverage. The help tests keep old
executable-child spellings only as labelled rejection cases. The primary agent
separately owns the final full-solution format/build/test gates, schema/path
probes, and independent reviews.

The primary's pre-review executable probes created one standard and one custom
schema-5 index through the built CLI, compared the documented DDL byte-for-byte
after newline/indent normalization, inspected every table/column/index and the
relevant foreign keys, and observed anchors `..` / `../workspace`, relative
project/document/key data, one logical partial with two role rows, a preferred
implementation, and zero foreign-key/rooted-path violations. Relocated
absolute-default, relative, and `--base-dir` queries all succeeded. Fifteen
positive and eleven negative CLI cases passed with their specified exit codes
and no partial stdout; schema/output sentinels remained unchanged. PP08 and
OH09 separately passed the deterministic cross-drive/UNC preflight and atomic
output-failure seams. The ignored probe tree was removed after inspection.
