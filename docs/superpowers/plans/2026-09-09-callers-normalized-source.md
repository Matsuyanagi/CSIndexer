# Callers Normalized Call-Site Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist one normalized source payload per indexed document, retain normalized ranges for declarations and calls, and expose exact normalized invocation/object-creation expressions from `callers --show-source` and `callers tree --show-source` without query-time source reconstruction.

**Architecture:** `SourceNormalizer` emits a document-wide token stream and an immutable original-token-to-normalized-offset map. Index snapshots and schema version 6 store the shared document payload plus UTF-16 ranges; query hydration loads each distinct payload once and slices in C#, preserving declaration-scoped source filters and attaching call-site text only when requested. Ordinary caller output consumes hydrated `StoredCall` rows, while caller tree keeps structural edges separate from ordered physical call-site associations.

**Tech Stack:** C# 14 / .NET 10, Roslyn syntax and semantic APIs, Microsoft.Data.Sqlite, xUnit v3, RTK-prefixed .NET and Git commands.

**Spec:** `docs/superpowers/specs/2026-09-09-callers-normalized-source-design.md`

## Global Constraints

- Schema version is exactly 6; analysis cache version is exactly 4.
- Create schema 6 directly. Do not migrate, dual-read, dual-write, or fall back from schema 5 or older.
- Persist one normalized source row per unique SHA-256/text pair and reference it from documents; declarations and calls persist only normalized UTF-16 ranges.
- On a SHA-256 conflict, require ordinal text equality and fail atomically if the text differs.
- Normalize each indexed syntax tree once and use `SourceNormalizer` as the sole token-emission implementation.
- A node slice must equal the existing per-node normalization byte-for-byte, excluding separators introduced only by tokens outside the node.
- Never use SQLite `substr` for normalized ranges. Bounds-check and slice .NET strings in C# UTF-16 code units.
- Preserve declaration-scoped `symbol find --include` / `--exclude`: every include and every file/source condition must pass on one physical declaration, and any matching exclude vetoes that declaration.
- Load shared normalized text only for source predicates or explicit source output; file-only and no-source queries must not hydrate it.
- `--show-source` is newly accepted only by `callers` and `callers tree`; the existing `symbol find` scope remains unchanged.
- Without `--show-source`, callers and caller-tree result ordering, JSON fields, table/tree/Mermaid output, and bytes remain unchanged.
- Caller tree retains one structural edge per caller/callee pair and every physical call site associated with retained spanning, cycle, cross, and depth-boundary edges.
- JSON returns exact normalized source. Table/tree source passes through `TableTextSanitizer`; Mermaid labels also escape `|`.
- Query-time normalized-source retrieval must not read/reparse source or reconstruct expressions from another field.
- Existing original-span location formatting remains unchanged and may read files to resolve line/column; line/column persistence is out of scope.
- Preserve cancellation and output-file atomicity at all new loops and failure points.
- Follow RED-GREEN-REFACTOR. Do not edit production code for a task before its focused RED has been run and recorded in the task report.
- Use one implementation agent at a time. Attempt `gpt-5.6-luna` with `max` first for each model-selection cycle; use the single `gpt-5.6-terra`/`max` fallback only for an explicit Luna availability or usage-limit failure.

---

## File and Responsibility Map

- Create `src/CsIndex.Core/Analysis/NormalizedSourceDocument.cs`: immutable normalized document, emitted-token offsets, range lookup, bounds-checked slicing.
- Modify `src/CsIndex.Core/Analysis/SourceNormalizer.cs`: share one cancellable token-emission routine between legacy node normalization and document normalization.
- Modify `src/CsIndex.Core/Analysis/AnalysisState.cs`: retain one `NormalizedSourceDocument` per indexed Roslyn document.
- Modify `src/CsIndex.Core/Analysis/SemanticExtractor.cs`: build document normalization once and assign ranges to every physical declaration and persisted call.
- Modify `src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs`: remove the pre-declaration compatibility fallback.
- Modify `src/CsIndex.Core/Model/IndexData.cs`: move normalized text/hash to `DocumentData`; replace declaration source payloads and add call ranges.
- Modify `src/CsIndex.Core/Caching/RequestHasher.cs`: set schema/cache versions to 6/4.
- Modify `src/CsIndex.Storage/Schema/SchemaMigrator.cs`: define only schema 6 with `normalized_sources`, document FK, and range columns.
- Modify `src/CsIndex.Storage/SqliteIndex.cs`: validate, deduplicate, persist, collision-check, and garbage-collect normalized payloads transactionally.
- Modify `src/CsIndex.Storage/QueryModels.cs`: expose normalized range metadata and optional hydrated source on declaration/call DTOs.
- Modify `src/CsIndex.Storage/QueryRepository.cs`: load distinct normalized document payloads once, validate/slice in C#, and avoid text reads when not requested.
- Modify `src/CsIndex.Query/QueryResults.cs`: carry `ShowSource` and caller-tree physical call-site associations.
- Modify `src/CsIndex.Query/SemanticQueryService.cs`: thread explicit source-hydration intent through callers and caller-tree queries.
- Modify `src/CsIndex.Query/CallerTreeBuilder.cs`: preserve each eligible physical call before structural-edge deduplication.
- Modify `src/CsIndex.Query/Symbols/SymbolCanonicalComparer.cs`: order call-site associations after structural edge ordering.
- Modify `src/CsIndex.Cli/Program.cs`: accept, document, and pass `--show-source` in exactly the two new command scopes.
- Modify `src/CsIndex.Cli/OutputFormatter.cs`: conditionally add call source to table and JSON.
- Modify `src/CsIndex.Cli/GraphOutputFormatter.cs`: conditionally render physical call sites in tree, Mermaid, and JSON.
- Modify focused Core, Storage, Query, and Integration test files named in each task.
- Modify `docs/CLI.md`, `docs/DB_SCHEMA.md`, `docs/SPEC.md`, `docs/IMPLEMENTATION_STATUS.md`, and `docs/TEST_PLAN.md`: record the shipped behavior and schema.

## Recovery and Resume Contract

The tracked spec and this tracked plan are the authoritative implementation record. Execution occurs on branch `codex/callers-normalized-source` in `E:/WorksDevelop/CSIndexer/.worktrees/callers-normalized-source`, forked from commit `86f884c` on 2026-09-09.

Create the ignored plan workspace with the Superpowers `sdd-workspace` helper. Its ledger is:

```text
.superpowers/sdd/2026-09-09-callers-normalized-source/progress.md
```

The first ledger line must be exactly:

```text
# SDD ledger - plan: docs/superpowers/plans/2026-09-09-callers-normalized-source.md
```

Before Task 1, append the branch, worktree, fork SHA, baseline restore/test results, a task/interface preflight table covering every task and every file/interface-sharing pair, and `Task 1: pending`. After each RED, GREEN, commit, review, fix round, ruling, or model fallback, append the exact command, exit code, pass/fail count, commit SHA, and report path before moving on.

After context compaction or a usage-limit stop:

