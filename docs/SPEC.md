# C#ソースコード・セマンティック解析CLI 実装指示書

あなたは、このリポジトリに **C#ソースコードのセマンティック解析・検索CLI** を実装するコーディングエージェントです。

英語で考えてください。ソースコード内のコメントは日本語で書いてください。
本書は設計仕様、実装順序、制約、未実装機能、復旧方法をまとめた唯一の基準文書です。記載されていない仕様を推測して勝手に補完しないでください。

> **Current canonical revision:** Section 34 is the authoritative contract for
> structured symbol paths, typed query conditions, logical declarations,
> schema version 6, portable persisted paths, help, ordering, and atomic query
> output. It supersedes conflicting details in sections 14, 16, 18, 19, 21,
> and 33. All unrelated requirements in sections 1-33 remain active.

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
      SymbolSignatureCanonicalizer.cs
      SymbolPathFormatter.cs

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
    Symbols/
      SymbolPathParser.cs
      SymbolPathResolver.cs
      TypedConditionCompiler.cs
      StructuralGlobMatcher.cs

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

非同期関与はnullable整数`AsyncInvolvementDepth`で表す。正規化ソースを持つsource-backed method/lambdaのうち、`None`以外の直接ロールを1つ以上持つものだけを起点（depth 0）とする。全ドキュメント抽出後、callerとcalleeの両方が同じeligible集合に含まれる解決済み通常呼び出し（`ReferenceKind.Invocation`）から`callee_definition_key -> caller_symbol_key`の逆辺を作り、全起点を同時にキューへ入れる複数始点BFSを1回実行する。metadata-only awaitableは起点にも中継nodeにも含めない。呼び出し元はcalleeの距離+1とし、最初または厳密に短い候補だけを更新する。各非起点には選択されたcalleeの`AsyncNextSymbolKey`も保存し、同距離の後続候補で上書きしない。この停止条件により自己再帰・相互再帰・複数循環でも有限に停止し、複数経路がある場合は最短距離と1つの決定的next hopだけを保持する。

伝播方向は「非同期関数へ到達する呼び出し元方向」のみである。非同期起点から呼ばれる同期関数へは伝播せず、非同期起点へ到達しない循環のdepthはnullのままとする。

CLIの`--async-status all|async|sync`はこの派生depthではなく直接`AsyncRole`にだけ適用する。`async`は`AsyncRole != None`、`sync`は`AsyncRole == None`のmethod/lambdaを対象にし、型などの非関数symbolを`sync`へ含めない。既存の`symbol list --async-involved`は`AsyncInvolvementDepth != null`による派生条件のまま維持し、両optionの併用はAND条件とする。

Coreモデルとschema version 6のSQLite列は次の対応とする。

| Coreモデル | SQLite列 |
|---|---|
| `SymbolData.AsyncRole` | `symbols.async_role INTEGER NOT NULL DEFAULT 0` |
| `SymbolData.AsyncInvolvementDepth` | `symbols.async_involvement_depth INTEGER` |
| `SymbolData.AsyncNextSymbolKey` | `symbols.async_next_symbol_id INTEGER` |
| `CallData.AsyncUsageKind` | `calls.async_usage_kind INTEGER NOT NULL DEFAULT 0` |

---

# 14. シンボル同一性

> **Superseded where conflicting:** the schema-5 logical/declaration identity
> and canonical semantic-path rules are specified in section 34. Sections
> 14.1-14.3 remain historical context for the earlier display-derived model.

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

> **Superseded where conflicting:** section 34 defines current executable-child
> containment, synthetic markers, anonymous ordinals, and logical identities.
> This section is retained for the unaffected extraction intent and history.

## 16.1 ラムダ

表示名例:

```text
Game.Player::Update().<lambda#1>
Game.Player::Update().<lambda#2>
```

ラムダをtargetとして扱うquery commandは、少なくとも次の表記を共通に解決する。

```text
Game::Player::Owner().<lambda#1>
Game::Player::Owner().<anonymous-method#2>
Game::Player::Owner().<lambda#*>
Game.Player::Owner().<anonymous-method#*>
```

複数targetを扱えるcommandは決定的順序ですべて処理する。一意rootが必要な`async tree`と`callers tree`は、filter適用後に0件または複数件なら候補を含む明示的なquery errorにする。候補は同一canonical display nameであればdocument pathとsymbol IDを併記する。

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

### 16.1.1 Immediate-owner表示用採番

lambdaとanonymous methodは、直近の字句上の実行可能owner内で1から始まる同じソース順sequenceを共有する。nested anonymous functionは直近のanonymous ownerに所属し、そのownerで新しいsequenceを開始する。

```text
Game.Player::Update().<lambda#1>
Game.Player::Update().<lambda#2>
Game.Player::Update().<lambda#3>
```

保存する`containing_symbol_id`は直近の字句上のownerを示す。ネストしたlambda内の呼び出しはそのlambda自身からの呼び出しとする。所有関係はcall edgeではない。

### 16.1.2 メンバー初期化子owner

フィールド、プロパティ、イベントの各初期化子には、互いに異なる合成ownerを割り当てる。

```text
Namespace.Type::<initializer:fieldName>
Namespace.Type::<initializer:PropertyName>
Namespace.Type::<initializer:EventName>
```

