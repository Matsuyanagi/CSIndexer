# Architecture Decisions

本書は設計判断とその履歴を記録する。現在の正式仕様は`docs/SPEC.md`、CLI契約は`docs/CLI.md`、DB定義は`docs/DB_SCHEMA.md`を参照する。古いdecisionの一部だけが後続decisionで置き換えられた場合は、後続decisionの`Supersedes`と現在の正式仕様を優先する。

## DEC-0001: SQLite provider

Status: Accepted

Context: 仕様は交換可能なSQLite接続層と、実装時点の互換性がある安定版の固定を要求する。
Decision: `Microsoft.Data.Sqlite` 10.0.10を採用し、Storageプロジェクト内へ隔離する。推移依存の`SQLitePCLRaw.lib.e_sqlite3` 2.1.11に高重大度の脆弱性警告があるため、修正版を含む`SQLitePCLRaw.bundle_e_sqlite3` 3.0.3を直接固定する。
Alternatives: System.Data.SQLite、sqlite-net、独自P/Invoke。
Consequences: Microsoft管理のADO.NET APIを使える。SQLite固有処理はStorage層から外へ漏らさない。NuGet監査を警告0のビルドで継続確認する必要がある。
Date: 2026-07-20

## DEC-0002: CLI argument parser

Status: Accepted

Context: CLI引数解析ライブラリは未確定事項であり、予約オプションを壊さず交換可能にする必要がある。
Decision: 初期版は外部依存を増やさず、CLIプロジェクト内の小さな専用パーサーを使用する。
Alternatives: System.CommandLine、Spectre.Console.Cli、CliFx。
Consequences: 仕様に必要な反復オプションとサブコマンドへ限定する。構文定義をCLI層へ隔離し、将来交換可能にする。
Date: 2026-07-20

## DEC-0003: Exit codes

Status: Accepted

Context: 具体的な終了コードは未確定だが、名前付き定数として分離する必要がある。
Decision: Success=0、InvalidArguments=2、AnalysisFailure=3、DatabaseFailure=4、RequireSingleFailure=5とする。
Alternatives: すべての失敗を1に統一する、sysexits互換番号を使う。
Consequences: スクリプトから失敗種別を識別できる。番号は`ExitCodes`だけに集約する。
Date: 2026-07-20

## DEC-0004: Test framework

Status: Accepted

Context: .NET 10で動作する保守中のテスト基盤が必要。
Decision: xUnit.net v3 3.2.2、Visual Studio runner 3.1.5、Microsoft.NET.Test.Sdk 18.8.1を固定する。
Alternatives: NUnit、MSTest。
Consequences: `dotnet test`とVisual Studio Test Explorerの両方で実行できる。
Date: 2026-07-20

## DEC-0005: Default caller scope

Status: Accepted (legacy `callers` command only)

Context: ラムダとローカル関数を独立callerにした場合の既定表示は未確定だった。
Decision: 既定を`direct`とし、直接のラムダ/ローカル関数を返す。`containing`と`both`を明示指定できる。
Alternatives: `containing`、`both`。
Consequences: DBの直接edgeと既定表示が一致する。外側methodが必要な利用者はoptionを指定する。この決定は既存の`callers`コマンドにだけ適用する。`callers tree`では、ラムダ内の呼び出しはラムダ自身からの辺とし、所有関係から外側methodへの辺を合成しない。
Date: 2026-07-20

## DEC-0006: Multiple project ordering

Status: Accepted

Context: solutionなしで複数`.csproj`が見つかる場合の表示順は未確定だった。
Decision: Windows pathの大文字小文字を区別しないordinal順で安定化する。
Alternatives: filesystem列挙順、MSBuild load順。
Consequences: 実行ごとの順序とfingerprintが決定的になる。
Date: 2026-07-20

## DEC-0007: SQLite journal and pooling

Status: Accepted

