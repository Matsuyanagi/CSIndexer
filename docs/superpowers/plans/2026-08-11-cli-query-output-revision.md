# CLI Query and Output Revision Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved `docs/2026-08-11.revised.md` contract for common function filters, lambda targets, stable table records, source normalization, output-format renaming, and atomic file output.

**Architecture:** Represent direct-async filtering independently from derived async involvement, and pass one function-target filter through CLI, query resolution, and storage. Generalize target resolution so method and lambda roots share deterministic source-backed resolution while method-only override expansion remains isolated. Route all result payloads through injected `TextWriter` instances, with table-only sanitization and a same-directory atomic output destination that commits only after successful rendering.

**Tech Stack:** C# 14 / .NET 10 (`net10.0-windows`), Roslyn 5.6, Microsoft.Data.Sqlite 10, xUnit v3, PowerShell/RTK commands.

## Global Constraints

- Treat `docs/2026-08-11.revised.md` as the approved delta; preserve `docs/SPEC.md` sections 13, 16, 21, and 33 unless the delta explicitly replaces them.
- Keep schema version at `4`; no SQLite column or migration is needed.
- Preserve profile isolation, project-scoped symbol identity, deterministic order, cancellation checks, schema-v4 old-DB rejection, descendant-only override expansion, and the absence of synthesized lambda-owner call edges.
- `--kind all` and `--async-status all` use the same internal representation and produce the same ordered payload as omission.
- Direct async means `StoredSymbol.AsyncRole != AsyncRole.None`; do not infer from names and do not reuse `AsyncInvolvementDepth`.
- Table sanitization is presentation-only. Do not alter stored normalized source, hashes, source search, or JSON `normalizedSource`.
- `--output` is removed, not aliased. `--output-format` chooses format; exact `-o` and `--output-file` choose destination.
- File output is UTF-8 without BOM and failure-atomic. Never truncate an existing destination or leave a temporary file after argument, query, database, cancellation, formatting, or write failure.
- Every behavior change follows RED-GREEN-REFACTOR. Do not weaken existing tests.
- Run commands from `E:\WorksDevelop\CSIndexer` and prefix shell commands with `rtk`.

## File Responsibility Map

- `src/CsIndex.Core/Model/IndexEnums.cs`: canonical direct-async filter enum shared by Query and Storage.
- `src/CsIndex.Query/Symbols/SymbolSearchRequest.cs` and `SymbolPatternMatcher.cs`: extended-search function-filter contract.
- `src/CsIndex.Query/ExecutableTargetResolver.cs`: deterministic method/lambda target resolution and post-expansion filtering.
- `src/CsIndex.Query/SemanticQueryService.cs`: public query APIs and command-specific target/filter placement.
- `src/CsIndex.Storage/QueryRepository.cs`: SQL predicates for list/candidate queries without schema changes.
- `src/CsIndex.Cli/CliArguments.cs` and `Program.cs`: option grammar, compatibility break, validation matrix, help, and writer wiring.
- `src/CsIndex.Cli/OutputFormatter.cs`: table/JSON payload writing, fixed record schemas, summaries, and injected writers.
- `src/CsIndex.Cli/GraphOutputFormatter.cs`: injected graph payload writer.
- `src/CsIndex.Cli/OutputDestination.cs`: path validation, temp-file ownership, commit, and cleanup.
- `src/CsIndex.Core/Analysis/SourceNormalizer.cs` and `Caching/RequestHasher.cs`: zero-width token handling and cache invalidation.
- Existing Core, Query, Storage, and Integration test projects: focused unit tests and end-to-end acceptance coverage.
- `docs/SPEC.md`, `CLI.md`, `DECISIONS.md`, `TEST_PLAN.md`, `IMPLEMENTATION_STATUS.md`, and `KNOWN_LIMITATIONS.md`: final normative documentation.

---

### Task 1: Direct-Async Filter Model and Storage Predicates

