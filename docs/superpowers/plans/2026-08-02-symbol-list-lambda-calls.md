# Symbol Listing, Lambda Calls, and Short Names Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add namespace-shortened presentation, recursive lambda-call inclusion, per-owner lambda naming coverage, and the `symbol list` CLI command without changing canonical indexed identities.

**Architecture:** Keep Roslyn extraction and persisted names fully qualified. Add repository/service query APIs for function listing and descendant caller IDs, then apply all display transformations in the CLI formatter. Make `callees` opt out of descendant lambda calls with `--exclude-lambda-calls`, while `symbol list` filters methods/lambdas and async involvement at query time.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), Roslyn, SQLite via `Microsoft.Data.Sqlite`, xUnit v3, existing CLI argument parser and output formatter.

## Global Constraints

- Default output must retain exact namespace-qualified names.
- `--short-names` is presentation-only; stored names, stable keys, query matching, and JSON `fullyQualifiedName` remain exact.
- `symbol list` defaults to `Method` and `Lambda`; accepted `--kind` values are only `method` and `lambda`.
- `--async-involved` means `async_involvement_depth IS NOT NULL`, including depth `0` roots.
- `callees` includes descendant lambda calls by default; `--exclude-lambda-calls` restores direct-caller-only behavior.
- Nested lambda ownership must be recursive and lambda numbering is per containing owner, starting at `<lambda#1>`.
- `Invoke()` execution sites do not affect lambda ownership or call extraction.
- Preserve `net10.0-windows`, nullable enabled, implicit usings, latest C# language version, deterministic builds, and warnings-as-errors.

---

### Task 1: Lock down lambda ownership and nested numbering with core tests

**Files:**
- Modify: `tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs`
- Inspect only: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`, `src/CsIndex.Core/Analysis/AnalysisState.cs`

**Interfaces:**
- Consumes: `AnalysisCoordinator` test helper and `IndexSnapshot.Symbols`.
- Produces: regression tests proving the existing owner map gives each method and lambda an independent counter and that nested lambda calls use the innermost lambda as caller.

- [ ] **Step 1: Write characterization tests for per-method and nested-lambda numbering.** Add a test source with `Update` containing two lambdas, `Do` containing two lambdas, and the first `Update` lambda containing a nested lambda. Assert display names are exactly `Player::Update()::<lambda#1>`, `Player::Update()::<lambda#2>`, `Player::Do()::<lambda#1>`, `Player::Do()::<lambda#2>`, and `Player::Update()::<lambda#1>::<lambda#1>`. Assert the nested lambda's `ContainingSymbolKey` points to the outer lambda stable key.
- [ ] **Step 2: Write a characterization test for calls in nested lambdas.** In the same source, invoke `Play`, `CreateCallbackInnerObj`, and `Calc` at the three nesting levels. Assert each invocation has a caller symbol of the expected lambda kind and that the `Calc` call caller is the nested lambda, not the enclosing method or outer lambda.
- [ ] **Step 3: Run the focused core tests and record the baseline.** Run `dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~AsyncSemanticExtractorTests`. If the characterization tests fail, continue with Step 4; if they pass, keep extraction unchanged and use them as regression coverage.
- [ ] **Step 4: Correct extraction only when the baseline exposes a regression.** Ensure `CreateLambdaOwners` traverses outer lambdas before nested lambdas, uses a counter keyed by `outerOwner`, and `DocumentAnalysisState.FindOwner` checks `LambdaOwners` before enclosing methods. Do not alter stable-key construction or persisted display names when the baseline already satisfies the tests.
- [ ] **Step 5: Re-run the focused core tests.** The new tests and all existing async extractor tests must pass.

### Task 2: Add a tested presentation shortener

**Files:**
- Create: `src/CsIndex.Cli/SymbolNameShortener.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`

**Interfaces:**
- Consumes: `StoredSymbol.DisplayName`, `StoredCall` display fields, and the `shortNames` boolean supplied by `Program`.
- Produces: `SymbolNameShortener.Shorten(string)` and formatter output with exact names by default and shortened human-facing names when requested.

