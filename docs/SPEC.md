# C#ソースコード・セマンティック解析CLI 実装指示書

あなたは、このリポジトリに **C#ソースコードのセマンティック解析・検索CLI** を実装するコーディングエージェントです。

英語で考えてください。ソースコード内のコメントは日本語で書いてください。
本書は設計仕様、実装順序、制約、未実装機能、復旧方法をまとめた唯一の基準文書です。記載されていない仕様を推測して勝手に補完しないでください。

実装中に判断が必要になった場合は、次のいずれかとして明示してください。

1. 本書に記載された確定仕様
2. 本書に記載された推奨案
3. 未確定事項
4. 実装上新たに発見した制約

未確定事項について、外部仕様や既存コードから確定できない場合は、勝手に恒久仕様を決めず、`docs/DECISIONS.md` に候補と影響を書いてください。ただし、作業全体を止める必要がない場合は、交換可能な実装として先へ進めてください。

---

# 1. プロジェクトの目的

Windows上で動作するCLIツールを作成する。

このツールは、中規模から大規模のC#コードベースをRoslynで解析し、検索に必要なセマンティック情報をSQLiteへ保存する。

主な対象規模は次のとおり。

* 5,000ファイル以上
* 複数プロジェクトを含むソリューション
* Unityプロジェクト
* `.sln` や `.csproj` が存在しないソースディレクトリ
* 条件付きコンパイルを含むコード
* ラムダ、ローカル関数、ジェネリック、オーバーロード、継承を含むコード

毎回すべてのC#ソースを解析するのではなく、一度Roslynで解析した検索用情報をSQLiteに保存し、2回目以降の検索を高速にする。

重要な考え方は次のとおり。

> Roslynの構文木やSemanticModelそのものを永続化するのではなく、Roslynから得られた検索用の事実を独自のセマンティックインデックスとして保存する。

---

# 2. 実装対象外の考え方

次の設計にはしないこと。

* `DiagnosticAnalyzer` を主実装にしない
* Roslynの内部キャッシュ形式を永続ストレージとして利用しない
* 構文木やSemanticModelを独自シリアライズしない
* YAMLを主データベースにしない
* 独自バイナリ形式を主データベースにしない
* メソッド名の文字列一致だけで呼び出し先を判断しない
* 全シンボルについて `SymbolFinder.FindReferencesAsync` を繰り返してインデックスを構築しない
* ソースコードを正規表現だけで解析しない
* コメント中のコードらしい文字列を参照や呼び出しとして扱わない

Roslyn APIを使用する独立したCLIとして実装する。

---

# 3. 確定技術要件

## 3.1 動作環境

* Windows向けCLI
* C#
* .NET 10
* CLI自身のTarget Frameworkは `net10.0-windows`
* 基準Runtime Identifierは `win-x64`
* Windows x64環境を既定の解析実行環境とする
* SQLiteを永続ストレージとして使用する

CLI自身が.NET 10で動作することと、解析対象コードが.NET 10向けであることを混同しないこと。

`.csproj` がないDirectoryModeでは、明示的な指定がない限り、解析対象に対して `NET10_0` や `NET10_0_OR_GREATER` を自動定義しない。

## 3.2 推奨プロジェクトファイル例

実際のパッケージバージョンは、実装時点で利用可能な互換性のある安定版を確認し、固定すること。バージョンを推測しないこと。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Roslyn関連では、少なくとも次の機能が必要になる。

* C#構文解析
* Workspaces
* MSBuildWorkspace
* AdhocWorkspace
* `Microsoft.Build.Locator` 相当のMSBuild検出
* SQLite接続ライブラリ

SQLiteライブラリの具体的な選択は交換可能にすること。推奨候補は `Microsoft.Data.Sqlite` だが、最終採用結果を `docs/DECISIONS.md` に記録すること。

---

# 4. 必須の作業継続・Compact復旧設計

LLMのCompact、コンテキスト消失、別セッションへの引き継ぎが発生しても作業を復旧できるようにする。

実装開始時に次のファイルを作成すること。

```text
docs/
  SPEC.md
  DECISIONS.md
  IMPLEMENTATION_STATUS.md
  KNOWN_LIMITATIONS.md
  DB_SCHEMA.md
  CLI.md
  TEST_PLAN.md

TASKS.md
```

## 4.1 `docs/SPEC.md`

本指示書の内容を、意味を変更せず保存する。

以後、本書と `docs/SPEC.md` が矛盾した場合は、ユーザーが後から明示的に変更した内容を優先し、その変更を `docs/DECISIONS.md` に記録する。

## 4.2 `docs/IMPLEMENTATION_STATUS.md`

必ず次の形式を維持する。

```markdown
# Implementation Status

## Current Phase

Phase 1 / Phase 2 / Phase 3 / Phase 4

## Last Completed Work

- 完了項目

## Currently Implementing

- 現在の作業

## Next Actions

1. 次の作業
2. その次の作業

## Build Status

- Command:
- Result:
- Date:

## Test Status

- Command:
- Passed:
- Failed:
- Skipped:
- Date:

## Known Broken Areas

- なし、または具体的な内容

## Important Files Changed

- path
- path

## Database Schema Version

- version

## CLI Commands Implemented

- command

## Pending Decisions

- decision ID
```

各作業単位の終了時に必ず更新すること。

## 4.3 `docs/DECISIONS.md`

決定ごとにIDを付ける。

```markdown
## DEC-0001: SQLite provider

Status: Accepted / Proposed / Rejected

Context:
Decision:
Alternatives:
Consequences:
Date:
```

## 4.4 `TASKS.md`

全フェーズの作業を削除せず保持する。

完了項目はチェックするだけにし、後続フェーズを削除しないこと。

```markdown
- [ ] Phase 1
  - [ ] ...
- [ ] Phase 2
  - [ ] ...
- [ ] Phase 3
  - [ ] ...
- [ ] Phase 4
  - [ ] ...
```

## 4.5 Compact後の復旧手順

新しい作業セッションの冒頭では、必ず次の順で読むこと。

1. `docs/SPEC.md`
2. `docs/IMPLEMENTATION_STATUS.md`
3. `TASKS.md`
4. `docs/DECISIONS.md`
5. `docs/KNOWN_LIMITATIONS.md`
6. 直近で変更されたソース
7. テスト結果

Compact前の会話記憶だけを頼りに作業を再開しないこと。

---

# 5. 全体アーキテクチャ

推奨構成は次のとおり。

```text
C# Solution / Project / Directory
    ↓
Input Resolver
    ↓
MSBuildWorkspace または AdhocWorkspace
    ↓
Compilation / SemanticModel / IOperation
    ↓
Semantic Extractors
    ↓
SQLite Semantic Index
    ↓
Query Engine
    ↓
Windows CLI
```

推奨ソリューション構成。

