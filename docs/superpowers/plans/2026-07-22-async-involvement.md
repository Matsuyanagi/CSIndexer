# 非同期関与情報 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Roslynで非同期ロールと呼び出し時の利用方法を抽出し、循環安全な呼び出し元方向の伝播結果をSQLiteと既存CLI出力へ追加する。

**Architecture:** 宣言・operation由来の直接情報を `AsyncRole` としてシンボルへ、呼び出し式の消費方法を `AsyncUsageKind` として辺へ保存する。全抽出完了後、解決済み通常呼び出しの逆グラフを複数始点BFSで走査し、非同期起点までの最短距離を `AsyncInvolvementDepth` として永続化する。

**Tech Stack:** .NET 10、C#、Microsoft.CodeAnalysis 5.6.0、Microsoft.Data.Sqlite 10.0.10、xUnit v3、PowerShell、RTK

## Global Constraints

- 伝播方向は「非同期関数へ到達する呼び出し元方向」だけとし、非同期関数から呼ばれる同期関数には伝播しない。
- 自己再帰・相互再帰・複数循環でも停止し、複数起点の最短距離を保存する。
- `Task`、`ValueTask`、`UniTask` と各ジェネリック型をawaitableとして扱い、`UniTaskVoid` と非同期ストリームは別ロールにする。
- UniTaskパッケージへの製品・テスト依存は追加せず、テストソース内の最小互換型で判定を検証する。
- ラムダとローカル関数のoperationを外側の所有関数へ混入させない。
- `Awaited`以外の呼び出し利用方法は既知のTask/ValueTask/UniTask返却だけに付け、同期・未知型の呼び出しは`None`にする。
- スキーマバージョンを2へ上げ、v1 DBを暗黙に削除・変換しない。
- `schema_info`がない非空の未認識SQLite DBは、WAL設定・DDLより前に拒否して変更しない。
- ユーザーが `230eaad` でコミットした `docs/SPEC.md` の字下げを保持し、実装用の追記以外は変更しない。
- シェルコマンドはAGENTS.mdに従い、すべて `rtk` を先頭に付ける。

---

### Task 1: 非同期モデルと循環安全な伝播器

**Files:**
- Modify: `src/CsIndex.Core/Model/IndexEnums.cs`
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Create: `src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs`
- Create: `tests/CsIndex.Core.Tests/AsyncInvolvementPropagatorTests.cs`

**Interfaces:**
- Produces: `AsyncRole`, `AsyncUsageKind`, `SymbolData.AsyncRole`, `SymbolData.AsyncInvolvementDepth`, `CallData.AsyncUsageKind`
- Produces: `AsyncInvolvementPropagator.Apply(IndexSnapshot snapshot)`
- Consumes: `IndexSnapshot.Symbols` と解決済み `ReferenceKind.Invocation` の `CallData.CalleeDefinitionKey`

- [ ] **Step 1: 最短距離と循環停止の失敗テストを書く**