1. Read this plan, its design spec, and only this plan's ledger.
2. Run `rtk git status --short --branch` and `rtk git log -8 --oneline --decorate` in the recorded worktree.
3. Treat a task as finished only when its ledger contains `Task N: complete` with an approved task review and commit SHA.
4. If RED exists without GREEN, resume production implementation. If GREEN exists without a review verdict, generate or reuse that task's review package and resume review. If a fix round is recorded, continue from its next recorded action.
5. At a usage-limit recovery boundary, start a new model-selection cycle: retry Luna/max first, then the one Terra/max fallback allowed by `AGENTS.md`, using the existing brief and report paths unchanged.
6. Trust committed history and fresh command output over conversational recollection. Never restart completed tasks.

Baseline already recorded before plan execution:

```text
rtk dotnet restore CsIndex.sln
Result: 9 projects restored; 0 errors; 0 warnings.

rtk dotnet test CsIndex.sln -c Release --no-restore
Result: 1194 tests passed; 0 warnings in 4 projects; exit 0.
```

## Acceptance Matrix

| Surface | Without `--show-source` | With `--show-source` |
|---|---|---|
| `symbol find --include/--exclude` | declaration-scoped matching unchanged | existing symbol-source presentation unchanged |
| `callers` table | byte-compatible current call lines | final TAB plus sanitized invocation/object-creation slice |
| `callers` JSON | no `normalizedSource` property | exact `normalizedSource` on every call object |
| `callers tree` text | byte-compatible current tree | ordered `@ location<TAB>source` lines per retained structural edge |
| `callers tree` Mermaid | byte-compatible current graph | one edge label containing all ordered sites separated by `<br/>` |
| `callers tree` JSON | exact two-field edge objects | each edge additionally owns ordered `callSites` |
| file-only query | no normalized text hydration | not applicable |
| source-filter query | hydrate each distinct shared payload once | same declaration-slice predicate semantics |

---

### Task 1: Build the Document Normalization and Range Map

**Files:**
- Create: `src/CsIndex.Core/Analysis/NormalizedSourceDocument.cs`
- Modify: `src/CsIndex.Core/Analysis/SourceNormalizer.cs`
- Modify: `tests/CsIndex.Core.Tests/SourceNormalizerTests.cs`

**Interfaces:**
- Consumes: Roslyn `SyntaxNode`, `SyntaxToken`, and `TextSpan`; `HashUtilities.Sha256(string)`; the current separator re-lexing rules in `SourceNormalizer`.
- Produces:

```csharp
public readonly record struct NormalizedSourceRange(int Start, int Length);

public sealed class NormalizedSourceDocument
{
    public string Text { get; }
    public byte[] Hash { get; }
    public NormalizedSourceRange GetRange(SyntaxNode node);
    public string Slice(NormalizedSourceRange range);
}

public static NormalizedSourceDocument SourceNormalizer.NormalizeDocument(
    SyntaxNode root,
    CancellationToken cancellationToken = default);

internal static NormalizedSourceDocument SourceNormalizer.NormalizeDocumentForTesting(
    SyntaxNode root,
    CancellationToken cancellationToken,
    Action? afterTokenMapped);
```

- `GetRange` accepts only nodes from the normalized root's syntax tree, selects the first and last emitted descendant token, excludes a separator inserted before the first token due only to an outside predecessor, and throws `InvalidOperationException` when no real token was emitted.
- `Slice` uses `string.AsSpan(range.Start, range.Length).ToString()` after checked nonnegative/positive/end bounds and throws `ArgumentOutOfRangeException` for an invalid caller-supplied range.
- `SourceNormalizer.Normalize(SyntaxNode)` and `NormalizeDocument(SyntaxNode)` call the same private token-emission loop. No second normalization policy is permitted.

- [ ] **Step 1: Add failing equivalence, overlap, Unicode, boundary, validation, and cancellation tests**

Add a fixture containing a method, accessor, initializer, local function, lambda, anonymous method, nested invocations, object creation, raw/interpolated strings, an emoji surrogate pair, ASCII/Japanese comments, and top-level statements. Compare document slices against the established node API rather than copying expected whitespace policy:

```csharp
[Fact]
public void NormalizeDocument_EveryIndexedNodeSliceEqualsPerNodeNormalization()
{
    var root = CSharpSyntaxTree.ParseText(SourceFixture).GetRoot();
    var document = SourceNormalizer.NormalizeDocument(root);
    var nodes = root.DescendantNodesAndSelf().Where(node => node is
        BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or
        EqualsValueClauseSyntax or LocalFunctionStatementSyntax or
        AnonymousFunctionExpressionSyntax or GlobalStatementSyntax or
        InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax);

    foreach (var node in nodes)
    {
        var range = document.GetRange(node);
        Assert.Equal(SourceNormalizer.Normalize(node).Text, document.Slice(range));
        Assert.True(range.Start >= 0);
        Assert.True(range.Length > 0);
        Assert.True(range.Start + range.Length <= document.Text.Length);
    }
}
```

Add explicit assertions that `B(A(f), A(10 + 20))` yields independent `A(f)`, `A(10+20)`, and `B(A(f),A(10+20))` slices, and that none includes the statement semicolon. Add `return M();` and assert the invocation slice is exactly `M()` rather than ` M()`, proving the outside separator is excluded. Assert the full document keeps literal token text such as `"日本語😀"`, a raw string, and an interpolated string while both comments disappear.

Use a node from another syntax tree and a fabricated empty/missing-token node to assert focused `InvalidOperationException` failures. Exercise `Slice` with negative start, zero length, overflowed end, and an end beyond `Text.Length`.

Add an internal cancellation seam to the common emitter and prove cancellation after the first mapped token:

```csharp
[Fact]
public void NormalizeDocument_CancellationAfterMappedTokenStopsBeforeCompletion()
{
    using var source = new CancellationTokenSource();
    var root = CSharpSyntaxTree.ParseText("class C { int M() => 1 + 2; }").GetRoot();

    Assert.Throws<OperationCanceledException>(() =>
        SourceNormalizer.NormalizeDocumentForTesting(
            root,
            source.Token,
            afterTokenMapped: source.Cancel));
}
```

- [ ] **Step 2: Run the focused tests and record the expected RED**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SourceNormalizerTests"
```

Expected: the new tests fail to compile because `NormalizedSourceDocument`, `NormalizedSourceRange`, and `NormalizeDocument` do not exist; all pre-existing normalizer cases remain unchanged. Record the exact compiler diagnostics and exit code before editing production files.

- [ ] **Step 3: Implement one shared cancellable token emitter**

Create an internal immutable emitted-token entry and store it only inside `NormalizedSourceDocument`:

```csharp
internal readonly record struct NormalizedTokenSpan(
    SyntaxTree SyntaxTree,
    TextSpan OriginalSpan,
    int RawKind,
    int NormalizedStart,
    int NormalizedLength)
{
    internal int NormalizedEnd => checked(NormalizedStart + NormalizedLength);
}
```

Refactor `SourceNormalizer` around a private emitter shaped as follows. Preserve `RequiresSeparator` and interpolated-string special handling exactly:

```csharp
private static NormalizedEmission EmitTokens(
    IEnumerable<SyntaxToken> tokens,
    CancellationToken cancellationToken,
    bool captureTokenSpans,
    Action? afterPairRelex,
    Action? afterTokenMapped)
{
    var builder = new StringBuilder();
    var spans = captureTokenSpans ? new List<NormalizedTokenSpan>() : null;
    SyntaxToken? previous = null;
    foreach (var token in tokens)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (token.IsMissing || token.Text.Length == 0)
        {
            continue;
        }

        if (previous is { } preceding &&
            RequiresSeparator(preceding, token, cancellationToken, afterPairRelex))
        {
            builder.Append(' ');
        }

        var start = builder.Length;
        builder.Append(token.Text);
        spans?.Add(new NormalizedTokenSpan(
            token.SyntaxTree,
            token.Span,
            token.RawKind,
            start,
            token.Text.Length));
        previous = token;
        afterTokenMapped?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }

    var text = builder.ToString();
    return new NormalizedEmission(text, HashUtilities.Sha256(text), spans ?? []);
}
```

`Normalize` projects `NormalizedSourceData` from this result with `captureTokenSpans: false`; `NormalizeDocument` constructs `NormalizedSourceDocument` with `captureTokenSpans: true`. Implement range lookup by matching the node's first and last nonmissing emitted descendant tokens to entries from the same syntax tree and exact original span/raw kind. Use checked arithmetic for all end calculations.

- [ ] **Step 4: Run focused and Core regression tests**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SourceNormalizerTests"
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore
```