```text
CsIndex.sln

src/
  CsIndex.Cli/
    Program.cs
    Commands/
      IndexCommand.cs
      SymbolCommand.cs
      DefinitionCommand.cs
      ReferencesCommand.cs
      CallersCommand.cs
      CalleesCommand.cs
      OverridesCommand.cs
      ConditionsCommand.cs

  CsIndex.Core/
    Input/
      InputMode.cs
      InputModeResolver.cs
      SolutionLoader.cs
      ProjectLoader.cs
      DirectoryLoader.cs
      SourceFileEnumerator.cs

    Analysis/
      AnalysisCoordinator.cs
      DeclarationExtractor.cs
      OperationWalker.cs
      CallGraphExtractor.cs
      InheritanceExtractor.cs
      ReferenceExtractor.cs
      ConditionalDirectiveScanner.cs
      GeneratedCodeDetector.cs
      InitializerOwnerResolver.cs

    Symbols/
      SymbolCanonicalizer.cs
      SymbolIdentity.cs
      SymbolQuery.cs
      SymbolQueryParser.cs
      SymbolMatcher.cs
      TypeNameNormalizer.cs

    Profiles/
      AnalysisProfile.cs
      ProfileBuilder.cs
      PreprocessorSymbolResolver.cs
      ProfileHasher.cs

    Unity/
      UnityProjectDetector.cs
      UnityVersionReader.cs
      UnityAssemblyBuilder.cs
      AsmDefReader.cs
      AsmRefReader.cs
      UnityReferenceResolver.cs
      UnitySymbolResolver.cs

    Caching/
      FileHasher.cs
      ProjectFingerprintBuilder.cs
      ChangeDetector.cs

  CsIndex.Storage/
    SqliteIndex.cs
    Schema/
      SchemaMigrator.cs
      Migrations/
    Repositories/
      ProfileRepository.cs
      ProjectRepository.cs
      DocumentRepository.cs
      SymbolRepository.cs
      CallRepository.cs
      RelationRepository.cs

  CsIndex.Query/
    DefinitionQuery.cs
    ReferenceQuery.cs
    CallerQuery.cs
    CalleeQuery.cs
    OverrideQuery.cs
    CallTreeQuery.cs

tests/
  CsIndex.Core.Tests/
  CsIndex.Storage.Tests/
  CsIndex.Query.Tests/
  CsIndex.IntegrationTests/
  TestData/
```

名称は変更可能だが、責務の分離は維持すること。

---

# 6. 入力モード

入力モードは次の3種類。

```csharp
enum InputMode
{
    Solution,
    Project,
    Directory
}
```

## 6.1 SolutionMode

対象:

* `.sln`
* `.slnx`

`MSBuildWorkspace` を使用する。

## 6.2 ProjectMode

対象:

* `.csproj`

`MSBuildWorkspace` を使用する。

## 6.3 DirectoryMode

対象:

* `.sln` と `.csproj` がないディレクトリ
* 明示的にDirectoryModeが指定されたディレクトリ

`AdhocWorkspace` と独自の `ProjectInfo` を使用する。

---

# 7. 入力モードの自動判定

基本ルール。

```text
1. 入力が .sln または .slnx
   → SolutionMode

2. 入力が .csproj
   → ProjectMode

3. 入力がディレクトリ
   3-1. 対象範囲内に .sln/.slnx が1個
        → SolutionMode

   3-2. .sln/.slnxがなく、.csprojが1個以上
        → ProjectMode

   3-3. .sln/.slnxも.csprojもない
        → DirectoryMode
```

複数の `.sln` または `.slnx` が見つかった場合、勝手に1つを選択しないこと。

候補を表示し、明示指定を要求する。

例:

```powershell
csindex index C:\Source\Game --solution Game.sln
```

複数の `.csproj` があり `.sln` がない場合の扱いは、次のようにする。

* 各 `.csproj` を個別にロード可能なら、すべて解析対象にする
* プロジェクト間参照はMSBuild評価結果を使用する
* 一部のプロジェクトがロードできない場合は警告し、継続可能なプロジェクトを解析する
* 完全にロード不能な場合だけ致命的エラーとする

---

# 8. ソースファイル列挙

DirectoryModeでは、指定ディレクトリ以下のすべての `.cs` ファイルを対象にする。

ただし、パス要素として `obj` を含むディレクトリ配下は常に除外する。

次のようなパスはいずれも除外する。

```text
obj/Foo.cs
src/obj/Foo.cs
src/Obj/Foo.cs
SRC/OBJ/Foo.cs
```

Windows上なので、パス比較は大文字小文字を区別しない。

参考実装。

```csharp
static bool IsTargetSourceFile(string rootPath, string filePath)
{
    if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        return false;

    string relativePath = Path.GetRelativePath(rootPath, filePath);

    string[] parts = relativePath.Split(
        new[]
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        },
        StringSplitOptions.RemoveEmptyEntries);

    return !parts.Any(
        part => part.Equals("obj", StringComparison.OrdinalIgnoreCase));
}
```

## 8.1 既定で除外しないディレクトリ

次は既定では除外しない。

```text
bin
Library
Temp
Packages
Assets
Library/PackageCache
Generated
GeneratedCode
```

理由は、「指定ディレクトリ以下のすべての `.cs`」を基本要件とするため。

ただし追加除外を指定できること。

```powershell
csindex index C:\Game `
  --exclude "**/Library/PackageCache/**" `
  --exclude "**/Temp/**"
```

`obj` 除外はユーザー指定で解除できない必須規則とする。

---

# 9. コメントの扱い

ソースコード中のコメントは解析対象外。

次はいずれも呼び出しや参照として登録しない。

```csharp
// player.Play();

/*
player.Play();
*/

/// <see cref="Player.Play"/>
```

実装では、呼び出し抽出に `IOperation` を使用する。

コメントはTriviaであり、通常の `IOperation` ツリーには含まれない。

XMLドキュメントコメント中の `cref` も対象外。

`nameof(Player.Play)` はコメントではないため、参照として保存してよいが、呼び出しには含めない。

---

# 10. 生成コードの扱い

物理的に `.cs` ファイルとして存在するなら、生成コードであっても解析対象にする。

例:

```text
Foo.g.cs
Foo.generated.cs
Foo.Designer.cs
GeneratedBindings.cs
```

ファイル名だけを理由に解析対象から除外しない。

## 10.1 生成コード判定

各Documentに次を記録する。

* `is_generated`
* `generation_kind`

判定候補:

* `*.g.cs`
* `*.generated.cs`
* `*.designer.cs`
* `*.AssemblyAttributes.cs`
* ファイル先頭の `<auto-generated>`
* ファイル先頭の `<autogenerated>`
* `Generated/`
* `GeneratedCode/`

これは検索フィルター用の判定であり、既定の解析除外には使わない。

## 10.2 検索時フィルター

既定:

```text
生成コードを含める
```

生成コードを除外:

```powershell
csindex callers "Player::Play()" --exclude-generated
```

生成コードだけ:

```powershell
csindex callers "Player::Play()" --only-generated
```

## 10.3 Source Generatorの非物理出力

RoslynのCompilation内にだけ存在し、物理的な `.cs` ファイルが存在しない生成ソースは、初期段階では対象外。

将来オプションを予約する。

```text
--generated-source physical
--generated-source all
--generated-source none
```

既定値は `physical`。

この機能はPhase 4まで予約し、初期実装で削除しないこと。

---

# 11. Unityプロジェクト対応

## 11.1 Unityプロジェクト判定

DirectoryModeで、少なくとも次の構成を確認する。

```text
Assets/
ProjectSettings/
ProjectSettings/ProjectVersion.txt
```

条件を満たす場合はUnityプロジェクト候補として扱う。

## 11.2 単一仮想プロジェクトだけでは不十分

Unityでは `.asmdef` や `.asmref` によってアセンブリ境界、参照関係、プラットフォーム制約が決まる。

可能な限りUnityのアセンブリ構造を復元すること。

## 11.3 Unityアセンブリ構築手順

```text
1. .asmdefを再帰検索
2. .asmrefを再帰検索
3. 各.csファイルを最も近い親.asmdefへ所属させる
4. .asmrefを解決する
5. asmdef間参照をProjectReference相当として構築する
6. asmdefに所属しないファイルをUnity既定アセンブリへ割り当てる
7. UnityEngine、UnityEditorなどの参照DLLを追加する
8. 条件付きシンボルを設定する
```