```csharp
using CsIndex.Core.Analysis;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class AsyncInvolvementPropagatorTests
{
    [Fact(Timeout = 5_000)]
    public void Apply_PropagatesShortestDistanceThroughCycle()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "a", AsyncRole.None);
        AddMethod(snapshot, "b", AsyncRole.None);
        AddMethod(snapshot, "c", AsyncRole.DeclaredAsync);
        AddCall(snapshot, "a", "b");
        AddCall(snapshot, "b", "a");
        AddCall(snapshot, "b", "c");

        AsyncInvolvementPropagator.Apply(snapshot);

        Assert.Equal(2, snapshot.Symbols["a"].AsyncInvolvementDepth);
        Assert.Equal(1, snapshot.Symbols["b"].AsyncInvolvementDepth);
        Assert.Equal(0, snapshot.Symbols["c"].AsyncInvolvementDepth);
    }

    [Fact(Timeout = 5_000)]
    public void Apply_LeavesCycleWithoutAsyncOriginUnrelated()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "a", AsyncRole.None);
        AddMethod(snapshot, "b", AsyncRole.None);
        AddCall(snapshot, "a", "b");
        AddCall(snapshot, "b", "a");

        AsyncInvolvementPropagator.Apply(snapshot);

        Assert.Null(snapshot.Symbols["a"].AsyncInvolvementDepth);
        Assert.Null(snapshot.Symbols["b"].AsyncInvolvementDepth);
    }

    [Fact]
    public void Apply_UsesShortestPathAcrossMultipleOrigins()
    {
        var snapshot = CreateSnapshot();
        AddMethod(snapshot, "caller", AsyncRole.None);
        AddMethod(snapshot, "bridge", AsyncRole.None);
        AddMethod(snapshot, "near", AsyncRole.ReturnsAwaitable);
        AddMethod(snapshot, "far", AsyncRole.ContainsAwait);
        AddCall(snapshot, "caller", "near");
        AddCall(snapshot, "caller", "bridge");
        AddCall(snapshot, "bridge", "far");

        AsyncInvolvementPropagator.Apply(snapshot);

        Assert.Equal(1, snapshot.Symbols["caller"].AsyncInvolvementDepth);
    }

    private static void AddMethod(IndexSnapshot snapshot, string key, AsyncRole role) =>
        snapshot.Symbols[key] = new SymbolData
        {
            StableKey = key,
            Kind = IndexedSymbolKind.Method,
            Name = key,
            NamespaceName = string.Empty,
            FullyQualifiedName = key,
            DisplayName = key,
            AsyncRole = role,
        };

    private static void AddCall(IndexSnapshot snapshot, string caller, string callee) =>
        snapshot.Calls.Add(new CallData
        {
            CallerSymbolKey = caller,
            CalleeSymbolKey = callee,
            CalleeDefinitionKey = callee,
            ReferenceKind = ReferenceKind.Invocation,
            DispatchKind = DispatchKind.Static,
            ResolutionStatus = ResolutionStatus.Resolved,
            ResolutionReason = ResolutionReason.None,
            DocumentKey = "document",
            SourceStart = 0,
            SourceLength = 1,
        });

    private static IndexSnapshot CreateSnapshot() => new()
    {
        InputRoot = "root",
        InputFingerprint = [],
        RequestHash = [],
        Profile = new AnalysisProfileData
        {
            Name = "test",
            InputMode = InputMode.Directory,
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        },
    };
}
```

- [ ] **Step 2: テストを実行し、API未実装で失敗することを確認する**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~AsyncInvolvementPropagatorTests`

Expected: FAIL。`AsyncRole` または `AsyncInvolvementPropagator` が存在しないことが原因でコンパイルに失敗する。

- [ ] **Step 3: 列挙値とモデルプロパティを追加する**

```csharp
[Flags]
public enum AsyncRole
{
    None = 0,
    DeclaredAsync = 1 << 0,
    ReturnsAwaitable = 1 << 1,
    ContainsAwait = 1 << 2,
    AsyncIterator = 1 << 3,
    ReturnsAsyncEnumerable = 1 << 4,
    AsyncVoid = 1 << 5,
    UniTaskVoid = 1 << 6,
    UsesAwaitForEach = 1 << 7,
    UsesAwaitUsing = 1 << 8,
}

public enum AsyncUsageKind
{
    None = 0,
    Awaited = 1,
    Forwarded = 2,
    Stored = 3,
    Passed = 4,
    Discarded = 5,
    Unobserved = 6,
}
```

`SymbolData` へ `AsyncRole AsyncRole { get; init; }` と `int? AsyncInvolvementDepth { get; init; }`、`CallData` へ `AsyncUsageKind AsyncUsageKind { get; init; }` を追加する。

- [ ] **Step 4: 複数始点BFSを最小実装する**

```csharp
using CsIndex.Core.Model;

namespace CsIndex.Core.Analysis;

public static class AsyncInvolvementPropagator
{
    private const AsyncRole OriginRoles =
        AsyncRole.DeclaredAsync |
        AsyncRole.ReturnsAwaitable |
        AsyncRole.ContainsAwait |
        AsyncRole.AsyncIterator |
        AsyncRole.ReturnsAsyncEnumerable |
        AsyncRole.AsyncVoid |
        AsyncRole.UniTaskVoid |
        AsyncRole.UsesAwaitForEach |
        AsyncRole.UsesAwaitUsing;

