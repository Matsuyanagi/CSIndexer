# Known Limitations

## Phase 1 / Phase 2

- 変更なしならRoslynを起動せずDBを再利用しますが、入力変更時は現在すべての対象Projectを再解析します。仕様22.3のプロジェクト単位差分更新と参照元Projectの保守的無効化は未実装です。
- キャッシュの事前判定は入力ルート内のソース・構成ファイル、明示参照、define fileをハッシュします。MSBuild評価後にだけ判明する入力ルート外のProjectReferenceや暗黙MetadataReferenceの変更は、`--rebuild`が必要な場合があります。
- 複数TFMの完全な並列インデックスと同一シンボルのProfile/TFM別表示は未実装です。`--framework`で1つを選択できます。
- 呼び出し抽出は通常呼び出し、オブジェクト生成、method group、delegate生成、`nameof`を扱います。プロパティaccessor、イベント、演算子、変換、関数ポインターはPhase 4です。
- 未解決・曖昧呼び出しと候補は保存しますが、高度なデリゲートフロー、`dynamic`の実行時候補、reflectionは追跡しません。
- 検索構文は通常型と通常メソッドを対象とし、ネスト型、ジェネリック型、配列型、nullable型、`ref/out/in`表記は予約済みエラーになります。
- `--generated-source all` / `none` は予約済みで、現在は既定の`physical`だけを受け付けます。
- 同じProfile名の再インデックスは、そのProfileの以前のデータを原子的に置き換えます。複数Profileは別名で保存できますが、`--all-profiles`横断検索はPhase 4です。
- Source Linkと外部シンボルのソース取得は未実装です。外部定義はシンボル名、アセンブリ名、「ソースなし」を返します。

## Unity / Phase 3

- Unity候補の検出、`.asmdef` / `.asmref`、既定アセンブリ分割、Unity参照DLL探索、Unityバージョンシンボルは未実装です。
- Unityディレクトリを明示的にDirectoryModeで解析することはできますが、現時点では単一仮想Projectになるため、Unity向けの正確な結果としては扱えません。
- 古い特殊フォルダー規則、Version Defines、Define Constraints、platform制約も未実装です。

## Phase 4

- 非物理Source Generator出力、ファイル単位差分、`semantic_hash`、仮想呼び出し候補の精密化、Source Link、call tree、DOT/YAML/JSONL、daemon、watch、IDE連携は未実装です。