合成ownerは、包含型、文書、表示名、ソース範囲、文書内容ハッシュを含むsource-backed stable keyを持つ。ラムダはその初期化子内のソース順に`<lambda#1>`から採番する。partial型の別ファイルにある同名でないメンバーも、文書とowner IDにより区別する。

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
Game::Player::Execute().<lambda#1>
```

  外側の名前付きメソッド:

```text
Execute
```

既存の`callers`コマンドは表示範囲として次のオプションを持つ。

```text
--caller-scope direct
--caller-scope containing
--caller-scope both
```

既定は`direct`である。ただし、これは既存の`callers`コマンドの表示上の規則であり、call edgeの所有者は常に直接のラムダである。`callers tree`は所有関係から外側メソッドへの辺を合成せず、delegateの`Invoke()`、イベント、コールバックの実行位置も推論しない。

## 16.4 初期化子

フィールド、プロパティ、イベントの初期値に含まれる実行コードには、メンバー単位の仮想所有者を与える。

```text
XClass::<initializer:fieldName>
XClass::<initializer:PropertyName>
XClass::<initializer:EventName>
```

初期化子内に複数のanonymous nodeがある場合は、その合成owner内でソース順に採番する。表示名だけを所有関係のキーにせず、stable keyとsymbol IDで関連付ける。top-level codeは`global::Program::<top-level-statements>` synthetic ownerとして同じcontainment規則を使用する。

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

> **Superseded where conflicting:** section 34 defines current csharp suffix
> matching, explicit exact boundaries, signature omission, typed matching, and
> override expansion. Earlier flat-name matcher details below are historical.

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

## 18.7 Override-aware method search

`--include-overrides` is an opt-in mode for method queries. It is accepted
only by the following five commands:

```text
csindex symbol find <method-query> --include-overrides
csindex definition <method-query> --include-overrides
csindex references <method-query> --include-overrides
csindex callers <method-query> --include-overrides
csindex callees <method-query> --include-overrides
```

Without the option, method resolution remains the existing exact lookup.
The option requires a method query; a type-only query, or the
`definition --at` form, fails with the invalid-argument message
`--include-overrides requires a method query.` The option is not accepted by
`symbol list`, `overrides`, `conditions`, or `index`.

Expansion is descendant-only and returns real stored method declarations.
An interface root is expanded in the scope of that exact interface contract:
`IPlayable::Play()` includes the interface member and the indexed real
implementations such as `Pianist::Play()`, `ProPianist::Play()`, and
`Game::Play()`. A query rooted in a derived interface does not include a type
that implements only the base interface.

A concrete root expands only through transitive overrides in that receiver
type's descendant branch. `Pianist::Play()` includes
`Pianist::Play()` and `ProPianist::Play()`, but not `Game::Play()` or another
sibling implementation of `IPlayable`. A concrete-rooted search does not
expand upward to base or interface contracts, does not cross into sibling
branches, and does not infer targets from receiver-value or runtime-flow
analysis. In particular, concrete `references` and `callers` results exclude
call sites whose static callee is `IPlayable::Play()`.

When a receiver inherits a method without declaring it, the resolver returns
the real inherited declaration and keeps the receiver branch as the expansion
scope. For example, `D1::Play()` may resolve to `InheritedBase::Play()` and
include `D2::Play()` below `D1`, but it does not emit a synthetic
`D1::Play()` symbol or include an override from another branch. A declared
`new` member resolves to that real declaration and is not treated as an
override.

---

# 19. SQLite設計

Current storage uses schema version 6 and analysis-cache version 4. The exact
DDL, including every column, index, foreign key, unique constraint, check
constraint, default, and delete action, is normative in `docs/DB_SCHEMA.md`.

The schema contains:

```text
schema_info
analysis_profiles
index_runs
projects
normalized_sources
documents
symbols
method_parameters
symbol_declarations
calls
call_candidates
symbol_relations
interface_method_bindings
conditional_symbols_used
```

`symbols` is a logical semantic table with structured path identity/display
components. Physical source location, generated state, and the typed
`ordinary | partial-definition | partial-implementation` role are stored in
`symbol_declarations`. The full normalized document text/hash is stored once in
`normalized_sources`; each `documents.normalized_source_id` references that
payload, and declarations and calls retain normalized UTF-16 start/length
ranges into it. The preferred declaration is the partial implementation when
available. Calls, relations, interface bindings, containment, and async links
reference logical symbol IDs.

`index_runs.input_root` is `.`, and `index_root_anchor` is relative from the
database directory to the one storage root. Project/document/source-derived
key paths are canonical forward-slash paths relative to that root. An ordinary
persisted project, document, or linked source must share the storage root's
Windows drive or UNC server/share. A physical C# document on another
volume/share is the sole exception only when the existing `GeneratedCodeDetector`
positively identifies it as generated: it remains in the Roslyn compilation but
contributes no persisted path, document, declaration, body fact, or query root.
It contributes one warning and one excluded-document count. A non-generated
cross-volume/share document is still rejected before SQLite mutation, and
same-volume generated source remains a normal indexed document.

A save validates logical/declaration/path invariants, inserts all rows and
deferred IDs in one transaction, runs `PRAGMA foreign_key_check`, and commits
only on success. Failure or cancellation rolls back. Schema version 4 or
older, a malformed marker, or a non-empty unrecognized database is rejected
without migration, deletion, truncation, implicit rebuild, or WAL change.

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

> **Superseded where conflicting:** current symbol-path grammar, typed option
> families, command scope, path/presentation options, and cardinality behavior
> are in section 34 and `docs/CLI.md`. Unrelated indexing intent remains active.

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

## 21.9 共通の実行可能target条件

method/lambdaを候補またはrootとして解決するquery commandは、次のoptionを共通に受理する。

```text
--kind all|method|lambda
--async-status all|async|sync
```

どちらも既定は`all`である。`--kind all`はkind predicateを追加しないため、exact `symbol find`では従来返していた型などを排除しない。`--async-status all`もdirect async predicateを追加しない。`async`と`sync`は保存済み`AsyncRole`を使い、method/lambdaだけを候補にする。

| command | filterを適用する対象 |
|---|---|
| `symbol find`、`symbol list` | 一致/一覧symbol |
| `source show`、`source search` | source-backed実行可能symbol |
| `definition`、`definition --at` | 解決targetとdefinition結果 |
| `references`、`callers` | 検索対象のcallee target |
| `callees` | 検索対象のcaller root |
| `async tree`、`callers tree` | 一意に解決するsource-backed executable root |
| `overrides` | method root。`--kind lambda`は非適用エラー |

filterはtarget/rootの解決にだけ使う。二次的に表示するcaller/callee、graph途中node、edgeを一律にfilterして既存の到達性を変えてはならない。`index`と`conditions`はfunction targetを持たないため、両optionを未知optionとして拒否する。

未知値は次のusage errorとする。

```text
Unknown symbol kind: <value>. Use all, method, or lambda.
Unknown async status: <value>. Use all, async, or sync.
```

## 21.10 出力形式、source layout、結果ファイル

通常のシンボル・ソース・query・`conditions`出力の`--output-format`は`table|json`、`async tree`は`tree|line|json`、`callers tree`は`tree|mermaid|json`である。既定は前者が`table`、tree commandが`tree`である。JSONL、YAML、Graphviz DOTは未実装であり、受理しない。

`-o <path>`と`--output-file <path>`は同義で、結果payloadを持つcommandの出力先を指定する。`index`は検索結果payloadを持たないため受理しない。短縮形として特別扱いするのは完全一致する`-o`だけであり、file extensionからformatを推測しない。output-fileを指定した場合は、stdoutへ出るはずだったformatter payloadをBOMなしUTF-8でfileへ書き、成功時のstdoutは空にする。diagnostic、warning、progress、errorはstderrのままとする。

global helpと各commandの`--help`はstdoutへ表示し、`--output-file`を併記しても結果fileを開いたりredirectしたりしない。

file outputは出力先と同じdirectoryのtemporary fileをlazyに開き、payloadの成功、flush、cancellation checkの後だけ既存fileを置換またはcommitする。argument/query/database/output error、cancel、write failure時は既存fileをtruncateせず、temporary fileをcleanupする。relative pathはprocess current directoryから絶対化し、parent directoryを自動作成しない。正規化比較で出力先が使用中SQLite DB pathと同一ならusage error、parent不足またはI/O failureは`Output error:`で始まるanalysis failureにする。空値、値なし、重複指定もusage errorである。

旧名`--output`は後方互換aliasではない。指定時は`Unknown option(s): --output`のusage error（exit code 2）にする。

正規化ソースをtableで表示するcommandは次を受理する。

```text
--source-layout single-line|multi-line
```

既定は`single-line`である。`symbol find`では`--show-source`と併用するときだけ、`source show`と`source search`では常に有効である。JSONとの併用、およびソースを表示しないcommandでの指定はusage errorとする。single-lineのrecord schemaは次のとおりで、同じ実行中にfield数を変えてはならない。

未知のlayout値は次のusage errorとする。

```text
Unknown source layout: <value>. Use single-line or multi-line.
```

| command | 1 record |
|---|---|
| `symbol find` | `<signature><TAB><path>:<line>:<column>`（metadata-onlyは空location） |
| `symbol find --show-source` | `<signature><TAB><path>:<line>:<column><TAB><normalized-source>`（metadata-onlyは空location/source） |
| `source show`、`source search` | `<signature><TAB><path>:<line>:<column><TAB><normalized-source>` |
| `symbol list` | `<signature>` |

single-lineのstdoutはrecordだけとし、件数summaryはstderrへ出す。`multi-line`はheading、symbol行、`    source: <normalized-source>`行を維持する。single-lineの各fieldとmulti-lineのsignature/sourceは、TAB、CRLF（1個のspace）、CR、LF、U+0085、U+2028、U+2029をASCII spaceへ表示時だけ置換し、各record/source行を1物理行にする。この表示変換はDBの`normalized_source`とhash、source search、JSONの`normalizedSource`を変更してはならない。

Schema version 6 retains the async-analysis fields, persisted async next-hop,
executable metadata, shared normalized document payloads, ranges, and
graph-query support. Source payload is stored once per normalized document as
specified in section 34 and `DB_SCHEMA.md`.

- symbol JSON: `asyncRole`（flags enumの文字列表現）、`isAsyncInvolved`（depthがnullでないか）、`asyncInvolvementDepth`（nullable整数）
- call JSON: `asyncUsageKind`（enumの文字列表現）
- symbol table: `symbol find`で`WriteSymbols`が出力する一致symbol行に限り、ロールが`None`かつdepthがnullの場合を除いて`[async: <AsyncRole>; depth: <number|null>]`を付ける。`definition`の定義位置行や`callers`のeffective caller行は対象外とする
- call table: 既存の呼び出し情報の後へ`[<AsyncUsageKind>]`を付ける

---

## 21.11 Override-aware method-query option

The five method-query commands listed in section 18.7 accept
`--include-overrides`; its default is off. Exact method queryでは`--kind all`または
`--kind method`を併用できる。`symbol find`, `definition`, and the call-oriented
commands report only real stored declarations in their matched or expanded
target sets. `references` and `callers` then search calls to those real callee
IDs, while `callees` searches calls made by each expanded real method.

The expansion is query-time, profile-scoped, deterministic, and cycle-safe.
It travels only to descendant implementations: interface searches use the
exact interface contract's bindings, while class and abstract-class searches
follow only descendant override branches. Expand real methods first, then
apply the kind/direct-async target filter. `--kind lambda` cannot be combined
with `--include-overrides`, and `overrides --kind lambda` is an explicit
non-applicable argument error. This option does not alter the independent
`--dispatch` presentation mode and never performs upward, sibling-branch, or
runtime-flow expansion.

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

終了コードは`Success=0`、`InvalidArguments=2`、`AnalysisFailure=3`、`DatabaseFailure=4`、`RequireSingleFailure=5`とし、名前付き定数へ集約する。詳細は`docs/CLI.md`とDEC-0003を参照する。

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

シンボル・ソース検索、実行可能シンボル属性、非同期最短経路、caller graph、演算子・変換・アクセサーのソース抽出は実装済みであり、正式仕様は第33章に定める。

残る実装候補:

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
* プロパティ・イベントアクセスを含む呼び出し関係の精密化
* 関数ポインター
* リフレクションの限定解析
* Source Link
* caller graph以外のgeneral call-tree view
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
Player::Execute().<lambda#1>
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

次は現在も確定していない。実装時に調査し、`docs/DECISIONS.md`へ記録する。これ以外の過去の未確定事項はDEC-0001からDEC-0026で決定済みであり、この一覧へ戻さない。

1. 匿名型をどこまで保存するか
2. Unityの古い特殊フォルダー規則
3. Unity Hub探索方法
4. UnityのVersion Definesの完全復元方法
5. `.asmdef` のDefine Constraintsの厳密な評価
6. Source GeneratorをMSBuildWorkspace経由でどこまで取得できるか
7. 外部シンボルを`symbols`テーブルへ保存する範囲
8. 同一論理シンボルを複数TFM/Profileにまたがって表示・集約する規則
9. ネスト型やジェネリック型を含む検索構文
10. コンパイルエラー数が多い場合の中断基準
11. Source Link対応
12. シンボリックリンクとジャンクションの扱い
13. DirectoryModeで同一`.cs`が複数参照された場合の扱い

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

---

# 33. シンボル・ソース・グラフ拡張の正式仕様

> Section 34 is authoritative for shared path, matcher, schema, and command
> rules. This section retains the compatible source/async/graph detail.

本章は、lambda検索、実行可能symbol属性、正規化source、非同期最短経路、caller graphの詳細仕様である。共通契約は第34章を優先する。

## 33.1 共通要件

* schema version 6以外（schema 5以下を含む）は既存DBを変更せずに拒否する。migration/auto-delete/implicit rebuildは行わず、delete/renameまたは新しい`--db`の後に明示的な`csindex index`を要求する。
* 構造解析にはRoslynの構文木、`SemanticModel`、シンボルを使用する。呼び出し、所有、最短経路、継承などの関係は表示名ではなくstable keyとDB上のsymbol IDで保持する。
* 再帰構造はvisited IDで循環を防止する。深いグラフは再帰呼び出しではなく反復処理で探索する。
* 解析、正規化、並べ替え、検索、グラフ構築、出力は処理途中でも`CancellationToken`を確認する。
* 結果順、同距離の経路選択、SQLの最終tie-breakerを決定的にする。同じ入力とprofileの再indexは同じ表示番号と同じ非同期next hopを選ぶ。
* すべての検索とグラフは選択したanalysis profile内に閉じる。同じprofileに同一assembly/TFM/完全修飾名を持つ別projectがあっても、source definitionはproject keyとsymbol IDで独立させる。
* JSONは表示済み文字列だけに依存せず、kind、accessibility、static、async、return type、source、node、edge、truncationなどを独立したfieldで表現する。
* 上限による打ち切りは全出力形式で明示し、黙って省略しない。

## 33.2 ラムダの検索、採番、所有関係

`symbol find`、`source show`、`definition`、`references`、`callers`、`callees`、`async tree`、`callers tree`は、該当するtarget/root位置で次のいずれでもラムダを検索できる。

```text
Game::Player::Function().<lambda#1>
Game::Player::Function().<anonymous-method#2>
Game::Player::Function().<lambda#*>
Game.Player::Function().<anonymous-method#*>
```

検索はラムダsuffix、owner付きsuffix、正式な完全表示名に対応し、`--kind lambda`と組み合わせられる。同じ番号を持つ別ownerのラムダが複数一致した場合は、すべてを決定的な順序で列挙する。

`async tree`と`callers tree`は単一のsource-backed executable rootだけを受理する。filter適用後に複数のラムダtargetが残る場合は、既存のgraph ambiguity契約に従って決定的順の候補を列挙して失敗する。ラムダownerの包含関係はcall edgeではなく、delegate `Invoke`、event、callback、reflection、runtime flowからlambdaのcall/reference edgeを推測しない。

lambdaとanonymous methodは直近の字句上の実行可能ownerごとに共有source-order sequenceを1から開始する。nested anonymous nodeは直近のanonymous ownerで新しいsequenceを開始する。追加または削除により同じimmediate owner内の後続ordinalが変わり得る。

フィールド、プロパティ、イベントの初期化子は次の合成ownerを持つ。

```text
Namespace.Type::<initializer:memberName>
```

初期化子ごとに独立して採番し、partial型の別文書にある初期化子も文書、ソース範囲、stable key、symbol IDで区別する。`containing_symbol_id`は直近の字句上のownerを指すため、ネストしたラムダ内のcall edgeはそのラムダからの辺になる。所有関係そのものをcall edgeへ変換してはならない。

## 33.3 実行可能シンボルの属性と表示

source-definedのメソッド、コンストラクター、ローカル関数、ラムダ、アクセサー、演算子、変換演算子について、少なくとも次を保存する。

* `public`、`protected`、`internal`、`private`、複合accessibility、または非適用
* `static`かどうか
* canonical return type key。戻り値が存在しない宣言ではnull
* method kindとowner symbol ID
* 既存のdirect async roleとderived async involvement
* source-backedかどうか、原文書とソース範囲、正規化ソースとそのhash

テキスト署名はC#宣言に近い次の順で組み立て、適用できない要素は表示しない。

```text
accessibility static async return-type display-name(parameters)
```

コンストラクターには戻り値を表示しない。ローカル関数、ラムダ、static constructorなど、C#宣言上accessibilityを持たないものにはaccessibilityを表示しない。`--short-names`は`displayName`と`signature`の所有者名前空間だけを省略し、戻り値型、引数型、conversion target、explicit-interface payload、canonical field、検索意味は変更しない。JSONでは`displayName`、`kind`、`accessibility`、`isStatic`、`isAsync`、`returnType`などを独立して出力する。

## 33.4 シンボル検索と正規化ソース

### 33.4.1 名前検索

`symbol find`は省略可能なstructured selectorと、namespace/type/method/file/sourceのtyped condition、kind、direct async-statusを受け付ける。selector省略時は少なくとも1つの明示selection conditionを必要とする。

concise conditionはstructural glob、`-literal`はliteral、`-regex`はbounded regexである。例:

```text
*.Gamer::Play
Tokyo.*::Play
Tokyo.Gamer::P*l*y
```

caseはnamespace/type/method/file/sourceごとに`strict|ignore`を独立指定する。同categoryの反復条件はCLI順のOR、category間はAND、includeはAND、excludeはORである。bare legacy matcher switchは拒否する。structured selector、overload omission、`--include-overrides`契約は第34章と`docs/CLI.md`に従う。

結果はcanonical display name、source path、source start、numeric symbol IDの順で安定化する。

### 33.4.2 ソースの正規化と保存

正規化対象はsource-backedのメソッド、コンストラクター、ローカル関数、ラムダ、アクセサー、演算子、変換演算子である。Roslynのactive syntax tokenから構築し、行コメント、ブロックコメント、XML document trivia、directive、inactive conditional branch、literal外のindentと改行を除く。文字列、文字、補間文字列、raw stringを含むliteral tokenの`Text`は変更しないため、複数行raw literalの内部改行は残り得る。

隣接tokenを連結すると別のtoken列へ変化する場合だけ、1個の空白を入れる。したがって次のように正規化し、識別子を連結してはならない。

```csharp
public static int Func(){var a=1;PrintVar(a);return a;}
```

`publicstaticintFunc(){vara=1;...}`のような文字列は生成しない。補間式の内部でもtoken境界を維持する。DBにはprofile、symbol ID、正規化文字列、SHA-256 hash、元ファイルとソース範囲を保持する。metadata-only symbolには正規化ソースを持たせない。

source幅0のmissing/omitted tokenは正規化文字列へ追加せず、その前後のseparator判定にも使わない。したがってarray rankの不要な空白は保持せず、`string[]args`、`int[,]matrix`、`int[][]values`、`string?[]items`のように正規化する。literal tokenまたは実source文字を持つtokenは削除しない。正規化文字列を更新した場合は、その値からSHA-256 hashを再計算する。

### 33.4.3 ソース表示と検索

`symbol find`は反復可能な`--include`と`--exclude`を名前条件と組み合わせられ、これらを1つも指定しなくてもよい。名前・属性で候補を絞った後、source-less候補を除き、excludeをORで先に短絡評価し、それを通過した候補にincludeをANDで短絡評価する。includeがなければexcludeを通過した候補を採用する。`--show-source`は表示だけを変更し、候補集合を変更しない。

`source show <symbol>`は一意のsource-backed logical executableのpreferred declaration sourceを表示する。`source search`は位置引数を受け付けず、typed condition、kind、direct async-statusの少なくとも1つを必須とする。両コマンドはtableとJSONを提供し、正式名、適用可能な属性、declaration role、ファイル、位置、正規化ソースを返す。実装上、DB optimizerが述語順を変更しても意味を変えてはならず、アプリケーション層ではexcludeによる早期除外を維持する。

tableの`single-line` layoutでは、`symbol find`は2field（`signature<TAB>location`）、`symbol find --show-source`と`source show`/`source search`は3field（`signature<TAB>location<TAB>normalized-source`）、`symbol list`はsignatureだけの1fieldを出力する。metadata-only `symbol find`候補も空fieldで同じfield数を保つ。summary、warning、progress、errorはrecord-only stdoutへ混在させず、summaryはstderrへ出す。`multi-line` layoutでは既存のheadingと`source:`行を維持する。

いずれのtable layoutでも、表示直前にTAB、CRLF（1個のspace）、CR、LF、U+0085、U+2028、U+2029をASCII spaceへ置換し、signatureとsource行を1物理行にする。これは表示層だけの変換であり、DB、hash、source search、JSONの保存値はlosslessに維持する。

## 33.5 非同期関数までの最短経路

`csindex async tree <symbol>`は、一意に解決されたsource-backed logical executable root（initializer/top-levelを含む）から呼び出し先方向へ進み、到達可能な非同期起点までの最短経路を1つ表示する。既定のtree出力に加えて`line`と`json`を提供する。lineは厳密に` -> `で接続する。root自身が非同期起点なら1nodeで終了し、到達不能なら成功結果として明示する。

非同期起点は、宣言`async`、`Task`/`Task<T>`、`ValueTask`/`ValueTask<T>`、`UniTask`/`UniTask<T>`、`UniTaskVoid`、非同期stream、または登録済みawaitable型など、Roslynで得たdirect async roleに基づく。名前が`Async`で終わるだけでは起点にしない。非同期型の追加登録を可能にする拡張点は保持するが、未登録型を名前だけで推測しない。非同期ラムダも起点に含める。

index時にeligible source-backed logical executable間の解決済みinvocation逆辺を決定的に並べた複数始点BFSを実行し、`async_involvement_depth`と1つの`async_next_symbol_id`を保存する。起点はdepth 0かつnext nullである。未訪問またはより短い距離を見つけたときだけ更新し、同距離では最初に記録したnextを置換しない。metadata-only awaitableは起点にもpath nodeにも含めない。

query時は再探索せず保存済みnext chainだけを反復的にたどる。各nodeは同一profileのeligible source-backed logical executableで正規化ソースを持ち、depthが1ずつ減少しなければならない。欠落ID、profile越境、cycle、非実行可能・source-less node、origin/non-origin状態の不整合はdatabase integrity errorとする。node上限を判定する前に取得済みnodeを検証する。

`--max-nodes`の既定は500でrootを含み、正の値だけを許可する。打ち切った場合はtree/line/JSONのすべてでtruncationを明示する。

## 33.6 呼び出し元グラフ

`csindex callers tree <symbol>`は、一意に解決されたsource-backed logical executable root（initializer/top-levelを含む）をrootとして、呼び出し元方向へprofile-scoped BFSを行う。rootのdepthは0、既定depthは3、`--depth 0`は深度無制限である。`--max-nodes`の既定は500でrootを含み、上限時は明示的にtruncateする。

対象edgeは解決済みinvocationとobject creationで、nodeはsource-backed logical executableに限定する。metadata-only・外部libraryの定義と`System`/`System.*` namespaceを除外する。source有無は文書・assembly情報で判定し、名前空間文字列だけで外部と決めない。同一depthの候補はcanonical semantic keyとstored relative locationで全体sortしてからnode上限を適用する。

nodeとedgeはIDで一意化し、cycleでも停止する。有限のdepth境界でもreverse edgeを読み、両端がすでに含まれるcycle/cross edgeは保持するが、より深いnodeは追加・enqueueしない。ラムダ内のcallはラムダ自身からのedgeとし、ownerへのedgeを合成しない。delegate `Invoke()`、event、callback、reflection、receiver data flow、runtime dispatchの実行位置は推論しない。

既定のtree出力はspanning treeを表示し、必要なら`Additional edges:`でcycle/cross edgeを示す。Mermaidは`flowchart TD`、`n<symbol-id>`の安全なnode ID、escape済み表示名label、callerからcalleeへの矢印を使用する。JSONはprofile、root、node depth、unique edge、truncationを独立fieldで返す。tree、Mermaid、JSONは同じnode/edge集合を表現する。

## 33.7 スキーマと更新の原子性

schema version 6はlogical path/metadata/async depth-nextを`symbols`へ、physical source location/roleとnormalized UTF-16 rangesを`symbol_declarations`へ、callsのnormalized UTF-16 rangesを`calls`へ、normalized document payloadを`normalized_sources`へ保存する。analysis-cache versionは4である。正確なDDLは第34章と`docs/DB_SCHEMA.md`を正式定義とする。

更新は1つのSQLite transactionで行い、全symbol row、自己参照、parameter/call/relation/interface binding/conditional symbolを保存して`PRAGMA foreign_key_check`に成功した場合だけcommitする。例外またはcancel時はrollbackして直前のindexを保持する。旧schema、未知schema、`schema_info`のない非空DBはWALやDDLを変更する前に拒否する。

## 33.8 エラー契約

次を区別して明示的なエラーにする。

* profileが存在しない
* schemaが古い、未知、または破損している
* symbolが0件、または一意性が必要なcommandで複数件
* 正規表現が不正、またはtimeoutした
* depthが負、max node数が0以下、数値形式が不正
* `source search`にinclude/excludeがない
* wildcard/regex、legacy override search/extended searchなど、同時指定できないoptionの組み合わせ
* 保存済み非同期pathの整合性違反

一意性が必要なgraph rootが曖昧な場合はcanonical候補を安定順で列挙し、同じ表示名の候補にはdocument pathとsymbol IDを付ける。詳細なCLI文言と終了コードは`docs/CLI.md`を参照する。

## 33.9 対象外と変更してはならない境界

この拡張では、旧schemaのmigration、delegate `Invoke()`やevent/callbackの実行位置推論、ラムダ所有関係からのcall edge合成、外部libraryのdecompile/Source Link取得、完全なdynamic/virtual dispatch、reflectionによる呼び出し推論を行わない。

実装は、循環安全、profile isolation、project-scoped source identity、決定的順序、途中cancel、transaction rollback、正規表現timeout、literal内のcomment marker保存、JSONの構造化field、全出力でのtruncation明示を維持しなければならない。

## 33.10 受け入れ条件

正式な受け入れ条件は`docs/TEST_PLAN.md`の「Symbol, source, and graph expansion acceptance matrix」に定める。少なくとも次の分類をすべて満たすこと。

1. suffix、owner suffix、完全名、nested、同番号複数ownerのラムダ検索
2. 関数・初期化子単位の採番、挿入時の局所的renumber、partial文書の区別
3. 全実行可能宣言kindの属性保存、適用可能な署名表示、canonical JSON、short-name表示
4. self async、最短経路、同距離の1経路、再index決定性、Task/ValueTask/UniTask、cycle、整合性検証、truncation
5. caller BFS、depth 0、node上限、cycle/cross edge、外部除外、ラムダ所有、Mermaid
6. exact、wildcard、component、regex、timeout、overload、別project同名symbol
7. comment除去、literal保存、token境界、source show/search、名前+source条件、exclude-first OR、include AND、case mode

各条件は汎用buildの成功だけで代用せず、条件を直接検証する自動テストへ対応付ける。

## 33.11 実装依存順

この機能群を変更するときは、原則として次の依存順を守る。

1. DB schemaとdomain modelの拡張
2. 関数属性、ラムダowner、正規化ソースの抽出
3. ラムダ命名規則
4. exact、wildcard、component、regex名前検索
5. ソース表示・検索query
6. 非同期最短距離とnext hopの生成・保存
7. `async tree`とtree/line/JSON出力
8. `callers tree`とtree/Mermaid/JSON出力
9. `symbol find`へのソース条件と`--show-source`の統合
10. CLI help、正式仕様、schema文書、決定記録の同期
11. 統合、cycle、旧schema拒否、cancel、決定的順序の回帰テスト

各段階で既存機能を含む関連テストを実行し、失敗を解消してから次へ進む。

---

# 34. Canonical structured-path and portable-index contract (current)

This section is normative for the current build. Schema version 6 and
analysis-cache version 4 are required.

## 34.1 Structured symbol-path grammar

A symbol path has three semantic fields:

```text
namespace path | type path | executable path
```

The two textual styles below map to the same structured constraints:

```text
csharp:   Game.Core.Player.Inventory::Load(int).Validate()
explicit: Game.Core::Player.Inventory::Load(int).Validate()
```

Balanced lexical scanning finds only top-level `::` separators. Exactly one
selects csharp style; exactly two select explicit style. Nested constructs such
as `global::System.String`, generics, tuples, arrays, pointers, nullable types,
and function pointers do not split a path.

Csharp style treats its dotted prefix as a suffix query over a possible
namespace prefix plus the complete type hierarchy. A csharp-form result copied
back into a query is valid but can broaden to another namespace/type boundary.
Explicit style fixes the namespace/type boundary and matches both hierarchies
exactly. The two-field form omits namespace and searches all namespaces.
`global` as the explicit namespace is exactly the empty/global namespace;
`@global` is an identifier literally named `global`.

Hierarchy components use `.`. In glob form, a whole `*` component matches one
level and a whole `**` component matches zero or more; embedded `*` stays
within its component. Escaped C# identifiers normalize to value text.
Nested type components remain distinct. Type generic arity is exact:
`Repository` is non-generic, `Repository<T>` is arity 1, and
`Repository<T,U>` is arity 2. Use an intentional type glob such as
`Repository*` to search multiple arities.

Executable children use `.` and require immediate containment:

```text
Game::Player::Run().Local().<lambda#1>
```

The following are explicit rejection examples, not accepted spellings:

```text
Game::Player::Run()::<lambda#1>    invalid: old child :: separator
Game::Player::Run()::Local()       invalid: three top-level fields
```

Empty fields/segments, zero or more than two top-level separators, unbalanced
or misordered delimiters, and a child not immediately contained by its
predecessor are query errors.

## 34.2 Generic and overload constraints

Generic-list omission and parameter-list omission are independent at every
callable segment:

| Form | Generic arity | Parameters |
| --- | --- | --- |
| `Method` | any | any |
| `Method<T>` | exactly 1 | any |
| `Method()` | exactly 0 | exactly zero |
| `Method<T>()` | exactly 1 | exactly zero |
| `Method(int)` | exactly 0 | exact one-`int` list |
| `Method<T>(T)` | exactly 1 | exact canonical list |

Bare `Method` is the intentionally broad all-arity/all-overload form. Once a
parameter list is present, omitting `<...>` means generic arity zero. Generic
placeholder names normalize by ordinal. A placeholder list expresses arity;
it is not a constructed invocation. Therefore
`Game::Player::Method<System.String>` is invalid.

C# aliases and their framework spellings have the same identity. Concrete
non-alias output is fully qualified. Identity distinguishes placeholder
ordinal, `ref`, `out`, `in`, `ref readonly`, nullable value types, array rank,
pointers, tuple shape, function pointers, and conversion target. Nullable
reference annotation is display-only identity metadata. Unsupported parameter
modifiers are rejected.

## 34.3 Complete special-callable catalog

Every supported bracketed callable has a copyable concrete form:

```text
Game::Player::[constructor](int,string)
Game::Player::[static-constructor]()
Game::Player::[destructor]()
Math::Number::[operator:+](Math.Number,Math.Number)
Math::Number::[checked-operator:+](Math.Number,Math.Number)
Game::Value::[conversion:implicit:int](Game.Value)
Game::Value::[conversion:explicit:string](Game.Value)
Game::Value::[checked-conversion:explicit:int](Game.Value)
Game::Player::[get:Name]()
Game::Player::[set:Name](string)
Game::Player::[init:Name](string)
Game::Player::[get:Item](int)
Game::Player::[set:Item](int,string)
Game::Player::[add:Changed](System.EventHandler)
Game::Player::[remove:Changed](System.EventHandler)
Game::Player::[get:Game.Contracts.IPlayer.Name]()
Game::Player::[set:Game.Contracts.IPlayer.Name](string)
Game::Player::[add:Game.Contracts.IEvents.Changed](System.EventHandler)
Game::Player::[explicit:System.IDisposable.Dispose]()
Game::Player::[explicit:Game.Contracts.IMapper.Map]<T>(T)
```

Explicit-interface accessors retain the accessor tag and use the fully
qualified interface member as the tag payload.

The accepted tag grammar is exactly:

```text
[constructor]
[static-constructor]
[destructor]
[operator:<token>]
[checked-operator:<token>]
[conversion:implicit:<type>]
[conversion:explicit:<type>]
[checked-conversion:explicit:<type>]
[get:<member>]
[set:<member>]
[init:<member>]
[add:<member>]
[remove:<member>]
[explicit:<fully-qualified-interface-member>]
```

Supported operator tokens are:

```text
+  -  !  ~  ++  --  true  false  *  /  %  &  |  ^
<<  >>  >>>  ==  !=  <  >  <=  >=
+=  -=  *=  /=  %=  &=  |=  ^=  <<=  >>=  >>>=
```

Source-only synthetic forms are:

```text
Game::Player::Run().<lambda#1>
Game::Player::Run().<anonymous-method#2>
Game::Player::<initializer:Score>
global::Program::<top-level-statements>
```

Queries may use `<lambda#*>` or `<anonymous-method#*>`. A concrete ordinal is a
positive integer. Lambda and anonymous-method nodes share source-order ordinals
within one immediate owner, and the counter resets for each nested owner.
Initializer and top-level callables participate in `--kind all` and direct
paths but add no new kind value. Compiler-only callables without a supported
source declaration receive no invented query name.