    public static void Apply(IndexSnapshot snapshot)
    {
        var reverseCalls = snapshot.Calls
            .Where(call => call.ReferenceKind == ReferenceKind.Invocation &&
                           call.ResolutionStatus == ResolutionStatus.Resolved &&
                           call.CalleeDefinitionKey is not null)
            .GroupBy(call => call.CalleeDefinitionKey!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(call => call.CallerSymbolKey).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var distance = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var symbol in snapshot.Symbols.Values.Where(symbol => (symbol.AsyncRole & OriginRoles) != 0))
        {
            distance[symbol.StableKey] = 0;
            queue.Enqueue(symbol.StableKey);
        }

        while (queue.TryDequeue(out var callee))
        {
            if (!reverseCalls.TryGetValue(callee, out var callers))
            {
                continue;
            }

            foreach (var caller in callers)
            {
                var candidate = distance[callee] + 1;
                if (distance.TryGetValue(caller, out var current) && current <= candidate)
                {
                    continue;
                }

                distance[caller] = candidate;
                queue.Enqueue(caller);
            }
        }

        foreach (var key in snapshot.Symbols.Keys.ToArray())
        {
            snapshot.Symbols[key] = snapshot.Symbols[key] with
            {
                AsyncInvolvementDepth = distance.TryGetValue(key, out var value) ? value : null,
            };
        }
    }
}
```

- [ ] **Step 5: 対象テストとCore全テストを通す**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~AsyncInvolvementPropagatorTests`

Expected: PASS 3 tests。

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj`

Expected: 全テストPASS、失敗0。

- [ ] **Step 6: Task 1をコミットする**

```powershell
rtk git add src/CsIndex.Core/Model/IndexEnums.cs src/CsIndex.Core/Model/IndexData.cs src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs tests/CsIndex.Core.Tests/AsyncInvolvementPropagatorTests.cs
rtk git commit -m "feat: add async involvement graph model"
```

### Task 2: Roslynによる非同期ロールと利用方法の抽出

**Files:**
- Create: `src/CsIndex.Core/Analysis/AsyncSymbolClassifier.cs`
- Create: `src/CsIndex.Core/Analysis/AsyncOperationClassifier.cs`
- Modify: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`
- Create: `tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs`

**Interfaces:**
- Consumes: Task 1の `AsyncRole`、`AsyncUsageKind`、`AsyncInvolvementPropagator.Apply`
- Produces: `AsyncSymbolClassifier.Classify(IMethodSymbol method, Compilation compilation)`
- Produces: `AsyncOperationClassifier.ClassifyInvocation(IInvocationOperation invocation, Compilation compilation)`
- Produces: 宣言・operation・呼び出し辺へ付与済みの非同期情報

- [ ] **Step 1: 宣言、UniTask、operation、所有者分離の失敗テストを書く**

`AsyncSemanticExtractorTests` に一時ディレクトリを解析する `AnalyzeAsync(string source)` を実装し、次のソースを入力する。

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Cysharp.Threading.Tasks
{
    public readonly struct UniTask { }
    public readonly struct UniTask<T> { }
    public readonly struct UniTaskVoid { }
    public interface IUniTaskAsyncEnumerable<T> { }
}

public sealed class AsyncCases
{
    public async Task LeafAsync() => await Task.Yield();
    public Task ForwardTask() => LeafAsync();
    public Cysharp.Threading.Tasks.UniTask UniTaskResult() => default;
    public Cysharp.Threading.Tasks.UniTask<int> GenericUniTaskResult() => default;
    public Cysharp.Threading.Tasks.UniTaskVoid FireAndForget() => default;
    public Cysharp.Threading.Tasks.IUniTaskAsyncEnumerable<int> UniTaskStream() => default!;

    public async IAsyncEnumerable<int> StreamAsync()
    {
        await Task.Yield();
        yield return 1;
    }

    public async Task ConsumeStreamAsync()
    {
        await foreach (var value in StreamAsync()) { }
    }

    public async Task DisposeAsync()
    {
        await using var resource = new AsyncResource();
    }

    public void Outer()
    {
        Func<Task> nested = async () => await LeafAsync();
    }

