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

Status: Accepted

Context: ラムダとローカル関数を独立callerにした場合の既定表示は未確定だった。
Decision: 既定を`direct`とし、直接のラムダ/ローカル関数を返す。`containing`と`both`を明示指定できる。
Alternatives: `containing`、`both`。
Consequences: DBの直接edgeと既定表示が一致する。外側methodが必要な利用者はoptionを指定する。
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

Status: Accepted

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

Status: Accepted

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

Status: Accepted

Context: 非同期起点へ到達する呼び出し元を検索時に毎回再帰CTEで求めるか、index作成時に導出して保存するかを決める必要がある。呼び出しグラフには自己再帰・相互再帰・複数起点があり、循環停止と決定的な最短距離が必要である。
Decision: 全Roslyn fact抽出後、解決済み`ReferenceKind.Invocation`の逆辺を作り、全非同期起点をdepth 0とするindex-timeの複数始点BFSを1回実行する。calleeからcallerの方向だけに進み、既訪問距離以下の候補は再展開せず、`AsyncInvolvementDepth`へ最短距離を保存する。query-time再帰CTEは採用しない。
Alternatives: query-time recursive CTE、起点ごとのDFS、呼び出し先方向への伝播。
Consequences: すべてのqueryで同じ結果をDB-onlyで返せ、自己再帰・相互再帰でも停止する。index時間と整数1列を使用し、呼び出しグラフ変更時は再indexが必要になる。保存するのは最短距離だけで、全経路や到達した全起点は保持しない。非同期関数から呼ばれる同期関数には伝播しない。
Date: 2026-07-22

## DEC-0017: Separate direct async roles from derived involvement

Status: Accepted

Context: 宣言`async`、awaitable返却、本文の`await`、非同期ストリームなどの直接事実と、別関数を経由して非同期起点へ到達するという派生事実は、意味と更新元が異なる。
Decision: Roslynから得る直接事実をflags enum `AsyncRole`、呼び出し辺での消費方法を`AsyncUsageKind`、逆辺BFSで得る派生最短距離をnullable `AsyncInvolvementDepth`として分離する。直接ロールを持つ起点自身もdepth 0を持つ。
Alternatives: 単一の`IsAsync` boolean、伝播先へ直接ロールをコピー、ロールと距離をquery時だけ合成。
Consequences: 「なぜ直接非同期か」と「何辺先で非同期へ到達するか」を区別できる。新しい直接ロールを追加しても伝播器の起点集合へ明示的に組み込める一方、モデル・DB・出力の3値を同期して保守する必要がある。
Date: 2026-07-22

## DEC-0018: Function listing and lambda call presentation

Status: Accepted

Context: 関数一覧、ラムダの識別子、callee検索の既定範囲、namespaceを含む名前の表示規則を一貫して定める必要がある。

Decision: `symbol list`の既定結果は`method`と`lambda`にする。`--kind method|lambda`と`--async-involved`で絞り込み、namespace短縮は`--short-names`によるpresentation-onlyの変換にする。JSONの`fullyQualifiedName`、stable key、namespace、parameter typeなどのcanonical fieldは短縮しない。ラムダ名は直接ownerごとに`<lambda#1>`から採番し、ネストしたラムダはその直近のラムダownerごとに再び採番する。`callees`は指定symbol配下のラムダdescendantによる呼び出しを再帰的に含め、`--exclude-lambda-calls`指定時だけmethod本体の直接呼び出しに限定する。

Alternatives: `symbol list`をmethodだけにする、document全体でラムダを連番にする、短縮名をJSON canonical fieldにも保存する、calleeを常に直接呼び出しだけにする。

Consequences: 一覧とcallee検索はラムダ本体の実行可能な呼び出しを既定で見落とさない。表示を短縮しても機械処理用の識別子は安定する。ラムダ番号はownerの構造を表すため、別owner間で番号を比較する意味はない。

Date: 2026-08-02