想定される仮想プロジェクト例。

```text
Assembly-CSharp
Assembly-CSharp-Editor
Game.Core
Game.Runtime
Game.Editor
Package.X
```

## 11.4 asmdefに所属しないファイル

最低限、次を区別する。

```text
Assets/**/Editor/**/*.cs
    → Assembly-CSharp-Editor相当

Assets/**/*.cs
    → Assembly-CSharp相当
```

古いUnityの特殊フォルダー規則は、最初から完全対応しなくてよい。

未対応規則は `docs/KNOWN_LIMITATIONS.md` に記録する。

## 11.5 Unity参照アセンブリの解決優先順位

```text
1. --referenceで指定されたDLL
2. UnityプロジェクトのLibrary/ScriptAssemblies
3. --unity-editorで指定されたUnity Editor
4. Unity Hubの既知インストール先
5. 解決できなければ警告し、部分解析を継続
```

例:

```powershell
csindex index C:\Game `
  --unity-editor "C:\Program Files\Unity\Hub\Editor\6000.1.0f1"
```

Unity参照が不足すると、次のようなコードを正しく解決できない可能性がある。

```csharp
GetComponent<AudioSource>().Play();
```

未解決呼び出しを破棄しないこと。

次のような状態を記録する。

```csharp
enum ResolutionStatus
{
    Resolved,
    Ambiguous,
    Unresolved,
    Dynamic
}
```

未解決理由も記録する。

```csharp
enum ResolutionReason
{
    None,
    MissingMetadataReference,
    CompilationError,
    AmbiguousCandidates,
    DynamicDispatch,
    UnsupportedConstruct,
    Unknown
}
```

---

# 12. 条件付きコンパイル

条件付きコンパイルは解析結果を変えるため、解析プロファイルとして管理する。

## 12.1 `.sln` / `.csproj` がある場合

MSBuild評価後の情報を優先する。

* Configuration
* Target Framework
* Runtime Identifier
* DefineConstants
* ParseOptionsのPreprocessorSymbolNames
* OS固有Target Frameworkから生成されるシンボル
* プロジェクトごとの条件

解析開始時に有効な条件を表示する。

例:

```text
Warning CSIDX1001:
Conditional compilation directives were found.

Profile:
  Configuration: Debug
  Target framework: net8.0-windows
  Runtime: win-x64

Active symbols:
  DEBUG
  TRACE
  NET
  NET8_0
  NET8_0_OR_GREATER
  WINDOWS

Only active conditional branches will be semantically indexed.
```

警告は解析失敗ではないため、原則として成功終了扱いにする。

## 12.2 CLIによる追加・削除

```powershell
csindex index Game.sln `
  --configuration Release `
  --framework net10.0-windows `
  --runtime win-x64 `
  --define FEATURE_AUDIO `
  --define USE_NEW_PLAYER `
  --undefine DEBUG
```

最終シンボル集合:

```text
MSBuildで取得したシンボル
+ --define
- --undefine
```

CLI指定を優先する。

## 12.3 一般DirectoryMode

既定プロファイル:

```text
Profile: generic-windows-x64
Operating system: Windows
Architecture: x64
Defined:
  WINDOWS
```

次は自動定義しない。

```text
NET
NET10_0
NET10_0_OR_GREATER
DEBUG
TRACE
```

明示指定例:

```powershell
csindex index C:\Source `
  --target-framework net10.0-windows
```

Target Frameworkを指定した場合、対応するフレームワークシンボルを生成する。

## 12.4 Unity DirectoryMode

既定プロファイル:

```text
Profile: unity-editor-windows-x64
Runtime: win-x64

Defined:
  UNITY_EDITOR
  UNITY_EDITOR_WIN
  UNITY_STANDALONE
  UNITY_STANDALONE_WIN
  UNITY_64
```

`UNITY_64` は推定値であることを警告する。

```text
Warning CSIDX1003:
UNITY_64 has been inferred from the Windows x64 analysis profile.
```

`--undefine UNITY_64` で解除可能にする。

## 12.5 Unityバージョンシンボル

`ProjectSettings/ProjectVersion.txt` からバージョンを取得できる場合、対応するUnityバージョンシンボルを生成する。

例:

```text
UNITY_6000
UNITY_6000_0
UNITY_6000_0_33
UNITY_6000_0_OR_NEWER
```

正確なUnityのシンボル生成規則を実装時に検証すること。

規則を推測して不正確に実装しないこと。

## 12.6 カスタムシンボル

取得できない場合は警告する。

```text
Warning CSIDX1004:
Unity custom scripting symbols could not be determined.
Use --define or --define-file to specify them.
```

例:

```powershell
csindex index C:\Game --define-file defines.txt
```

```text
ODIN_INSPECTOR
USE_ADDRESSABLES
FEATURE_MULTIPLAYER
```

## 12.7 複数解析プロファイル

同じSQLiteに複数プロファイルを保存可能にする設計を維持する。

```powershell
csindex index C:\Game `
  --profile-name editor `
  --define UNITY_EDITOR

csindex index C:\Game `
  --profile-name windows-player `
  --undefine UNITY_EDITOR `
  --define UNITY_STANDALONE_WIN
```

検索:

```powershell
csindex callers "Player::Play()" --profile editor
```

```powershell
csindex callers "Player::Play()" --all-profiles
```

Phase 1では単一プロファイルしか実装しなくてもよいが、DBスキーマから `analysis_profile_id` を削除しないこと。

---

# 13. Roslyn解析方針

## 13.1 構文だけではなくセマンティック情報を使う

次のコードだけでは対象メソッドを判別できない。

```csharp
Play();
```

必ず `SemanticModel` または `IOperation` で解決する。

呼び出し抽出例:

```csharp
var operation = semanticModel.GetOperation(invocationSyntax, cancellationToken);