## 34.4 Typed condition families

| Category | Concise glob | Literal | Regex | Case policy |
| --- | --- | --- | --- | --- |
| namespace | `--namespace` | `--namespace-literal` | `--namespace-regex` | `--namespace-case strict|ignore` |
| type | `--type` | `--type-literal` | `--type-regex` | `--type-case strict|ignore` |
| method | `--method` | `--method-literal` | `--method-regex` | `--method-case strict|ignore` |
| file | `--file` | `--file-literal` | `--file-regex` | `--file-case strict|ignore` |
| include source | `--include` | `--include-literal` | `--include-regex` | `--source-case strict|ignore` |
| exclude source | `--exclude` | `--exclude-literal` | `--exclude-regex` | `--source-case strict|ignore` |

Case defaults to `strict` independently for each category. Duplicate case
options, invalid values, invalid regex, or regex timeout abort the query without
a partial payload. The legacy bare `--regex` and `--ignore-case` options are
removed and rejected.

Namespace/type/method/file glob and literal conditions are structural. A `*`
inside one component matches characters without crossing its boundary; a whole
`*` component matches exactly one level. A whole `**` component matches zero or
more levels, while an embedded `**` is equivalent to `*`. Method glob respects
balanced signature punctuation. Method literal retains omission semantics.
Method regex evaluates the whole canonical executable text. File conditions
use stored forward-slash paths. Source literal, glob, and regex are unanchored
substring matches over normalized source and may span supported newline forms.

