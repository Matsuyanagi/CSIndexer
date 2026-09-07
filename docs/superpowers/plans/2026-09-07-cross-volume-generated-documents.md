# Cross-volume Generated Compilation Documents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow generated C# documents supplied by MSBuild from another Windows volume or UNC share to remain in Roslyn compilation while excluding their source and paths from the portable index.

**Architecture:** `AnalysisCoordinator.PrepareAsync` creates one immutable `AnalysisDocumentSelection` immediately after workspace loading. The selection is shared by portable-path mapping, semantic extraction, diagnostics/counting, and per-project fingerprinting, so solution and project inputs use identical classification and no downstream component re-detects the disposition.

**Tech Stack:** C# 14 / .NET 10, Roslyn `Workspace` and `MSBuildWorkspace`, xUnit v3, SQLite integration tests, RTK-prefixed .NET and Git commands.

**Spec:** `docs/superpowers/specs/2026-09-07-cross-volume-generated-documents-design.md`

## Global Constraints

- Do not hard-code xUnit, a package name, a NuGet cache directory, or an MSBuild target/property.
- A cross-volume/share document is compilation-only only when the existing `GeneratedCodeDetector` positively identifies it.
- Ordinary cross-volume/share linked source remains an input error before SQLite is opened or mutated.
- Same-volume/share generated source remains a normal indexed physical document.
- Compilation-only documents remain in the original Roslyn `Project` and `Compilation`, but contribute no `DocumentData`, declaration, source body, call body, relation body, or query root.
- Persist no compilation-only absolute path and create no synthetic stored path.
- A per-project fingerprint represents each compilation-only document using the fixed marker `compilation-only-generated`, its Roslyn document name, generation kind, and content hash; ordering is ordinal and deterministic.
- Emit one warning and add exactly one to `DocumentsExcluded` for each compilation-only document.
- Preserve cancellation checks around text access and within classification, mapping, fingerprint, and extraction loops.
- Do not change the SQLite schema, query grammar, `--generated-source` behavior, or deployment/install location.
- Apply the behavior to every `MSBuildWorkspace` route: a directory that auto-selects a solution, an explicit `.sln`/`.slnx`, an explicit `.csproj`, directory `--mode project`, and directory `--solution`. Forced `--mode directory` remains the source-enumerator route and normally has no injected MSBuild documents.
- Follow RED-GREEN-REFACTOR. Do not edit production code before the focused failing test has been run and its expected failure recorded in the task report.
- Use `gpt-5.6-luna` with `max` reasoning effort for each implementation task. If Luna is unavailable for the current model-selection cycle, record the exact error in the ledger and retry that same brief once with `gpt-5.6-terra` at `max`.
- Never run concurrent implementation agents in this worktree.

---

## File and Responsibility Map

- Create `src/CsIndex.Core/Analysis/AnalysisDocumentSelection.cs`: immutable document disposition, warning, exclusion-count, and compilation-only fingerprint input.
- Modify `src/CsIndex.Core/Analysis/AnalysisCoordinator.cs`: classify once after workspace load and pass the selection to preparation.
- Modify `src/CsIndex.Core/Analysis/PreparedAnalysis.cs`: own the selection, aggregate warnings/counts, and map indexable documents only.
- Modify `src/CsIndex.Core/Analysis/SemanticExtractor.cs`: retain full compilations but iterate only selected indexable documents for source extraction.
- Modify `src/CsIndex.Core/Caching/ProjectFingerprintBuilder.cs`: hash mapped documents and path-free compilation-only items deterministically.
- Create `tests/CsIndex.Core.Tests/CompilationOnlyGeneratedDocumentTests.cs`: focused synthetic cross-volume behavior, semantics, fingerprints, and cancellation tests.
- Modify `tests/CsIndex.Core.Tests/PortableAnalysisPathTests.cs`: preserve and strengthen the ordinary cross-volume rejection/sentinel regression.
- Modify `tests/CsIndex.IntegrationTests/MsBuildWorkspaceTests.cs`: exercise the real xUnit v3 MSBuild-injected reporter without vendor-specific properties.
- Modify `docs/SPEC.md`, `docs/KNOWN_LIMITATIONS.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/TEST_PLAN.md`, `docs/DB_SCHEMA.md`, and `docs/CLI.md`: document the implemented portable-index exception and its input-mode scope.