if (operation is IInvocationOperation invocation)
{
    IMethodSymbol target = invocation.TargetMethod;
}
```

可能なら個々のInvocationSyntaxを再検索するより、メソッド本体のOperationツリーを一度歩く実装を優先する。

## 13.2 抽出する主な情報

* 型定義
* メソッド定義
* コンストラクター
* ローカル関数
* ラムダ
* 呼び出し関係
* メソッドグループ参照
* デリゲート生成
* `nameof` 参照
* 継承関係
* オーバーライド関係
* インターフェイス実装関係
* ソース位置
* 生成コードフラグ
* 解決状態
* 条件付きコンパイル情報

## 13.3 呼び出しと参照を分離する

次は同じ扱いにしないこと。

```csharp
Play();
Action action = Play;
nameof(Play);
```

参考分類:

```csharp
enum ReferenceKind
{
    Invocation,
    ObjectCreation,
    MethodGroup,
    DelegateCreation,
    NameOf,
    Operator,
    Conversion,
    DynamicInvocation,
    Unknown
}
```

コメントとXML `cref` は含めない。

## 13.4 非同期ロール、呼び出し利用方法、非同期関与

非同期の直接的な性質はシンボルのflags enum `AsyncRole` に保存する。宣言由来の判定にはRoslynのシンボルを、本文由来の判定にはRoslynのoperationを使用し、名前の接尾辞（`Async`など）だけでは判定しない。

| `AsyncRole` | Roslynによる判定条件 |
|---|---|
| `None` | 直接ロールなし |
| `DeclaredAsync` | `IMethodSymbol.IsAsync` |
| `ReturnsAwaitable` | 戻り値の`OriginalDefinition`が、コンパイル内の`Task` / `Task<T>`、`ValueTask` / `ValueTask<T>`、`Cysharp.Threading.Tasks.UniTask` / `UniTask<T>`のいずれかとシンボル同値 |
| `ContainsAwait` | 所有関数内にRoslynが構築した`IAwaitOperation`がある |
| `AsyncIterator` | `IMethodSymbol.IsAsync && IMethodSymbol.IsIterator` |
| `ReturnsAsyncEnumerable` | 戻り値の`OriginalDefinition`が`IAsyncEnumerable<T>`または`Cysharp.Threading.Tasks.IUniTaskAsyncEnumerable<T>`とシンボル同値 |
| `AsyncVoid` | `IMethodSymbol.IsAsync && IMethodSymbol.ReturnsVoid` |
| `UniTaskVoid` | 戻り値が`Cysharp.Threading.Tasks.UniTaskVoid`とシンボル同値。fire-and-forgetを通常のawaitable返却と区別する |
| `UsesAwaitForEach` | `IForEachLoopOperation.IsAsynchronous` |
| `UsesAwaitUsing` | `IUsingOperation.IsAsynchronous`または`IUsingDeclarationOperation.IsAsynchronous` |

`Task`、`ValueTask`、`UniTask`の非generic/generic型は`ReturnsAwaitable`として同じ扱いにする。`UniTaskVoid`と非同期ストリームは、それぞれ`UniTaskVoid`、`ReturnsAsyncEnumerable`という別ロールにする。既知型の照合にはmetadata nameから取得したシンボルとの`SymbolEqualityComparer.Default`を使用し、同名の利用者定義型を誤認しない。

ラムダとローカル関数は外側メソッドとは別の所有者である。`FindOwner`が決めた所有者だけへoperation由来ロールをORし、ネストした`await`、`await foreach`、`await using`を外側へ漏らさない。async lambda/local functionはそれ自体が非同期起点になり、起点の`AsyncInvolvementDepth`は0である。フィールド、event field、プロパティの初期化子だけをsynthetic initializer所有者として登録し、ラムダ本体内のローカル変数初期化子や引数の既定値をinitializer所有者として登録しない。

解決済み呼び出し辺にはenum `AsyncUsageKind`を保存する。`IInvocationOperation`から親operationを上へたどり、複数条件に一致するときは次表の上から順に優先する。

| 優先順 | `AsyncUsageKind` | Roslyn operationと例 |
|---:|---|---|
| 1 | `Awaited` | `IAwaitOperation`: `await LoadAsync()` |
| 2 | `Forwarded` | `IReturnOperation`: `return LoadAsync()`、式本体による返却 |
| 3 | `Discarded` | targetが`IDiscardOperation`の`ISimpleAssignmentOperation`: `_ = LoadAsync()` |
| 4 | `Stored` | `IVariableInitializerOperation`または`ISimpleAssignmentOperation`: `var task = LoadAsync()` |
| 5 | `Passed` | `IArgumentOperation`: `WhenAll(LoadAsync())` |
| 6 | `Unobserved` | `IExpressionStatementOperation`: `LoadAsync();` |
| 7 | `None` | 上記に該当しない、非呼び出し参照、または構造上awaitされていない非awaitable呼び出し |

親operationの走査は同じ所有関数内の変換、括弧、条件アクセス、`ConfigureAwait`などの呼び出し連鎖を越えて継続するが、`IAnonymousFunctionOperation`または`ILocalFunctionOperation`に達した時点で停止する。これにより、ラムダやローカル関数内の呼び出しが外側の代入・引数・return文脈を継承しない。複数祖先に一致する場合の上記優先順位は同じ所有者内だけに適用する。

呼び出し式が実際の`IAwaitOperation`配下にある場合は、戻り値が既知型でないcustom awaitableでも`Awaited`とする。`Forwarded`、`Discarded`、`Stored`、`Passed`、`Unobserved`は、呼び出し先の戻り値が`ReturnsAwaitable`と同じ既知の`Task` / `ValueTask` / `UniTask`型である場合だけ付ける。それ以外の同期・未知型の呼び出し辺は`None`とする。

呼び出し式と`await`が別文のときはデータフローを遡らないため、生成元の呼び出し辺を`Awaited`へ変更しない。ただし実際の`await`は所有関数の`ContainsAwait`として記録する。

非同期関与はnullable整数`AsyncInvolvementDepth`で表す。ソース情報を優先して統合済みのシンボルのうち、`None`以外の直接ロールを1つ以上持つものを起点（depth 0）とする。全ドキュメント抽出後、解決済みの通常呼び出し（`ReferenceKind.Invocation`）から`callee_definition_key -> caller_symbol_key`の逆辺を作り、全起点を同時にキューへ入れる複数始点BFSを1回実行する。呼び出し元はcalleeの距離+1とし、既訪問の最短距離以下になる候補は再投入しない。この停止条件により自己再帰・相互再帰・複数循環でも有限に停止し、複数経路がある場合は最短距離だけを保持する。

伝播方向は「非同期関数へ到達する呼び出し元方向」のみである。非同期起点から呼ばれる同期関数へは伝播せず、非同期起点へ到達しない循環のdepthはnullのままとする。

Coreモデルとschema version 2のSQLite列は次の対応とする。

| Coreモデル | SQLite列 |
|---|---|
| `SymbolData.AsyncRole` | `symbols.async_role INTEGER NOT NULL DEFAULT 0` |
| `SymbolData.AsyncInvolvementDepth` | `symbols.async_involvement_depth INTEGER` |
| `CallData.AsyncUsageKind` | `calls.async_usage_kind INTEGER NOT NULL DEFAULT 0` |

---

# 14. シンボル同一性

SQLite内部参照には整数IDを使用する。

再解析後の対応付けには `stable_key` を使用する。

## 14.1 通常の名前付きシンボル

基本構成:

```text
解析プロファイル
+ プロジェクトまたはアセンブリ識別子
+ Target Framework
+ Documentation Comment ID
```

概念例:

```text
profile:editor
assembly:Game.Runtime
tfm:unity
M:Game.Audio.AudioPlayer.Play(System.String,System.Single)
```

Documentation Comment IDが取得できないシンボルについては、独自キーを使用する。

## 14.2 ジェネリックメソッド

実際の構築済みメソッドと元定義を分離する。

例:

```csharp
Convert<int>(value);
Convert<string>(text);
```

記録するもの:

* 実際の `TargetMethod`
* `TargetMethod.OriginalDefinition`
* 必要なら構築型引数

通常の「このメソッドの呼び出し箇所」検索は `OriginalDefinition` 基準で検索可能にする。

## 14.3 拡張メソッド

Reduced extension methodを考慮する。

検索の正規化キー候補:

```csharp
target.ReducedFrom ?? target.OriginalDefinition
```

ただし、実際のTargetMethod情報も保持する。

---

# 15. オーバーロード

同名メソッドは、名前ではなく `IMethodSymbol` 単位で保存する。

例:

```csharp
void Play();
void Play(string name);
void Play(int id);
```

別シンボルとして扱う。

```text
M:X.Play
M:X.Play(System.String)
M:X.Play(System.Int32)
```

呼び出し側では `IInvocationOperation.TargetMethod` を使用する。

コンパイルエラーなどで一意に決まらない場合は、候補を保存可能にする。

* `Resolved`
* `Ambiguous`
* `Unresolved`
* `Dynamic`

曖昧な候補は `SemanticModel.GetSymbolInfo()` から取得できる範囲で保存する。

---

# 16. ラムダ、ローカル関数、初期化子

## 16.1 ラムダ

表示名例:

```text
Game.Player::Update()::<lambda#1>
Game.Player::Update()::<lambda#2>
```

通し番号だけを永続キーにしない。

永続識別候補:

```text
ContainingMethodStableKey
+ DocumentId
+ SyntaxKind
+ SourceSpan
+ DocumentContentHash
```

概念例:

```text
owner: M:Game.Player.Update
document: Player.cs
start: 1842
length: 27
version: sha256:...
```

表示用番号と永続識別子を分離する。

## 16.2 ローカル関数

例:

```csharp
void Run()
{
    void Play(int count) { }
}
```

キー候補:

```text
ContainingMethodStableKey
+ LocalFunctionName
+ ParameterSignature
+ DeclarationAnchor
```

## 16.3 ラムダ内の呼び出し元

例:

```csharp
void Execute()
{
    button.Click += () => player.Play();
}
```

直接呼び出し元:

```text
Execute::<lambda#1>
```

  外側の名前付きメソッド:

```text
Execute
```

クエリで両方扱えるようにする。

推奨オプション:

```text
--caller-scope direct
--caller-scope containing
--caller-scope both
```

正確な既定値は未確定。候補と影響を `docs/DECISIONS.md` に記録する。

## 16.4 初期化子

名前付きメソッド外の実行コードに仮想所有者を与える。

```text
XClass::<instance-initializer>
XClass::<static-initializer>
PropertyName::<initializer>
Program::<top-level-statements>
```

---

# 17. 継承、オーバーライド、仮想ディスパッチ

例:

```csharp
BaseClass value = GetObject();
value.Run();
```

静的解決先:

```text
BaseClass.Run()
```

実行時候補:

```text
XClass.Run()
YClass.Run()
```

これらを混同しない。

## 17.1 保存する関係

```csharp
enum SymbolRelationKind
{
    Inherits,
    Implements,
    Overrides,
    ExplicitlyImplements,
    ImplicitlyImplements,
    PartialDefinition,
    PartialImplementation
}
```

## 17.2 検索モード

```text
--dispatch static
```

Roslynが静的に確定した呼び出し先だけ。

```text
--dispatch virtual
```

オーバーライド候補を含める。

```text
--dispatch all
```

インターフェイス実装候補も含める。

既定は `static`。

結果には区別を表示する。

```text
Program.cs:42
  static target: BaseClass.Run()
  possible runtime target: XClass.Run()