Repeated conditions in one category are ordered OR alternatives, evaluated in
CLI order with short-circuiting. Different categories are ANDed. Repeated
includes are ANDed, and any matching exclude rejects a row. All
declaration-scoped file/source conditions must pass on the same physical
declaration; passing declarations project to one logical result.

`--kind all|method|lambda` applies to direct executable category.
`--async-status all|async|sync` applies to direct async status.
`symbol list --async-involved` is the distinct transitive-involvement filter.

## 34.5 Logical symbol and declaration model

`symbols` stores one logical semantic identity. `symbol_declarations` stores
physical source locations and normalized UTF-16 ranges into the shared
`normalized_sources` document payload. The exact declaration-role
domain is:

```text
ordinary | partial-definition | partial-implementation
```

A partial definition/implementation pair creates one logical row and two role
rows. The implementation is preferred when present; otherwise the definition
or ordinary declaration is preferred. A definition-only partial is valid.
Role order is ordinary, partial-definition, partial-implementation.

Logical stable keys are independent of preferred declaration, formatted style,
partial role, and machine-specific rooted paths. When semantic identity alone
cannot distinguish a source callable (for example a local, anonymous, or
synthetic node), a logical key may use only stored relative path, span, and
ordinal discriminators. Declaration keys add their stored relative document
identity, span, and role. Calls, candidates, relations, interface bindings,
async links, graph roots, and cardinality reference logical IDs. A partial pair
is never two candidates and never creates a partial self relation.