Expected: all `SourceNormalizerTests` pass; the complete Core test project passes with zero warnings. If the shared emitter changes any pre-existing normalized string, treat it as a production regression and correct the emitter rather than updating the old expected value.

- [ ] **Step 5: Inspect and commit Task 1**

Run `rtk git diff --check` and inspect the exact Task 1 diff. Commit only the three Task 1 files:

```powershell
rtk git add src\CsIndex.Core\Analysis\NormalizedSourceDocument.cs src\CsIndex.Core\Analysis\SourceNormalizer.cs tests\CsIndex.Core.Tests\SourceNormalizerTests.cs
rtk git commit -m "feat: add normalized document source ranges"
```

Record the GREEN commands, commit SHA, task report, and review verdict in the ledger before Task 2.

---

### Task 2: Replace Declaration Payload Duplication with Schema 6 Document Ranges

**Files:**
- Modify: `src/CsIndex.Core/Analysis/AnalysisState.cs`
- Modify: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`
- Modify: `src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs`
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Modify: `src/CsIndex.Core/Caching/RequestHasher.cs`
- Modify: `src/CsIndex.Storage/Schema/SchemaMigrator.cs`
- Modify: `src/CsIndex.Storage/SqliteIndex.cs`
- Modify: `src/CsIndex.Storage/QueryModels.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Modify: `src/CsIndex.Query/CallerTreeBuilder.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Modify: `tests/CsIndex.Core.Tests/RequestHasherTests.cs`
- Modify: `tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs`
- Modify: `tests/CsIndex.Core.Tests/LogicalDeclarationExtractionTests.cs`
- Modify: `tests/CsIndex.Core.Tests/AsyncInvolvementPropagatorTests.cs`
- Modify: `tests/CsIndex.Core.Tests/CallablePathExtractionTests.cs`
- Modify: `tests/CsIndex.Core.Tests/PortableAnalysisPathTests.cs`
- Modify: `tests/CsIndex.Core.Tests/ProjectScopedSourceSymbolIdentityTests.cs`
- Modify: `tests/CsIndex.Storage.Tests/SqliteIndexTests.cs`
- Modify: `tests/CsIndex.Storage.Tests/SchemaFiveLogicalSymbolTests.cs`
- Modify: `tests/CsIndex.Storage.Tests/PortablePathPersistenceTests.cs`
- Modify: `tests/CsIndex.Query.Tests/CallerTreeBuilderTests.cs`
- Modify: `tests/CsIndex.Query.Tests/StructuralGlobMatcherTests.cs`
- Modify: `tests/CsIndex.Query.Tests/SymbolCanonicalComparerTests.cs`
- Modify: `tests/CsIndex.Query.Tests/TypedConditionCompilerTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SymbolPathResolverTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CompilationOnlyGeneratedDocumentPersistenceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/RootSelectionOrchestrationTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/PortableIndexAcceptanceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs`

The fourteen additional fixture files above are compatibility call sites of the
Task 2 DTO replacement, not compatibility behavior. Update them only as needed
to construct or assert the exact Schema 6 document/range shape; do not restore
removed per-symbol or per-declaration normalized-source payload fields. If a
fresh project compile or layer test exposes another direct test-fixture/schema
assertion call site of the same removed DTO members, changed constructors, or
new required range columns, it is also in Task 2 scope under the same
mechanical-only constraint and must be recorded in the task report.

**Interfaces:**
- Consumes: Task 1 `NormalizedSourceDocument`, `NormalizedSourceRange`, and `SourceNormalizer.NormalizeDocument`.
- Produces the exact snapshot shape:

```csharp
public sealed record DocumentData
{
    // existing fields remain
    public required string NormalizedSource { get; init; }
    public required byte[] NormalizedSourceHash { get; init; }
}

public sealed record SymbolDeclarationData
{
    // existing key, symbol/document, role, original span, generation fields remain
    public required int NormalizedStart { get; init; }
    public required int NormalizedLength { get; init; }
}

public sealed record CallData
{
    // existing endpoint, kind, document, original span, unresolved fields remain
    public required int NormalizedStart { get; init; }
    public required int NormalizedLength { get; init; }
}
```

- Remove `SymbolDeclarationData.NormalizedSource`, `SymbolDeclarationData.NormalizedSourceHash`, `SymbolData.NormalizedSource`, and `SymbolData.NormalizedSourceHash` rather than retaining compatibility members.
- `DocumentAnalysisState` gains `public required NormalizedSourceDocument NormalizedSource { get; init; }`.
- Produces the exact query DTO shape:

```csharp
public sealed record StoredDeclaration(
    long Id,
    string DeclarationKey,
    long SymbolId,
    long DocumentId,
    string DocumentPath,
    DeclarationRole Role,
    int SourceStart,
    int SourceLength,
    int NormalizedStart,
    int NormalizedLength,
    string? NormalizedSource,
    bool IsGenerated);

public sealed record StoredCall(
    long Id,
    long CallerSymbolId,
    long? CallerContainingSymbolId,
    long? CalleeSymbolId,
    long? CalleeDefinitionId,
    ReferenceKind ReferenceKind,
    DispatchKind DispatchKind,
    ResolutionStatus ResolutionStatus,
    ResolutionReason ResolutionReason,
    AsyncUsageKind AsyncUsageKind,
    long DocumentId,
    string DocumentPath,
    int SourceStart,
    int SourceLength,
    int NormalizedStart,
    int NormalizedLength,
    string? NormalizedSource,
    bool IsGenerated,
    string? UnresolvedName,
    string? ReceiverTypeKey);
```

- Keep `GetDeclarationsAsync(..., bool includeSourceText, ...)`; it always reads range metadata and materializes `NormalizedSource` only when true.
- Add `bool includeSourceText = false` immediately before `CancellationToken cancellationToken = default` to `GetCallsByCalleeAsync`, `GetCallsByCallerAsync`, and `GetCallsByCallerIncludingLambdaDescendantsAsync`; update every positional caller explicitly.
- Rename the internal test observer to `NormalizedSourcePayloadReadObserver`; invoke it once per distinct `normalized_sources` row read, not once per declaration/call slice.

- [ ] **Step 1: Write schema, snapshot, extraction, hydration, and source-filter RED tests**

Update `RequestHasherTests` to require the exact serialized versions and constants:

```csharp
Assert.Equal(6, RequestHasher.SchemaVersion);
Assert.Equal(4, RequestHasher.AnalysisCacheVersion);
```

In Core extraction tests, select each emitted `DocumentData`, declaration, and call, then assert every range is positive, in bounds, and reproduces the previous expected normalized text:

```csharp
var document = Assert.Single(snapshot.Documents, value => value.Key == declaration.DocumentKey);
var slice = document.NormalizedSource.AsSpan(
    declaration.NormalizedStart,
    declaration.NormalizedLength).ToString();