Context: WAL採用は未確定で、CLIは短命processとして動く。
Decision: foreign keysとWALを有効化し、connection poolingは無効化する。
Alternatives: rollback journal、pooling有効。
Consequences: 原子的な更新と検索並行性を得る。短命CLI・テスト終了時にDB file handleを確実に解放できる。
Date: 2026-07-20

## DEC-0008: FTS5

Status: Accepted

Context: FTS5利用は未確定だったが、Phase 1/2のqueryは構造化されたexact matchが中心である。
Decision: schema version 1ではFTS5を使用せず、B-tree indexを使用する。
Alternatives: symbol display/name用FTS5 virtual table。
Consequences: schemaとmigrationを単純に保つ。将来の部分一致・全文検索で再評価する。
Date: 2026-07-20

## DEC-0009: Stable symbol key

Status: Superseded in part by DEC-0030 and DEC-0031

Context: stable keyの最終形式とDocumentation Comment IDを持たないシンボルの形式が未確定だった。
Decision: `profile + assembly + tfm + Documentation Comment ID`を基本とする。取得不能時はsymbol kind、fully-qualified display、source path/spanを含むfallbackを使用する。lambda/initializerはowner、document、syntax kind、span、content hashを使用する。
Alternatives: Roslyn `SymbolKey`だけを永続化、display stringだけを使用。
Consequences: Roslyn内部形式を唯一のDB契約にせず、overloadを区別できる。fallback symbolはsource移動でkeyが変わる。
Date: 2026-07-20

## DEC-0010: Default database location

Status: Accepted

Context: DBを入力内かuser data領域へ置くかは未確定だった。
Decision: index時は入力rootの`.csindex/index.sqlite`、query時はcurrent directoryの同pathを既定とし、`--db`で上書きする。
Alternatives: `%LOCALAPPDATA%`、常に明示指定。
Consequences: repositoryごとに自己完結し、移動・削除が分かりやすい。`.csindex/`をVCS ignoreする必要がある。
Date: 2026-07-20

## DEC-0011: Path normalization

Status: Superseded by DEC-0031

Context: path正規化形式は未確定だった。
Decision: `Path.GetFullPath`による絶対Windows pathとし、比較はordinal ignore-case、DBには元のcaseを保持する。
Alternatives: URI、root-relative path、強制lowercase。
Consequences: definition-atとfilesystem読取りが直接対応する。DBの別machineへの可搬性は限定される。
Date: 2026-07-20

## DEC-0012: nameof references

Status: Accepted

Context: `nameof`を既定referencesへ含めるかは未確定だった。
Decision: `ReferenceKind.NameOf`として保存し、`references`の既定結果へ含める。`callers`には含めない。
Alternatives: option指定時のみ、完全除外。
Consequences: source-levelの参照を欠落させず、呼び出しedgeとの混同も避けられる。
Date: 2026-07-20

## DEC-0013: Profile replacement semantics

Status: Accepted

Context: 同一DB内の更新単位と複数Profileの初期対応範囲を定める必要がある。
Decision: profile nameをprofile hashへ含め、同じProfileの再indexは以前のrunを1 transactionで置換する。別名Profileは同一DBへ共存できる。
Alternatives: 入力runを無制限に追記、DB全体を毎回置換。
Consequences: `--profile`検索を提供できる。`--all-profiles`横断検索と同名Profile内の複数input共存はPhase 4まで未対応。
Date: 2026-07-20

## DEC-0014: Multi-TFM/Profile result presentation

Status: Proposed

Context: 同一symbolが複数Project、TFM、Profileに存在する場合の集約表示は未確定である。
Decision: Phase 4で、profile/TFMを保持した重複表示と論理symbol単位のgrouping optionを比較する。
Alternatives: 常に統合、常に別行。
Consequences: schemaはprofile IDとproject TFMを保持し、将来の選択を妨げない。
Date: 2026-07-20

## DEC-0015: Unity version defines

Status: Proposed

Context: Unity version symbolとVersion Definesの厳密な生成規則は未確定である。
Decision: 公式Unity仕様と実Editor出力を照合するまで推測実装しない。
Alternatives: version文字列から近似生成する。
Consequences: Phase 3着手時の調査項目として残り、現在はUnity version symbolsを生成しない。
Date: 2026-07-20