```

`new` によるメソッド隠蔽は `Overrides` として扱わない。

---

# 18. 名前空間省略検索

検索構文の基本形:

```text
[namespace.]type::method[(parameter-types)]
```

## 18.1 名前空間指定

```powershell
csindex callers "GameNS.Player::Play()"
```

`GameNS.Player.Play()` のみ対象。

## 18.2 名前空間省略

```powershell
csindex callers "Player::Play()"
```

次の両方を対象にする。

```text
GameNS.Player::Play()
PianoNS.Player::Play()
```

名前空間省略時は曖昧エラーにせず、全候補へ展開する。

結果例:

```text
Query matched 2 symbols:

  GameNS.Player::Play()
  PianoNS.Player::Play()

12 call sites found.
```

単一候補を要求:

```powershell
csindex callers "Player::Play()" --require-single
```

複数候補なら非ゼロ終了。

## 18.3 オーバーロード省略

```text
Player::Play
```

すべての `Play` オーバーロード。

```text
Player::Play()
```

引数なしだけ。

```text
Player::Play(System.String)
```

`System.String`を受け取るもの。

```text
Player::Play(string)
```

C#キーワード型を正規化し、`System.String` と同一視する。

## 18.4 大文字小文字

C#と同様に区別する。

## 18.5 型検索

```powershell
csindex symbol find "Player"
```

単純型名が `Player` のすべて。

```powershell
csindex symbol find "GameNS.Player"
```

完全修飾型名。

## 18.6 将来考慮する構文

初期実装で無理に対応しなくてよいが、パーサー拡張を妨げないこと。

* ネスト型
* ジェネリック型
* ジェネリックメソッド
* 配列型
* Nullable型
* `ref` / `out` / `in`
* タプル
* 関数ポインター
* 演算子
* 明示的インターフェイス実装

未対応形式は明確なエラーを返す。

---

# 19. SQLite設計

SQLiteを主データベースにする。

推奨配置:

```text
.csindex/
  index.sqlite
  manifest.json
```

## 19.1 必須テーブル

### schema_info

```sql
CREATE TABLE schema_info (
    version INTEGER NOT NULL
);
```

`schema_info`が存在しないDBを初期化できるのは、SQLiteの内部objectを除くuser table / index / view / triggerが1つもない場合だけとする。非空の未認識DBでは、`PRAGMA journal_mode=WAL`やDDLを実行する前に明示的なエラーを返し、既存object、行、journal modeを変更しない。空の新規DBは通常どおり現行schemaで初期化する。

### analysis_profiles

```sql
CREATE TABLE analysis_profiles (
    id                    INTEGER PRIMARY KEY,
    name                  TEXT NOT NULL,
    input_mode            INTEGER NOT NULL,
    configuration         TEXT,
    target_framework      TEXT,
    runtime_identifier    TEXT,
    operating_system      TEXT,
    architecture          TEXT,
    preprocessor_symbols  TEXT NOT NULL,
    profile_hash          BLOB NOT NULL UNIQUE
);
```

`preprocessor_symbols` の保存形式は、決定的な順序を持つJSON配列などを使用する。

### projects

```sql
CREATE TABLE projects (
    id                    INTEGER PRIMARY KEY,
    analysis_profile_id   INTEGER NOT NULL,
    name                  TEXT NOT NULL,
    assembly_name         TEXT,
    project_path          TEXT,
    target_framework      TEXT,
    project_fingerprint   BLOB NOT NULL,

    FOREIGN KEY(analysis_profile_id)
      REFERENCES analysis_profiles(id)
);
```

### documents

```sql
CREATE TABLE documents (
    id                    INTEGER PRIMARY KEY,
    project_id            INTEGER NOT NULL,
    normalized_path       TEXT NOT NULL,
    content_hash          BLOB NOT NULL,
    semantic_hash         BLOB,
    is_generated          INTEGER NOT NULL DEFAULT 0,
    generation_kind       INTEGER NOT NULL DEFAULT 0,

    UNIQUE(project_id, normalized_path),

    FOREIGN KEY(project_id)
      REFERENCES projects(id)
);
```

### symbols

```sql
CREATE TABLE symbols (
    id                    INTEGER PRIMARY KEY,
    analysis_profile_id   INTEGER NOT NULL,
    project_id            INTEGER,
    stable_key            TEXT NOT NULL,
    kind                  INTEGER NOT NULL,

    name                  TEXT NOT NULL,
    namespace_name        TEXT NOT NULL DEFAULT '',
    type_simple_name      TEXT,
    type_metadata_name    TEXT,
    fully_qualified_name  TEXT NOT NULL,
    display_name          TEXT NOT NULL,

    containing_symbol_id  INTEGER,
    arity                 INTEGER NOT NULL DEFAULT 0,
    parameter_count       INTEGER,

    method_kind           INTEGER,
    accessibility         INTEGER,

    is_static             INTEGER NOT NULL DEFAULT 0,
    is_abstract           INTEGER NOT NULL DEFAULT 0,
    is_virtual            INTEGER NOT NULL DEFAULT 0,
    is_override           INTEGER NOT NULL DEFAULT 0,

    async_role            INTEGER NOT NULL DEFAULT 0,
    async_involvement_depth INTEGER,

    source_document_id    INTEGER,
    source_start          INTEGER,
    source_length         INTEGER,

    is_generated          INTEGER NOT NULL DEFAULT 0,

    UNIQUE(analysis_profile_id, stable_key),

    FOREIGN KEY(containing_symbol_id)
      REFERENCES symbols(id),

    FOREIGN KEY(source_document_id)
      REFERENCES documents(id)
);
```

### method_parameters

```sql
CREATE TABLE method_parameters (
    method_id       INTEGER NOT NULL,
    ordinal         INTEGER NOT NULL,
    name            TEXT,
    type_key        TEXT NOT NULL,
    ref_kind        INTEGER NOT NULL,
    is_optional     INTEGER NOT NULL DEFAULT 0,

    PRIMARY KEY(method_id, ordinal),

    FOREIGN KEY(method_id)
      REFERENCES symbols(id)
);
```

### calls

```sql
CREATE TABLE calls (
    id                      INTEGER PRIMARY KEY,
    analysis_profile_id     INTEGER NOT NULL,

    caller_symbol_id        INTEGER NOT NULL,
    callee_symbol_id        INTEGER,
    callee_definition_id    INTEGER,

    reference_kind          INTEGER NOT NULL,
    dispatch_kind           INTEGER NOT NULL,
    resolution_status       INTEGER NOT NULL,
    resolution_reason       INTEGER NOT NULL,
    async_usage_kind        INTEGER NOT NULL DEFAULT 0,

    document_id             INTEGER NOT NULL,
    source_start            INTEGER NOT NULL,
    source_length           INTEGER NOT NULL,

    unresolved_name         TEXT,
    receiver_type_key       TEXT,

    FOREIGN KEY(caller_symbol_id)
      REFERENCES symbols(id),

    FOREIGN KEY(callee_symbol_id)
      REFERENCES symbols(id),

    FOREIGN KEY(document_id)
      REFERENCES documents(id)
);
```

### symbol_relations

```sql
CREATE TABLE symbol_relations (
    analysis_profile_id INTEGER NOT NULL,
    source_symbol_id    INTEGER NOT NULL,
    target_symbol_id    INTEGER NOT NULL,
    relation_kind       INTEGER NOT NULL,

    PRIMARY KEY(
        analysis_profile_id,
        source_symbol_id,
        target_symbol_id,
        relation_kind
    )
);
```

### conditional_symbols_used

```sql
CREATE TABLE conditional_symbols_used (
    analysis_profile_id INTEGER NOT NULL,
    document_id         INTEGER NOT NULL,
    symbol_name         TEXT NOT NULL,
    occurrence_count    INTEGER NOT NULL,

    PRIMARY KEY(
        analysis_profile_id,
        document_id,
        symbol_name
    )
);
```

## 19.2 必須インデックス

```sql
CREATE INDEX ix_symbols_name
ON symbols(name);