Assert.Equal("void Run(){Local();void Local(){Target();}}", slice);

var invocation = Assert.Single(snapshot.Calls, call => call.ReferenceKind == ReferenceKind.Invocation);
Assert.Equal("Target()", document.NormalizedSource.AsSpan(
    invocation.NormalizedStart,
    invocation.NormalizedLength).ToString());
```

Assert nested local/lambda declarations overlap their containing declaration, object creation excludes `;`, top-level and primary-constructor extents retain their existing expected strings, and Japanese/emoji literals slice correctly. Add reflection/compile-time assertions that obsolete source/hash properties no longer exist. Change the async propagation legacy-snapshot test to require a real declaration and assert a symbol with only `SourceDocumentKey` is not treated as source-backed.

In `SqliteIndexTests`, require all of these schema facts:

```sql
normalized_sources(id, normalized_source_hash, normalized_source)
documents.normalized_source_id NOT NULL
symbol_declarations.normalized_start NOT NULL
symbol_declarations.normalized_length NOT NULL
calls.normalized_start NOT NULL
calls.normalized_length NOT NULL
```

Assert `symbol_declarations` has neither `normalized_source` nor `normalized_source_hash`, `normalized_sources.normalized_source_hash` is unique, `ix_documents_normalized_source` exists, and all three FKs use the design's deletion rules. Save two documents in different projects/profiles with ordinal-equal normalized text and assert one payload row and two referencing documents. Replace one profile and assert an unreferenced payload is deleted while a payload referenced by another profile remains.

Add atomic rejection cases for a bad document hash, unknown declaration/call document key, negative start, zero length, checked end overflow, out-of-bounds range, and a forced equal-hash/unequal-text collision. Each test seeds a valid database/sentinel, captures its bytes or rows, attempts `SaveAsync`, and proves the prior database is unchanged after failure/cancellation. Extend the legacy schema theory so version 5 is rejected by index and query with the existing `csindex index` guidance.

In `SymbolPathResolverTests` and `SymbolSourceQueryTests`, use one document containing two methods:

```csharp
void A() { Marker(); }
void B() { Other(); }
```

Assert `--include Marker`/equivalent request returns only `A`; source terms cannot leak through the shared document. For partial declarations, assert all includes must match one physical declaration, any exclude vetoes only that physical declaration, file and source predicates are evaluated on the same declaration, and one passing declaration yields one logical root.

Replace cell-count expectations with payload-count expectations. A file-only resolution must observe zero payload reads; a source predicate across multiple declarations in one document must observe exactly one. Null/invalid string-query entry points must still fail before any payload read.

- [ ] **Step 2: Run focused RED commands**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RequestHasherTests|FullyQualifiedName~ExecutableSymbolExtractionTests|FullyQualifiedName~LogicalDeclarationExtractionTests|FullyQualifiedName~AsyncInvolvementPropagatorTests"
rtk dotnet test tests\CsIndex.Storage.Tests\CsIndex.Storage.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SqliteIndexTests|FullyQualifiedName~SchemaFiveLogicalSymbolTests"
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolPathResolverTests|FullyQualifiedName~SymbolSourceQueryTests|FullyQualifiedName~PhaseOneAcceptanceTests.StringQueryApisRejectNullOrWhitespaceBeforeReadingSource"
```

Expected: compile failures name the new document/range members and removed legacy expectations; schema assertions still see version 5 and declaration payload columns. The new document-leak regression may pass before schema work only when it exercises the old declaration payload, but its hydration-count assertion must be RED because reads are currently per declaration cell. Record each command separately.

- [ ] **Step 3: Move normalization ownership into document extraction**

In `ExtractProjectDocumentsAndDeclarationsAsync`, obtain the C# root and semantic model before committing `DocumentData`. Normalize the root exactly once:

```csharp
var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
if (root is null || semanticModel is null)
{
    _snapshot.Warnings.Add($"Syntax or semantic model could not be loaded: {path}");
    continue;
}

var normalizedDocument = SourceNormalizer.NormalizeDocument(root, cancellationToken);
var documentData = new DocumentData
{
    Key = documentKey,
    ProjectKey = projectState.Data.Key,
    NormalizedPath = path,
    ContentHash = contentHash,
    NormalizedSource = normalizedDocument.Text,
    NormalizedSourceHash = normalizedDocument.Hash,
    IsGenerated = generated.IsGenerated,
    GenerationKind = generated.Kind,
};
var documentState = new DocumentAnalysisState
{
    Document = document,
    Data = documentData,
    NormalizedSource = normalizedDocument,
};
```

Replace every `SourceNormalizer.Normalize(node)` at the current method, accessor, primary-constructor parameter-list, initializer, expression-bodied member, top-level root, local-function, and anonymous-function sites with `documentState.NormalizedSource.GetRange(node)`. Change `AddDeclaration` to accept `NormalizedSourceRange` and assign its start/length.

Change resolved, dynamic, ambiguous, unresolved, object-creation, method-reference, and async-operation call construction so the helper receives the exact `SyntaxNode` already used for original `SpanStart`/`Span.Length`, obtains one range from `DocumentAnalysisState`, and populates both normalized fields. Never derive a normalized range from the original span numerically.

Remove all symbol source/hash projection assignments from `FinalizeDeclarationProjections`. Simplify `AsyncInvolvementPropagator.HasSourceDeclaration` to return true only when `PreferredDeclarationKey` resolves to a declaration belonging to that symbol.

- [ ] **Step 4: Implement schema 6 transactional persistence and validation**

Set the constants:

```csharp
public const int SchemaVersion = 6;
public const int AnalysisCacheVersion = 4;
```

Define `normalized_sources` before `documents`, make `normalized_source_hash` `UNIQUE`, add `documents.normalized_source_id`, replace declaration payload columns with ranges, and add call ranges. Add:

```sql
CREATE INDEX ix_documents_normalized_source
ON documents(normalized_source_id);
```

Before opening the database, `ValidateSnapshot` must hash every `DocumentData.NormalizedSource` and compare bytes, build a document-key dictionary, and validate every declaration/call with checked arithmetic:

```csharp
private static void ValidateNormalizedRange(
    string rowKind,
    string rowKey,
    int start,
    int length,
    DocumentData document)
{
    if (start < 0 || length <= 0)
    {
        throw new InvalidOperationException(
            $"{rowKind} '{rowKey}' has an invalid normalized range.");
    }

    int end;
    try { end = checked(start + length); }
    catch (OverflowException exception)
    {
        throw new InvalidOperationException(
            $"{rowKind} '{rowKey}' has an overflowing normalized range.", exception);
    }

    if (end > document.NormalizedSource.Length)
    {
        throw new InvalidOperationException(
            $"{rowKind} '{rowKey}' normalized range exceeds document '{document.Key}'.");
    }
}
```