## Recovery and Resume Contract

The tracked spec and this tracked plan are the authoritative recovery record. Execution occurs on branch `codex/cross-volume-generated-documents` in `E:/WorksDevelop/CSIndexer/.worktrees/cross-volume-generated-documents`, forked from `main` after this plan commit.

At execution start, create the plan-scoped ignored workspace with:

```powershell
bash "C:/Users/hy/.codex/plugins/cache/openai-curated-remote/superpowers/6.3.0/skills/subagent-driven-development/scripts/sdd-workspace" "docs/superpowers/plans/2026-09-07-cross-volume-generated-documents.md"
```

Its ledger is:

```text
.superpowers/sdd/2026-09-07-cross-volume-generated-documents/progress.md
```

The first ledger line must be exactly:

```text
# SDD ledger - plan: docs/superpowers/plans/2026-09-07-cross-volume-generated-documents.md
```

Before Task 1, append the feature branch, worktree, fork SHA, baseline command/result, the required task/interface preflight table, and `Task 1: pending`. After every RED, GREEN, commit, review, fix round, or ruling, append the exact command, exit code, pass/fail count, commit SHA, and report path before moving on.

After context compaction or a usage-limit stop:

1. Read this plan, its spec, and only this plan's ledger.
2. Run `rtk git status --short --branch` and `rtk git log -8 --oneline --decorate` in the recorded worktree.
3. Treat every `Task N: complete` ledger line as finished; do not repeat it.
4. If a task has a RED line but no GREEN line, resume at production implementation. If it has a GREEN line but no review-complete line, generate the review package and resume review. If it has a fix-round line, continue at the next recorded round.
5. At a usage-limit recovery boundary, try a fresh Luna/max implementer first, then the single Terra/max fallback allowed by `AGENTS.md`, using the existing brief and report files.
6. Trust committed history and fresh command output over conversational recollection.

## Acceptance Matrix

| Input form | Resolver/loader route | Required result |
|---|---|---|
| `csindex index .` with one solution | `InputMode.Solution` / `MSBuildWorkspace` | cross-volume generated documents are compilation-only |
| `csindex index CsIndex.sln` | `InputMode.Solution` / `MSBuildWorkspace` | same behavior |
| `csindex index tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj` | `InputMode.Project` / `MSBuildWorkspace` | same behavior |
| `csindex index . --mode project` | `InputMode.Project` / `MSBuildWorkspace` | same behavior for every loaded project |
| `csindex index . --solution CsIndex.sln` | `InputMode.Solution` / `MSBuildWorkspace` | same behavior |
| `csindex index . --mode directory` | `InputMode.Directory` / source enumerator | unchanged; no MSBuild document injection |

---

### Task 1: Classify Documents Once Before Portable Path Mapping

**Files:**
- Create: `src/CsIndex.Core/Analysis/AnalysisDocumentSelection.cs`
- Modify: `src/CsIndex.Core/Analysis/AnalysisCoordinator.cs`
- Modify: `src/CsIndex.Core/Analysis/PreparedAnalysis.cs`
- Create: `tests/CsIndex.Core.Tests/CompilationOnlyGeneratedDocumentTests.cs`
- Modify: `tests/CsIndex.Core.Tests/PortableAnalysisPathTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/MsBuildWorkspaceTests.cs`

**Interfaces:**
- Consumes: `PathNormalizer.SameVolumeShare`, `PathNormalizer.EnsureSameVolumeShare`, `GeneratedCodeDetector.Detect`, `HashUtilities.Sha256`, `LoadedWorkspace`, and `AnalysisPathMappings`.
- Produces:

```csharp
internal sealed record CompilationOnlyGeneratedDocument(
    ProjectId ProjectId,
    DocumentId DocumentId,
    string Name,
    GenerationKind GenerationKind,
    ImmutableArray<byte> ContentHash);

internal sealed class AnalysisDocumentSelection
{
    internal ImmutableArray<CompilationOnlyGeneratedDocument> CompilationOnlyDocuments { get; }
    internal ImmutableArray<string> Warnings { get; }
    internal int CompilationOnlyCount { get; }
    internal bool IsIndexable(Document document);
    internal bool IsCompilationOnly(Document document);
    internal ImmutableArray<CompilationOnlyGeneratedDocument> GetCompilationOnly(Project project);

    internal static Task<AnalysisDocumentSelection> CreateAsync(
        string storageRoot,
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken,
        Action? afterDocumentClassificationItem = null);
}
```

- `PreparedAnalysis` gains `internal AnalysisDocumentSelection DocumentSelection { get; }` and owns precomputed combined warnings/counts.
- `AnalysisPathMappings.CreateAsync` accepts `AnalysisDocumentSelection documentSelection` immediately before `CancellationToken` and skips every document for which `IsIndexable` is false.
- `AnalysisCoordinator` gains the test seam `internal Action? AfterDocumentClassificationItem { get; set; }`.

- [ ] **Step 1: Write focused tests that name the rejected and compilation-only branches**

Create theory coverage for both MSBuild input modes and use an `AdhocWorkspace`/`TextLoader` so a different-drive path can be represented without mounting another drive:

```csharp
[Theory]
[InlineData(InputMode.Solution)]
[InlineData(InputMode.Project)]
public async Task Prepare_CrossVolumeGeneratedDocumentIsCompilationOnlyForEveryMsBuildMode(InputMode mode)
{
    using var temporary = new TempDirectory();
    var projectPath = temporary.Write("Game/Game.csproj", "<Project />");
    var indexedPath = temporary.Write("Game/Main.cs", "namespace App; public sealed class Main { }");
    var generatedPath = CreateDifferentDrivePath(temporary.Path, @"packages\runner\DefaultRunnerReporters.cs");
    const string generatedSource = "// <auto-generated/>\nnamespace Runner; public sealed class Reporter { }";
    var loaded = CreateLoadedWorkspace(projectPath,
        (indexedPath, "Main.cs", File.ReadAllText(indexedPath)),
        (generatedPath, "DefaultRunnerReporters.cs", generatedSource));
    var input = new ResolvedInput(mode, projectPath, temporary.Path, [projectPath]);
    var paths = CreateStandardPaths(temporary.Path);
    var coordinator = AnalysisCoordinator.CreateForTesting((_, _, _) => Task.FromResult(loaded));

    using var prepared = await coordinator.PrepareAsync(
        input,
        new IndexOptions { InputPath = projectPath, ForcedMode = mode },
        paths,
        TestContext.Current.CancellationToken);

    var generatedDocument = Assert.Single(prepared.Projects.Single().Documents,
        document => document.FilePath == generatedPath);
    Assert.True(prepared.DocumentSelection.IsCompilationOnly(generatedDocument));
    Assert.Equal(1, prepared.DocumentsExcluded);
    var warning = Assert.Single(prepared.Warnings,
        value => value.Contains(generatedPath, StringComparison.OrdinalIgnoreCase));
    Assert.Contains("kept in the compilation", warning, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("excluded from the portable index", warning, StringComparison.OrdinalIgnoreCase);
    Assert.Throws<InputResolutionException>(() => prepared.Mappings.GetDocumentPath(generatedDocument));
}
```

Add a theory whose hand-authored expectations cover `GenerationKind.FileName`, `GenerationKind.GeneratedDirectory`, and `GenerationKind.AssemblyAttributes`. Add one same-volume `Bindings.g.cs` test that asserts it remains mapped and later produces a normal generated `DocumentData`. Keep `Prepare_RejectsCrossVolumeDocumentDisposesWorkspaceAndSkipsContinuation` asserting the ordinary source path, `same-volume/share`, disposed workspace, and untouched continuation sentinel.