CREATE INDEX ix_symbols_short_method
ON symbols(type_simple_name, name, parameter_count);

CREATE INDEX ix_symbols_namespace_type_method
ON symbols(namespace_name, type_simple_name, name, parameter_count);

CREATE INDEX ix_symbols_fully_qualified
ON symbols(fully_qualified_name);

CREATE INDEX ix_symbols_location
ON symbols(source_document_id, source_start);

CREATE INDEX ix_calls_callee
ON calls(callee_definition_id);

CREATE INDEX ix_calls_caller
ON calls(caller_symbol_id);

CREATE INDEX ix_calls_location
ON calls(document_id, source_start);

CREATE INDEX ix_relations_target
ON symbol_relations(target_symbol_id, relation_kind);
```

## 19.3 トランザクション

プロジェクトまたは更新単位ごとにトランザクションを使用する。

解析途中の不完全な更新で既存インデックスを破損しないこと。

推奨手順:

```text
1. 解析結果をメモリ上または一時テーブルへ作成
2. SQLiteトランザクション開始
3. 古い対象データを削除
4. 新しい対象データを挿入
5. 整合性確認
6. コミット
```

---

# 20. 定義位置検索

ソース位置から呼び出し先を検索できるようにする。

例:

```powershell
csindex definition --at "src\Player.cs:120:17"
```

DB上では指定位置を含む最小の呼び出し範囲を検索する。

参考SQL:

```sql
SELECT *
FROM calls
WHERE document_id = $documentId
  AND source_start <= $position
  AND source_start + source_length >= $position
ORDER BY source_length ASC
LIMIT 1;
```

その後、`callee_symbol_id` または `callee_definition_id` から定義位置を返す。

外部アセンブリでソースが存在しない場合は、少なくとも次を表示する。

* 完全修飾シンボル名
* アセンブリ名
* ソース定義なし
* 可能ならメタデータ参照元

Source Link対応は将来課題。

---

# 21. CLI仕様

実行ファイル名の作業名は `csindex` とする。

正式名称は後から変更可能。

## 21.1 インデックス作成

```powershell
csindex index Game.sln
```

```powershell
csindex index Game.csproj
```

```powershell
csindex index C:\UnityProjects\MyGame
```

主なオプション:

```text
--db <path>
--mode auto|solution|project|directory
--solution <path>
--configuration <name>
--framework <tfm>
--runtime <rid>
--profile-name <name>
--define <symbol>
--undefine <symbol>
--define-file <path>
--reference <dll>
--unity-editor <directory>
--exclude <glob>
--generated-source physical|all|none
--rebuild
```

## 21.2 シンボル検索

```powershell
csindex symbol find "Player::Play"
```

## 21.3 定義検索

```powershell
csindex definition "Player::Play()"
```

```powershell
csindex definition --at "src\Player.cs:120:17"
```

## 21.4 参照検索

```powershell
csindex references "Player::Play()"
```

## 21.5 呼び出し元

```powershell
csindex callers "Player::Play()" --dispatch static
```

## 21.6 呼び出し先

```powershell
csindex callees "GameNS.Player::Update()"
```

## 21.7 オーバーライド

```powershell
csindex overrides "BaseClass::Run()"
```

## 21.8 条件付きコンパイル情報

```powershell
csindex conditions
```

出力例:

```text
Conditional symbols found:

  UNITY_EDITOR             124 files
  UNITY_STANDALONE_WIN      38 files
  ENABLE_IL2CPP             11 files
  USE_ADDRESSABLES           7 files

Undefined in current profile:

  ENABLE_IL2CPP
  USE_ADDRESSABLES
```

## 21.9 出力形式

予約する。

```text
--output table
--output json
--output jsonl
--output yaml
--output dot
```

主DBはSQLiteのままにし、JSON、JSONL、YAML、DOTは出力形式として実装する。

初期段階では `table` と `json` を優先してよい。

schema version 2では既存の`table` / `json`出力へ非同期解析情報を追加する。新しいコマンドやフィルターは追加しない。

- symbol JSON: `asyncRole`（flags enumの文字列表現）、`isAsyncInvolved`（depthがnullでないか）、`asyncInvolvementDepth`（nullable整数）
- call JSON: `asyncUsageKind`（enumの文字列表現）
- symbol table: `symbol find`で`WriteSymbols`が出力する一致symbol行に限り、ロールが`None`かつdepthがnullの場合を除いて`[async: <AsyncRole>; depth: <number|null>]`を付ける。`definition`の定義位置行や`callers`のeffective caller行は対象外とする
- call table: 既存の呼び出し情報の後へ`[<AsyncUsageKind>]`を付ける

---

# 22. キャッシュと差分更新

## 22.1 解析プロファイルハッシュ

次の情報を含める。

```text
解析ツールバージョン
DBスキーマバージョン
Roslynバージョン
入力モード
Configuration
Target Framework
Runtime Identifier
OS
CPUアーキテクチャ
プリプロセッサシンボル集合
参照アセンブリ一覧とハッシュ
除外規則
生成コード設定
Unityバージョン
```

## 22.2 プロジェクトフィンガープリント

次を含める。

```text
.csproj
.sln関連情報
Directory.Build.props
Directory.Build.targets
Directory.Packages.props
global.json
ParseOptions
CompilationOptions
ProjectReference
MetadataReference
Source Generator設定
.asmdef
.asmref
ソースファイル集合
各ソースの内容ハッシュ
```

取得できない情報は明示的に記録する。

## 22.3 初期版の更新粒度

Phase 1ではプロジェクト単位の再解析を採用する。

```text
変更なし
  → SQLiteを再利用