- [ ] **Step 1: Write failing unit/output tests.** Add assertions that `Nop.Core.Caching.DistributedCacheLocker::RunWithHeartbeatAsync(System.String,System.TimeSpan,System.TimeSpan,System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>,System.Threading.CancellationTokenSource)` becomes `DistributedCacheLocker::RunWithHeartbeatAsync(String,TimeSpan,TimeSpan,Func<CancellationToken, Tasks.Task>,CancellationTokenSource)`; add tests for nested generic arguments, arrays, nullable suffixes, lambda display names, JSON `fullyQualifiedName` preservation, and call table caller/callee shortening.
- [ ] **Step 2: Run only the new formatter tests.** Run `dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~OutputFormatterTests`; verify failures occur because no short-name API/formatter overload exists.
- [ ] **Step 3: Implement `SymbolNameShortener`.** Strip namespace qualification from the owner before `::`, recursively shorten qualified type tokens inside parameter and generic text, retain generic punctuation/arrays/nullable markers, and leave lambda suffixes intact. Keep the algorithm string-based and independent of Roslyn/query-time compilation.
- [ ] **Step 4: Thread the option through `OutputFormatter`.** Change the constructor to accept `bool shortNames = false`; use the shortener only for table `DisplayName`, call `caller`/`callee`, relation source/target, and JSON human-facing display fields. Keep JSON `fullyQualifiedName`, stable keys, namespace fields, and exact parameter type arrays unchanged.
- [ ] **Step 5: Re-run formatter tests and the full integration output test class.** Confirm default output is byte-compatible for existing cases and `--short-names` only changes requested presentation.

### Task 3: Add repository APIs for function listing and recursive lambda callers

**Files:**
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Modify: `tests/CsIndex.Storage.Tests/SqliteIndexTests.cs`

**Interfaces:**
- Consumes: `StoredProfile.Id`, `IndexedSymbolKind`, `GeneratedFilter`, and persisted `symbols.containing_symbol_id`/`async_involvement_depth`.
- Produces: `FindFunctionSymbolsAsync(long profileId, IndexedSymbolKind? kind, bool asyncInvolved, CancellationToken)` and `GetCallsByCallerIncludingLambdaDescendantsAsync(long profileId, IEnumerable<long> rootIds, GeneratedFilter, IReadOnlySet<ReferenceKind>?, CancellationToken)`.

- [ ] **Step 1: Write failing storage tests.** Seed a temporary SQLite snapshot with a method, a local function, an outer lambda, a nested lambda, and calls from each. Assert function listing returns method/lambda kinds, `kind` filters work, and `asyncInvolved` excludes null-depth symbols. Assert recursive calls include direct lambda and nested lambda callers but do not include an unrelated method.
- [ ] **Step 2: Run the focused storage tests and confirm the expected missing-method failures.** Run `dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --filter FullyQualifiedName~SqliteIndexTests`.
- [ ] **Step 3: Implement `FindFunctionSymbolsAsync`.** Reuse the existing symbol projection/read path, constrain `s.kind` to method/lambda by default, apply optional single-kind and `s.async_involvement_depth IS NOT NULL` predicates, join documents/projects for locations and assemblies, and order by display name/path/source offset.
- [ ] **Step 4: Implement recursive call retrieval.** Use a SQLite recursive CTE rooted at the selected symbol IDs; walk all `containing_symbol_id` descendants so lambdas nested below local functions are found, then select only callers with `kind = Lambda` in the recursive set plus direct roots. Reuse existing generated/reference-kind predicates and `BuildCallSelect` so `StoredCall` fields remain consistent.
- [ ] **Step 5: Re-run focused storage tests and the complete storage test project.** Confirm SQL parameterization and profile isolation remain intact.

### Task 4: Expose listing and descendant-call queries in `SemanticQueryService`

**Files:**
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Add/modify: `tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs`

