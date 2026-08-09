# Symbol, Source, and Graph Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved lambda naming/search, executable metadata, normalized-source search, persisted async shortest path, and bounded caller-tree features formalized in `docs/SPEC.md` section 33.

**Architecture:** Extend Core extraction with token-safe normalized source, return types, stable initializer/lambda ownership, and a deterministic async next hop. Persist those facts in SQLite schema version 4, then add focused Query request/result types for pattern/source search and bounded graph traversal. Keep canonical storage separate from short-name presentation and expose the features through small CLI command handlers and output formatters.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), Roslyn 5.6.0 APIs already referenced by the solution, SQLite through `Microsoft.Data.Sqlite`, xUnit v3, the existing hand-written CLI parser, and RTK-prefixed PowerShell commands.

## Global Constraints

- `docs/SPEC.md` section 33 and its linked durable command/schema/acceptance documents are authoritative; the approved design is `docs/superpowers/specs/2026-08-08-symbol-source-graph-expansion-design.md`.
- Backward database compatibility is not required. Increase schema and request-hash versions from 3 to 4 and reject every unsupported version without modifying it.
- Do not add external dependencies.
- Use Roslyn symbols/tokens for extraction and normalization; do not strip comments or whitespace with regular expressions.
- Preserve literal token text and insert a space whenever token concatenation would alter lexical tokenization.
- Number every lambda in source order within its nearest non-lambda executable owner, including nested lambdas.
- Attribute calls written inside a lambda to that lambda. Do not synthesize ownership edges as calls or infer delegate invocation.
- Persist exactly one async shortest-path next hop. Strictly shorter paths replace it; equal paths never replace it.
- Every graph traversal is profile-scoped, cancellation-aware, deterministic, iterative, and protected by visited IDs and node limits.
- `symbol find` source exclusions are ORed and evaluated before ANDed includes. Standalone `source search` requires at least one source condition; `symbol find` does not.
- `--short-names` is presentation-only. Never shorten canonical stored fields or use shortened names for identity/search.
- Preserve existing exact-query, `--include-overrides`, generated-source, dispatch, caller-scope, and lambda-callee behavior unless the authoritative requirements explicitly supersede it.
- Follow RED-GREEN-REFACTOR for every behavior change. Never weaken an existing test to obtain GREEN.
- Use only RTK-prefixed shell commands.

---

### Task 1: Extract executable metadata, normalized source, and stable lambda owners

**Files:**
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Modify: `src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs`
- Modify: `src/CsIndex.Core/Analysis/AnalysisState.cs`
- Modify: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`
- Create: `src/CsIndex.Core/Analysis/SourceNormalizer.cs`
- Create: `tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs`
- Create: `tests/CsIndex.Core.Tests/SourceNormalizerTests.cs`
- Modify: `tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs` only if shared extraction helpers make that necessary

**Interfaces:**
- Consumes: Roslyn `IMethodSymbol`, `IAnonymousFunctionOperation`, `SyntaxNode.DescendantTokens`, and the existing `DocumentAnalysisState.FindOwner` ownership maps.
- Produces: `SymbolData.ReturnTypeKey`, `SymbolData.NormalizedSource`, and `SymbolData.NormalizedSourceHash`.
- Produces: `SourceNormalizer.Normalize(SyntaxNode node)` returning `NormalizedSourceData`.
- Preserves: immediate lexical lambda `ContainingSymbolKey`; changes only the counter/display owner to the nearest non-lambda executable symbol.

- [ ] **Step 1: Write RED tests for token-safe source normalization.**

Add focused tests with exact expected output:

```csharp
[Fact]
public void Normalize_RemovesTriviaWithoutJoiningTokensOrChangingLiterals()
{
    var node = CSharpSyntaxTree.ParseText("""
        class C
        {
            public static int Func()
            {
                var a = 1; // initialize
                var text = "/*keep*/ //keep";
                int/*gap*/value = a;
                return value;
            }
        }
        """).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

    var result = SourceNormalizer.Normalize(node);

    Assert.Equal(
        "public static int Func(){var a=1;var text=\"/*keep*/ //keep\";int value=a;return value;}",
        result.Text);
    Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(result.Text)), result.Hash);
}