    private sealed class AsyncResource : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => default;
    }
}
```

テストでは `LeafAsync` に `DeclaredAsync | ReturnsAwaitable | ContainsAwait`、`ForwardTask` に `ReturnsAwaitable`、UniTask系に対応ロール、`StreamAsync` に `AsyncIterator | ReturnsAsyncEnumerable` が付くことを検証する。`ConsumeStreamAsync` には `UsesAwaitForEach`、`DisposeAsync` には `UsesAwaitUsing` が付くことも検証する。ラムダには `DeclaredAsync | ReturnsAwaitable | ContainsAwait` と起点距離0が付き、`Outer` には非同期ロールも距離も付かないことを検証する。

- [ ] **Step 2: 呼び出し利用方法の失敗テストを書く**

同じテストソースへ次を加え、`LeafAsync` 向けの呼び出し辺をsource spanまたは呼び出し元名で特定して全分類を検証する。

```csharp
public async Task Awaited() { await LeafAsync(); }
public Task Forwarded() => LeafAsync();
public void Stored() { var task = LeafAsync(); }
public void Passed() { Consume(LeafAsync()); }
public void Discarded() { _ = LeafAsync(); }
public void Unobserved() { LeafAsync(); }
private static void Consume(Task task) { }
```

期待値は順に `Awaited`、`Forwarded`、`Stored`、`Passed`、`Discarded`、`Unobserved` とする。

- [ ] **Step 3: 新規テストを実行して非同期情報が未抽出のため失敗することを確認する**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~AsyncSemanticExtractorTests`

Expected: FAIL。`AsyncRole.None`、`AsyncUsageKind.None`、距離nullが返ることが原因になる。

- [ ] **Step 4: シンボル分類器を実装する**

```csharp
using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Analysis;

public static class AsyncSymbolClassifier
{
    private static readonly string[] AwaitableTypes =
    [
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task`1",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask`1",
        "Cysharp.Threading.Tasks.UniTask",
        "Cysharp.Threading.Tasks.UniTask`1",
    ];

    private static readonly string[] AsyncEnumerableTypes =
    [
        "System.Collections.Generic.IAsyncEnumerable`1",
        "Cysharp.Threading.Tasks.IUniTaskAsyncEnumerable`1",
    ];

    public static AsyncRole Classify(IMethodSymbol method, Compilation compilation)
    {
        var role = AsyncRole.None;
        if (method.IsAsync) role |= AsyncRole.DeclaredAsync;
        if (method.IsAsync && method.ReturnsVoid) role |= AsyncRole.AsyncVoid;
        if (method.IsAsync && method.IsIterator) role |= AsyncRole.AsyncIterator;
        if (Matches(method.ReturnType, compilation, AwaitableTypes)) role |= AsyncRole.ReturnsAwaitable;
        if (Matches(method.ReturnType, compilation, AsyncEnumerableTypes)) role |= AsyncRole.ReturnsAsyncEnumerable;
        if (Matches(method.ReturnType, compilation, ["Cysharp.Threading.Tasks.UniTaskVoid"])) role |= AsyncRole.UniTaskVoid;
        return role;
    }

    private static bool Matches(ITypeSymbol type, Compilation compilation, IEnumerable<string> metadataNames)
    {
        var definition = type.OriginalDefinition;
        return metadataNames
            .Select(compilation.GetTypeByMetadataName)
            .Where(candidate => candidate is not null)
            .Any(candidate => SymbolEqualityComparer.Default.Equals(definition, candidate));
    }
}
```

- [ ] **Step 5: operation分類器を実装する**

`AsyncOperationClassifier.ClassifyInvocation` は現在の`Compilation`を受け取り、親operationを上へ走査する。`IAnonymousFunctionOperation`または`ILocalFunctionOperation`で走査を停止し、同じ所有者内の祖先だけに次の優先順位を適用する。

```csharp
IAwaitOperation                                  => AsyncUsageKind.Awaited
IReturnOperation                                 => AsyncUsageKind.Forwarded
ISimpleAssignmentOperation { Target: IDiscardOperation } => AsyncUsageKind.Discarded
IVariableInitializerOperation or ISimpleAssignmentOperation => AsyncUsageKind.Stored
IArgumentOperation                               => AsyncUsageKind.Passed
IExpressionStatementOperation                    => AsyncUsageKind.Unobserved
_                                                => AsyncUsageKind.None
```

分類済み値を上書きするヘルパーは `Awaited`、`Forwarded`、`Discarded`、`Stored`、`Passed`、`Unobserved`、`None` の優先順を固定する。これにより `await FooAsync().ConfigureAwait(false)` の内側呼び出しも `Awaited` になる。`Awaited`はcustom awaitableにも適用するが、それ以外の値は`AsyncSymbolClassifier`と共有するmetadata symbol equalityで既知Task/ValueTask/UniTask返却を確認できた場合だけ返し、非awaitableは`None`にする。

- [ ] **Step 6: SemanticExtractorへ宣言・operation・辺分類を接続する**

- 宣言メソッド、ローカル関数、ラムダ、`EnsureMethod` で作るメタデータメソッドの `SymbolData` を `with { AsyncRole = AsyncSymbolClassifier.Classify(...) }` で補強する。
- `AwaitExpressionSyntax`、`CommonForEachStatementSyntax`、`UsingStatementSyntax`、using宣言の各operationを取得し、`FindOwner` で得たシンボルへ該当ロールをORする。
- `AddResolvedCall` へ `AsyncUsageKind` を渡し、`IInvocationOperation` では `AsyncOperationClassifier.ClassifyInvocation(invocation, projectState.Compilation)` を設定する。
- synthetic initializer所有者はfield/event-field/propertyの実際のinitializer clauseだけに作り、local/parameter/default initializerを登録しない。
- 全プロジェクトのfact抽出後に `AsyncInvolvementPropagator.Apply(snapshot)` を1回呼ぶ。
- `UpsertSymbol` はsource情報を優先する既存規則を維持しつつ、同じstable keyの `AsyncRole` をORして失わないようにする。

- [ ] **Step 7: 新規テスト、Core全テストを通す**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~AsyncSemanticExtractorTests`