Add a cancellation test that sets `AfterDocumentClassificationItem` to cancel after the first generated document; assert `OperationCanceledException` and disposed `LoadedWorkspace`. Add a `TextLoader` that throws from `LoadTextAndVersionAsync`; assert the read failure propagates, the workspace is disposed, and the continuation sentinel remains false.

Use `new FileTextLoader(unreadablePath, Encoding.UTF8)` for the read-failure document so the test exercises Roslyn's real file loader rather than a mock. Add these exact fixture helpers to the new test class:

```csharp
private static IndexPathResolver CreateStandardPaths(string storageRoot) =>
    IndexPathResolver.CreateForIndex(
        Path.Combine(storageRoot, ".csindex", "index.sqlite"),
        storageRoot);

private static string CreateDifferentDrivePath(string referencePath, string relativePath)
{
    var currentDrive = char.ToUpperInvariant(Path.GetPathRoot(referencePath)![0]);
    var otherDrive = currentDrive == 'Z' ? 'Y' : 'Z';
    return $"{otherDrive}:\\{relativePath}";
}
```

The `CreateLoadedWorkspace` helper must create a real `AdhocWorkspace`, one C# project with `CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)`, platform metadata references from `TRUSTED_PLATFORM_ASSEMBLIES`, and one `DocumentInfo` per `(Path, Name, Source)` tuple using `TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create(), path))`. Return a `LoadedWorkspace` with empty warnings and zero initial exclusions; never mock `Project`, `Document`, or `SourceText`.

Also add the real MSBuild regression now, before production changes, to `MsBuildWorkspaceTests`. Load `tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj`, locate `DefaultRunnerReporters.cs`, and prepare/analyze without any xUnit-specific MSBuild property:

```csharp
var reporter = Assert.Single(prepared.Projects
    .SelectMany(project => project.Documents)
    .Where(document => document.FilePath is not null)
    .Where(document => Path.GetFileName(document.FilePath)
        .Equals("DefaultRunnerReporters.cs", StringComparison.OrdinalIgnoreCase)));

var crossVolume = !PathNormalizer.SameVolumeShare(input.RootPath, reporter.FilePath!);
if (crossVolume)
{
    Assert.True(prepared.DocumentSelection.IsCompilationOnly(reporter));
    Assert.DoesNotContain(result.Snapshot.Documents,
        document => document.NormalizedPath.Contains("DefaultRunnerReporters.cs", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(result.Snapshot.Warnings,
        warning => warning.Contains("DefaultRunnerReporters.cs", StringComparison.OrdinalIgnoreCase));
}
else
{
    Assert.Contains(result.Snapshot.Documents,
        document => document.NormalizedPath.Contains("DefaultRunnerReporters.cs", StringComparison.OrdinalIgnoreCase));
}
```

The environmental branch is intentional: synthetic tests always execute the cross-volume branch, while this test proves real MSBuild injection remains usable both when the NuGet cache shares the repository volume and on the reported E:-repository/C:-NuGet layout.

- [ ] **Step 2: Run the Task 1 tests and record the expected RED**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CompilationOnlyGeneratedDocumentTests.Prepare_|FullyQualifiedName~PortableAnalysisPathTests.Prepare_RejectsCrossVolumeDocument"
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~MsBuildWorkspaceTests.ProjectMode_KeepsExternalGeneratedReporterCompilationOnly"
```

Expected: the new synthetic generated cases and, on the reported host, the real reporter regression fail with the existing same-volume/share error (or fail to compile because `DocumentSelection` does not exist); the existing ordinary-source rejection remains green. Record the exact failure in the task report before editing production files.

- [ ] **Step 3: Implement immutable classification**

Implement `AnalysisDocumentSelection.CreateAsync` with this branch order:

```csharp
foreach (var project in projects)
{
    foreach (var document in project.Documents)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document.FilePath is not { } documentPath ||
            PathNormalizer.SameVolumeShare(storageRoot, documentPath))
        {
            indexableDocumentIds.Add(document.Id);
            afterDocumentClassificationItem?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            continue;
        }

        var text = await document.GetTextAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var generated = GeneratedCodeDetector.Detect(documentPath, text);
        if (!generated.IsGenerated)
        {
            PathNormalizer.EnsureSameVolumeShare(storageRoot, documentPath);
            throw new UnreachableException();
        }

        compilationOnlyDocuments.Add(new CompilationOnlyGeneratedDocument(
            project.Id,
            document.Id,
            document.Name,
            generated.Kind,
            HashUtilities.Sha256(text.ToString()).ToImmutableArray()));
        warnings.Add(
            $"Generated document was kept in the compilation but excluded from the portable index because it is on another volume/share: {PathNormalizer.NormalizeAbsolute(documentPath)}");
        afterDocumentClassificationItem?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