## DEC-0016: Async involvement propagation timing and direction

Status: Accepted (path persistence extended by DEC-0023)

Context: 非同期起点へ到達する呼び出し元を検索時に毎回再帰CTEで求めるか、index作成時に導出して保存するかを決める必要がある。呼び出しグラフには自己再帰・相互再帰・複数起点があり、循環停止と決定的な最短距離が必要である。
Decision: 全Roslyn fact抽出後、解決済み`ReferenceKind.Invocation`の逆辺を作り、全非同期起点をdepth 0とするindex-timeの複数始点BFSを1回実行する。calleeからcallerの方向だけに進み、既訪問距離以下の候補は再展開せず、`AsyncInvolvementDepth`へ最短距離を保存する。query-time再帰CTEは採用しない。
Alternatives: query-time recursive CTE、起点ごとのDFS、呼び出し先方向への伝播。
Consequences: すべてのqueryで同じ結果をDB-onlyで返せ、自己再帰・相互再帰でも停止する。呼び出しグラフ変更時は再indexが必要になる。DEC-0023により最短距離に加えて選択した1つのnext hopを保存するが、全経路や到達した全起点は保持しない。非同期関数から呼ばれる同期関数には伝播しない。
Date: 2026-07-22

## DEC-0017: Separate direct async roles from derived involvement

Status: Accepted

Context: 宣言`async`、awaitable返却、本文の`await`、非同期ストリームなどの直接事実と、別関数を経由して非同期起点へ到達するという派生事実は、意味と更新元が異なる。
Decision: Roslynから得る直接事実をflags enum `AsyncRole`、呼び出し辺での消費方法を`AsyncUsageKind`、逆辺BFSで得る派生最短距離をnullable `AsyncInvolvementDepth`として分離する。直接ロールを持つ起点自身もdepth 0を持つ。
Alternatives: 単一の`IsAsync` boolean、伝播先へ直接ロールをコピー、ロールと距離をquery時だけ合成。
Consequences: 「なぜ直接非同期か」と「何辺先で非同期へ到達するか」を区別できる。新しい直接ロールを追加しても伝播器の起点集合へ明示的に組み込める一方、モデル・DB・出力の3値を同期して保守する必要がある。
Date: 2026-07-22

## DEC-0018: Function listing and lambda call presentation

Status: Accepted (lambda numbering superseded by DEC-0020)

Context: 関数一覧、ラムダの識別子、callee検索の既定範囲、namespaceを含む名前の表示規則を一貫して定める必要がある。

Decision: `symbol list`の既定結果は`method`と`lambda`にする。`--kind method|lambda`と`--async-involved`で絞り込み、namespace短縮は`--short-names`によるpresentation-onlyの変換にする。JSONの`fullyQualifiedName`、stable key、namespace、parameter typeなどのcanonical fieldは短縮しない。ラムダの正式な表示採番はDEC-0020に従う。既存の`callees`は表示上の集約機能として、指定symbol配下のラムダdescendantによる呼び出しを再帰的に含め、`--exclude-lambda-calls`指定時だけmethod本体の直接呼び出しに限定する。この集約はラムダ所有関係を永続的なcall edgeへ変換しない。

Alternatives: `symbol list`をmethodだけにする、document全体でラムダを連番にする、短縮名をJSON canonical fieldにも保存する、calleeを常に直接呼び出しだけにする。

Consequences: 一覧とcallee検索はラムダ本体の実行可能な呼び出しを既定で見落とさない。表示を短縮しても機械処理用の識別子は安定する。ラムダ番号は最寄りの非ラムダ実行可能owner内のソース順を表すため、別owner間で番号を比較する意味はない。

Date: 2026-08-02

## DEC-0019: Branch-scoped interface bindings and query-time override expansion

Status: Accepted