**Files:**
- Modify: `src/CsIndex.Core/Model/IndexEnums.cs`
- Modify: `src/CsIndex.Query/Symbols/SymbolSearchRequest.cs`
- Modify: `src/CsIndex.Query/Symbols/SymbolPatternMatcher.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Modify: `tests/CsIndex.Query.Tests/SymbolPatternMatcherTests.cs`
- Modify: `tests/CsIndex.Storage.Tests/SqliteIndexTests.cs`

**Interfaces:**
- Produces: `AsyncStatusFilter` with `All`, `Async`, and `Sync` values.
- Produces: `SymbolSearchRequest.AsyncStatus` defaulting to `AsyncStatusFilter.All`.
- Produces: `QueryRepository.FindFunctionSymbolsAsync(long, IndexedSymbolKind?, AsyncStatusFilter, bool, CancellationToken)`.
- Consumes: persisted `StoredSymbol.Kind`, `StoredSymbol.AsyncRole`, and `StoredSymbol.AsyncInvolvementDepth` only.

- [ ] **Step 1: Add failing matcher tests for direct async semantics**

Add focused cases that construct method, lambda, and type `StoredSymbol` values and assert:

```csharp
Assert.True(Matcher(AsyncStatusFilter.Async).IsMatch(Method(asyncRole: AsyncRole.ReturnsAwaitable)));
Assert.False(Matcher(AsyncStatusFilter.Async).IsMatch(Method(asyncRole: AsyncRole.None)));
Assert.True(Matcher(AsyncStatusFilter.Sync).IsMatch(Method(asyncRole: AsyncRole.None, depth: 1)));
Assert.False(Matcher(AsyncStatusFilter.Sync).IsMatch(Type(asyncRole: AsyncRole.None)));
Assert.True(Matcher(AsyncStatusFilter.All).IsMatch(Type(asyncRole: AsyncRole.None)));
```

Also assert kind and async status combine with AND semantics.

- [ ] **Step 2: Run the matcher tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SymbolPatternMatcherTests`

Expected: FAIL because `AsyncStatusFilter` and `SymbolSearchRequest.AsyncStatus` do not exist.

- [ ] **Step 3: Implement the enum and in-memory matcher predicate**

Add the shared enum with `All = 0` so `default` means no predicate:

```csharp
public enum AsyncStatusFilter
{
    All = 0,
    Async = 1,
    Sync = 2,
}
```

Append `AsyncStatusFilter AsyncStatus = AsyncStatusFilter.All` to `SymbolSearchRequest`. In `SymbolPatternMatcher.IsMatch`, apply kind first, then apply async/sync only to `Method` or `Lambda`; `All` must not exclude type symbols.

- [ ] **Step 4: Add failing repository tests for SQL filtering and async-involved composition**

Extend the existing SQLite fixture with method/lambda rows covering `AsyncRole.None`, a non-zero direct role, and `AsyncInvolvementDepth > 0`. Assert exact ordered IDs for:

```csharp
await repository.FindFunctionSymbolsAsync(profileId, null, AsyncStatusFilter.All, false, token);
await repository.FindFunctionSymbolsAsync(profileId, null, AsyncStatusFilter.Async, false, token);
await repository.FindFunctionSymbolsAsync(profileId, null, AsyncStatusFilter.Sync, true, token);
```

The last result must contain a sync method with non-null involvement depth.