Expected: 新規テスト全PASS。

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj`

Expected: 全テストPASS、失敗0。

- [ ] **Step 8: Task 2をコミットする**

```powershell
rtk git add src/CsIndex.Core/Analysis/AsyncSymbolClassifier.cs src/CsIndex.Core/Analysis/AsyncOperationClassifier.cs src/CsIndex.Core/Analysis/SemanticExtractor.cs tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs
rtk git commit -m "feat: extract async roles and call usage"
```

### Task 3: SQLiteスキーマv2とDB-only復元

**Files:**
- Modify: `src/CsIndex.Core/Caching/RequestHasher.cs`
- Modify: `src/CsIndex.Storage/Schema/SchemaMigrator.cs`
- Modify: `src/CsIndex.Storage/SqliteIndex.cs`
- Modify: `src/CsIndex.Storage/QueryModels.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Modify: `tests/CsIndex.Storage.Tests/SqliteIndexTests.cs`

**Interfaces:**
- Consumes: `SymbolData.AsyncRole`、`SymbolData.AsyncInvolvementDepth`、`CallData.AsyncUsageKind`
- Produces: `StoredSymbol.AsyncRole`、`StoredSymbol.AsyncInvolvementDepth`、`StoredCall.AsyncUsageKind`
- Produces: schema version 2の `symbols.async_role`、`symbols.async_involvement_depth`、`calls.async_usage_kind`

- [ ] **Step 1: DB保存・復元の失敗テストを書く**

`SqliteIndexTests.CreateSnapshot` へcallerとcalleeのメソッド、解決済み呼び出しを追加し、次を設定する。

```csharp
AsyncRole = AsyncRole.DeclaredAsync | AsyncRole.ReturnsAwaitable,
AsyncInvolvementDepth = 0,
AsyncUsageKind = AsyncUsageKind.Awaited,
```

保存後に新しい `QueryRepository` からシンボルと呼び出しを読み、同じ値が戻ることを検証する。また `SchemaMigrator.CurrentVersion` と `RequestHasher.SchemaVersion` が2であることを検証する。

- [ ] **Step 2: Storageテストを実行して列またはモデル不足で失敗することを確認する**

Run: `rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --filter FullyQualifiedName~SqliteIndexTests`

Expected: FAIL。新しいStoredモデル値が復元されない、またはスキーマバージョンが1であることが原因になる。

- [ ] **Step 3: スキーマをv2へ更新する**

`RequestHasher.SchemaVersion` を2へ変更する。新規DB作成SQLをversion 2として次の列を含むように変更する。

```sql
-- symbols
async_role               INTEGER NOT NULL DEFAULT 0,
async_involvement_depth  INTEGER,

-- calls
async_usage_kind         INTEGER NOT NULL DEFAULT 0,
```

既存の `EnsureMigratedAsync` の不一致時エラーを維持し、v1を削除またはALTERしない。`schema_info`がない場合はSQLite内部object以外のuser table / index / view / triggerを検査し、1つでも存在する未認識DBはWAL設定・DDLより前にエラーとして変更しない。

- [ ] **Step 4: INSERT、SELECT、readerを同じ列順で更新する**