`definition` returns all applicable physical declaration rows. Ordinary symbol
and graph output uses the preferred declaration for location/source projection.
Source search may return multiple passing declarations while keeping their one
logical symbol identity. Table/text declaration records use a dedicated role
column, and declaration-unit JSON uses `declarationRole` with exactly the three
values above; the role is never appended to the symbol path.

## 34.6 Command and root-selection contract

`docs/CLI.md` and verbose help contain the exact accepted-option matrix. The
command-level selection rules are:

| Command | Selector/cardinality |
| --- | --- |
| `symbol find [selector]` | selector or explicit condition/kind/async required; empty match succeeds; optional `--require-single` |
| `symbol list` | no selector; empty match succeeds; optional conditions |
| `source search` | no selector; explicit condition/kind/async required; empty match succeeds |
| `source show <selector>` | exactly one logical source-backed root |
| `definition <selector>` | one or more roots; optional `--require-single` |
| `definition --at <path:line:column>` | position mode; no selector/root conditions |
| `references`, `callers`, `callees` | selector required; one or more roots; optional `--require-single` |
| `overrides` | selector required; method roots only; optional `--require-single` |
| `async tree`, `callers tree` | exactly one logical source-backed root |
| `conditions` | no selector or root conditions |

After terminal help handling, execution order is:

```text
parse CLI
-> validate command option scope
-> open/check schema and profile
-> parse selector and compile typed conditions
-> filter declarations and select logical roots
-> apply kind/direct-async/generated root filters
-> de-duplicate logical roots
-> cardinality check
-> optional exact override expansion
-> relation/graph traversal
-> canonical sort
-> shared formatting
-> flush and atomic output commit
```

Root conditions do not remove secondary relation or graph results. No traversal
starts before filtering, partial normalization, and cardinality. Valid empty
list/search queries return exit 0. Required-root commands report a query error
for zero roots. `source show` and graph commands also report a query error for
ambiguity; flat multi-root commands accept ambiguity unless `--require-single`
is present, in which case a post-filter count other than one returns exit 5.

`--include-overrides` is limited to `symbol find`, `definition`, `references`,
`callers`, and `callees`. It requires one exact wildcard-free method selector
and rejects condition-only, lambda/synthetic, initializer, and top-level roots.
Expansion occurs after root filtering/cardinality and before command work.

## 34.7 Schema version 6 invariants

The exact DDL is `docs/DB_SCHEMA.md`. Version 6 contains:

```text
schema_info
analysis_profiles
index_runs
projects
normalized_sources
documents
symbols
method_parameters
symbol_declarations
calls
call_candidates
symbol_relations
interface_method_bindings
conditional_symbols_used
```