Context: A method-only relation between an interface contract and its
implementation cannot distinguish a class that implements a derived interface
from a sibling that implements only the base interface. Inherited method
queries also need to return a real declaration while preserving the queried
receiver branch.

Decision: Persist `interface_method_bindings` per analysis profile with the
implementing type, exact interface contract method, and real implementation
method. Resolve `--include-overrides` at query time with a seed containing the
real method and receiver type branch. Interface searches expand through the
exact contract's scoped bindings; class and abstract-class searches expand
only through descendant `Overrides` branches. Inherited aliases return their
real base declaration rather than a synthetic receiver declaration.

Alternatives: Store a global method-to-interface edge without type context,
materialize a transitive expansion closure at index time, or infer targets
from receiver-value/runtime flow.

Consequences: Queries stay profile-scoped, deterministic, cycle-safe, and
descendant-only. Concrete searches do not include base/interface contracts,
sibling implementations, or interface-statically-typed call sites. The
feature originally introduced schema and request-hash version 3. DEC-0022
supersedes only that version number and its version-2 rebuild consequence;
the query and binding decisions in this record remain accepted.

Date: 2026-08-05

## DEC-0020: Function-scoped lambda display numbering

Status: Superseded in part by DEC-0030

Context: Nested lambdas need stable, searchable display names without losing
their immediate lexical owner for call attribution. Field, property, and event
initializers also need distinct owners.

Decision: Number every lambda in source order within its nearest non-lambda
executable owner. Keep the immediate lexical owner in `containing_symbol_id`.
Create a synthetic initializer owner named
`Namespace.Type::<initializer:memberName>` for field, property, and event
initializers; its stable key is source-backed, rather than a display string
alone.

Supersedes: only the nested-lambda numbering sentence in DEC-0018. The
remainder of DEC-0018, including function-list defaults, canonical-field
rules, and `callees` lambda-descendant behavior, remains accepted.

Alternatives: Reset the counter at every nested lambda, number the entire
document globally, or treat ownership as a call edge.

Consequences: A nested lambda is displayed under the surrounding function or
initializer with the next owner-scoped number, while calls written inside it
remain attributed to the immediate lambda. Adding a lambda can renumber only
later lambdas under that same owner.

Date: 2026-08-08

## DEC-0021: Token-normalized executable source and source-filter semantics

Status: Superseded in part by DEC-0030

Context: Source search needs stable text without corrupting literals or token
boundaries, and must combine predictably with symbol filters.

Decision: Build normalized source from Roslyn active syntax tokens. Preserve
each literal token `Text`, omit trivia/directives/disabled text and layout
outside literal tokens, and add one space only when adjacent token text would
otherwise tokenize differently. A multiline raw literal may therefore retain
embedded newlines. Store the layout-normalized text and SHA-256 hash. Evaluate
source excludes first with OR semantics, then AND all includes; use ordinal
matching by default and ordinal ignore-case only when requested.

Alternatives: Regex-based comment stripping, searching original files at
query time, or FTS-only search semantics.

Consequences: Comments are unavailable to source search, literals are
preserved, and arbitrary substring predicates can require a scan of
source-backed executable candidates. `--show-source` affects output only.

Date: 2026-08-08

## DEC-0022: Schema version 4 with non-mutating rebuild rejection

Status: Superseded by DEC-0031

Context: Executable metadata, normalized source, and a persisted async path
need storage additions incompatible with schema version 3.

Decision: Set the schema and request-hash versions to 4. Add return type,
normalized source/hash, and a self-referencing async-next ID to `symbols`.
Insert symbols first, then update containing and async-next IDs in the same
transaction once numeric IDs are known. Reject version 3 and every other
unsupported version without ALTER, deletion, WAL changes, or user-data
mutation. Add profile-prefixed indexes for symbol kind, owner, async depth,
async next hop, name/component lookups, and a partial source-backed executable
lookup. Source-only repository paths use a direct
`source_document_id IS NOT NULL` predicate so the partial index is usable.

Supersedes: only DEC-0019's schema/request-hash version 3 and its version-2
rebuild consequence. DEC-0019's branch-scoped binding and query-time override
expansion decisions remain accepted.