- [ ] **Step 5: Run repository tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FindFunctionSymbols`

Expected: FAIL because the repository signature and async-role predicate are absent.

- [ ] **Step 6: Implement parameterized SQL predicates without a schema change**

Add `$async_status`, `$all_async_status`, `$async_status_async`, and executable-kind guards. The effective predicate must be equivalent to:

```sql
AND (
  $async_status = $all_async_status
  OR ($async_status = $async_status_async AND s.async_role <> 0)
  OR ($async_status = $async_status_sync AND s.async_role = 0)
)
AND ($async_involved = 0 OR s.async_involvement_depth IS NOT NULL)
```

Keep the existing `ORDER BY s.display_name, d.normalized_path, s.source_start, s.id`.

- [ ] **Step 7: Run focused Query and Storage tests and confirm GREEN**

Run both commands from Steps 2 and 5. Expected: PASS, zero warnings.

- [ ] **Step 8: Commit the filter foundation**

```powershell
rtk git add src/CsIndex.Core/Model/IndexEnums.cs src/CsIndex.Query/Symbols/SymbolSearchRequest.cs src/CsIndex.Query/Symbols/SymbolPatternMatcher.cs src/CsIndex.Storage/QueryRepository.cs tests/CsIndex.Query.Tests/SymbolPatternMatcherTests.cs tests/CsIndex.Storage.Tests/SqliteIndexTests.cs
rtk git commit -m "feat: add direct async query filtering"
```

### Task 2: Shared Method/Lambda Target Resolver

**Files:**
- Create: `src/CsIndex.Query/ExecutableTargetResolver.cs`
- Modify: `src/CsIndex.Query/MethodTargetResolver.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Modify: `src/CsIndex.Query/Symbols/SymbolPatternMatcher.cs`
- Create: `tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs`
- Modify: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`

**Interfaces:**
- Produces: `FunctionTargetFilter(IndexedSymbolKind? Kind, AsyncStatusFilter AsyncStatus)` where `default` is unfiltered.
- Produces: `ExecutableTargetResolver.ResolveAsync(long profileId, string queryText, bool sourceOnly, bool includeOverrides, FunctionTargetFilter filter, CancellationToken)`.
- Produces: optional `FunctionTargetFilter` parameters on definition/reference/caller/callee/override/graph/source service methods while preserving existing overloads.
- Consumes: `MethodTargetResolver` only for exact method roots and descendant-only override expansion.

- [ ] **Step 1: Write failing service acceptance tests for target placement**

Create `FunctionTargetFilterTests` using `SemanticIndexFixture`. Cover all matrix rows from revised section 2.2 and assert the filter applies to the target/root, not secondary callers/callees/nodes. Include these explicit cases:

```csharp
var lambdaDefinition = await fixture.Query.FindDefinitionsAsync(
    "::<lambda#1>", filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All), cancellationToken: token);
var lambdaCallees = await fixture.Query.FindCalleesAsync(
    "Alpha.LambdaPlayer::Execute()::<lambda#1>", GeneratedFilter.Include,
    filter: new(IndexedSymbolKind.Lambda, AsyncStatusFilter.All), cancellationToken: token);
var syncInvolved = await fixture.Query.ListSymbolsAsync(
    kind: null, asyncStatus: AsyncStatusFilter.Sync, asyncInvolved: true, cancellationToken: token);
```

Test omission vs explicit `All` by ordered symbol IDs. Test method, constructor, local function, accessor, operator, and conversion as `Kind=Method`. Test type exclusion for async/sync.

- [ ] **Step 2: Write failing graph-root tests for lambda, ambiguity, and path preservation**

Add lambda roots to `async tree` and `callers tree`, including two owners with the same `<lambda#1>`. Assert exact owner-qualified queries work, suffix-only ambiguity lists candidates in stable order, and filtering a root does not remove intermediate saved path/caller nodes.

- [ ] **Step 3: Run focused integration tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~FunctionTargetFilterTests|FullyQualifiedName~GraphQueryTests"`

Expected: FAIL because target APIs are method-only and do not accept the common filter.

- [ ] **Step 4: Implement `FunctionTargetFilter` and `ExecutableTargetResolver`**

Use this contract:

```csharp
public readonly record struct FunctionTargetFilter(
    IndexedSymbolKind? Kind,
    AsyncStatusFilter AsyncStatus)
{
    public bool Matches(StoredSymbol symbol) => /* executable-aware kind/direct-role predicate */;
}
```

Resolution rules:

1. Exact method syntax uses `MethodTargetResolver`; expand overrides first, then filter each real target.
2. Lambda marker syntax uses existing canonical wildcard/suffix matching over stored executable symbols.
3. `sourceOnly` requires source-backed `Method`/`Lambda` with normalized source.
4. Multi-target commands retain repository order.
5. Graph commands call the same resolver, then enforce exactly one filtered root and retain existing candidate formatting.
6. `Kind=Lambda` plus override expansion fails; `Kind=null` and `Kind=Method` allow exact method override expansion.
7. Never synthesize delegate/event/callback edges.