- `SqliteIndex.InsertSymbolAsync` とcall挿入へ3値をパラメーター化して追加する。
- `StoredSymbol` の `IsOverride` 後へ `AsyncRole` と `AsyncInvolvementDepth` を追加する。
- `StoredCall` の `ResolutionReason` 後へ `AsyncUsageKind` を追加する。
- `QueryRepository` の全symbol SELECT、`BuildCallSelect`、`ReadSymbolsAsync`、`ReadCallsAsync` のordinalを同期する。
- enum値はSQLite INTEGERへ `(int)` で保存し、readerで対応enumへcastする。

- [ ] **Step 5: Storageテストと全テストを通す**

Run: `rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj`

Expected: 全StorageテストPASS。

Run: `rtk dotnet test CsIndex.sln`

Expected: 全テストPASS、失敗0。

- [ ] **Step 6: Task 3をコミットする**

```powershell
rtk git add src/CsIndex.Core/Caching/RequestHasher.cs src/CsIndex.Storage/Schema/SchemaMigrator.cs src/CsIndex.Storage/SqliteIndex.cs src/CsIndex.Storage/QueryModels.cs src/CsIndex.Storage/QueryRepository.cs tests/CsIndex.Storage.Tests/SqliteIndexTests.cs
rtk git commit -m "feat: persist async analysis in schema v2"
```

### Task 4: 既存CLIのJSON/table出力

**Files:**
- Modify: `src/CsIndex.Cli/CsIndex.Cli.csproj`
- Create: `src/CsIndex.Cli/Properties/AssemblyInfo.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj`
- Create: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`

**Interfaces:**
- Consumes: `StoredSymbol.AsyncRole`、`StoredSymbol.AsyncInvolvementDepth`、`StoredCall.AsyncUsageKind`
- Produces: symbol JSONの `asyncRole`、`isAsyncInvolved`、`asyncInvolvementDepth`
- Produces: call JSON/tableの `asyncUsageKind`

- [ ] **Step 1: JSON出力の失敗テストを書く**

CLI assemblyからIntegrationTestsへ次の `InternalsVisibleTo` を設定し、IntegrationTestsのcsprojへ `..\..\src\CsIndex.Cli\CsIndex.Cli.csproj` のproject referenceを追加する。

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CsIndex.IntegrationTests")]
```

`OutputFormatterTests` では `Console.Out` を `StringWriter` へ一時差し替え、`WriteSymbols` のJSONを `JsonDocument` で解析する。

```csharp
Assert.Equal("DeclaredAsync, ReturnsAwaitable", symbol.GetProperty("asyncRole").GetString());
Assert.True(symbol.GetProperty("isAsyncInvolved").GetBoolean());
Assert.Equal(0, symbol.GetProperty("asyncInvolvementDepth").GetInt32());
```

別テストで `WriteCalls` の `calls[0].asyncUsageKind` が `"Awaited"` であることを検証する。Consoleを使用するテストcollectionは `DisableParallelization = true` にする。

- [ ] **Step 2: table出力の失敗テストを書く**

`WriteSymbols` のtable出力に `[async: DeclaredAsync, ReturnsAwaitable; depth: 0]`、`WriteCalls` に `[Awaited]` が含まれることを別テストで検証する。

- [ ] **Step 3: OutputFormatterテストを実行して追加フィールド不足で失敗することを確認する**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~OutputFormatterTests`

Expected: FAIL。JSON propertyまたはtable補足が存在しないことが原因になる。

- [ ] **Step 4: JSON/table出力を最小実装する**

`ToSymbolObject` へ次を追加する。

```csharp
asyncRole = symbol.AsyncRole.ToString(),
isAsyncInvolved = symbol.AsyncInvolvementDepth is not null,
asyncInvolvementDepth = symbol.AsyncInvolvementDepth,
```

call objectへ `asyncUsageKind = call.AsyncUsageKind.ToString()` を追加する。tableではロールが `None` で距離nullなら補足を出さず、それ以外だけ非同期ロールと距離を表示する。

- [ ] **Step 5: OutputFormatterテストとIntegration全テストを通す**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~OutputFormatterTests`