Semantic path identity/display components live in dedicated `symbols` columns;
the removed legacy `fully_qualified_name` and `display_name` columns are not
part of version 6. `normalized_sources` owns one complete normalized document
per unique SHA-256/text pair; `documents.normalized_source_id` references it,
and declarations/calls store only normalized UTF-16 start/length ranges.
`declaration_role` has a database check constraint for numeric
values 1, 2, and 3. Logical identity is unique per profile, declaration identity
is unique per key and location/role, and all call/relation endpoints use logical
symbol foreign keys.

Snapshot validation rejects rooted stored paths, invalid anchors, duplicate or
split logical identities, invalid declaration roles/preference, declaration
endpoints used as logical endpoints, and partial self relations. Saving is one
transaction followed by foreign-key validation; failure rolls back without
replacing the prior valid snapshot.

Each normalized document is produced once at index time by `SourceNormalizer`.
Its full normalized text and SHA-256 hash are stored in `normalized_sources`;
equal hashes are verified with ordinal text equality before a payload is shared.
Declarations and calls store independent `normalized_start` and
`normalized_length` ranges. Ranges are UTF-16 code-unit offsets and lengths,
must be positive and in bounds, and are sliced in C# rather than with SQLite
`substr`. Declaration extents intentionally overlap for containing methods,
local functions, and lambdas; each slice still equals the existing per-node
normalization.