Within the existing save transaction, upsert each distinct payload before inserting its document. Use `INSERT OR IGNORE`, then select both `id` and stored text by hash. Compare stored/new text with `StringComparison.Ordinal`; throw `InvalidOperationException` naming a normalized-source hash collision on inequality. Pass the selected ID to document insertion. Persist normalized ranges on declarations/calls. After new profile rows are complete and before foreign-key validation/commit, run:

```sql
DELETE FROM normalized_sources
WHERE NOT EXISTS (
    SELECT 1 FROM documents d WHERE d.normalized_source_id = normalized_sources.id
);
```

Do not catch and commit any failure; retain the existing rollback path.

- [ ] **Step 5: Hydrate declaration slices from distinct shared payloads**

Change declaration selects to return range columns unconditionally and no text columns. Materialize metadata, then, only when `includeSourceText` is true, gather distinct `DocumentId` values, load the joined payload once per document ID, and slice after full reader disposal. Implement a shared helper with this contract:

```csharp
private async Task<IReadOnlyDictionary<long, string>> LoadNormalizedSourcesByDocumentAsync(
    SqliteConnection connection,
    IEnumerable<long> documentIds,
    CancellationToken cancellationToken);

private static string SliceNormalizedSource(
    string rowKind,
    long rowId,
    long documentId,
    int start,
    int length,
    IReadOnlyDictionary<long, string> documents);
```

The loader query joins `documents d` to `normalized_sources ns`, orders by `d.id`, invokes `NormalizedSourcePayloadReadObserver` once per returned row, and rejects duplicate document IDs with unequal text. `SliceNormalizedSource` performs checked nonnegative/positive/end validation and throws `IndexDatabaseException` containing the row kind, row ID, and document ID. It never reads a file and never calls Roslyn or `SourceNormalizer`.

Update `StoredSymbol`'s legacy constructor adapter so it no longer accepts normalized source/hash or synthesizes `PreferredDeclaration`; preferred source is populated only from real declaration rows. Update every fixture/construction call at compile errors to the new DTO signatures instead of adding compatibility overloads.

- [ ] **Step 6: Add range metadata to all call reads without hydrating text by default**

Extend `BuildCallSelect` and `ReadCallsAsync` to read `c.normalized_start` and `c.normalized_length`. Add the explicit `includeSourceText` argument to the three bulk call methods, materialize rows first, and call the same distinct-document loader/slicer only when true. `FindCallAtAsync` continues to request false because definition-at needs call identity, not call text.

At all Query call sites, pass `includeSourceText: false` explicitly in this task. Task 3 and Task 4 are the only tasks that will pass true. This keeps the schema/query migration independently green and proves no accidental hydration.

- [ ] **Step 7: Run focused and layer regression tests**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RequestHasherTests|FullyQualifiedName~ExecutableSymbolExtractionTests|FullyQualifiedName~LogicalDeclarationExtractionTests|FullyQualifiedName~AsyncInvolvementPropagatorTests"
rtk dotnet test tests\CsIndex.Storage.Tests\CsIndex.Storage.Tests.csproj -c Release --no-restore
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolPathResolverTests|FullyQualifiedName~SymbolSourceQueryTests|FullyQualifiedName~PhaseOneAcceptanceTests"
rtk dotnet build CsIndex.sln -c Release --no-restore
```

Expected: all commands exit 0 with zero warnings. Inspect the generated SQLite database with test SQL and prove source payload reads are zero for file-only/no-source paths and one per distinct document for source filters.

- [ ] **Step 8: Inspect and commit Task 2**

Run `rtk git diff --check`, inspect schema SQL, every `CallData` creation, every `AddDeclaration` call, and every `StoredDeclaration`/`StoredCall` constructor. Search:

```powershell
rtk rg -n "NormalizedSourceHash|normalized_source_hash|normalized_source" src tests
```

Expected: document/payload ownership and its tests/docs are the only valid source/hash hits; no declaration column, transient symbol field, or compatibility fallback remains. Commit all and only Task 2 files:

```powershell
rtk git add src\CsIndex.Core src\CsIndex.Storage tests\CsIndex.Core.Tests tests\CsIndex.Storage.Tests tests\CsIndex.IntegrationTests\SymbolPathResolverTests.cs tests\CsIndex.IntegrationTests\SymbolSourceQueryTests.cs tests\CsIndex.IntegrationTests\PhaseOneAcceptanceTests.cs
rtk git commit -m "feat: persist normalized document source ranges"
```

Record the GREEN commands, commit SHA, task report, and review verdict in the ledger before Task 3.

---

### Task 3: Add `callers --show-source` Hydration and Output

**Files:**
- Modify: `src/CsIndex.Query/QueryResults.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliSymbolPathOptionMatrixTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/VerboseHelpTests.cs`
- Create: `tests/CsIndex.IntegrationTests/CallerSourceOutputTests.cs`

**Interfaces:**
- Consumes: Task 2 `StoredCall.NormalizedStart`, `NormalizedLength`, optional `NormalizedSource`, and `QueryRepository.GetCallsByCalleeAsync(..., includeSourceText, cancellationToken)`.
- Produces:

```csharp
public sealed record CallResult(
    RootSelection Selection,
    IReadOnlyList<StoredCall> Calls,
    IReadOnlyList<StoredSymbol> EffectiveCallers,
    IReadOnlyList<StoredRelation> PossibleRuntimeTargets,
    IReadOnlyDictionary<long, StoredSymbol> SymbolsById,
    bool ShowSource = false);

public Task<CallResult> SemanticQueryService.FindCallersAsync(
    RootSelection selection,
    GeneratedFilter generatedFilter,
    DispatchSearchMode dispatchMode,
    CallerScope callerScope,
    bool showSource = false,
    CancellationToken cancellationToken = default);
```

- Add `bool showSource = false` immediately before cancellation to the two string-query `FindCallersAsync` overloads as well, after their existing `includeOverrides` option. Update every positional call to named arguments where needed.
- `FindReferencesAsync` and `FindCalleesAsync` construct `CallResult` with `ShowSource: false` and never request source text.
- `OutputFormatter.WriteCalls` reads only `result.ShowSource`; there is no separate formatter flag that can disagree with the hydrated result.

- [ ] **Step 1: Add CLI scope, DB-only hydration, exact source, and no-flag RED tests**

In the option matrix, include `show-source` in exactly `symbol find` (existing), `callers`, and `callers tree`. Assert it is rejected by every other command and that `--show-source=true` is rejected as a value-bearing spelling.

Add normal/verbose help expectations:

```text
callers --show-source: include the normalized invocation or object-creation expression
callers tree --show-source: include normalized source for every physical call site
```

Create an indexed fixture with these calls:

```csharp
static float A(float value) => value;
static int A(int value) => value;
static void B(float first, int second) { }

static void Caller(float f)
{
    B(
        /* 日本語 */ A(f),
        A(10 + 20));
}
```

Assert the two `A` call rows contain exact `A(f)` and `A(10+20)`, the outer `B` call contains both nested expressions, and no source contains `;` or the comment.

Call `SemanticQueryService.FindCallersAsync(..., showSource: true)` after moving or replacing the fixture source file and assert the original indexed normalized strings still hydrate. Do not format locations in this test; its purpose is to prove normalized-source retrieval is DB-only. Observe `NormalizedSourcePayloadReadObserver` and assert one read for all call rows in the same document. Repeat with `showSource: false` and assert zero reads and all `StoredCall.NormalizedSource` values are null.

Capture current no-flag table and JSON output before production edits. Add strict expected snapshots and then add flagged expectations:

```text
  <path>:<line>:<column>  <caller> -> <callee> [Invocation, Resolved] [None]\tA(10+20)