Alternatives: ALTER migration, automatic database recreation, or resolving
async paths without a stored next hop.

Consequences: Existing indexes must be rebuilt. A successful index replacement
is atomic and DB-only queries can reconstruct the persisted fields. The
profile-prefixed indexes are part of schema v4 itself; changing them before
release does not introduce an in-place migration.

Date: 2026-08-08

## DEC-0023: Persist one deterministic async shortest-path next hop

Status: Accepted

Context: A shortest distance alone cannot reproduce one chosen route when
equal-length paths exist.

Decision: Use deterministic reverse multi-source BFS over resolved invocation
edges whose caller and callee are both source-backed methods/lambdas with
normalized source; metadata-only awaitable methods are not origins or path
nodes. Sort origins and adjacency deterministically, record `depth + 1` and the
callee next hop on first or strictly shorter discovery, and never replace an
equal-distance hop. Query-time display follows only the persisted chain
and validates decreasing depth, profile membership, cycles, source-backed
method/lambda executability, normalized source availability, and coherent
origin/non-origin state. Only a symbol with a direct async-origin role may end
the path at depth zero, and every fetched hop is validated before truncation is
reported.

Alternatives: Re-run a graph search while rendering, persist every route, or
choose a route from an unspecified SQL order.

Consequences: `async tree` emits one repeatable path and reports inconsistent
stored data as an error. It does not retain all reachable async origins or all
equal paths.

Date: 2026-08-08

## DEC-0024: Bounded source-backed caller graph

Status: Accepted

Context: Caller visualization must terminate on cycles and avoid presenting
external or inferred runtime behavior as indexed source facts.

Decision: Traverse resolved invocation/object-creation caller edges
breadth-first within one profile. Retain unique nodes and edges, apply depth
and node limits, include only source-backed method/lambda callers, exclude
`System` namespaces, and render graph IDs from symbol IDs. Do not synthesize
lambda ownership edges or infer delegate `Invoke`, events, callbacks,
reflection, or runtime dispatch. At a finite depth boundary, still inspect the
reverse edges of boundary nodes and retain an edge when both endpoints are
already present; do not admit or enqueue a deeper node.

Alternatives: Recursive unbounded traversal, display-name graph identifiers,
or delegate/data-flow inference.

Consequences: Tree, Mermaid, and JSON output describe the same bounded static
graph, including cycle edges between already included nodes. Mermaid labels use
the displayed name (canonical by default and shortened with `--short-names`).
Some runtime execution paths are intentionally absent.

Date: 2026-08-08

## DEC-0025: Canonical wildcard and bounded regex symbol search

Status: Superseded by DEC-0030

Context: Symbol search needs pattern flexibility without changing existing
exact-query resolution or letting regex execution depend on culture or run
without a bound.

Decision: Preserve the exact resolver only for a positional pattern with no
wildcard, no `::<lambda#` marker, and no matching modifier (`--regex`,
`--ignore-case`, component, kind, include, or exclude filters). `--show-source`
is presentation-only and does not disqualify that path. Outside it, evaluate
canonical stored display-name and component fields. Without `--regex`, only `*`
is special and matches zero or more characters; every other character is
literal. With `--regex`, all name patterns are culture-invariant .NET regular
expressions with a two-second timeout, and `*` retains regex meaning. Use
case-sensitive matching by default; `--ignore-case` enables culture-invariant
regex ignore-case for names and ordinal ignore-case for source terms.

Alternatives: Apply SQL `LIKE` semantics, use the current culture, compile
unbounded regexes, or shorten names before matching.

Consequences: Wildcard/regex matching is predictable and canonical fields are
never changed by `--short-names`. Invalid or timed-out patterns fail as query
errors instead of silently producing a partial result.

Date: 2026-08-08

## DEC-0026: Project-scoped identity for source definitions

Status: Accepted