Expected: 新規テスト全PASS。

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj`

Expected: 全IntegrationテストPASS。

- [ ] **Step 6: Task 4をコミットする**

```powershell
rtk git add src/CsIndex.Cli/CsIndex.Cli.csproj src/CsIndex.Cli/Properties/AssemblyInfo.cs src/CsIndex.Cli/OutputFormatter.cs tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj tests/CsIndex.IntegrationTests/OutputFormatterTests.cs
rtk git commit -m "feat: show async analysis in query output"
```

### Task 5: 継続実装用ドキュメントと最終検証

**Files:**
- Modify: `docs/SPEC.md`
- Modify: `docs/DECISIONS.md`
- Modify: `docs/DB_SCHEMA.md`
- Modify: `docs/CLI.md`
- Modify: `docs/TEST_PLAN.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`
- Modify: `docs/KNOWN_LIMITATIONS.md`
- Add: `docs/superpowers/plans/2026-07-22-async-involvement.md`

**Interfaces:**
- Consumes: 完成したenum名、DB列名、CLI property名、テスト結果
- Produces: 後続実装者が判定根拠、伝播方向、循環停止方法、限界を再現できる文書

- [ ] **Step 1: 正式仕様と判断記録を追記する**

`docs/SPEC.md` へ次を明記する。

- `AsyncRole` と各判定条件
- `Task`、`ValueTask`、`UniTask`、`UniTaskVoid`、非同期ストリームの区別
- `AsyncUsageKind` の意味と優先順位
- 呼び出し元方向だけに伝播すること
- 複数始点BFS、訪問済み最短距離、自己再帰・相互再帰の停止条件
- `async_role`、`async_involvement_depth`、`async_usage_kind` のDB列

`docs/DECISIONS.md` へ、query-time再帰CTEではなくindex-time BFSを採用した理由と、直接ロールと派生距離を分離した理由を新しいdecisionとして追加する。

- [ ] **Step 2: 利用者向け・保守者向け文書を同期する**

- `docs/DB_SCHEMA.md`: schema v2と追加3列
- `docs/CLI.md`: JSON propertyとtable補足
- `docs/TEST_PLAN.md`: Task/ValueTask/UniTask、所有者分離、全usage、chain/cycle/shortest path、DB-only復元、出力試験
- `docs/IMPLEMENTATION_STATUS.md`: 非同期解析を実装済みとして記録
- `docs/KNOWN_LIMITATIONS.md`: custom awaitableの未await中継、dynamic、delegate flow、runtime dispatch、全経路非保存

- [ ] **Step 3: 文書の用語・プレースホルダー・差分を検査する**

Run: `rtk rg -n "TBD|TODO|AsyncRole|AsyncUsageKind|AsyncInvolvementDepth|UniTask|循環" docs`

Expected: `TBD` と `TODO` は新規追記部分に存在せず、各用語がSPEC・判断・DB・CLI・テスト・制限文書で一致する。

Run: `rtk git diff --check`

Expected: exit 0、whitespace errorなし。

- [ ] **Step 4: 完全なRelease検証を新規実行する**

Run: `rtk dotnet test CsIndex.sln --configuration Release`

Expected: 全テストPASS、失敗0。

Run: `rtk dotnet build CsIndex.sln --configuration Release --no-restore`

Expected: Build succeeded、error 0。

- [ ] **Step 5: 要件チェックを行う**

次を実際のコード、DB reader、テスト結果、文書差分に照らして確認する。

```text
[ ] 宣言asyncとawaitable返却が別ロール
[ ] Task/ValueTask/UniTask/UniTaskVoid/async stream対応
[ ] await/await foreach/await using対応
[ ] ラムダ・ローカル関数の所有者分離
[ ] 呼び出しusage全分類
[ ] 呼び出し元方向だけの伝播
[ ] cycleで停止
[ ] 最短距離
[ ] schema v2永続化とDB-only読込
[ ] JSON/table出力
[ ] docs配下へ決定・方法・限界を記録
```

- [ ] **Step 6: ドキュメントをコミットする**

ユーザーの既存変更は `230eaad` で独立コミット済みなので、今回追記した全文書と計画書をコミットする。

```powershell
rtk git add docs/SPEC.md docs/DECISIONS.md docs/DB_SCHEMA.md docs/CLI.md docs/TEST_PLAN.md docs/IMPLEMENTATION_STATUS.md docs/KNOWN_LIMITATIONS.md docs/superpowers/plans/2026-07-22-async-involvement.md
rtk git commit -m "docs: record async analysis behavior"
```