- [ ] **Step 5: Route service methods through the common resolver**

Preserve old overloads by forwarding `filter: default`. Apply filters as follows:

- `SearchSymbolsAsync`: candidate/match result; explicit `All` leaves exact type search unchanged.
- `ShowSourceAsync`/`SearchSourceAsync`: source-backed result.
- `FindDefinitionsAsync` and `FindDefinitionAtAsync`: definition result.
- `FindReferencesAsync`/`FindCallersAsync`: callee target only.
- `FindCalleesAsync`: caller root only.
- `FindAsyncPathAsync`/`FindCallerTreeAsync`: root only.
- `FindOverridesAsync`: method target only.
- `ListSymbolsAsync`: forward kind, async status, and async involvement to Storage.

Change graph error text from `source-backed method` to `source-backed executable`.

- [ ] **Step 6: Run focused tests and refactor while GREEN**

Run the Step 3 command. Then run: `rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore`.

Expected: PASS, no changed ordering in existing tests.

- [ ] **Step 7: Commit shared target resolution**

```powershell
rtk git add src/CsIndex.Query tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs tests/CsIndex.IntegrationTests/GraphQueryTests.cs
rtk git commit -m "feat: resolve filtered method and lambda targets"
```

### Task 3: CLI Filter Grammar, Output-Format Rename, and Help Matrix

**Files:**
- Modify: `src/CsIndex.Cli/CliArguments.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`

**Interfaces:**
- Produces: `ParseFunctionTargetFilter(CliArguments)` with `all -> null/All`, `method`, `lambda`, `async`, and `sync` mappings.
- Produces: `--output-format` as the only format option name.
- Produces: exact short-option normalization `-o <path>` to the canonical `output-file` key; file writing is completed in Task 6.
- Consumes: Task 2 service filter parameters.

- [ ] **Step 1: Add failing parser and command-matrix tests**

Assert:

```csharp
Assert.Equal("result.json", CliArguments.Parse(["-o", "result.json"]).GetSingle("output-file"));
Assert.Equal("result.json", CliArguments.Parse(["--output-file=result.json"]).GetSingle("output-file"));
Assert.Throws<CliUsageException>(() => CliArguments.Parse(["-o"]));
Assert.Throws<CliUsageException>(() => CliArguments.Parse(["-o", "a", "--output-file", "b"]).GetSingle("output-file"));
```

End-to-end, verify all method/lambda search commands accept `--kind all|method|lambda` and `--async-status all|async|sync`; `index` and `conditions` reject both. Verify `overrides --kind lambda` and `--kind lambda --include-overrides` return argument exit code 2 with an explanatory message. Verify `--output` is unknown for every command that used to accept it.