```

For JSON, parse the document and assert every call object has `normalizedSource` only with the flag. Assert the exact JSON string preserves raw/newline/control characters in a literal, while the table form contains one physical output line and replaces TAB, CR/LF, NEL, U+2028, and U+2029 through `TableTextSanitizer`.

- [ ] **Step 2: Run the focused tests and record the expected RED**

Run:

```powershell
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CallerSourceOutputTests|FullyQualifiedName~CliSymbolPathOptionMatrixTests|FullyQualifiedName~VerboseHelpTests|FullyQualifiedName~CliCommandTests"
```

Expected: `callers --show-source` and caller-tree scope rows are rejected as unknown options; direct API tests fail to compile because `CallResult.ShowSource` and the `showSource` service parameter do not exist. Existing no-flag snapshots remain green. Record the failing cases before production edits.

- [ ] **Step 3: Thread explicit source intent through caller querying**

Update all `FindCallersAsync` overloads to propagate one `showSource` value. The root-selection overload calls:

```csharp
var calls = await repository.GetCallsByCalleeAsync(
    selection.Profile.Id,
    searchIds,
    generatedFilter,
    CallKinds,
    includeSourceText: showSource,
    cancellationToken);
```

Retain current dispatch/root expansion, endpoint hydration, and canonical call ordering. Return:

```csharp
return new CallResult(
    selection,
    orderedCalls,
    hydration.EffectiveCallers,
    orderedTargets,
    hydration.SymbolsById,
    ShowSource: showSource);
```

Every references/callees result supplies `ShowSource: false`. Do not infer source intent from non-null DTO values.

- [ ] **Step 4: Accept the flag in the ordinary callers command and update help**

Add only `"show-source"` to `RunCallersAsync.allowedOptions`; parse with `parsed.HasFlag("show-source")`; pass the boolean to `FindCallersAsync`. Add the command help option:

```csharp
new HelpOption(
    "--show-source",
    "Include the normalized invocation or object-creation expression"),
```

Update the shared verbose option-scope reference so it names `callers` and `callers tree` while continuing to state every rejecting command. Do not add the flag to `QueryOptions`, because that would over-permit unrelated commands.

- [ ] **Step 5: Add conditional table and JSON fields without changing the no-flag branch**

Leave the existing no-source JSON projection and table write expression structurally intact in the `!result.ShowSource` branch. In the true branch, append the property after `receiverTypeKey`:

```csharp
normalizedSource = RequireNormalizedCallSource(call),
```

`RequireNormalizedCallSource` throws `IndexDatabaseException` naming the call ID and document ID if a result marked `ShowSource` lacks text. For table output:

```csharp
var suffix = result.ShowSource
    ? "\t" + TableTextSanitizer.Sanitize(RequireNormalizedCallSource(call))
    : string.Empty;
_writer.WriteLine(existingLine + suffix);
```

Keep exact stored text for JSON; do not call the table sanitizer there. Retain cancellation checks before each projection/write and before returning.

- [ ] **Step 6: Run focused and query/CLI regression tests**

Run:

```powershell
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CallerSourceOutputTests|FullyQualifiedName~CliSymbolPathOptionMatrixTests|FullyQualifiedName~VerboseHelpTests|FullyQualifiedName~CliCommandTests"
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~FunctionTargetFilterTests|FullyQualifiedName~RootSelectionOrchestrationTests|FullyQualifiedName~GraphQueryTests|FullyQualifiedName~OutputFormatterTests"
rtk dotnet build src\CsIndex.Cli\CsIndex.Cli.csproj -c Release --no-restore
```

Expected: every command exits 0 with zero warnings. Compare captured no-flag payloads byte-for-byte, not only as parsed JSON.

- [ ] **Step 7: Inspect and commit Task 3**

Run `rtk git diff --check`; inspect every allowed-option array and every `FindCallersAsync` overload/call. Commit only Task 3 files:

```powershell
rtk git add src\CsIndex.Query\QueryResults.cs src\CsIndex.Query\SemanticQueryService.cs src\CsIndex.Cli\Program.cs src\CsIndex.Cli\OutputFormatter.cs tests\CsIndex.IntegrationTests\CliSymbolPathOptionMatrixTests.cs tests\CsIndex.IntegrationTests\CliCommandTests.cs tests\CsIndex.IntegrationTests\VerboseHelpTests.cs tests\CsIndex.IntegrationTests\CallerSourceOutputTests.cs
rtk git commit -m "feat: show normalized caller call-site source"
```

Record the GREEN commands, commit SHA, task report, and review verdict in the ledger before Task 4.

---

### Task 4: Preserve and Render Physical Call Sites in Caller Trees

**Files:**
- Modify: `src/CsIndex.Query/QueryResults.cs`
- Modify: `src/CsIndex.Query/CallerTreeBuilder.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Modify: `src/CsIndex.Query/Symbols/SymbolCanonicalComparer.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `src/CsIndex.Cli/GraphOutputFormatter.cs`
- Modify: `tests/CsIndex.Query.Tests/CallerTreeBuilderTests.cs`
- Modify: `tests/CsIndex.Query.Tests/SymbolCanonicalComparerTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Create: `tests/CsIndex.IntegrationTests/CallerTreeSourceOutputTests.cs`

**Interfaces:**
- Consumes: Task 2 call source hydration and Task 3 `--show-source` option contract.
- Produces:

```csharp
public sealed record CallerTreeCallSite(
    long CallerSymbolId,
    long CalleeSymbolId,
    StoredCall Call);

public sealed record CallerTreeResult(
    RootSelection Selection,
    StoredSymbol Root,
    IReadOnlyList<CallerTreeNode> Nodes,
    IReadOnlyList<CallerTreeEdge> Edges,
    IReadOnlyList<CallerTreeCallSite> CallSites,
    bool ShowSource,
    bool Truncated);

internal Task<CallerTreeResult> CallerTreeBuilder.BuildAsync(
    RootSelection selection,
    StoredSymbol root,
    int depth,
    int maxNodes,
    bool showSource,
    CancellationToken cancellationToken);
```

- Add `bool showSource = false` immediately before cancellation to all selection and string-query `SemanticQueryService.FindCallerTreeAsync` overloads.
- Add this comparer API:

```csharp
internal static IReadOnlyList<CallerTreeCallSite> OrderCallerTreeCallSites(
    IEnumerable<CallerTreeCallSite> callSites,
    IReadOnlyList<CallerTreeEdge> orderedEdges,
    CancellationToken cancellationToken,
    Action? afterOrderingComparison = null);
```

- For equal structural edge order, compare `Call.DocumentPath` ordinal, `SourceStart`, `SourceLength`, then `Call.Id`. Reject a call-site association whose pair is absent from `orderedEdges`.
- Make `OutputFormatter.ResolveLocation` `internal static` without changing its behavior so graph formatting shares the established path/line/column conversion.

- [ ] **Step 1: Add RED tests for duplicate, boundary, cycle/cross, ordering, and all formats**