```

Back the indexable IDs with a `FrozenSet<DocumentId>` and expose only immutable arrays. `IsIndexable` and `IsCompilationOnly` must use `DocumentId`, not path text, because linked documents and multi-target projects may reuse physical paths.

- [ ] **Step 4: Wire preparation and mapping to the single selection**

In `AnalysisCoordinator.PrepareAsync`, preserve the disposal `try/catch` and use this exact order:

```csharp
loaded = await _loadWorkspaceAsync(input, options, cancellationToken);
var documentSelection = await AnalysisDocumentSelection.CreateAsync(
    input.RootPath,
    loaded.Projects,
    cancellationToken,
    AfterDocumentClassificationItem);
var mappings = await AnalysisPathMappings.CreateAsync(
    input.RootPath,
    paths,
    loaded.Projects,
    documentSelection,
    cancellationToken,
    AfterPathValidationItem);
return new PreparedAnalysis(input, paths, loaded, documentSelection, mappings);
```

In `PreparedAnalysis`, calculate once in the constructor:

```csharp
_warnings = loadedWorkspace.Warnings.Concat(documentSelection.Warnings).ToImmutableArray();
_documentsExcluded = checked(loadedWorkspace.DocumentsExcluded + documentSelection.CompilationOnlyCount);
```

Return those values from `Warnings` and `DocumentsExcluded`. In the mapping loop, check `documentSelection.IsIndexable(document)` before validating the document path or requesting its syntax tree. Continue validating every project path and every selected document/source-tree path exactly as before.

- [ ] **Step 5: Run focused GREEN and surrounding portable-path tests**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CompilationOnlyGeneratedDocumentTests.Prepare_|FullyQualifiedName~PortableAnalysisPathTests"
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~MsBuildWorkspaceTests"
```

Expected: all selected tests pass with zero warnings. Confirm the ordinary cross-volume failure still occurs before the continuation sentinel, the generated warning/count is exactly once, and the task report names the real reporter path and whether it exercised the same-volume or cross-volume branch.

- [ ] **Step 6: Commit Task 1**

```powershell
rtk git add src\CsIndex.Core\Analysis\AnalysisDocumentSelection.cs src\CsIndex.Core\Analysis\AnalysisCoordinator.cs src\CsIndex.Core\Analysis\PreparedAnalysis.cs tests\CsIndex.Core.Tests\CompilationOnlyGeneratedDocumentTests.cs tests\CsIndex.Core.Tests\PortableAnalysisPathTests.cs tests\CsIndex.IntegrationTests\MsBuildWorkspaceTests.cs
rtk git commit -m "fix: classify external generated documents before path mapping"
```

Record the commit and request a task-scoped spec/quality review before Task 2.

---

### Task 2: Exclude Source Extraction While Preserving Compilation Semantics and Fingerprints

**Files:**
- Modify: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`
- Modify: `src/CsIndex.Core/Caching/ProjectFingerprintBuilder.cs`
- Modify: `tests/CsIndex.Core.Tests/CompilationOnlyGeneratedDocumentTests.cs`

**Interfaces:**
- Consumes: Task 1's `PreparedAnalysis.DocumentSelection`, `AnalysisDocumentSelection.IsIndexable`, and `GetCompilationOnly(Project)`.
- Produces:

```csharp
internal Task<byte[]> ProjectFingerprintBuilder.BuildAsync(
    Project project,
    string? storedProjectPath,
    AnalysisPathMappings paths,
    AnalysisDocumentSelection documentSelection,
    CancellationToken cancellationToken);