Context: Two projects in the same analysis profile may intentionally use the
same assembly name, target framework, fully qualified type, and method
signature. Assembly identity alone would merge their source definitions and
redirect calls or graph roots across project boundaries.

Decision: Include the owning `ProjectData.Key` in every source-definition and
source-target stable key, including types, methods, local functions, synthetic
owners, calls, relations, interface bindings, and constructed source targets.
Resolve source ownership from the current compilation, compilation references,
or the syntax-tree-to-project map. Keep metadata-only symbol identity scoped by
assembly identity and target framework rather than by the referencing project.

Alternatives: Add a project column only at query time, merge equal source
definitions by assembly/FQN, or duplicate every metadata symbol per project.

Consequences: Same-profile duplicate source definitions persist with distinct
symbol IDs and source text, while shared framework/library metadata stays
deduplicated. Ambiguous graph-root diagnostics add document path and symbol ID
when canonical display names alone cannot distinguish candidates.

Date: 2026-08-09

## DEC-0027: Common executable target filters

Status: Superseded in part by DEC-0030

Context: `--kind` had command-specific behavior and `--async-involved` is a
derived reachability value, not a direct declaration property. Lambda targets
also need the same resolution grammar in flat queries and graph roots without
changing the meaning of displayed callers, callees, or graph paths.

Decision: Accept `--kind all|method|lambda` and
`--async-status all|async|sync` on every command that resolves an executable
target/root. Represent `all` as no kind/direct-async predicate; use stored
`AsyncRole`, not `AsyncInvolvementDepth`, for direct async filtering; and
apply the predicates only while resolving the target/root. Resolve canonical
lambda grammar (`::<lambda#n>`, owner-qualified forms, and `::<lambda#*>`)
through the common executable resolver. Expand real method overrides before
applying the kind/direct-async predicate. Keep `index` and `conditions` out of
the filter matrix because they have no executable target.

Alternatives: Apply a display-wide graph filter, overload
`--async-involved` with direct-async semantics, retain per-command lambda
parsers, or infer lambda call/reference edges from delegate/event/runtime flow.

Consequences: An `all` filter preserves legacy exact type-query behavior,
while `async`/`sync` never include nonfunction symbols. Graph reachability and
secondary caller/callee presentation remain intact. `--kind lambda` is invalid
with method-only override expansion and with `overrides`; stored static facts
remain the only lambda call/reference facts, so delegate `Invoke`, event,
callback, reflection, and runtime-flow references are intentionally absent.

Date: 2026-08-11

## DEC-0028: Injected atomic result output

Status: Accepted

Context: Result redirection must not change formatter semantics, corrupt an
existing output file on a failed query/cancel/write, or redirect diagnostics.
The legacy output-format option name also conflicts with the distinct need to
name an output destination.

Decision: Rename the active format option to `--output-format` and reserve
`-o` / `--output-file` for the destination. Inject a `TextWriter` into the
normal, graph, JSON, and conditions formatters rather than changing
process-global `Console.Out`. When a file is requested, allocate a same-
directory temporary file only when payload writing begins; write BOM-less
UTF-8; flush and observe cancellation; then atomically replace/move the final
file. Keep stdout empty on a successful redirected payload and keep
diagnostics on stderr.

Alternatives: Keep `--output` as an alias, infer format from file extension,
call `Console.SetOut`, truncate the final path before query execution, or
write temporary files in a system temporary directory.

Consequences: File bytes match the payload that the selected formatter would
write to stdout, and failed query/format/database/write/cancel paths retain
the previous output file. Missing parents are output errors rather than
implicit directory creation. Normalized output/database path equality is
rejected before I/O. The legacy `--output` spelling is deliberately a usage
error, not a deprecation warning.

Date: 2026-08-11

## DEC-0029: Cache invalidation for zero-width array-rank normalization

Status: Superseded in part by DEC-0031

Context: Roslyn omitted array-rank tokens have no source text, but allowing
them to participate in separator decisions produced stale normalized source
such as `string[  ]args`. The persisted schema already stores the corrected
text and its hash, so a table migration is unnecessary.