- [ ] **Step 2: Run CLI tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~CliCommandTests`

Expected: FAIL on short-option parsing, new values, command wiring, and renamed format option.

- [ ] **Step 3: Implement exact `-o` parsing and canonical duplicate detection**

In `CliArguments.Parse`, recognize only `token == "-o"`; consume one non-empty following value and store it under `output-file`. Leave `-opath` and `-o=<path>` unrecognized as options. Keep long-option behavior unchanged. Ensure both aliases share one value list so `GetSingle("output-file")` reports one-only duplication.

- [ ] **Step 4: Implement common option parsing and validation**

Use exact messages from the revised spec:

```text
Unknown symbol kind: <value>. Use all, method, or lambda.
Unknown async status: <value>. Use all, async, or sync.
--kind lambda is not applicable to overrides.
```

Allow `--include-overrides` with exact method queries when kind is omitted, `all`, or `method`; reject `lambda`. Apply both filters before `--require-single` counts.

- [ ] **Step 5: Replace the format option and synchronize all help text**

Change every allowed-option list, parser lookup, help entry, usage string, and test from `output` to `output-format`. Do not keep an alias. Define the reusable `source-layout` and output-file help entries in this task, but add them to a command help page only in the task that makes that option operational.

- [ ] **Step 6: Run CLI tests and confirm GREEN**

Run the Step 2 command. Expected: PASS and no old `--output` text in CLI source/help tests.

- [ ] **Step 7: Commit CLI grammar and filter wiring**

```powershell
rtk git add src/CsIndex.Cli/CliArguments.cs src/CsIndex.Cli/Program.cs tests/CsIndex.IntegrationTests/CliCommandTests.cs
rtk git commit -m "feat: expose common function query filters"
```

### Task 4: Zero-Width Token Normalization and Cache Invalidation

**Files:**
- Modify: `src/CsIndex.Core/Analysis/SourceNormalizer.cs`
- Modify: `src/CsIndex.Core/Caching/RequestHasher.cs`
- Modify: `tests/CsIndex.Core.Tests/SourceNormalizerTests.cs`
- Create: `tests/CsIndex.Core.Tests/RequestHasherTests.cs`
- Modify: `tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs`

**Interfaces:**
- Produces: normalized text that ignores missing/omitted zero-width tokens before separator decisions.
- Produces: `RequestHasher.AnalysisCacheVersion = 2`, included in every request hash while `SchemaVersion` remains `4`.

- [ ] **Step 1: Add failing array-rank and hash tests**

Parse declarations and assert exact normalized fragments:

```csharp
Assert.Contains("string[]args", Normalize("void M(string[] args){}"));
Assert.Contains("int[,]matrix", Normalize("void M(int[,] matrix){}"));
Assert.Contains("int[][]values", Normalize("void M(int[][] values){}"));
Assert.Contains("string?[]items", Normalize("void M(string?[] items){}"));
```

Add separate cases proving array creation sizes, attributes, indexers, collection expressions, interpolated/raw literals, and comment removal remain unchanged. Assert `HashUtilities.Sha256(result.Text)` equals `result.Hash`.

- [ ] **Step 2: Run Core tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SourceNormalizerTests|FullyQualifiedName~RequestHasherTests"`

Expected: FAIL on array-rank spacing and absent analysis cache version.

- [ ] **Step 3: Filter zero-width tokens before pair separation**

In `NormalizeTokens`, skip tokens when `token.IsMissing || token.Text.Length == 0` before reading/updating `previous`. Do not modify non-empty literal `Text`. Continue calculating SHA-256 from the final corrected string.

- [ ] **Step 4: Invalidate analysis cache without changing schema**

Add:

```csharp
public const int AnalysisCacheVersion = 2;
```

Include it in the anonymous object serialized by `RequestHasher.Build`. Add a hash test proving otherwise identical request material changes when the version component changes via a testable internal helper or golden hash update; keep `SchemaVersion == 4`.

- [ ] **Step 5: Run focused and existing extraction tests and confirm GREEN**

Run the Step 2 command, then:

`rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ExecutableSymbolExtractionTests`

Expected: PASS; literal/comment/token-boundary regressions remain green.

- [ ] **Step 6: Commit normalization and cache invalidation**

```powershell
rtk git add src/CsIndex.Core tests/CsIndex.Core.Tests
rtk git commit -m "fix: normalize zero-width array rank tokens"
```

### Task 5: Fixed Table Records and Display-Only Sanitization

**Files:**
- Create: `src/CsIndex.Cli/SourceLayout.cs`
- Create: `src/CsIndex.Cli/TableTextSanitizer.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`

**Interfaces:**
- Produces: `SourceLayout.SingleLine` and `SourceLayout.MultiLine`.
- Produces: `TableTextSanitizer.Sanitize(string?)` replacing CRLF as one space and TAB/CR/LF/U+0085/U+2028/U+2029 as ASCII space.
- Produces: `OutputFormatter(string format, bool shortNames, SourceLayout sourceLayout, TextWriter writer, TextWriter diagnosticsWriter)`; callers may retain convenience defaults for tests.

- [ ] **Step 1: Add failing formatter tests for record schemas**