```

- `SemanticExtractor` private extraction receives both `AnalysisPathMappings` and `AnalysisDocumentSelection`. Its public project-list overload creates a selection before mappings; its `PreparedAnalysis` overload reuses the already-created selection without adding warning/count twice.

- [ ] **Step 1: Add semantic-boundary and fingerprint tests**

Add a test with one indexed source and one generated cross-volume source:

```csharp
[Fact]
public async Task Analyze_UsesCompilationOnlyDeclarationForBindingButDoesNotIndexItsSourceOrBody()
{
    const string indexedSource = """
        namespace App;
        public sealed class Consumer
        {
            public int Run() => GeneratedApi.Helper.Value();
        }
        """;
    const string generatedSource = """
        // <auto-generated/>
        namespace GeneratedApi;
        public static class Helper
        {
            public static int Value() => Hidden();
            private static int Hidden() => 42;
        }
        """;

    var snapshot = await AnalyzeWithCompilationOnlyDocumentAsync(
        indexedSource,
        generatedSource,
        @"packages\generator\Helper.generated.cs");

    var consumer = Assert.Single(snapshot.Symbols.Values, symbol => symbol.Name == "Run");
    var value = Assert.Single(snapshot.Symbols.Values, symbol => symbol.Name == "Value");
    Assert.NotNull(consumer.PreferredDeclarationKey);
    Assert.Null(value.PreferredDeclarationKey);
    Assert.Null(value.SourceDocumentKey);
    Assert.DoesNotContain(snapshot.Symbols.Values, symbol => symbol.Name == "Hidden");
    Assert.Contains(snapshot.Calls, call =>
        call.CallerSymbolKey == consumer.StableKey && call.CalleeDefinitionKey == value.StableKey);
    Assert.DoesNotContain(snapshot.Calls, call => call.CallerSymbolKey == value.StableKey);
    Assert.Single(snapshot.Documents);
    Assert.All(snapshot.Declarations.Values, declaration =>
        Assert.DoesNotContain("Helper.generated.cs", declaration.DocumentKey, StringComparison.OrdinalIgnoreCase));
}
```

Add fingerprint cases with literal expected relationships:

```csharp
Assert.Equal(firstRelocatedFingerprint, secondRelocatedFingerprint);
Assert.NotEqual(firstRelocatedFingerprint, changedContentFingerprint);
Assert.NotEqual(fileNameGenerationFingerprint, generatedDirectoryFingerprint);
```

Construct the generation-kind pair with the same `DocumentInfo.Name` and source content but different original file paths, so only `GenerationKind` changes. Use two different external absolute roots with the same name/kind/content for relocation equality. Analyze the same prepared value twice and assert identical fingerprint bytes to catch nondeterministic ordering.

- [ ] **Step 2: Run semantic/fingerprint tests and record RED**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CompilationOnlyGeneratedDocumentTests.Analyze_|FullyQualifiedName~CompilationOnlyGeneratedDocumentTests.ProjectFingerprint_"
```

Expected: source extraction currently requests a missing document mapping and/or the project fingerprint requests a path for the compilation-only document. The expected failure is the missing exclusion/fingerprint behavior, not fixture setup.

- [ ] **Step 3: Restrict source extraction to indexable documents**

Pass `documentSelection` through both extractor entry points. The project-list overload must perform the same classification path as coordinator preparation:

```csharp
var documentSelection = await AnalysisDocumentSelection.CreateAsync(
    storageRoot,
    projects,
    cancellationToken);
var mappings = await AnalysisPathMappings.CreateAsync(
    storageRoot,
    paths,
    projects,
    documentSelection,
    cancellationToken);
snapshot.Warnings.AddRange(documentSelection.Warnings);
snapshot.DocumentsExcluded = checked(
    snapshot.DocumentsExcluded + documentSelection.CompilationOnlyCount);
```