プロジェクト内のファイル変更
  → そのプロジェクトを再解析

公開シグネチャ変更
  → 参照元プロジェクトも再解析する保守的設計
```

正確な影響範囲解析はPhase 4。

## 22.4 コメントだけの変更

将来的には次の2種類のハッシュを持つ。

```text
content_hash
semantic_hash
```

ただしコメント追加でも後続コードの位置が変わる。

Phase 1では `content_hash` が変われば再解析する。

不正確な最適化を早期導入しないこと。

---

# 23. 警告とエラー

解析できる範囲は継続する。

例:

* 一部プロジェクトをロードできない
* UnityEngine参照が不足
* 条件付きコンパイルの別ブランチが未解析
* カスタムUnityシンボル不明
* 一部呼び出しが未解決
* コンパイルエラーがある

これらは原則として警告。

次は致命的エラー候補。

* 入力が存在しない
* 入力モードを決定できない
* SQLiteを作成・更新できない
* 全解析対象をロードできない
* DBスキーマが破損している
* 明示された必須参照が読み込めない

警告は原則終了コード0。

CLI引数不正、致命的解析失敗、DB失敗は非ゼロ。

具体的な終了コード番号は未確定なので、名前付き定数として分離し、`docs/DECISIONS.md` に記録する。

---

# 24. 実装順序

以下のPhaseを削除しないこと。

後続Phaseを実装できる構造をPhase 1から維持すること。

---

## Phase 1: 基本的な実用版

実装対象:

* Windows / .NET 10 CLI
* SQLite
* `.sln`
* `.slnx`
* `.csproj`
* `MSBuildWorkspace`
* 通常メソッド
* コンストラクター
* ローカル関数
* ラムダ
* 正確なオーバーロード解決
* 拡張メソッド
* ジェネリックメソッド
* 定義検索
* 直接参照検索
* 直接呼び出し元検索
* 呼び出し先検索
* オーバーライド関係の記録
* 名前空間省略検索
* コメント無視
* 物理的な生成 `.cs` の解析
* 生成コード検索フィルター
* プロジェクト単位キャッシュ
* 単一解析プロファイル
* JSONまたはテーブル出力
* DBマイグレーション基盤
* 復旧用ドキュメント

Phase 1であっても、次をDBから削除しない。

* `analysis_profile_id`
* `generation_kind`
* `resolution_status`
* `dispatch_kind`
* `symbol_relations`

### Phase 1受け入れテスト

少なくとも以下を自動テストする。

1. `AClass.Play()` と `BClass.Play()` を区別できる
2. `Play()` から正しい定義へ移動できる
3. `Play()` を呼び出すメソッドを検索できる
4. 引数なしと `Play(string)` を区別できる
5. `Player::Play()` が複数namespaceへ展開される
6. コメント内の `Play()` を無視する
7. `.g.cs` を解析する
8. `--exclude-generated` で生成コードを除外する
9. ラムダを独立した呼び出し元として保存する
10. 拡張メソッドを元定義へ正規化できる
11. ジェネリック構築メソッドと元定義を区別できる
12. 2回目の検索でRoslyn再解析を必要としない
13. DB破損時に明確なエラーを出す

---

## Phase 2: DirectoryMode

実装対象:

* ディレクトリ入力
* `AdhocWorkspace`
* 指定ディレクトリ以下の全 `.cs`
* `obj` 常時除外
* `--exclude`
* 仮想プロジェクト
* `--reference`
* `--define`
* `--undefine`
* `--define-file`
* Windows x64解析プロファイル
* 不足参照の警告
* 条件付きディレクティブ一覧
* 未解決シンボル保存

### Phase 2受け入れテスト

1. `.sln` と `.csproj` がないディレクトリを解析できる
2. 5,000個以上の `.cs` を列挙できる
3. 任意階層の `obj` を除外する
4. `bin` は既定では除外しない
5. `--exclude` が機能する
6. `WINDOWS` が有効になる
7. `NET10_0` は自動定義されない
8. `--target-framework net10.0-windows` で対応シンボルを有効化できる
9. 不足参照があっても可能な範囲を保存する

---

## Phase 3: Unity対応

実装対象:

* Unityプロジェクト検出
* Unityバージョン取得
* `.asmdef`
* `.asmref`
* Unity既定アセンブリ
* `Assembly-CSharp`
* `Assembly-CSharp-Editor`
* UnityEngine参照
* UnityEditor参照
* `Library/ScriptAssemblies`
* `--unity-editor`
* Unity Hub探索
* Unity条件付きシンボル
* Editorプロファイル
* Windows Playerプロファイル
* asmdefの参照関係
* asmdefのプラットフォーム条件
* 取得不能なUnity設定の警告

### Phase 3受け入れテスト

1. `.sln` がないUnityプロジェクトを検出できる
2. asmdefごとに仮想Projectを分ける
3. asmdef参照を解決する
4. `Editor` フォルダーをEditorアセンブリへ分ける
5. UnityEngineの型を解決できる
6. `UNITY_EDITOR_WIN` を有効化する
7. Unityバージョンシンボルを生成する
8. 不足Unity参照を明示する
9. asmdef制約により除外されたソースを識別する
10. プロファイルを指定して検索できる

---

## Phase 4: 高度な解析と最適化

実装候補:

* 複数解析プロファイルの完全対応
* Source Generatorの非物理生成ソース
* GeneratorDriver実行
* ファイル単位の差分更新
* 宣言変更による影響範囲解析
* コメントのみの変更最適化
* `semantic_hash`
* 高度なデリゲート追跡
* 仮想呼び出し候補の精密化
* インターフェイス実装候補
* `dynamic`
* 演算子
* 暗黙変換
* 明示変換
* プロパティgetter/setter
* イベントadd/remove
* 関数ポインター
* リフレクションの限定解析
* Source Link
* 呼び出しツリー
* Graphviz DOT
* YAML / JSONL出力
* 常駐デーモン
* ファイル監視
* IDE連携
* 大規模DBの性能最適化

Phase 4の項目を、初期実装時に「不要」として削除しないこと。

---

# 25. テスト用サンプル

以下のようなテストコードを作成する。

## 25.1 名前空間省略

```csharp
namespace GameNS
{
    public class Player
    {
        public void Play() { }
    }
}

namespace PianoNS
{
    public class Player
    {
        public void Play() { }
    }
}
```

検索:

```text
Player::Play()
```

期待:

```text
GameNS.Player::Play()
PianoNS.Player::Play()
```

## 25.2 オーバーロード

```csharp
public class Player
{
    public void Play() { }
    public void Play(string name) { }
    public void Play(int id) { }

    public void Execute()
    {
        Play();
        Play("song");
        Play(10);
    }
}
```

各呼び出しが正しい定義へ結び付くこと。

## 25.3 継承

```csharp
public abstract class BaseClass
{
    public abstract void Run();
}