[Fact]
public void Normalize_ExcludesDirectivesAndDisabledTextAndPreservesRawStrings()
{
    const string source = """"
        class C
        {
            string Run()
            {
        #if WINDOWS
                Windows();
        #else
                Other();
        #endif
                return """/*keep*/
        //keep""";
            }
        }
        """";
    var options = new CSharpParseOptions(preprocessorSymbols: ["WINDOWS"]);
    var node = CSharpSyntaxTree.ParseText(source, options).GetRoot()
        .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

    var result = SourceNormalizer.Normalize(node);

    Assert.Contains("Windows();", result.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("Other();", result.Text, StringComparison.Ordinal);
    Assert.Contains("\"\"\"/*keep*/\n//keep\"\"\"", result.Text, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Write RED extraction tests for return types and executable source.**

Analyze one compiling source containing a constructor, ordinary method, operator, conversion, property getter/setter, local function, and lambda. Assert:

```csharp
Assert.Equal("System.Threading.Tasks.Task<System.Int32>", Find("ExecuteAsync").ReturnTypeKey);
Assert.Null(Find(".ctor").ReturnTypeKey);
Assert.Equal("System.Int32", FindLambda().ReturnTypeKey);
Assert.Equal("System.Int32", Find("get_Value").ReturnTypeKey);
Assert.NotNull(Find("ExecuteAsync").NormalizedSource);
Assert.NotNull(FindLambda().NormalizedSourceHash);
```

Also assert accessors are indexed as `IndexedSymbolKind.Method`, their source spans point to the accessor declaration, and the normalized source is the accessor syntax rather than the whole property.

- [ ] **Step 3: Write RED lambda naming tests.**

Use a source with two fields, a property, an event, nested lambdas, and a partial type split across two files. Assert these exact names exist once:

```text
Test.A::<initializer:member1>::<lambda#1>
Test.A::<initializer:member2>::<lambda#1>
Test.A::<initializer:Property>::<lambda#1>
Test.A::<initializer:Changed>::<lambda#1>
Test.A::Run()::<lambda#1>
Test.A::Run()::<lambda#2>
Test.A::Run()::<lambda#3>
```

Assert nested lambda `ContainingSymbolKey` still points to its immediate outer lambda, while all three `Run` names use the method as their display/counter owner. Assert calls inside the nested lambda have that nested lambda as `CallerSymbolKey`.

- [ ] **Step 4: Run the focused tests and confirm RED.**

Run:

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter "FullyQualifiedName~SourceNormalizerTests|FullyQualifiedName~ExecutableSymbolExtractionTests"
```

Expected: compile failures for the new source fields/normalizer and assertion failures for accessor extraction and owner-scoped nested lambda names.

- [ ] **Step 5: Implement the source model and normalizer.**

Add:

```csharp
public sealed record NormalizedSourceData(string Text, byte[] Hash);

public static class SourceNormalizer
{
    public static NormalizedSourceData Normalize(SyntaxNode node);
}
```

Build the output from active descendant tokens. Before appending a token after another token, parse/cache the adjacent token-text pair and add one space when concatenation would not reproduce the same two tokens. Always preserve each token's `Text`. Hash the completed text with `HashUtilities.Sha256(result)`.

Add nullable properties to `SymbolData`:

```csharp
public string? ReturnTypeKey { get; init; }
public string? NormalizedSource { get; init; }
public byte[]? NormalizedSourceHash { get; init; }
```

- [ ] **Step 6: Extract metadata and source for every required executable kind.**

Update `SymbolCanonicalizer.CreateMethod` to set `ReturnTypeKey` except for constructors/static constructors. In `SemanticExtractor`, normalize `BaseMethodDeclarationSyntax`, `AccessorDeclarationSyntax`, `LocalFunctionStatementSyntax`, and every `AnonymousFunctionExpressionSyntax`. Add accessor ownership lookup to `DocumentAnalysisState.FindOwner` so calls inside accessors resolve to the accessor method.

For lambdas, set return type from `IAnonymousFunctionOperation.Symbol.ReturnType`. Do not add normalized source to metadata-only methods created by `EnsureMethod`.

- [ ] **Step 7: Implement initializer displays and owner-scoped lambda numbering.**

Create initializer owner display names as:

```csharp
var display = $"{containingType.DisplayName}::<initializer:{memberName}>";
```

Copy namespace/type fields from the containing type. During source-ordered lambda enumeration, walk the in-memory containing-symbol chain from the immediate owner past lambda symbols to the first non-lambda owner. Use that key for the counter and display prefix, but persist the immediate owner in `ContainingSymbolKey`.

- [ ] **Step 8: Run focused and full Core tests.**

Run:

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter "FullyQualifiedName~SourceNormalizerTests|FullyQualifiedName~ExecutableSymbolExtractionTests"
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj
```

Expected: all focused and Core tests pass with zero warnings.

- [ ] **Step 9: Review and commit Task 1.**

Inspect the diff for source-token corruption, immediate-owner regressions, and unrelated refactors, then commit:

```powershell
rtk git add src/CsIndex.Core tests/CsIndex.Core.Tests
rtk git commit -m "feat(core): extract executable source metadata"
```

---

### Task 2: Persist the deterministic async next hop in the Core snapshot

**Files:**
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Modify: `src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs`
- Modify: `tests/CsIndex.Core.Tests/AsyncInvolvementPropagatorTests.cs`
- Modify: `tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs`

**Interfaces:**
- Consumes: resolved invocation `CallData` and existing async-origin `AsyncRole` flags.
- Produces: nullable `SymbolData.AsyncNextSymbolKey` pointing toward the one selected async origin.
- Invariant: if both symbols have depths, `source.AsyncInvolvementDepth == next.AsyncInvolvementDepth + 1`.

- [ ] **Step 1: Write RED unit tests for next-hop propagation and deterministic ties.**

Build snapshots directly and assert:

```csharp
[Fact]
public void Apply_RecordsOneNextHopAndDoesNotReplaceAnEqualPath()
{
    var snapshot = CreateSnapshot();
    AddMethod(snapshot, "root", AsyncRole.None);
    AddMethod(snapshot, "middle-a", AsyncRole.None);
    AddMethod(snapshot, "middle-b", AsyncRole.None);
    AddMethod(snapshot, "async-a", AsyncRole.DeclaredAsync);
    AddMethod(snapshot, "async-b", AsyncRole.DeclaredAsync);
    AddCall(snapshot, "root", "middle-a");
    AddCall(snapshot, "root", "middle-b");
    AddCall(snapshot, "middle-a", "async-a");
    AddCall(snapshot, "middle-b", "async-b");

    AsyncInvolvementPropagator.Apply(snapshot);

    Assert.Equal(2, snapshot.Symbols["root"].AsyncInvolvementDepth);
    Assert.Equal("middle-a", snapshot.Symbols["root"].AsyncNextSymbolKey);
}

[Fact]
public void Apply_UpdatesNextHopWhenAStrictlyShorterPathIsFound()
{
    var snapshot = CreateSnapshot();
    AddMethod(snapshot, "root", AsyncRole.None);
    AddMethod(snapshot, "bridge", AsyncRole.None);
    AddMethod(snapshot, "far", AsyncRole.DeclaredAsync);
    AddMethod(snapshot, "near", AsyncRole.DeclaredAsync);
    AddCall(snapshot, "root", "bridge");
    AddCall(snapshot, "bridge", "far");
    AddCall(snapshot, "root", "near");

    AsyncInvolvementPropagator.Apply(snapshot);

    Assert.Equal(1, snapshot.Symbols["root"].AsyncInvolvementDepth);
    Assert.Equal("near", snapshot.Symbols["root"].AsyncNextSymbolKey);
}

[Fact]
public void Apply_OriginsHaveNullNextAndCyclesTerminate()
{
    var snapshot = CreateSnapshot();
    AddMethod(snapshot, "a", AsyncRole.None);
    AddMethod(snapshot, "b", AsyncRole.None);
    AddMethod(snapshot, "origin", AsyncRole.DeclaredAsync);
    AddCall(snapshot, "a", "b");
    AddCall(snapshot, "b", "a");
    AddCall(snapshot, "b", "origin");
    AddCall(snapshot, "origin", "origin");

    AsyncInvolvementPropagator.Apply(snapshot);

    Assert.Equal("b", snapshot.Symbols["a"].AsyncNextSymbolKey);
    Assert.Equal("origin", snapshot.Symbols["b"].AsyncNextSymbolKey);
    Assert.Null(snapshot.Symbols["origin"].AsyncNextSymbolKey);
}
```

- [ ] **Step 2: Run the focused test and confirm RED.**

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~AsyncInvolvementPropagatorTests
```

Expected: compile failure because `AsyncNextSymbolKey` does not exist.

- [ ] **Step 3: Implement deterministic adjacency and next-hop assignment.**

Add:

```csharp
public string? AsyncNextSymbolKey { get; init; }
```

Order eligible calls by callee definition key, document key, source start, caller key, and source length before building reverse adjacency. Order async origins by stable key. During BFS, assign `next[caller] = callee` only when first visited or when `candidate < current`; keep the existing value when `candidate == current`.

Write both depth and next key back with one record update. Origins explicitly retain null next keys.

- [ ] **Step 4: Run focused and full Core tests.**

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~AsyncInvolvementPropagatorTests
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj
```

Expected: all tests pass with zero warnings.

- [ ] **Step 5: Review and commit Task 2.**

```powershell
rtk git add src/CsIndex.Core/Model/IndexData.cs src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs tests/CsIndex.Core.Tests
rtk git commit -m "feat(core): record async shortest path hop"
```

---

### Task 3: Upgrade SQLite persistence and readers to schema version 4

**Files:**
- Modify: `src/CsIndex.Core/Caching/RequestHasher.cs`
- Modify: `src/CsIndex.Storage/Schema/SchemaMigrator.cs`
- Modify: `src/CsIndex.Storage/SqliteIndex.cs`
- Modify: `src/CsIndex.Storage/QueryModels.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Modify: `tests/CsIndex.Storage.Tests/SqliteIndexTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs` for constructor-call compilation only when the stored-record shape changes

**Interfaces:**
- Consumes: Task 1 source/return fields and Task 2 async next stable key.
- Produces: schema version 4 columns and `StoredSymbol` fields `MethodKind`, `ReturnTypeKey`, `NormalizedSource`, `NormalizedSourceHash`, and `AsyncNextSymbolId`.
- Produces: stable, profile-scoped symbol reads with numeric ID as the final ordering tie-breaker.

- [ ] **Step 1: Write RED schema and round-trip tests.**

Update version expectations to 4 and add assertions using `PRAGMA table_info(symbols)`/`foreign_key_list(symbols)` for:

```text
return_type_key
normalized_source
normalized_source_hash
async_next_symbol_id
```

Save a snapshot containing a method and async origin, then query it back and assert exact return type, normalized text/hash, method kind, and resolved numeric next ID. Verify a schema version 3 database is rejected without row, object, or journal-mode mutation.

- [ ] **Step 2: Run focused Storage tests and confirm RED.**

```powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --filter FullyQualifiedName~SqliteIndexTests
```

Expected: version assertions and missing-column/record-member assertions fail.

- [ ] **Step 3: Create schema version 4.**

Set `RequestHasher.SchemaVersion = 4`, initialize `schema_info` with 4, and add:

```sql
return_type_key         TEXT,
normalized_source       TEXT,
normalized_source_hash  BLOB,
async_next_symbol_id    INTEGER,

FOREIGN KEY(async_next_symbol_id)
  REFERENCES symbols(id) ON DELETE SET NULL
```

Add an index on `(analysis_profile_id, async_next_symbol_id)` and retain every version 3 table/index. Rename version-specific creation helpers to reflect the current schema without adding ALTER migration code.

- [ ] **Step 4: Persist new values atomically.**

Extend `InsertSymbolAsync` for return/source values. After all symbol IDs exist, add an update pass equivalent to containing-symbol updates:

```csharp
UPDATE symbols
SET async_next_symbol_id = $next_id
WHERE id = $id;
```

Resolve `AsyncNextSymbolKey` only through the current snapshot's stable-key-to-ID map. A missing referenced key is an integrity error and must roll back the save transaction.

- [ ] **Step 5: Extend stored models and all symbol SELECT/read paths.**

Add the new fields and expose the already persisted `method_kind` in `StoredSymbol`. Refactor repeated symbol column lists/read ordinals into one shared projection/helper so `FindSymbolCandidatesAsync`, `GetSymbolsByIdsAsync`, and `FindFunctionSymbolsAsync` cannot drift.

Every symbol order becomes:

```sql
ORDER BY s.display_name, d.normalized_path, s.source_start, s.id
```

- [ ] **Step 6: Run Storage, Query, and Integration tests.**

```powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
```

Expected: all tests pass after existing `StoredSymbol` fixtures are updated with explicit named arguments for the new fields.

- [ ] **Step 7: Review and commit Task 3.**

```powershell
rtk git add src/CsIndex.Core/Caching/RequestHasher.cs src/CsIndex.Storage tests
rtk git commit -m "feat(storage): persist executable source and async paths"
```

---

### Task 4: Add symbol pattern search and standalone source queries

**Files:**
- Create: `src/CsIndex.Query/Symbols/SymbolSearchRequest.cs`
- Create: `src/CsIndex.Query/Symbols/SymbolPatternMatcher.cs`
- Create: `src/CsIndex.Query/Symbols/SourceTextFilter.cs`
- Modify: `src/CsIndex.Query/QueryResults.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Create: `tests/CsIndex.Query.Tests/SymbolPatternMatcherTests.cs`
- Create: `tests/CsIndex.Query.Tests/SourceTextFilterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs`
- Create: `tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs`

**Interfaces:**
- Consumes: schema v4 `StoredSymbol` source/metadata fields and the existing exact `FindSymbolsAsync` path.
- Produces: `SymbolSearchRequest`, `SearchSymbolsAsync`, `ShowSourceAsync`, and `SearchSourceAsync`.
- Preserves: existing exact parser/resolver for all existing commands and `--include-overrides` behavior.

- [ ] **Step 1: Write RED pure matcher tests.**

Define and test this request shape:

```csharp
public sealed record SymbolSearchRequest(
    string? Pattern,
    string? NamespacePattern,
    string? TypePattern,
    string? MethodPattern,
    IndexedSymbolKind? Kind,
    bool UseRegex,
    bool IgnoreCase,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes,
    bool ShowSource);
```

Tests cover `*.Gamer::Play`, `Tokyo.*::Play`, `Tokyo.Gamer::P*l*y`, omitted-parameter overload matching, full parameter matching, component AND semantics, the anchored regex from the requirements, culture invariance, invalid regex, forced short timeout, lambda suffix `::<lambda#1>`, owner suffix `Function()::<lambda#2>`, and full lambda name.

- [ ] **Step 2: Write RED source-filter tests proving exclude-first short circuit.**

Give `SourceTextFilter` an internal test seam for its contains predicate and assert no include predicate is invoked after an exclude matches:

```csharp
Assert.False(SourceTextFilter.IsMatch(
    source,
    includes: ["required"],
    excludes: ["blocked"],
    comparison: StringComparison.Ordinal,
    contains: ProbeContains));
Assert.DoesNotContain("required", evaluatedTerms);
```

Also cover include AND, exclude OR, include-only, exclude-only, and ignore-case behavior.

- [ ] **Step 3: Run Query unit tests and confirm RED.**

```powershell
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj --filter "FullyQualifiedName~SymbolPatternMatcherTests|FullyQualifiedName~SourceTextFilterTests"
```

Expected: compile failures for the new request/matcher/filter types.

- [ ] **Step 4: Implement bounded pattern and source matchers.**

`SymbolPatternMatcher` compiles regexes with `RegexOptions.CultureInvariant` plus optional `IgnoreCase`, and a two-second production timeout. Wildcard mode escapes every character and translates only `*` to `.*`, anchoring the result. Match method patterns against both full `DisplayName` and a parameter-list-free method display. Regex `*` remains regex syntax in regex mode.

`SourceTextFilter` evaluates excludes first with `Any`, then includes with `All`, using `Ordinal` or `OrdinalIgnoreCase`.

- [ ] **Step 5: Add profile-scoped candidate reads and service APIs.**

Add a repository method that returns source/function candidates without Roslyn:

```csharp
Task<IReadOnlyList<StoredSymbol>> FindExecutableSymbolsAsync(
    long profileId,
    bool sourceOnly,
    CancellationToken cancellationToken = default);
```

Add service methods:

```csharp
Task<QueryContext> SearchSymbolsAsync(
    SymbolSearchRequest request,
    string? profileName = null,
    CancellationToken cancellationToken = default);

Task<QueryContext> ShowSourceAsync(
    string queryText,
    string? profileName = null,
    CancellationToken cancellationToken = default);

Task<QueryContext> SearchSourceAsync(
    IReadOnlyList<string> includes,
    IReadOnlyList<string> excludes,
    bool ignoreCase,
    string? profileName = null,
    CancellationToken cancellationToken = default);
```

Use the existing exact `FindSymbolsAsync` path only when a positional non-lambda request has no wildcard, regex, component filters, source filters, or kind override. Otherwise filter stored canonical fields. Source filters remove source-less symbols. `ShowSourceAsync` returns only source-backed executable symbols.

- [ ] **Step 6: Add integration fixtures and RED/GREEN service tests.**

Extend the fixture with overloaded methods, nested lambdas, normalized comments/literals, field/property/event initializer lambdas, and matching/non-matching source bodies. Test exact compatibility, wildcard/component/regex results, lambda suffixes, name-plus-source filters, source show, standalone source search, source-less exclusion, profile isolation, and deterministic ordering.

Run:

```powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~SymbolSourceQueryTests
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
```

Expected: all pass with zero warnings.

- [ ] **Step 7: Review and commit Task 4.**

```powershell
rtk git add src/CsIndex.Query src/CsIndex.Storage/QueryRepository.cs tests
rtk git commit -m "feat(query): search symbols and normalized source"
```

---

### Task 5: Add async-path and bounded caller-tree query services

**Files:**
- Modify: `src/CsIndex.Query/QueryResults.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Create: `src/CsIndex.Query/AsyncPathResolver.cs`
- Create: `src/CsIndex.Query/CallerTreeBuilder.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs` only if a focused multi-ID read is required
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs`
- Create: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`

**Interfaces:**
- Consumes: exact source method resolution, `StoredSymbol.AsyncNextSymbolId`, and existing `GetCallsByCalleeAsync`/`GetSymbolsByIdsAsync`.
- Produces: `AsyncPathResult` and `CallerTreeResult` with explicit nodes, edges, `Found`, and `Truncated` state.
- Caller-tree edges always point from caller to callee.

Service signatures are:

```csharp
Task<AsyncPathResult> FindAsyncPathAsync(
    string queryText,
    int maxNodes = 500,
    string? profileName = null,
    CancellationToken cancellationToken = default);

Task<CallerTreeResult> FindCallerTreeAsync(
    string queryText,
    int depth = 3,
    int maxNodes = 500,
    string? profileName = null,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 1: Write RED async-path integration tests.**

Add fixture methods for self-async, a three-node chain, equal shortest routes, unreachable sync code, a cycle reaching async, and deliberately corrupt DB rows. Assert:

```csharp
var result = await fixture.Query.FindAsyncPathAsync("Alpha.AsyncGraph::Start()", 500);
Assert.Equal(
    ["Alpha.AsyncGraph::Start()", "Alpha.AsyncGraph::Middle()", "Alpha.AsyncGraph::EndAsync()"],
    result.Nodes.Select(node => node.DisplayName));
Assert.False(result.Truncated);
```

Assert the equal-route result follows persisted `AsyncNextSymbolId`, not a query-time re-sort. Assert self-async has one node, unreachable has `Found == false`, truncation sets `Truncated`, and corrupt/cyclic next IDs terminate with a focused exception.

- [ ] **Step 2: Write RED caller-tree integration tests.**

Add direct, transitive, mutual-recursive, lambda, and `System`/metadata calls. Assert depth zero means unbounded, default test depth three stops correctly, max nodes counts the root, cycle edges remain, node IDs are unique, external symbols are absent, and a lambda-to-target call has the lambda as caller without a synthesized containing-method edge.

- [ ] **Step 3: Run the focused tests and confirm RED.**

```powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~GraphQueryTests
```

Expected: compile failures for the graph result/service APIs.

- [ ] **Step 4: Implement async path reconstruction.**

Add:

```csharp
public sealed record AsyncPathResult(
    StoredProfile Profile,
    StoredSymbol Root,
    IReadOnlyList<StoredSymbol> Nodes,
    bool Found,
    bool Truncated);
```

Resolve exactly one source-backed method. If root depth is null, return `Found == false`. Otherwise iteratively fetch/follow `AsyncNextSymbolId`, checking profile membership, visited IDs, decreasing depth, and max nodes. A depth-zero root ends with null next. Any other missing/null/mismatched hop throws a focused `IndexDatabaseException` or query integrity exception.

- [ ] **Step 5: Implement breadth-first caller-tree construction.**

Add:

```csharp
public sealed record CallerTreeNode(StoredSymbol Symbol, int Depth);
public sealed record CallerTreeEdge(long CallerSymbolId, long CalleeSymbolId);
public sealed record CallerTreeResult(
    StoredProfile Profile,
    StoredSymbol Root,
    IReadOnlyList<CallerTreeNode> Nodes,
    IReadOnlyList<CallerTreeEdge> Edges,
    bool Truncated);
```

Use a queue of `(symbolId, depth)`. Query invocation/object-creation callers for each frontier or batched depth. Load caller symbols, keep only source-backed symbols outside `System`/`System.*`, sort by display/path/start/ID, add every edge whose endpoints are included, and enqueue an unseen caller only within depth/node limits. `depth == 0` means no depth limit. Never traverse containing-symbol ownership.

- [ ] **Step 6: Run focused and complete query/integration tests.**

```powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~GraphQueryTests
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
```

Expected: all pass with zero warnings.

- [ ] **Step 7: Review and commit Task 5.**

```powershell
rtk git add src/CsIndex.Query src/CsIndex.Storage/QueryRepository.cs tests/CsIndex.IntegrationTests
rtk git commit -m "feat(query): resolve async paths and caller trees"
```

---

### Task 6: Expose the new CLI commands and output formats

**Files:**
- Modify: `src/CsIndex.Cli/CliArguments.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `src/CsIndex.Cli/SymbolNameShortener.cs`
- Create: `src/CsIndex.Cli/SymbolSignatureFormatter.cs`
- Create: `src/CsIndex.Cli/GraphOutputFormatter.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`

**Interfaces:**
- Consumes: Tasks 4 and 5 service/result APIs.
- Produces: `symbol find` pattern/source options, `async tree`, `callers tree`, `source show`, and `source search` command routes.
- Produces: tree/line/JSON async output and tree/Mermaid/JSON caller output.

- [ ] **Step 1: Write RED parser and command-validation tests.**

Add tests for repeatable `--include`/`--exclude`, flags `--regex`, `--ignore-case`, `--show-source`, component options, `--kind method|lambda`, and nested command dispatch. Test invalid regex, source search without conditions, negative depth, zero max nodes, unknown output values, missing/ambiguous roots, and unsupported options.

Expected command forms:

```text
csindex symbol find <pattern> [--namespace <p>] [--type <p>] [--method <p>]
    [--kind method|lambda] [--regex] [--include <text>] [--exclude <text>]
    [--ignore-case] [--show-source]
csindex async tree <symbol> [--output tree|line|json] [--max-nodes 500]
csindex callers tree <symbol> [--depth 3] [--max-nodes 500]
    [--output tree|mermaid|json]
csindex source show <symbol> [--output table|json]
csindex source search (--include <text> | --exclude <text>)...
    [--ignore-case] [--output table|json]
```

- [ ] **Step 2: Write RED output-formatter tests.**

Assert table symbol lines use this ordering:

```text
public static async Task<Int32> Gamer::Play(String,CancellationToken)
```

Assert JSON keeps canonical return/parameter fields while `displayName`/signature honors `--short-names`; `normalizedSource` appears only when requested. Assert async tree indentation, exact line separator, JSON `found`/`truncated`/nodes, Mermaid `flowchart TD`, ID-based nodes, escaping, caller-to-callee arrows, and truncation comments.

- [ ] **Step 3: Run focused CLI tests and confirm RED.**

```powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter "FullyQualifiedName~CliCommandTests|FullyQualifiedName~OutputFormatterTests"
```

Expected: unknown-command/option failures and missing output APIs.

- [ ] **Step 4: Extend argument parsing and dispatch.**

Add the three new flags to `CliArguments.Flags`; keep component/source options repeatable through existing `GetMany`. Route nested commands before the existing flat `callers` route:

```csharp
"async" when args.Length > 1 && args[1] == "tree" => RunAsyncTreeAsync(args[2..], token),
"callers" when args.Length > 1 && args[1] == "tree" => RunCallerTreeAsync(args[2..], token),
"source" when args.Length > 1 && args[1] == "show" => RunSourceShowAsync(args[2..], token),
"source" when args.Length > 1 && args[1] == "search" => RunSourceSearchAsync(args[2..], token),
```

Construct `SymbolSearchRequest` in `RunSymbolAsync`. Require a positional pattern or at least one namespace/type/method condition. Standalone source search rejects empty include+exclude lists; symbol find does not.

- [ ] **Step 5: Implement signature and graph presentation.**

Keep canonical fields in `OutputFormatter`; move executable declaration text into `SymbolSignatureFormatter`. It maps `IndexedAccessibility`, static/async flags, return type, display name, and parameter types without inventing modifiers for not-applicable values.

`GraphOutputFormatter` emits:

- async tree with `└─` indentation;
- async line with exact ` -> ` separators and no header;
- async JSON with structured symbol objects;
- caller text tree;
- Mermaid with `n{symbolId}` IDs, escaped labels, unique edges, and a truncation comment;
- caller JSON with node depth and edge IDs.

Caller-tree output defaults to `tree`. An async query with no reachable origin emits `No reachable asynchronous function: <root>` in tree and line modes, and JSON with `found: false`, the root object, and an empty `nodes` array. A truncated async path keeps `found: true`, sets `truncated: true`, and appends `<truncated>` to tree/line output after the final included symbol.

- [ ] **Step 6: Implement command help and integration behavior.**

Update global and command-specific help. Add CLI acceptance tests for lambda suffixes, wildcard/component/regex searches, name-plus-source filtering, source display/search, self/unreachable/truncated async paths, caller depth/cycles/lambdas/external exclusion, short names, table/tree/line/JSON/Mermaid, and all validation failures.

- [ ] **Step 7: Run Integration and full solution tests.**

```powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
rtk dotnet test CsIndex.sln --configuration Release
```

Expected: all tests pass with zero warnings.

- [ ] **Step 8: Review and commit Task 6.**

```powershell
rtk git add src/CsIndex.Cli tests/CsIndex.IntegrationTests
rtk git commit -m "feat(cli): expose source and graph queries"
```

---

### Task 7: Update durable documentation and complete acceptance verification

**Files:**
- Modify: `docs/SPEC.md`
- Modify: `docs/CLI.md`
- Modify: `docs/DB_SCHEMA.md`
- Modify: `docs/DECISIONS.md`
- Modify: `docs/TEST_PLAN.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`
- Modify: `docs/KNOWN_LIMITATIONS.md` only for limitations changed or introduced by this feature
- Do not change the formal requirements in `docs/SPEC.md` without an explicit product decision.

**Interfaces:**
- Consumes: the actual implemented CLI, schema, and verified behavior from Tasks 1-6.
- Produces: durable documentation matching the shipped implementation and a complete acceptance evidence record.

- [ ] **Step 1: Update schema and decision documentation.**

Document version 4 columns, indexes, self-foreign-key update order, version 3 rejection, normalized-source storage, deterministic single async next hop, function-scoped lambda numbering, source filtering, wildcard/regex semantics, and caller-tree limits. Add accepted decisions after DEC-0019 rather than rewriting historical decisions; explicitly supersede the nested-lambda numbering part of DEC-0018.

- [ ] **Step 2: Update CLI, specification, status, limitations, and test plan.**

Include every exact command/option/default/output mode and validation rule. State that caller trees do not follow delegate invocation and that arbitrary normalized-source substring search may scan source-backed functions. Map every section 17 acceptance bullet in the authoritative requirements to a named automated test or verification command.

- [ ] **Step 3: Run focused project tests freshly.**

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --configuration Release
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --configuration Release
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj --configuration Release
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --configuration Release
```

Expected: every project passes with zero warnings and zero failures.

- [ ] **Step 4: Run full Release verification.**

```powershell
rtk dotnet test CsIndex.sln --configuration Release
rtk dotnet build CsIndex.sln --configuration Release
rtk git diff --check
rtk git status --short
```

Expected: complete suite passes, build reports zero warnings/errors, diff check is clean, and status contains only the intended documentation changes before commit.

- [ ] **Step 5: Perform an independent final review.**

Review the complete implementation against every authoritative requirement, with special attention to schema rejection, source token correctness, exact-query regression, equal-distance async ties, graph cycles/limits, lambda ownership, profile isolation, deterministic ordering, and canonical-versus-short output. Return every Critical or Important finding to the implementing agent, add a focused RED regression test, fix it, and rerun the affected and full verification commands.

- [ ] **Step 6: Commit documentation and final fixes.**

```powershell
rtk git add docs src tests
rtk git commit -m "docs: complete symbol source graph expansion"
```

- [ ] **Step 7: Confirm final hygiene.**

```powershell
rtk git status --short
rtk git log -8 --oneline
```

Expected: tracked worktree is clean and the task commits are present in dependency order.