The prepared overload passes `prepared.DocumentSelection` and does not change the snapshot count/warnings because `AnalysisCoordinator.AnalyzeAsync` already seeded them. In `ExtractProjectDocumentsAndDeclarationsAsync`, filter before ordering so no missing mapping is requested:

```csharp
foreach (var document in projectState.Project.Documents
             .Where(documentSelection.IsIndexable)
             .OrderBy(
                 document => document.FilePath is null ? document.Name : mappings.GetDocumentPath(document),
                 StringComparer.OrdinalIgnoreCase))
```

Leave the existing missing-file, non-C#, and `obj` exclusions after this selection; they continue incrementing `DocumentsExcluded`. Because `ProjectAnalysisState.Documents` receives only selected document states, the existing fact/body pass naturally excludes compilation-only bodies.

- [ ] **Step 4: Add deterministic path-free compilation-only fingerprint items**

Pass the selection to `ProjectFingerprintBuilder.BuildAsync`. Keep the mapped-document loop but filter it with `documentSelection.IsIndexable`. Then append compilation-only items after deterministic sorting:

```csharp
foreach (var document in documentSelection.GetCompilationOnly(project)
             .OrderBy(item => item.Name, StringComparer.Ordinal)
             .ThenBy(item => item.GenerationKind)
             .ThenBy(item => Convert.ToHexString(item.ContentHash.AsSpan()), StringComparer.Ordinal))
{
    cancellationToken.ThrowIfCancellationRequested();
    Append(aggregate, "compilation-only-generated");
    Append(aggregate, document.Name);
    Append(aggregate, ((int)document.GenerationKind).ToString(CultureInfo.InvariantCulture));
    aggregate.AppendData(document.ContentHash.AsSpan());
}
```

Do not append `DocumentId`, project-machine paths, source-tree paths, or the original absolute file path.

- [ ] **Step 5: Run focused and extraction regression suites**

Run:

```powershell
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CompilationOnlyGeneratedDocumentTests|FullyQualifiedName~PortableAnalysisPathTests|FullyQualifiedName~ProjectScopedSourceSymbolIdentityTests|FullyQualifiedName~CallablePathExtractionTests|FullyQualifiedName~ExecutableSymbolExtractionTests|FullyQualifiedName~AsyncSemanticExtractorTests|FullyQualifiedName~SemanticExtractorCancellationTests"
```

Expected: all selected tests pass with zero warnings. Inspect the semantic test assertions to confirm that `Value` is a declaration-less call target, `Hidden` is absent, and only the indexed document is persisted.

- [ ] **Step 6: Commit Task 2**

```powershell
rtk git add src\CsIndex.Core\Analysis\SemanticExtractor.cs src\CsIndex.Core\Caching\ProjectFingerprintBuilder.cs tests\CsIndex.Core.Tests\CompilationOnlyGeneratedDocumentTests.cs
rtk git commit -m "fix: keep external generated inputs compilation only"
```

Record the commit and request a task-scoped spec/quality review before Task 3.

---

### Task 3: Document the Behavior and Run Final Acceptance

**Files:**
- Modify: `docs/SPEC.md`
- Modify: `docs/KNOWN_LIMITATIONS.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`
- Modify: `docs/TEST_PLAN.md`
- Modify: `docs/DB_SCHEMA.md`
- Modify: `docs/CLI.md`

**Interfaces:**
- Consumes: verified behavior and exact warning/count semantics from Tasks 1-2.
- Produces: durable user/developer documentation and final branch verification evidence.

- [ ] **Step 1: Update active documentation with the implemented distinction**

Use this terminology consistently:

```text
An ordinary persisted project, document, or linked source must share the storage root's Windows drive or UNC server/share. A cross-volume/share physical C# document positively identified by GeneratedCodeDetector is kept in the Roslyn compilation but excluded from the portable index. Its path, source, declarations, and body-derived facts are not persisted; it contributes one warning and one excluded-document count. Same-volume generated source remains indexed normally.
```