public sealed class XClass : BaseClass
{
    public override void Run() { }
}

public sealed class YClass : BaseClass
{
    public override void Run() { }
}

public class Caller
{
    public void Execute(BaseClass value)
    {
        value.Run();
    }
}
```

`--dispatch static` では `BaseClass.Run()`。

`--dispatch virtual` では `XClass.Run()` と `YClass.Run()` を候補に含める。

## 25.4 ラムダ

```csharp
public class Player
{
    public void Play() { }

    public void Execute()
    {
        Action action = () => Play();
    }
}
```

呼び出し元:

```text
Player::Execute()::<lambda#1>
```

外側所有者:

```text
Player::Execute()
```

## 25.5 コメント

```csharp
public class Player
{
    public void Play() { }

    public void Execute()
    {
        // Play();

        /*
        Play();
        */
    }
}
```

呼び出し件数は0。

## 25.6 拡張メソッド

```csharp
public static class PlayerExtensions
{
    public static void Play(this Player player) { }
}

public class Caller
{
    public void Execute(Player player)
    {
        player.Play();
    }
}
```

Reduced methodと元定義を記録する。

## 25.7 ジェネリック

```csharp
public class Converter
{
    public T Convert<T>(object value) => (T)value;

    public void Execute()
    {
        Convert<int>(1);
        Convert<string>("x");
    }
}
```

構築型引数と `OriginalDefinition` を区別する。

## 25.8 条件付きコンパイル

```csharp
public class PlatformPlayer
{
    public void Execute()
    {
#if WINDOWS
        PlayWindows();
#else
        PlayOther();
#endif
    }

    private void PlayWindows() { }
    private void PlayOther() { }
}
```

Windowsプロファイルでは `PlayWindows()` のみ呼び出しとして保存する。

---

# 26. 性能方針

対象は5,000ファイル以上。

次の点を守る。

* 同じDocumentのSyntaxTreeを不必要に何度も取得しない
* 同じSemanticModelを不必要に何度も取得しない
* メソッドごとにSolution全体検索を実行しない
* SQLite挿入を1行ごとの自動コミットにしない
* バルク挿入またはPrepared Statementを利用する
* 呼び出し辺はメソッド本体のOperationツリー走査でまとめて抽出する
* キャンセル可能にする
* メモリ使用量を計測できるログを用意する
* インデックス作成時間と検索時間を分けて表示する
* 初回解析と2回目検索のベンチマークを作成する

性能最適化のために正確性を損なわないこと。

---

# 27. ロギング

最低限、次を表示する。

```text
Input mode
Input path
Projects detected
Documents detected
Documents excluded
Generated documents
Analysis profile
Active preprocessor symbols
Metadata references
Unresolved references
Compilation diagnostics summary
Symbols indexed
Calls indexed
Relations indexed
Elapsed time
Database path
Cache reused / rebuilt
```

詳細ログオプションを用意する。

```text
--verbose
--diagnostics
```

ログと検索結果を混在させない設計を検討する。

推奨:

* 通常結果: stdout
* 警告・進捗: stderr

---

# 28. 実装中の禁止事項

* 後続Phaseの設計を削除しない
* DBスキーマから将来必要な識別列を削除しない
* 未解決呼び出しを黙って捨てない
* 複数候補を勝手に1件へ絞らない
* 名前空間省略をエラーにしない
* コメントを検索対象にしない
* `.g.cs` を既定除外しない
* `obj` を解析対象にしない
* CLIの.NET 10と解析対象のTarget Frameworkを混同しない
* Unityソースを.NET 10参照だけで解析しない
* Unityプロジェクトをすべて1アセンブリとして確定仕様にしない
* 条件付きコンパイルの非アクティブブランチを解析済みと表示しない
* Roslynの内部型名を永続DBの唯一の契約にしない
* スキーマ変更時に既存DBを無言で破棄しない
* Compact後に記憶だけで作業を続けない

---

# 29. 未確定事項

次は確定仕様ではない。

実装時に調査し、`docs/DECISIONS.md` に記録する。

1. SQLiteライブラリの正式採用
2. CLI引数解析ライブラリ
3. 正確な終了コード番号
4. `--caller-scope` の既定値
5. 複数 `.csproj` の表示順
6. SQLiteのWALモード採用
7. FTS5の利用有無
8. Symbol stable keyの最終フォーマット
9. Documentation Comment IDを取得できないシンボルの形式
10. 匿名型をどこまで保存するか
11. Unityの古い特殊フォルダー規則
12. Unity Hub探索方法
13. UnityのVersion Definesの完全復元方法
14. `.asmdef` のDefine Constraintsの厳密な評価
15. Source GeneratorをMSBuildWorkspace経由でどこまで取得できるか
16. 外部シンボルをsymbolsテーブルへ保存する範囲
17. 同一シンボルが複数Project、TFM、Profileに存在する場合の表示
18. ネスト型やジェネリック型を含む検索構文
19. `nameof` を既定references結果へ含めるか
20. プロパティ、イベント、演算子をPhase 1へ含めるか
21. コンパイルエラー数が多い場合の中断基準
22. Source Link対応
23. 解析DBを入力ディレクトリ内に作るか、ユーザーデータ領域に作るか
24. パスの正規化形式
25. シンボリックリンクとジャンクションの扱い
26. DirectoryModeで同一 `.cs` が複数参照された場合の扱い

未確定事項を推測で最終仕様にしないこと。

---

# 30. 作業の進め方

各作業単位で次を実行する。

```text
1. 仕様を確認
2. TASKS.mdの対象項目を確認
3. 実装
4. ビルド
5. テスト
6. 失敗を修正
7. docs/IMPLEMENTATION_STATUS.mdを更新
8. docs/DECISIONS.mdを更新
9. docs/KNOWN_LIMITATIONS.mdを更新
10. TASKS.mdを更新
```

大規模な変更前には既存テストを実行する。

既存の動作を壊した場合、後続機能を追加する前に修正する。

---

# 31. 最初に実行する作業

リポジトリが空の場合:

1. `CsIndex.sln` を作成
2. 推奨プロジェクト構成を作成
3. `docs/` と `TASKS.md` を作成
4. 本仕様を `docs/SPEC.md` へ保存
5. Phase 1のタスクを細分化
6. SQLiteライブラリとCLIライブラリ候補を調査
7. `docs/DECISIONS.md` に採用結果を記録
8. 最小の `csindex --help` を動作させる
9. DBマイグレーション基盤を実装
10. 小さなテストプロジェクトをMSBuildWorkspaceでロードする
11. メソッド定義をSQLiteへ保存する
12. `IInvocationOperation` から呼び出し辺を保存する
13. `callers` と `definition` の最小検索を実装する
14. テストを追加する

既存リポジトリの場合:

1. ファイル構成を調査
2. ビルド方法を確認
3. 既存仕様との衝突を確認
4. 既存コードを破棄せず、本仕様との差分をまとめる
5. `docs/IMPLEMENTATION_STATUS.md` に現状を書く
6. 既存テストを実行
7. 最小変更から開始する

---

# 32. 最終的に報告する内容

作業終了時には、少なくとも次を報告する。

* 実装したPhase
* 実装済みCLIコマンド
* ビルド結果
* テスト結果
* SQLiteスキーマバージョン
* 解析できるC#要素
* 未対応要素
* 既知の精度上の制限
* Unity対応状況
* 条件付きコンパイル対応状況
* キャッシュ再利用の条件
* 次に実装すべきタスク
* 変更された主要ファイル

「すべて完成した」と曖昧に報告せず、Phaseと機能単位で明示すること。