For one mixed source-backed/metadata-only result set, assert exact physical lines and field counts:

```csharp
Assert.Matches(@"^[^\t]+\t[^\t]*$", symbolFindLine);
Assert.Matches(@"^[^\t]+\t[^\t]*\t[^\t]*$", symbolFindWithSourceLine);
Assert.DoesNotContain('\t', symbolListLine);
```

Assert single-line summary is written only to the diagnostics writer, zero results leave payload empty, and multi-line retains the heading plus one sanitized signature line and one sanitized `    source:` line per result.

- [ ] **Step 2: Add failing control-character and losslessness tests**

Use source containing `\t`, CRLF, standalone CR/LF, U+0085, U+2028, U+2029, and a raw multiline literal. Assert table physical lines are not split and contain only separator tabs. Separately serialize JSON and assert `normalizedSource` equals the original unsanitized stored value.

- [ ] **Step 3: Run formatter tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~OutputFormatterTests`

Expected: FAIL because table output still has headings/indentation and writes through `Console`.

- [ ] **Step 4: Implement injected writers and sanitization**

Replace every payload `Console.WriteLine` in `OutputFormatter` with `_writer.WriteLine`; write single-line symbol/source/list summaries to `_diagnosticsWriter`. Implement records as:

```csharp
writer.WriteLine($"{signature}\t{location}");
writer.WriteLine($"{signature}\t{location}\t{source}");
writer.WriteLine(signature);
```

Use empty location/source fields for metadata-only candidates. Keep all JSON object names and values unchanged and send JSON to `_writer`.

- [ ] **Step 5: Parse and validate `--source-layout` only where meaningful**

Default to `single-line`. Accept it only for `symbol find --show-source`, `source show`, and `source search`; reject JSON combinations, `symbol find` without `--show-source`, symbol list, and all non-source commands. Emit the exact unknown-value message from the revised spec.

- [ ] **Step 6: Run formatter and CLI tests and confirm GREEN**

Run the Step 3 command and the Task 3 CLI test command. Expected: PASS; JSON remains lossless.

- [ ] **Step 7: Commit table layout changes**

```powershell
rtk git add src/CsIndex.Cli tests/CsIndex.IntegrationTests/OutputFormatterTests.cs tests/CsIndex.IntegrationTests/CliCommandTests.cs
rtk git commit -m "feat: emit stable single-line symbol records"
```

### Task 6: Atomic Output Files and Graph Writer Injection

**Files:**
- Create: `src/CsIndex.Cli/OutputDestination.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `src/CsIndex.Cli/GraphOutputFormatter.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`

**Interfaces:**
- Produces: `OutputDestination.Create(string? outputPath, string databasePath)` with `Writer`, `Commit()`, and cleanup-on-dispose semantics.
- Produces: `OutputException`, caught before the generic exception handler and rendered as `Output error: ...` with exit code `3`.
- Produces: `GraphOutputFormatter(bool shortNames, TextWriter writer)` and writer-aware `OutputFormatter.WriteJson`.

- [ ] **Step 1: Add failing destination unit/integration tests**

Cover exact `-o`/`--output-file` equality, UTF-8 without BOM, stdout empty on success, stderr preservation, successful replacement, missing parent, duplicate aliases, output path equal to DB, and extension-independent format selection. For every payload family compare file bytes/text with captured stdout:

- table and JSON symbol/source/query output
- async tree, line, and JSON
- caller tree, Mermaid, and JSON
- conditions table and JSON

- [ ] **Step 2: Add failing failure-atomicity tests**

Pre-create a destination with sentinel content. Trigger query failure, formatter cancellation, and write/replace failure; assert sentinel content is unchanged and no sibling temporary file remains. Also assert no output file is opened for help.

- [ ] **Step 3: Run CLI/formatter tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CliCommandTests|FullyQualifiedName~OutputFormatterTests"`

Expected: FAIL because output-file is parsed but no destination/writer implementation exists.

- [ ] **Step 4: Implement path validation and destination ownership**