Decision: Omit missing/zero-width tokens from both normalized text and
separator decisions, recompute the normalized-source SHA-256 hash, retain
schema version 4, and set `AnalysisCacheVersion = 2` in the request hash.

Alternatives: Raise the SQLite schema version, normalize only at display time,
or reuse the existing cache and require every user to notice stale source.

Consequences: The next matching index request automatically rebuilds because
its request hash changes. A user who directly queries an already-built legacy
DB must run `index --rebuild` before expecting refreshed normalized source.

Date: 2026-08-11

## DEC-0030: Structured symbol paths and independently typed conditions

Status: Accepted

Context: Display-derived flat names, one global wildcard/regex mode, and
function-scoped anonymous numbering cannot express an exact namespace/type
boundary, nested executable containment, complete source-callable names, or
independent search intent without ambiguity.

Decision: Store and query a structured namespace/type/executable semantic
path. Csharp form is a documented namespace/type suffix search; explicit form
fixes the boundary exactly. Executable children use `.` and stored immediate
containment. Generic/parameter omission follows the three-state callable rule,
and the complete bracketed/synthetic callable catalog is canonical. Lambda and
anonymous-method nodes share positive source-order ordinals per immediate
owner. Compile namespace, type, method, file, include, and exclude conditions
as independently selected glob/literal/regex forms with independent strict or
ignore case categories. Same-category alternatives OR, categories AND,
includes AND, and excludes OR. Apply declaration/root conditions before
logical cardinality and traversal. Formatting style never changes identity or
canonical order.

Supersedes: DEC-0025 completely; DEC-0020's numbering/marker details;
DEC-0021's process-global source ignore-case switch while retaining its token
normalization, stored hash, and include/exclude composition; and DEC-0009's
display-derived fallback identity details. Project/profile scoping and other
unaffected identity decisions remain accepted.

Alternatives: Preserve the flat display-name grammar, infer namespace/type
boundaries, retain global matcher switches, or invent compatibility aliases.

Consequences: The bare legacy matcher switches and old executable-child
spelling are rejected. Csharp output remains copyable but may broaden; explicit
output is exact. Every command uses one typed option matrix and one root-only
selection pipeline. Anonymous ordinals are stable only inside one indexed
snapshot.

Date: 2026-09-01

## DEC-0031: Schema version 5 logical declarations and portable index roots

Status: Accepted

Context: Machine-specific rooted persistence and one physical symbol row per
source declaration prevent safe relocation and split partial definition and
implementation into duplicate query/call/graph identities.

Decision: Set `SchemaVersion = 5` and `AnalysisCacheVersion = 3`. Store one
logical `symbols` row and physical `symbol_declarations` rows with the exact
roles `ordinary`, `partial-definition`, and `partial-implementation`; prefer
the implementation when present. Calls, relations, interface bindings,
containment, async links, graph roots, and cardinality use logical IDs. Persist
canonical forward-slash project/document/declaration paths relative to one
storage root plus `index_runs.index_root_anchor` relative from the database
directory. Reject cross-drive or cross-UNC-share layouts before mutation.
`--base-dir` is a read-only reconstruction override; path and symbol styles are
presentation-only.

Supersedes: DEC-0011 completely; DEC-0022's schema layout/version; DEC-0029's
schema/cache/rebuild consequence while retaining its zero-width-token
normalization; and the rooted-path/display-derived portions of DEC-0009.
DEC-0028's atomic destination design remains accepted and now covers every
schema-5 query formatter.

Alternatives: Migrate old files in place, silently delete/rebuild on `index`,
store rooted paths as a fallback, model multiple unrelated roots, or preserve
separate partial callable identities.

Consequences: Version 4 and older are rejected without modification. Users
must delete/rename the old database or choose a new `--db` and run
`csindex index` explicitly. Standard and custom database layouts relocate when
their relative relationship is preserved, and `--base-dir` handles a changed
read-time root without rewriting stored data. A partial pair is one logical
candidate with role-preserving definition output.

Date: 2026-09-01