Extend the builder fixture so the same caller invokes the same callee twice. Assert one `CallerTreeEdge` but two `CallerTreeCallSite` entries with distinct `StoredCall.Id` and original spans. Add separate cases for:

- a retained cycle edge between already-materialized nodes;
- a retained cross edge that becomes `Additional edges:`;
- a finite depth boundary where both endpoint nodes already exist;
- a would-be edge whose new caller is rejected by `maxNodes`;
- a system/non-source caller rejected by the existing eligibility check.

The first three retain all physical sites; the last two retain neither edge nor site. Run the same cases with `showSource: false` and prove call-site metadata remains available but source strings are null and payload reads are zero. With true, assert source is hydrated on each site.

Add comparer tests with deliberately shuffled input. The expected order is structural edge index first, then ordinal path, source start, source length, and ID. Add cancellation during comparison and a missing-edge association rejection.

Create caller-tree format snapshots from a graph where `Caller` invokes `Target` twice and another retained caller/callee pair is a cross edge. Expected flagged text includes:

```text
Target
└─ Caller
   @ src/Caller.cs:7:9\tTarget(first)
   @ src/Caller.cs:8:9\tTarget(second)
Additional edges:
  OtherCaller -> Target
    @ src/Other.cs:4:5\tTarget(value)
```

Expected Mermaid keeps one edge and one label:

```text
    n12 -->|"src/Caller.cs:7:9 Target(first)<br/>src/Caller.cs:8:9 Target(second)"| n4
```

Use a normalized expression containing `|`, quotes, `<`, `>`, `&`, and raw-string newlines; assert the Mermaid label cannot be terminated early, text remains one physical record per call site, and JSON preserves exact source.

For flagged JSON, assert every edge object has this exact property order and content:

```json
{
  "callerSymbolId": 12,
  "calleeSymbolId": 4,
  "callSites": [
    {
      "id": 91,
      "location": { "path": "src/Caller.cs", "line": 7, "column": 9, "offset": 123 },
      "normalizedSource": "Target(first)"
    }
  ]
}
```

Capture strict no-flag tree, Mermaid, and JSON snapshots and require byte identity. Verify truncated output still places `<truncated>` exactly where the current formatter does.

- [ ] **Step 2: Run focused RED commands**

Run:

```powershell
rtk dotnet test tests\CsIndex.Query.Tests\CsIndex.Query.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CallerTreeBuilderTests|FullyQualifiedName~SymbolCanonicalComparerTests"
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CallerTreeSourceOutputTests|FullyQualifiedName~GraphQueryTests|FullyQualifiedName~OutputFormatterTests|FullyQualifiedName~CliCommandTests"
```

Expected: compile failures identify `CallerTreeCallSite`, `CallerTreeResult.CallSites`, `ShowSource`, and the new builder/service parameters. No-flag legacy cases remain otherwise unchanged. Record both RED commands.

- [ ] **Step 3: Preserve physical calls alongside structural edges**

In `CallerTreeBuilder`, replace the deduplicated candidate-edge-only staging collection with paired staging:

```csharp
var candidateEdges = new List<CallerTreeEdge>();
var candidateEdgeSet = new HashSet<CallerTreeEdge>();
var candidateCallSites = new List<CallerTreeCallSite>();

foreach (var call in calls)
{
    cancellationToken.ThrowIfCancellationRequested();
    if (!TargetsCallee(call, calleeNode.Symbol.Id))
    {
        continue;
    }

    var edge = new CallerTreeEdge(call.CallerSymbolId, calleeNode.Symbol.Id);
    candidateCallSites.Add(new CallerTreeCallSite(
        edge.CallerSymbolId,
        edge.CalleeSymbolId,
        call));
    if (candidateEdgeSet.Add(edge))
    {
        candidateEdges.Add(edge);
    }
}
```

Pass `includeSourceText: showSource` to `GetCallsByCalleeAsync`. Whenever an edge survives the existing endpoint/depth/max-node eligibility checks and enters the final `edgeSet`, retain every staged call-site whose caller/callee pair equals that edge. When an already-added edge is encountered from another frontier item, still append newly observed physical call IDs exactly once; use a `HashSet<(long CallerSymbolId, long CalleeSymbolId, long CallId)>` for association identity.

Do not retain sites before both endpoint nodes are accepted. Apply the same rule in the depth-boundary branch. After canonical edge ordering, call `OrderCallerTreeCallSites`; return both collections and `ShowSource: showSource`.

- [ ] **Step 4: Propagate the caller-tree flag through service and CLI**

Add `showSource` to each caller-tree service overload and pass it unchanged to `BuildAsync`. In `RunCallerTreeAsync`, add `"show-source"` directly to that command's allowed options, add the command help description from Task 3, and call:

```csharp
var result = await service.FindCallerTreeAsync(
    selection,
    depth,
    maxNodes,
    showSource: parsed.HasFlag("show-source"),
    cancellationToken);
```

Do not add the option to the global/query option families.

- [ ] **Step 5: Extend the presentation model without disturbing no-flag formatting**

Extend `CallerTreePresentation` with `SpanningEdges` and a call-site lookup grouped by `CallerTreeEdge`. Preserve the existing `Nodes`, `Edges`, `AdditionalEdges`, `NodesById`, and `IndentById` ordering code. Build the lookup from already ordered `result.CallSites`; do not sort again inside a formatter.

Branch each formatter on `result.ShowSource`:

- In false branches, execute the current write loops and current JSON `List<CallerTreeEdge>` exactly.
- In text true branch, after each non-root caller node, locate its selected spanning edge and write each site with one deeper indentation, `@ `, the resolved `path:line:column`, TAB, and `TableTextSanitizer.Sanitize(source)`. Under each additional edge, write its sites with four leading spaces.
- In Mermaid true branch, group every site on the edge, render `path:line:column source`, join with `<br/>`, and emit `-->|"label"|`. Extend `EscapeMermaidLabel` to escape `|` in addition to its current quote/ampersand/angle/newline handling; test escaping order so inserted entities are not escaped twice.
- In JSON true branch, project a separate anonymous edge type with `callerSymbolId`, `calleeSymbolId`, and `callSites`; each call site has `id`, an exact location object from `OutputFormatter.ResolveLocation`, and exact `normalizedSource`.

Use a shared `RequireCallSiteSource` helper that throws `IndexDatabaseException` naming call/document IDs when `ShowSource` is true but hydration is missing. Keep cancellation checks around grouping, location resolution, and every output element.

- [ ] **Step 6: Run Query, graph, and CLI regression tests**

Run:

```powershell
rtk dotnet test tests\CsIndex.Query.Tests\CsIndex.Query.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CallerTreeBuilderTests|FullyQualifiedName~SymbolCanonicalComparerTests"
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CallerTreeSourceOutputTests|FullyQualifiedName~GraphQueryTests|FullyQualifiedName~OutputFormatterTests|FullyQualifiedName~CliCommandTests|FullyQualifiedName~CliSymbolPathOptionMatrixTests|FullyQualifiedName~VerboseHelpTests"
rtk dotnet build src\CsIndex.Cli\CsIndex.Cli.csproj -c Release --no-restore
```

Expected: all commands exit 0 with zero warnings; all strict no-flag snapshots are byte-identical; every retained physical call ID appears exactly once under its structural edge.