Resolve relative paths against `Environment.CurrentDirectory`. Before query execution, reject a normalized destination equal to the active DB path as `CliUsageException`. Report missing parent and other I/O failures through `OutputException`.

When rendering begins, create a uniquely named temp file in the destination directory with `FileMode.CreateNew` and `new UTF8Encoding(false)`. `Commit()` must flush/dispose, then use `File.Replace` when the destination exists or `File.Move` when it does not. `Dispose()` without commit closes and deletes the temp file and never closes `Console.Out`.

- [ ] **Step 5: Inject the same writer into every payload formatter**

Replace all graph `Console.WriteLine` calls and static JSON console writes with the injected writer. Keep help on `Console.Out` and diagnostics/warnings/progress/errors on `Console.Error`. Open the temp file only after successful argument/query processing and call `Commit()` only after the formatter returns without cancellation/error.

- [ ] **Step 6: Run focused tests and confirm GREEN**

Run the Step 3 command. Expected: PASS, no temp files, no BOM, and byte-equivalent payloads.

- [ ] **Step 7: Commit atomic output support**

```powershell
rtk git add src/CsIndex.Cli tests/CsIndex.IntegrationTests
rtk git commit -m "feat: add atomic result file output"
```

### Task 7: Complete the Required Acceptance Matrix

**Files:**
- Modify: `tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs`
- Modify as failures require: files already owned by Tasks 1-6

**Interfaces:**
- Consumes: all completed filter, resolver, layout, normalizer, and output contracts.
- Produces: direct automated coverage for every numbered requirement in revised sections 8.1-8.5.

- [ ] **Step 1: Build a coverage table in test names before changing implementation**

Add/extend parameterized tests so each numbered acceptance item maps to an assertion. Use descriptive names such as `KindAllMatchesOmissionAcrossCommandMatrix`, `AsyncStatusFiltersAfterOverrideExpansion`, `SingleLineRecordsHaveFixedFieldsForMetadataSymbols`, and `OutputFailurePreservesExistingDestination`.

- [ ] **Step 2: Run the full Integration test project and record remaining RED failures**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore`

Expected: any uncovered edge cases fail for a specific asserted contract; existing tests must remain enabled.

- [ ] **Step 3: Make the minimum implementation corrections for each failing contract**

Fix only behavior required by revised sections 8.1-8.5. Preserve target-only graph filtering, exact-query type behavior for `all`, stored-edge limitations for lambdas, record field schemas, and destination atomicity.

- [ ] **Step 4: Re-run Integration tests until GREEN**

Run the Step 2 command after each correction. Expected final result: all tests pass, zero failed, zero skipped because of this feature.

- [ ] **Step 5: Run Core, Query, and Storage projects to catch cross-layer regressions**

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj -c Release --no-restore
```

Expected: all pass with zero warnings.

- [ ] **Step 6: Commit acceptance closure**

```powershell
rtk git add src tests
rtk git commit -m "test: close cli revision acceptance matrix"
```

### Task 8: Synchronize Normative Documentation

**Files:**
- Modify: `docs/SPEC.md`
- Modify: `docs/CLI.md`
- Modify: `docs/DECISIONS.md`
- Modify: `docs/TEST_PLAN.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`
- Modify: `docs/KNOWN_LIMITATIONS.md`

**Interfaces:**
- Produces: documentation matching actual defaults, values, applicability matrix, errors, file semantics, and reindex requirement.
- Consumes: verified implementation behavior from Tasks 1-7; do not document unimplemented behavior.

- [ ] **Step 1: Update the formal specification and CLI reference**

Add the common `--kind`/`--async-status` matrix, direct-vs-derived async distinction, lambda target grammar, `--source-layout` record schemas, display sanitization, `--output-format`, and `-o`/`--output-file` atomic contract. Remove normative uses of the old `--output` option.

- [ ] **Step 2: Record architectural decisions and limitations**

Add accepted decisions for common executable target filtering and injected atomic output. State that delegate `Invoke`, event, callback, reflection, and runtime-flow lambda references are not inferred. State that schema remains v4, cache version changes, reindex occurs on the next index request, and direct queries against an already-built DB need `index --rebuild` to refresh normalized source.