**Interfaces:**
- Consumes: repository APIs from Task 3.
- Produces: `ListSymbolsAsync(IndexedSymbolKind? kind, bool asyncInvolved, string? profileName, CancellationToken)` returning `QueryContext`, and `FindCalleesAsync(string queryText, GeneratedFilter generatedFilter, bool includeLambdaCalls = true, string? profileName = null, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Extend fixture source with nested lambda calls.** Add a class containing one ordinary method call, an outer lambda call, an inner object-creation call, and calls in two nested lambdas; ensure the source compiles without requiring runtime delegate invocation.
- [ ] **Step 2: Write failing integration tests.** Test `ListSymbolsAsync()` defaults to methods and lambdas, `kind: IndexedSymbolKind.Lambda` returns only lambdas, and `asyncInvolved: true` excludes non-involved symbols. Test `FindCalleesAsync` returns direct plus nested lambda calls by default and only direct calls with `includeLambdaCalls: false`. Reserve text parsing and invalid-value assertions for the CLI task, where `CliUsageException` is defined.
- [ ] **Step 3: Run the new integration tests and verify they fail due to absent APIs/behavior.** Run `dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~PhaseOneAcceptanceTests`.
- [ ] **Step 4: Implement `ListSymbolsAsync`.** Resolve the latest/profile-selected profile, accept an already validated `IndexedSymbolKind?` (`null` means method plus lambda), call `FindFunctionSymbolsAsync`, and return a `QueryContext`. Keep string-to-enum validation in `Program`.
- [ ] **Step 5: Implement the `includeLambdaCalls` branch.** Keep the current direct repository call when false; call the recursive repository method when true. Keep `CallKinds` limited to invocation/object creation and preserve generated filtering.
- [ ] **Step 6: Re-run the focused integration tests and then all Query/Integration tests.** Confirm existing `callees` behavior for methods without lambdas is unchanged.

### Task 5: Add CLI command/option parsing and list output

**Files:**
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `src/CsIndex.Cli/CliArguments.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Add: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`

**Interfaces:**
- Consumes: `SemanticQueryService.ListSymbolsAsync`, formatter `shortNames`, and existing `ParseQueryArguments`/`ParseGeneratedFilter` helpers.
- Produces: `csindex symbol list`, `--short-names` accepted across symbol/definition/reference/caller/callee/override commands, and `--exclude-lambda-calls` accepted by `callees`.

- [ ] **Step 1: Write failing CLI tests.** Exercise `symbol list`, `symbol list --kind method`, `symbol list --kind lambda`, `symbol list --async-involved`, `symbol list --output json`, `callees ... --exclude-lambda-calls`, and `symbol find ... --short-names`. Assert JSON uses `symbols`, table output includes locations/async annotations, and unknown `--kind`/unsupported option combinations return invalid-argument behavior. Add a real temporary SQLite fixture and invoke `Program.Main` with `--db` so tests verify option parsing and output end-to-end.
- [ ] **Step 2: Run the CLI-focused tests and verify failures.** Run `dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~CliCommandTests`.
- [ ] **Step 3: Add the `symbol list` dispatch.** Match `args[0] == "symbol" && args[1] == "list"`, parse `db`, `profile`, `output`, `kind`, `async-involved`, `short-names`, and `help`, reject positionals other than none, convert only `method`/`lambda` to `IndexedSymbolKind`, call `ListSymbolsAsync`, then write with a dedicated `WriteSymbolList` method.
- [ ] **Step 4: Add `WriteSymbolList`.** For table output print count, one formatted symbol per line, location, and async annotation; for JSON output serialize `{ profile, symbols = ... }` using the same `ToSymbolObject` projection as `symbol find` and the short-name presentation option.
- [ ] **Step 5: Thread `--short-names` through every command.** Add it to each command's allowed option set, pass `parsed.HasFlag("short-names")` to `OutputFormatter`, and update help text. Add `exclude-lambda-calls` to `RunCalleesAsync`, pass `includeLambdaCalls: !parsed.HasFlag(...)`, and reject it for other commands.
- [ ] **Step 6: Re-run CLI tests and verify `--help` text.** Run `dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~CliCommandTests` and invoke the built CLI help command to confirm the new usage lines are present.

### Task 6: Document behavior and verify the complete repository

**Files:**
- Modify: `docs/CLI.md`
- Modify: `docs/DECISIONS.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`
- Modify: `docs/KNOWN_LIMITATIONS.md` if an obsolete lambda-search limitation is listed
- Test/verify: solution test and build commands

**Interfaces:**
- Consumes: final CLI flags and output schemas from Tasks 2–5.
- Produces: user-facing command documentation and a verified build/test result.

- [ ] **Step 1: Update CLI documentation.** Document the exact defaults and examples for `--short-names`, `symbol list`, `--kind`, `--async-involved`, and `callees --exclude-lambda-calls`; show that JSON `fullyQualifiedName` stays canonical while display names can be shortened.
- [ ] **Step 2: Update decisions/status documentation.** Record that namespace shortening is presentation-only, lambda numbering is per owner, recursive lambda descendant calls are default for `callees`, and list defaults to method plus lambda.
- [ ] **Step 3: Run focused tests for each changed area.** Run:

  ```powershell
  dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj
  dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj
  dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
  dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
  ```

- [ ] **Step 4: Run the full verification commands.** Run `dotnet test CsIndex.sln --configuration Release` and `dotnet build CsIndex.sln --configuration Release`; require exit code 0 and no warnings-as-errors failures.
- [ ] **Step 5: Inspect the final diff and status.** Run `git diff --check`, `git status --short`, and review every changed file against the four requirements before reporting completion.
- [ ] **Step 6: Commit the implementation.** Stage only source, test, and documentation files for this feature and commit with `feat: add symbol listing and lambda call output`.