- [ ] **Step 7: Inspect and commit Task 4**

Run `rtk git diff --check`. Inspect max-node/depth/cycle branches for edge/site agreement, compare no-flag formatter blocks to their pre-task form, and inspect Mermaid escaping. Commit only Task 4 files:

```powershell
rtk git add src\CsIndex.Query\QueryResults.cs src\CsIndex.Query\CallerTreeBuilder.cs src\CsIndex.Query\SemanticQueryService.cs src\CsIndex.Query\Symbols\SymbolCanonicalComparer.cs src\CsIndex.Cli\Program.cs src\CsIndex.Cli\OutputFormatter.cs src\CsIndex.Cli\GraphOutputFormatter.cs tests\CsIndex.Query.Tests\CallerTreeBuilderTests.cs tests\CsIndex.Query.Tests\SymbolCanonicalComparerTests.cs tests\CsIndex.IntegrationTests\GraphQueryTests.cs tests\CsIndex.IntegrationTests\OutputFormatterTests.cs tests\CsIndex.IntegrationTests\CliCommandTests.cs tests\CsIndex.IntegrationTests\CallerTreeSourceOutputTests.cs
rtk git commit -m "feat: show physical call sites in caller trees"
```

Record the GREEN commands, commit SHA, task report, and review verdict in the ledger before Task 5.

---

### Task 5: Publish Durable User, Schema, Status, and Test Documentation

**Files:**
- Modify: `docs/CLI.md`
- Modify: `docs/DB_SCHEMA.md`
- Modify: `docs/SPEC.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`
- Modify: `docs/TEST_PLAN.md`

**Interfaces:**
- Consumes: the implemented schema 6, CLI help, output contracts, hydration behavior, and verified test names from Tasks 1-4.
- Produces: durable operator/developer documentation that matches the shipped commands and is sufficient to reconstruct the feature after a future interruption.

- [ ] **Step 1: Update command documentation from the implemented help**

In `docs/CLI.md`, add runnable examples for:

```powershell
csindex callers "Game::Player::Tick()" --show-source
csindex callers "Game::Player::Tick()" --show-source --output-format json
csindex callers tree "Game::Player::Tick()" --show-source
csindex callers tree "Game::Player::Tick()" --show-source --output-format mermaid
csindex callers tree "Game::Player::Tick()" --show-source --output-format json
```

State that ordinary callers returns the normalized invocation/object-creation expression for each row; caller tree associates all physical sites with one structural edge; table/tree sanitize record-breaking controls; JSON preserves exact source; no-flag payloads omit source fields. Keep `symbol find --include`/`--exclude` documented as matching each function declaration slice, not the whole document.

- [ ] **Step 2: Replace the schema and data-flow documentation**

In `docs/DB_SCHEMA.md`, set schema 6 and analysis cache 4; document `normalized_sources`, `documents.normalized_source_id`, declaration/call range columns, unique hash plus ordinal collision verification, the document index, foreign keys, transactional orphan cleanup, and UTF-16/C# slicing. Explicitly list removed declaration source/hash columns and state that schema 5 is rejected rather than migrated.

In `docs/SPEC.md`, describe index-time document normalization, declaration/call range ownership, conditional query hydration, declaration-scoped source filtering, physical caller-tree associations, and the query-time no-reparse/no-fallback invariant.

- [ ] **Step 3: Update implementation status and executable test plan**

In `docs/IMPLEMENTATION_STATUS.md`, mark schema 6 normalized source ranges, `callers --show-source`, and all caller-tree formats implemented. Do not claim source locations are persisted; retain the note that location line/column resolution may read original files.

In `docs/TEST_PLAN.md`, list the exact focused test classes and the final commands from this plan. Include the integrity, deduplication, UTF-16, declaration-filter isolation, physical-edge-site, escaping, no-source hydration, DB-only hydration, and no-flag byte-compatibility cases.

Documentation-only changes do not require an artificial failing unit test. Validate their literal claims against production constants/help and focused passing tests instead.

- [ ] **Step 4: Run documentation consistency scans**

Run:

```powershell
rtk rg -n "Schema (Version )?5|schema_version.?=.?5|AnalysisCacheVersion.?=.?3|symbol_declarations.*normalized_source|callers.*show-source|normalized_sources" docs src tests
rtk rg -n "T[B]D|T[O]DO|PLACE[H]OLDER|implement la[t]er" docs\superpowers\specs\2026-09-09-callers-normalized-source-design.md docs\superpowers\plans\2026-09-09-callers-normalized-source.md docs\CLI.md docs\DB_SCHEMA.md docs\SPEC.md docs\IMPLEMENTATION_STATUS.md docs\TEST_PLAN.md
```

Expected: old version/source-column hits occur only in intentional legacy-rejection history/tests; no placeholder markers occur; all command examples match actual option scopes.

- [ ] **Step 5: Run final fresh verification**

Run in this order and record every exit code/count in the ledger:

```powershell
rtk dotnet restore CsIndex.sln
rtk dotnet build CsIndex.sln -c Release --no-restore
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-build --no-restore
rtk dotnet test tests\CsIndex.Storage.Tests\CsIndex.Storage.Tests.csproj -c Release --no-build --no-restore
rtk dotnet test tests\CsIndex.Query.Tests\CsIndex.Query.Tests.csproj -c Release --no-build --no-restore
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-build --no-restore
rtk dotnet format CsIndex.sln --verify-no-changes --no-restore
rtk git diff --check
rtk git status --short --branch
```

Expected: restore/build succeed; all four test projects pass with zero failures/warnings; format and diff checks exit 0; status contains only the five intended documentation files before the Task 5 commit.

- [ ] **Step 6: Inspect and commit Task 5**

Compare documented help and schema names directly with `Program.cs` and `SchemaMigrator.cs`. Commit:

```powershell
rtk git add docs\CLI.md docs\DB_SCHEMA.md docs\SPEC.md docs\IMPLEMENTATION_STATUS.md docs\TEST_PLAN.md
rtk git commit -m "docs: document normalized caller source"
```

Record the commit SHA and task review verdict. Then generate one independent final branch review package covering the entire feature diff from the recorded fork SHA through Task 5. Resolve every critical/important finding through the SDD fix loop, rerun affected focused tests after each fix, and rerun the full final verification after the final fix commit.

---

## Final Completion Gate

The feature is complete only when all of the following are recorded in `.superpowers/sdd/2026-09-09-callers-normalized-source/progress.md`:

- Tasks 1-5 each have RED/GREEN evidence where applicable, a commit SHA, an approved spec-compliance review, and an approved code-quality review.
- The independent final branch reviewer reports no unresolved critical or important findings.
- `RequestHasher.SchemaVersion == 6` and `AnalysisCacheVersion == 4` are verified in source and tests.
- A schema inspection proves shared payload ownership and no declaration payload columns.
- A source-filter regression proves no cross-declaration/document leakage.
- A DB-only query regression proves normalized call source survives source-file replacement and no source-normalization callback is used.
- Exact no-flag output snapshots and every flagged callers/caller-tree format pass.
- Fresh Release restore/build, all four test projects, formatter validation, `git diff --check`, and final status checks pass with zero errors, failures, or warnings.
- The branch is handed off through `superpowers:finishing-a-development-branch`; no merge, worktree deletion, or other destructive cleanup occurs without the user's explicit choice.