Query hydration loads each distinct referenced document payload only when a
source-bearing query requests it. No-source paths leave `NormalizedSource`
null and do not read payload text. Missing documents, payloads, or malformed
ranges raise an integrity error naming the affected row/document; there is no
source-file reparse, reconstruction, legacy-column fallback, or SQL substring
fallback. Existing location formatting is separate and may read the original
file to translate original UTF-16 offsets into line and column; line/column
locations are not persisted.

There is no old-schema migration, auto-deletion, or fallback. Querying or
indexing schema version 5 or older fails before modifying it. `--rebuild` does
not authorize deletion. The recovery is to delete or rename the old database,
or choose a new `--db` path, and then explicitly run `csindex index`.

## 34.8 Portable persisted paths

All persisted project, document, declaration, and source-key paths are
canonical forward-slash paths relative to one storage root. The run stores
`input_root = '.'` and an `index_root_anchor` relative from the database
directory to that root. The default `.csindex/index.sqlite` layout has anchor
`..`; a custom database stores its corresponding relative anchor.

Linked sources may use normalized leading `../`. The database directory,
storage root, and every persisted source location must share a Windows drive or
UNC server/share. This remains a one-root model, not a multi-root fallback. An
ordinary cross-volume/share project, document, or linked source fails in
preflight before SQLite mutation. The only positive exception is a physical C#
document that the existing `GeneratedCodeDetector` identifies as generated:
Roslyn keeps it for compilation semantics, while portable paths, documents,
declarations, body-derived facts, and query roots exclude it. Each such
compilation-only document emits one warning and increments `DocumentsExcluded`
once. Named symbols used by indexed source may survive as declaration-less
dependency endpoints. Same-volume generated source remains normally indexed.
The detector rule is vendor-neutral: it does not name a package, NuGet cache,
MSBuild target, or runtime switch. It therefore resolves a reported external
test-runner reporter incident generically rather than adding an xUnit-specific
exception.

The default query base is `FullPath(database directory + index_root_anchor)`.
`--base-dir` overrides that base for the current read without changing any
stored value. `--path-style absolute|relative` controls reconstructed display.
Rooted and effective-base-relative `definition --at` locations map to the same
stored document identity. Relocation works when the database/source relative
layout is preserved, or when `--base-dir` identifies the new root.

Physical source files are opened lazily. A missing or unreadable reconstructed
source is reported only by a command that must read it; formatting a stored
location alone does not require file existence.

## 34.9 Presentation, ordering, help, and output

`--symbol-path-style csharp|explicit` selects symbol formatting and
`--short-names` omits only the owner namespace from displayed paths. It never
shortens parameter, return, conversion-target, or explicit-interface payload
types. Table, JSON, tree, line, and Mermaid output use
the same formatters. Canonical ordering uses semantic identity plus stored
relative location, never formatted text or reconstructed rooted paths. Style,
short names, path style, base override, case mode, and output format therefore
cannot reorder an equivalent result. Trees are parent-first; declaration rows
then use role, stored path, and source position.

Normal help is concise. For global scope and every recognized command,
`--help --verbose` and `--help-verbose` are byte-identical and order-independent.
Help is terminal before required positionals, database/source opening, or output
creation, but tokenization and allowed-option scope are validated first. An
unknown option/command is still a usage error. `index --verbose` without help
retains runtime progress meaning; query `--verbose` is help-only.

Without `--output-file`, payload goes to stdout and diagnostics to stderr.
File output renders to an owned same-directory temporary file, flushes, checks
cancellation, and atomically commits. Query, regex, path, formatting, flush,
cancellation, replace, or commit failure preserves any existing destination and
removes owned temporary files where possible. Help never creates a destination.

Cancellation is checked during candidate processing, matching, traversal,
ordering, formatting, flush, and immediately before commit. No failure exposes
a partial committed payload.

## 34.10 Exit categories

```text
0  success (including valid empty list/search results)
2  invalid arguments or query
3  input, analysis, cancellation, source, or output failure
4  SQLite or incompatible-schema failure
5  --require-single failure
```

Exit 2 includes command-inapplicable options, removed matcher switches, legacy
or malformed path grammar, invalid generic/anonymous/glob/regex/case/style
input, and forbidden combinations. A required-root zero match and single-root
ambiguity remain query errors; exit 5 is reserved for the explicit
`--require-single` contract.

## 34.11 Normalized caller-source contract

`symbol find --include` and `--exclude` match the normalized slice of each
physical function declaration. Conditions are never evaluated against the
complete document, and repeated include terms cannot be distributed across
different declarations. A logical symbol is selected when at least one of its
physical declarations passes the complete condition set.

`callers --show-source` hydrates the normalized invocation or object-creation
expression for each physical call row. Table output appends one sanitized
`normalizedSource` value; JSON appends exact `normalizedSource`. Without the
flag, normalized source text is not hydrated and `normalizedSource` is omitted.

`callers tree --show-source` retains one structural edge per caller/callee
pair and associates every eligible physical call site with that edge,
including retained cycle, cross, and finite-depth edges. Sites are ordered by
structural edge order, document path, original source start, original source
length, and call ID. Text tree output writes one sanitized `@ path:line:column`
line per site. Mermaid uses one escaped edge label with sites joined by
`<br/>`; JSON adds ordered `callSites` entries containing `id`, exact
`location`, and exact `normalizedSource`. Ordinary table and text-tree sources
use `TableTextSanitizer` for TAB, CR/LF, NEL, U+2028, and U+2029. Mermaid
escapes `&`, quotes, brackets, angle brackets, and `|`, converts CR/LF to
`<br/>`, and joins sites with `<br/>`; JSON remains lossless. The query retains
`CallerTreeResult.CallSites` metadata when `ShowSource` is false, with each
source string null. Only the caller-tree no-flag formatter avoids enumerating
`CallSites` or emitting `callSites`; ordinary callers still enumerate
`CallResult.Calls`, but do not hydrate or emit `normalizedSource`. Caller/tree
selection, ordering, and bytes remain compatible.

The final `--show-source` scope is exactly `symbol find`, `callers`, and
`callers tree`. It is rejected by `references`, `callees`, `overrides`, and
`async tree`, and the flag does not accept a value.