- [ ] **Step 3: Update test/status documents with implemented coverage**

Map revised sections 8.1-8.5 to concrete test classes/methods. Update current phase, completed work, important files, schema version, command list, and remaining limitations. Preserve the last verified build/test totals until Task 9 replaces them with a fresh full-suite record; do not write speculative counts.

- [ ] **Step 4: Validate documentation consistency**

Run:

```powershell
rtk rg -n -e "--output( |$)" docs src/CsIndex.Cli tests/CsIndex.IntegrationTests
rtk rg -n -e "--kind|--async-status|--source-layout|--output-format|--output-file" docs/CLI.md docs/SPEC.md src/CsIndex.Cli/Program.cs
rtk rg -n -e "schema version 4|Schema version 4|schema v4" docs/SPEC.md docs/IMPLEMENTATION_STATUS.md docs/KNOWN_LIMITATIONS.md
```

Expected: the first command finds only deliberate historical text explaining that `--output` is rejected; all active grammar uses `--output-format`.

- [ ] **Step 5: Commit documentation**

```powershell
rtk git add docs
rtk git commit -m "docs: specify revised cli query and output contracts"
```

### Task 9: Fresh Final Verification and Diff Review

**Files:**
- Inspect only: entire worktree diff and generated build/test outputs.
- Modify: `docs/IMPLEMENTATION_STATUS.md` and `docs/TEST_PLAN.md` with the fresh verification record.
- Modify other files only if a verification failure exposes an in-scope defect; repeat RED-GREEN for that defect.

**Interfaces:**
- Produces: evidence required by `docs/2026-08-11.revised.md` section 10 and `superpowers:verification-before-completion`.

- [ ] **Step 1: Inspect the actual diff and scope**

Run:

```powershell
rtk git status --short
rtk git diff --check
rtk git diff --stat origin/main...HEAD
rtk git diff --name-only origin/main...HEAD
```

Confirm no schema DDL/version change and no unrelated refactor.

- [ ] **Step 2: Run formatting verification**

Run: `rtk dotnet format CsIndex.sln --verify-no-changes --no-restore`

Expected: exit code 0, no files requiring formatting.

- [ ] **Step 3: Run Release build**

Run: `rtk dotnet build CsIndex.sln -c Release --no-restore`

Expected: exit code 0, 0 warnings, 0 errors.

- [ ] **Step 4: Run the complete test suite freshly**

Run: `rtk dotnet test CsIndex.sln -c Release --no-build --no-restore`

Expected: exit code 0, 0 failed tests; record exact passed/skipped totals.

- [ ] **Step 5: Record the fresh verification evidence**

Write the exact Release build warning/error totals, test passed/failed/skipped totals, command lines, and verification date into `docs/IMPLEMENTATION_STATUS.md` and the fresh-verification section of `docs/TEST_PLAN.md`. Commit only these evidence updates:

```powershell
rtk git add docs/IMPLEMENTATION_STATUS.md docs/TEST_PLAN.md
rtk git commit -m "docs: record cli revision verification"
```

- [ ] **Step 6: Exercise help and representative CLI smoke cases**

Run the built executable for global help and every command help. Confirm valid commands list `--kind all|method|lambda`, `--async-status all|async|sync`, `--source-layout single-line|multi-line`, `--output-format`, and `-o`/`--output-file` only where applicable, with no active `--output` entry. Run one stdout/file equivalence smoke case against the fixture or a fresh temporary index.

- [ ] **Step 7: Perform final implementation and documentation review**

Review acceptance criteria, failure paths, cancellation, writer ownership, temp cleanup, deterministic ordering, and JSON losslessness. If a defect is found, add a focused failing test, fix it, and repeat Steps 1-6.

- [ ] **Step 8: Commit any verification-only corrections**

If Step 7 required changes:

```powershell
rtk git add -u
rtk git commit -m "fix: close final cli revision review"
```

Do not create an empty commit when no correction was needed.