In `docs/CLI.md`, explicitly list the five MSBuild input forms from the acceptance matrix and state that forced directory mode does not load injected MSBuild documents. In `docs/DB_SCHEMA.md`, retain the single-root invariant and clarify that compilation-only inputs are not database documents. In `docs/IMPLEMENTATION_STATUS.md`, mark the feature implemented only after Tasks 1-2 are green. In `docs/TEST_PLAN.md`, add the synthetic classifier/semantic/fingerprint suite and real `DefaultRunnerReporters.cs` regression.

- [ ] **Step 2: Validate documentation and formatting**

Run:

```powershell
rtk git diff --check
rtk dotnet format CsIndex.sln --verify-no-changes --no-restore
```

Expected: both commands exit 0 with no formatting changes required.

- [ ] **Step 3: Build and run the full automated suite from a clean command invocation**

Run:

```powershell
rtk dotnet build CsIndex.sln -c Release --no-restore
rtk dotnet test CsIndex.sln -c Release --no-build --no-restore
```

Expected: build exit 0, test exit 0, zero failed tests, and zero warnings. Record the exact project and test counts from command output.

- [ ] **Step 4: Run manual command-form acceptance with worktree-local databases**

Create the acceptance database directory under this plan's ignored SDD workspace, then run the freshly built CLI rather than the installed `C:\DosFree\csindex\csindex.exe`:

```powershell
rtk dotnet run --project src\CsIndex.Cli\CsIndex.Cli.csproj -c Release --no-build -- index . --db .superpowers\sdd\2026-09-07-cross-volume-generated-documents\auto.sqlite --rebuild
rtk dotnet run --project src\CsIndex.Cli\CsIndex.Cli.csproj -c Release --no-build -- index CsIndex.sln --db .superpowers\sdd\2026-09-07-cross-volume-generated-documents\solution.sqlite --rebuild
rtk dotnet run --project src\CsIndex.Cli\CsIndex.Cli.csproj -c Release --no-build -- index tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj --db .superpowers\sdd\2026-09-07-cross-volume-generated-documents\project.sqlite --rebuild
```

Expected: all three commands exit 0; on the reported E:/C: layout, stderr warns that `DefaultRunnerReporters.cs` was kept in compilation and excluded from the portable index. Inspect each resulting database through normal query commands or the storage integration seam and confirm no `documents.normalized_path` contains `DefaultRunnerReporters.cs` or an absolute drive/UNC path.

Do not replace the installed executable in `C:\DosFree\csindex`; deployment is an external side effect and requires a separate user decision after branch completion.

- [ ] **Step 5: Commit documentation**

```powershell
rtk git add docs\SPEC.md docs\KNOWN_LIMITATIONS.md docs\IMPLEMENTATION_STATUS.md docs\TEST_PLAN.md docs\DB_SCHEMA.md docs\CLI.md
rtk git commit -m "docs: explain compilation-only generated inputs"
```

- [ ] **Step 6: Request final whole-branch review and verify any fix wave**

Generate one review package from the recorded fork SHA through `HEAD`. Dispatch a `gpt-5.6-sol`/max final reviewer with the spec, this plan, the task ledger, and the package. If it reports findings, dispatch one fix implementer with the complete list, run the covering tests, generate a fix-range package, and perform one scoped re-review. Record every disposition and ruling in the ledger.

After the final review is clean, rerun:

```powershell
rtk git status --short --branch
rtk git diff --check
rtk dotnet build CsIndex.sln -c Release --no-restore
rtk dotnet test CsIndex.sln -c Release --no-build --no-restore
rtk dotnet format CsIndex.sln --verify-no-changes --no-restore
```

Only fresh output from this final tree can support completion claims. Then use `superpowers:finishing-a-development-branch` and present the required merge/push/keep options; do not merge, push, publish, deploy, or delete the worktree without the user's corresponding choice.
