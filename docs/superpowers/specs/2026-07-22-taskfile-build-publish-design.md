# Taskfile build / publish design

## 目的

リポジトリルートから一貫したコマンドでCSIndexerをビルド、配布できるように、Taskfile v3形式の`taskfile.yml`を追加する。

## 対象範囲

- `task build`でsolution全体をビルドする。
- `task publish`で`CsIndex.Cli`をWindows x64向けに自己完結形式でpublishする。
- publish成果物は単一ファイル化せず、依存ファイルを含む通常のフォルダー形式とする。
- build configuration、runtime identifier、publish出力先をTask変数で上書き可能にする。

テスト、パッケージ生成、複数RIDの一括publish、MSBuild publish profileの追加は今回の対象外とする。

## Taskfile構成

リポジトリルートへ`taskfile.yml`を置き、Taskfile schema version 3を使用する。

既定変数は次のとおりとする。

| 変数 | 既定値 | 用途 |
|---|---|---|
| `CONFIGURATION` | `Release` | build / publish configuration |
| `RID` | `win-x64` | publish対象runtime identifier |
| `PUBLISH_DIR` | `artifacts/publish/<RID>` | publish出力先 |

Task CLIから`task build CONFIGURATION=Debug`や`task publish RID=win-x64 PUBLISH_DIR=...`の形式で上書きできるよう、各既定値はTaskfile templateの`default`関数で定義する。

## build task

`build`は次の処理だけを行う。

```text
dotnet build CsIndex.sln --configuration <CONFIGURATION>
```

solution全体を対象とし、既定configurationは`Release`とする。Task固有のキャッシュや`--no-restore`は指定せず、NuGet restoreとMSBuildの標準的な増分ビルドに従う。

## publish task

`publish`は次の処理を行う。

```text
dotnet publish src/CsIndex.Cli/CsIndex.Cli.csproj
  --configuration <CONFIGURATION>
  --runtime <RID>
  --self-contained true
  --output <PUBLISH_DIR>
```

`publish`から`build` taskを依存実行しない。`dotnet publish`自身のビルド処理を使用し、二重ビルドを避ける。

`PublishSingleFile`は設定しない。成果物には自己完結実行に必要な.NET runtimeと依存ファイルが含まれ、`csindex.exe`は`artifacts/publish/win-x64`へ出力される。

## エラー処理

Taskfile側で終了コードを変換しない。`dotnet build`または`dotnet publish`が失敗した場合は、そのcommandの非ゼロ終了コードによってtaskも失敗させる。

## 検証

実装後に次を確認する。

1. `task --list`に`build`と`publish`が表示される。
2. `task build`が成功し、solution全体をRelease構成でビルドする。
3. `task publish`が成功し、`artifacts/publish/win-x64/csindex.exe`と依存ファイルを生成する。
4. publish成果物が自己完結型であり、単一ファイルではないことを出力内容とMSBuildログで確認する。
5. `task build CONFIGURATION=Debug`など、変数上書きがcommandへ反映されることをdry-runまたは実行結果で確認する。
6. `taskfile.yml`の構文検査と`git diff --check`を通す。

## 判断

MSBuild publish profileは、配布variantが1種類だけの現段階では追加しない。Taskfileへ直接commandを記述することで、build / publishの入口と既定値を1ファイルで把握できる構成を採用する。
