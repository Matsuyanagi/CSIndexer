# C# Symbol Paths, Typed Matching, and Portable Index Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current symbol-query spelling, global matcher switches, and absolute-path persistence with the approved C#-aligned/explicit symbol paths, independently typed conditions, schema-v5 logical declarations, and relocatable source paths.

**Architecture:** Build one semantic symbol-path and signature model in Core, persist logical symbols and physical declaration rows separately in schema v5, and make Query resolve both input spellings against that model through stored immediate containment. Compile typed conditions independently, apply them only to logical roots (or declaration rows for `source search`), and route every human-facing name/path through shared formatters and the query-time index path resolver.

**Tech Stack:** C# 14 / .NET 10 (`net10.0-windows`), Roslyn 5.6, Microsoft.Data.Sqlite 10, System.Text.Json, xUnit v3, RTK-prefixed .NET/Git commands.

## Global Constraints

- The normative design is `docs/superpowers/specs/2026-08-16-csharp-symbol-path-design.md`; an apparent contradiction or unspecified observable behavior must be returned to the primary agent before editing code.
- This is a breaking replacement. Do not accept the old `[namespace.]type::method[(parameter-types)]` interpretation, `::` between executable children, `--regex`, or query-time `--ignore-case`; do not add aliases, fallback parsing, or no-match reinterpretation.
- Accept both structured forms everywhere a positional selector is allowed: csharp `dotted-type::executable` and explicit `namespace::type::executable`. Exactly one top-level `::` is csharp; exactly two is explicit; `A::B::C()` is always explicit.
- Csharp dotted type input always suffix-matches the candidate's complete namespace-plus-type path. Explicit input fixes the namespace/type boundary. `global` is the exact global namespace token; `@global` is a literal namespace identifier.
- Executable children use `.` and every child must be an immediate persisted containment child. Parameter-list and generic-list omission are independent at every named/special segment and follow design sections 8.2-8.3 exactly.
- Concrete parameter spelling prefers C# aliases, fully qualifies every non-alias named type, omits parameter names/return type, preserves ref mode and structural type shape, ignores nullable-reference annotations for identity, and keeps nullable-value types distinct.
- Support exactly the callable/special/synthetic catalog in design sections 9-10. Lambda and `delegate` anonymous-method markers share one per-immediate-owner source-order counter but retain distinct marker text.
- `--kind all` includes method, lambda, initializer, and top-level nodes and equals omission. `--kind method` and `--kind lambda` keep the exact design membership. Direct async filtering uses only `AsyncRole`, with no owner leakage.
- Each namespace/type/method/file/source condition independently selects literal, glob, or regex mode. Concise options mean glob. Case defaults to ordinal strict and is independently configurable per semantic category.
- Root selection, direct kind/async filtering, declaration-row projection, de-duplication, and cardinality checks happen before override expansion, call/relation traversal, or graph traversal. Secondary results are never filtered by root conditions.
- Schema version is exactly `5`. There is no migration and no automatic rebuild/deletion. Schema 4 or older is rejected without modifying the database and the error instructs the user to delete/rename or choose another DB and run `csindex index` explicitly.
- Persist only storage-root-relative forward-slash project/document/source-derived paths and the database-directory-relative index-root anchor. Never persist a machine-specific absolute path. Cross-volume or cross-UNC-share indexing fails before database mutation.
- `--base-dir` is query/read-time only. `--path-style absolute` is the default; `relative` emits the stored path. Neither option changes stored data, identities, filtering, cache keys, or canonical ordering.
- `--symbol-path-style csharp` is the default. `explicit` changes presentation only. `--short-names` omits only the owner namespace; it never shortens parameter, conversion, or explicit-interface payload types.
- All unordered logical results use the exact canonical semantic sort key from design section 16.3. Formatting, case, base directory, path style, and output format must not change result order.
- Preserve the current lazy, UTF-8-no-BOM, failure-atomic output destination. Cancellation/failure before commit preserves an existing destination and cleans owned temporary files where possible.
- Preserve existing fixed table schemas/source-layout behavior unless this design explicitly adds a declaration-role/path field. Table sanitization remains presentation-only; stored normalized source, hashes, source matching, and JSON source text remain lossless.
- Every behavior change uses RED-GREEN-REFACTOR. Do not weaken, delete, or skip a test merely to reach GREEN. Feature-owned acceptance runs must contain no skip.
- Run commands from `E:\WorksDevelop\CSIndexer`, prefix every command with `rtk`, and use PowerShell only with `-NoProfile`.
- Execute implementation tasks sequentially in one isolated worktree. Do not run concurrent implementation agents in the same workspace. Each task receives an independent spec-compliance review and code-quality review before the next task starts.

## File Responsibility Map

- `src/CsIndex.Core/Model/IndexEnums.cs`, `IndexData.cs`, and new symbol model files: declaration roles, semantic type/signature data, canonical path components, and snapshot invariants.
- `src/CsIndex.Core/Symbols/SymbolSignatureCanonicalizer.cs`, `SymbolCanonicalizer.cs`, and `SymbolPathFormatter.cs`: Roslyn-to-semantic identity, C# display spelling, logical stable keys, concrete segment construction, and the only human-facing path formatter.
- `src/CsIndex.Core/Analysis/SemanticExtractor.cs`: complete source-callable coverage, immediate containment, anonymous ordinals, partial normalization, and declaration extraction.
- `src/CsIndex.Core/Input/IndexPathResolver.cs`, `AnalysisCoordinator.cs`, and `Caching/RequestHasher.cs`: index/query path contexts, same-volume/share validation, relative persistence, and cache invalidation.
- `src/CsIndex.Storage/Schema/SchemaMigrator.cs`, `SqliteIndex.cs`, `QueryModels.cs`, and `QueryRepository.cs`: schema v5, transactional logical/declaration persistence, preferred declarations, lazy declaration loading, semantic candidate queries, and relative-path projections.
- `src/CsIndex.Query/Symbols/SymbolPathSyntax.cs`, `BalancedTextScanner.cs`, and `SymbolPathParser.cs`: immutable selector AST and balanced structural parsing.
- `src/CsIndex.Query/Symbols/TypedCondition.cs` and `TypedConditionCompiler.cs`: literal/glob/regex validation, category-aware case behavior, structural glob semantics, timeout, and boolean composition.
- `src/CsIndex.Query/Symbols/SymbolPathResolver.cs` and `SymbolCanonicalComparer.cs`: candidate-aware resolution, immediate containment, logical/declaration projection, and canonical ordering.
- `src/CsIndex.Query/SemanticQueryService.cs`, `ExecutableTargetResolver.cs`, `MethodTargetResolver.cs`, `SourcePositionResolver.cs`, `AsyncPathResolver.cs`, and `CallerTreeBuilder.cs`: command-neutral root orchestration, expansion/traversal after selection, and path-aware source access.
- `src/CsIndex.Cli/CliArguments.cs` and `Program.cs`: breaking option grammar, exact command matrix, cardinality, query path context, help dispatch, and verbose help.
- `src/CsIndex.Cli/SymbolSignatureFormatter.cs`, `OutputFormatter.cs`, and `GraphOutputFormatter.cs`: shared symbol/path presentation in table, JSON, text, tree, line, Mermaid, source headers, diagnostics, and ambiguity lists.
- Core, Query, Storage, and Integration test projects: focused RED/GREEN coverage plus the design section 22 acceptance matrix.
- `docs/SPEC.md`, `CLI.md`, `DB_SCHEMA.md`, `DECISIONS.md`, `TEST_PLAN.md`, `IMPLEMENTATION_STATUS.md`, and `KNOWN_LIMITATIONS.md`: final normative and operational documentation.

## Task Dependency Order

```text
1 signature semantics
  -> 2 callable path/extraction
  -> 3 logical declarations/partial identity
  -> 4 portable path context
  -> 5 schema-v5 storage
  -> 6 structured parser
  -> 7 shared formatter/order
  -> 8 typed condition compiler
  -> 9 symbol-path resolver
  -> 10 query-service orchestration
  -> 11 CLI grammar/help
  -> 12 output/path integration
  -> 13 acceptance closure
  -> 14 normative docs/final verification/review
```

No task may consume an interface defined by a later task. When an existing public/internal API must remain callable until its consumer is converted, forward it to the new implementation without accepting legacy syntax; remove the forwarding overload in the consumer-conversion task.

---

### Task 1: Canonical C# Type and Callable Signature Semantics

**Normative sections:** 7.3, 8.2-8.4, 9.2, 20.2, 22.3.

**Files:**
- Create: `src/CsIndex.Core/Symbols/CanonicalTypeSignature.cs`
- Create: `src/CsIndex.Core/Symbols/SymbolSignatureCanonicalizer.cs`
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Modify: `src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs`
- Create: `tests/CsIndex.Core.Tests/SymbolSignatureCanonicalizerTests.cs`
- Modify: `tests/CsIndex.Core.Tests/ProjectScopedSourceSymbolIdentityTests.cs`

**Interfaces:**
- Produces: `CanonicalTypeSignature(string IdentityKey, string DisplayText)`; `IdentityKey` is deterministic semantic identity and `DisplayText` is the canonical concrete C# spelling.
- Produces: `CanonicalParameterSignature(CanonicalTypeSignature Type, int RefKind)`.
- Produces: `SymbolSignatureCanonicalizer.CanonicalizeType(ITypeSymbol)`, `CanonicalizeParameter(IParameterSymbol)`, and `CanonicalizeMethod(IMethodSymbol)`.
- Produces: `SymbolSignatureCanonicalizer.ParseSelectorType(string, IReadOnlyDictionary<string, int>)` for syntactic query types and `IsMatch(CanonicalTypeSelector, CanonicalTypeSignature)` for candidate-aware comparison.
- Changes: `MethodParameterData` stores both `TypeKey` (semantic identity) and `TypeDisplay` (canonical C# display); `SymbolData` stores `ReturnTypeKey`/`ReturnTypeDisplay` and `ConversionTypeKey`/`ConversionTypeDisplay` separately.
- Consumes: Roslyn symbols only; it does not parse complete symbol paths, access SQLite, or decide namespace/type ownership.

- [ ] **Step 1: Write failing canonical type/signature tests**

Add table-driven tests with Roslyn compilations that assert these exact equivalence/distinction rules:

```csharp
AssertEquivalent("int", "System.Int32", "global::System.Int32");
AssertEquivalent("object", "dynamic");
AssertEquivalent("string", "string?");
AssertDistinct("int", "int?");
AssertDistinct("int[]", "int[,]");
AssertDistinct("int*", "int");
AssertEquivalent("(int,string)", "(int Left,string Right)");
AssertDistinct("delegate*<int,void>", "delegate* unmanaged<int,void>");
AssertEquivalent("T", "U", placeholderOrdinal: 0);
AssertDistinct("ref int", "out int");
AssertDistinct("in int", "ref readonly int");
```

Also assert concrete display text exactly uses `bool`, `int`, `nint`, `nuint`, `string`, and `object`, while a non-alias named type is `System.Collections.Generic.List<string>` and has no leading `global::`. Assert unqualified non-alias selector input such as `Customer` is rejected, while `Game.Models.Customer` is accepted. Assert `Method<System.String>` is rejected as a definition-placeholder list rather than interpreted as a constructed generic method.

- [ ] **Step 2: Run the focused tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SymbolSignatureCanonicalizerTests`

Expected: FAIL because the canonical signature types and APIs do not exist.

- [ ] **Step 3: Add the immutable canonical signature records**

Create the following public data boundary; keep the internal identity encoding private to `SymbolSignatureCanonicalizer`:

```csharp
public sealed record CanonicalTypeSignature(string IdentityKey, string DisplayText);

public sealed record CanonicalTypeSelector(
    string SyntaxText,
    IReadOnlyDictionary<string, int> GenericPlaceholders);

public sealed record CanonicalParameterSignature(
    CanonicalTypeSignature Type,
    int RefKind);

public sealed record CanonicalMethodSignature(
    int GenericArity,
    IReadOnlyList<string> GenericParameterNames,
    IReadOnlyList<CanonicalParameterSignature> Parameters,
    CanonicalTypeSignature? ReturnType,
    CanonicalTypeSignature? ConversionTargetType);
```

The private identity encoder must recurse through named/generic types, arrays and rank, pointers, tuples without element names, function-pointer calling convention/ref modes, nullable value wrappers, and type/method placeholder ordinals. It must remove only nullable-reference annotations and map `dynamic` to the same identity as `object`.

- [ ] **Step 4: Implement Roslyn and selector canonicalization**

Use one `SymbolDisplayFormat` only for final display fragments; do not derive semantic identity by string-replacing a display name. `ParseSelectorType` must parse with Roslyn C# type syntax, bind generic placeholder identifiers to the supplied ordinal map, map aliases/framework spellings, preserve structural punctuation, and reject diagnostics or unqualified non-alias names. `IsMatch` must compare the typed identity recursively so `N.RefType?` can ignore reference nullability without collapsing `N.ValueType?` into `N.ValueType`.

Change `SymbolCanonicalizer.CreateMethod` to populate:

```csharp
TypeKey = canonicalParameter.Type.IdentityKey;
TypeDisplay = canonicalParameter.Type.DisplayText;
ReturnTypeKey = methodSignature.ReturnType?.IdentityKey;
ReturnTypeDisplay = methodSignature.ReturnType?.DisplayText;
```

Preserve parameter ref kind in `RefKind`; parameter names, `params`, `scoped`, `this`, optional markers, and defaults never enter path identity.

- [ ] **Step 5: Run focused identity and existing stable-key tests**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolSignatureCanonicalizerTests|FullyQualifiedName~ProjectScopedSourceSymbolIdentityTests"`

Expected: PASS, zero warnings. Refactor only while this command stays GREEN.

- [ ] **Step 6: Commit the signature foundation**

```powershell
rtk git add src/CsIndex.Core/Model/IndexData.cs src/CsIndex.Core/Symbols/CanonicalTypeSignature.cs src/CsIndex.Core/Symbols/SymbolSignatureCanonicalizer.cs src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs tests/CsIndex.Core.Tests/SymbolSignatureCanonicalizerTests.cs tests/CsIndex.Core.Tests/ProjectScopedSourceSymbolIdentityTests.cs
rtk git commit -m "feat: add canonical C# signature semantics"
```

**Positive cases:** aliases/framework names, fully qualified custom names, generic placeholder ordinals, nested generics, nullable reference/value, arrays, pointers, tuples, function pointers, all ref modes, conversion target identity.

**Negative cases:** invalid C# type syntax, unqualified non-alias names, constructed generic-method notation, empty placeholder entries, duplicate placeholder names, wildcard inside parameter syntax.

**Out of scope:** complete symbol-path parsing, glob/regex matching, persistence, output styles.

### Task 2: Semantic Callable Paths and Complete Source Extraction

**Normative sections:** 6.2, 7.3, 8.1, 9, 10, 11, 20.2, 22.4 (callable categories and ordinals).

**Files:**
- Modify: `src/CsIndex.Core/Model/IndexEnums.cs`
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Modify: `src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs`
- Modify: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`
- Modify: `src/CsIndex.Core/Analysis/AsyncSymbolClassifier.cs`
- Create: `tests/CsIndex.Core.Tests/CallablePathExtractionTests.cs`
- Modify: `tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs`
- Modify: `tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs`

**Interfaces:**
- Produces: `CallablePathSegmentKind` with exactly `Named`, `Special`, `Lambda`, `AnonymousMethod`, `Initializer`, and `TopLevelStatements`.
- Produces: `SymbolPathData(NamespacePath, TypeDisplayPath, TypeIdentityPath, ExecutableDisplayPath, ExecutableIdentityPath, SegmentDisplay, SegmentIdentity, SegmentKind)` on every executable `SymbolData`.
- Produces: canonical segments for the exact bracketed/synthetic catalog and immediate `ContainingSymbolKey` edges.
- Consumes: Task 1's canonical type/method signatures.
- Does not assign physical declaration roles; Task 3 owns logical/declaration splitting and partial pairing.

- [ ] **Step 1: Add a failing all-callables extraction fixture**

Create one Roslyn fixture containing ordinary/nested methods, local functions inside methods/lambdas/top-level code, primary/instance/static constructors, destructor, checked and unchecked operators/conversions, ordinary and explicit-interface methods/accessors, auto/body/expression-bodied accessors, custom event accessors, field/property/event initializers, simple/parenthesized lambdas, and `delegate` anonymous methods.

Assert exact `SegmentDisplay` examples:

```csharp
AssertPath("[constructor](int,string)");
AssertPath("[static-constructor]()");
AssertPath("[destructor]()");
AssertPath("[operator:+](Game.Number,Game.Number)");
AssertPath("[checked-operator:+](Game.Number,Game.Number)");
AssertPath("[conversion:implicit:int](Game.Number)");
AssertPath("[checked-conversion:explicit:int](Game.Number)");
AssertPath("[get:Item](int)");
AssertPath("[set:Item](int,string)");
AssertPath("[init:Name](string)");
AssertPath("[add:Changed](System.EventHandler)");
AssertPath("[remove:Changed](System.EventHandler)");
AssertPath("[explicit:System.IDisposable.Dispose]()");
AssertPath("<initializer:Factory>.<lambda#1>");
AssertPath("<top-level-statements>.Local().<anonymous-method#1>");
```

Assert a method/local/lambda chain uses `Run(int).Local(string).<lambda#1>` and never `Run(int)::Local(string)`. Assert every child's `ContainingSymbolKey` is the immediate lexical owner, including local-inside-lambda and nested anonymous functions.

- [ ] **Step 2: Add failing ordinal and exclusion tests**

For one owner containing lambda, anonymous method, lambda, assert markers `lambda#1`, `anonymous-method#2`, `lambda#3`. For a nested anonymous owner, assert its child counter restarts at 1. Insert an earlier anonymous function and assert only later siblings of that immediate owner renumber.

Assert absence of implicit default constructor, field-like event synthesized add/remove, backing-field callables, async/iterator `MoveNext`, closure methods, record-synthesized equality/hash/clone/print members, and record positional-property synthesized accessors. Assert an explicitly written auto accessor is present and a primary constructor is present.

- [ ] **Step 3: Run the extraction tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CallablePathExtractionTests|FullyQualifiedName~ExecutableSymbolExtractionTests|FullyQualifiedName~AsyncSemanticExtractorTests"`

Expected: FAIL because the semantic path model, complete special catalog, anonymous-method distinction, and per-immediate-owner numbering are absent.

- [ ] **Step 4: Implement exact segment mapping**

Add the immutable semantic path boundary with stable enum values:

```csharp
public enum CallablePathSegmentKind
{
    Named = 1,
    Special = 2,
    Lambda = 3,
    AnonymousMethod = 4,
    Initializer = 5,
    TopLevelStatements = 6,
}

public sealed record SymbolPathData(
    string NamespacePath,
    string TypeDisplayPath,
    string TypeIdentityPath,
    string ExecutableDisplayPath,
    string ExecutableIdentityPath,
    string SegmentDisplay,
    string SegmentIdentity,
    CallablePathSegmentKind SegmentKind);
```

Add these exact mappings in `SymbolCanonicalizer`; do not expose Roslyn metadata names such as `.ctor`, `op_Addition`, or `get_Name` in a concrete symbol path:

```text
Constructor                  -> [constructor]
StaticConstructor            -> [static-constructor]
Destructor                   -> [destructor]
UserDefinedOperator          -> [operator:<C# token>]
CheckedUserDefinedOperator   -> [checked-operator:<C# token>]
Conversion                   -> [conversion:<implicit|explicit>:<target type>]
CheckedConversion            -> [checked-conversion:<implicit|explicit>:<target type>]
PropertyGet/Set/Init          -> [get|set|init:<semantic member name>]
EventAdd/EventRemove          -> [add|remove:<semantic member name>]
ExplicitInterfaceMethod      -> [explicit:<fully qualified interface member>]
```

Generic placeholders follow the bracket, and parameter lists follow Task 1 formatting. Use `Item` or the semantic metadata indexer name. Explicit-interface payload namespaces/types are fully qualified and C# keyword-escaped.

- [ ] **Step 5: Complete extraction and immediate containment**

Replace the current single non-lambda-owner counter with a dictionary keyed by the immediate owner stable key. Enumerate both lambda syntax and `AnonymousMethodExpressionSyntax` in one source-order sequence and choose the marker from syntax kind. Create source-backed symbols for explicitly written auto accessors and bodyless interface/abstract/extern/partial declarations. Create only the two approved synthetic owner kinds: initializer and top-level statements.

Build `ExecutableDisplayPath` by appending the current concrete segment to the immediate parent's path with `.`, and build `ExecutableIdentityPath` from semantic segment identities. Build outer-to-inner `TypeDisplayPath` and generic-arity-based `TypeIdentityPath`; do not guess namespace/type boundaries from dotted text.

- [ ] **Step 6: Apply direct async semantics to every executable kind**

Classify a symbol from its own operation/syntax only. Initializer nodes remain `AsyncRole.None` when only a child is async. Top-level statements receive a non-zero direct role only for direct top-level async operations. Local/lambda child roles never flow into an owner during extraction; derived involvement remains the separate existing propagation step.

- [ ] **Step 7: Run focused Core tests and confirm GREEN**

Run the Step 3 command, then run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore`

Expected: both commands PASS with zero warnings.

- [ ] **Step 8: Commit semantic callable extraction**

```powershell
rtk git add src/CsIndex.Core/Model/IndexEnums.cs src/CsIndex.Core/Model/IndexData.cs src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs src/CsIndex.Core/Analysis/SemanticExtractor.cs src/CsIndex.Core/Analysis/AsyncSymbolClassifier.cs tests/CsIndex.Core.Tests/CallablePathExtractionTests.cs tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs tests/CsIndex.Core.Tests/AsyncSemanticExtractorTests.cs
rtk git commit -m "feat: extract canonical callable paths"
```

**Positive cases:** every design section 10.1 category, exact special tags, nested type paths, locals at every ownership depth, mixed anonymous numbering, initializers, top-level statements, direct async roles.

**Negative cases:** every design section 10.2 compiler-only category, old child separator in generated paths, non-immediate ownership, lambda/local role leakage.

**Out of scope:** partial declaration pairing, SQLite, query parsing, search matching, CLI output.

### Task 3: Logical Symbols, Declaration Roles, and Partial Normalization

**Normative sections:** 14, 20.5, 22.4 (partial identity/projection).

**Files:**
- Modify: `src/CsIndex.Core/Model/IndexEnums.cs`
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Modify: `src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs`
- Modify: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`
- Modify: `src/CsIndex.Core/Analysis/AnalysisState.cs`
- Modify: `src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs`
- Create: `tests/CsIndex.Core.Tests/LogicalDeclarationExtractionTests.cs`
- Modify: `tests/CsIndex.Core.Tests/InterfaceMethodBindingExtractorTests.cs`
- Modify: `tests/CsIndex.Core.Tests/ProjectScopedSourceSymbolIdentityTests.cs`

**Interfaces:**
- Produces: `DeclarationRole` with numeric values `Ordinary = 1`, `PartialDefinition = 2`, `PartialImplementation = 3`.
- Produces: `SymbolDeclarationData(Key, SymbolKey, DocumentKey, Role, SourceStart, SourceLength, NormalizedSource, NormalizedSourceHash, IsGenerated)`.
- Changes: `IndexSnapshot.Declarations` is a `Dictionary<string, SymbolDeclarationData>` keyed ordinally; `SymbolData.PreferredDeclarationKey` points to one associated declaration.
- Produces: `SymbolCanonicalizer.NormalizeLogicalMethod(IMethodSymbol)` mapping an implementation part to its definition part before stable-key, call, relation, containment, or binding creation.
- Consumes: Task 2 path/containment data and Task 1 signatures.

- [ ] **Step 1: Write failing logical/declaration tests**

Index a partial definition/implementation split across two files and a definition-only partial method. Assert:

```csharp
Assert.Single(snapshot.Symbols.Values.Where(symbol => symbol.Name == "LoadAsync"));
Assert.Equal(2, DeclarationsFor("LoadAsync").Count);
Assert.Equal(
    new[] { DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation },
    DeclarationsFor("LoadAsync").OrderBy(row => row.Role).Select(row => row.Role));
Assert.Equal(
    DeclarationRole.PartialImplementation,
    DeclarationByKey(Logical("LoadAsync").PreferredDeclarationKey!).Role);
Assert.Equal(
    DeclarationRole.PartialDefinition,
    DeclarationByKey(Logical("Validate").PreferredDeclarationKey!).Role);
```

Assert the two declaration keys differ by stored document/span/role while the logical stable key is identical. Assert no `PartialDefinition` or `PartialImplementation` self-relation is emitted for the paired logical symbol.

- [ ] **Step 2: Add failing graph/call/binding identity assertions**

Place calls, a local function, a lambda, an override, and an interface implementation in the implementation body. Assert caller/callee keys, containment keys, override relations, interface bindings, async-next keys, and call candidates reference the one logical key exactly once. Assert the bodyless definition emits no body call edges. Assert direct/logical source-derived properties equal the preferred implementation, not path order.

- [ ] **Step 3: Run the logical-declaration tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~LogicalDeclarationExtractionTests|FullyQualifiedName~InterfaceMethodBindingExtractorTests|FullyQualifiedName~ProjectScopedSourceSymbolIdentityTests"`

Expected: FAIL because source data is still stored on one symbol row and partial parts can produce separate source identities/relations.

- [ ] **Step 4: Split logical and physical extraction data**

Move document/span/source/hash/generated fields from callable logical construction into `SymbolDeclarationData`. Use this declaration key shape with only portable inputs; Task 4 will convert the path before extraction persists it:

```csharp
public string GetDeclarationKey(
    string logicalSymbolKey,
    string storedDocumentPath,
    int sourceStart,
    int sourceLength,
    DeclarationRole role) =>
    $"{logicalSymbolKey}|declaration:{storedDocumentPath}:{sourceStart}:{sourceLength}:{(int)role}";
```

For source-backed non-partial callables and the approved synthetic owners, create one `Ordinary` declaration. Type rows remain logical containment/type-hierarchy symbols and do not enter the callable-declaration table; metadata-only callables likewise have no declaration. Set the preferred key after all callable parts have been observed: implementation, else definition, else ordinary.

- [ ] **Step 5: Normalize every identity-producing Roslyn method**

Use one normalization function before all dictionary lookups and edge creation:

```csharp
public IMethodSymbol NormalizeLogicalMethod(IMethodSymbol method)
{
    var unreduced = method.ReducedFrom ?? method;
    var definition = unreduced.PartialDefinitionPart ?? unreduced;
    return definition.OriginalDefinition;
}
```

Remove `GetTargetStableKey`/`actualTarget` persistence of `|constructed:` and `|reduced:` callable rows. Calls may retain call-site dispatch/resolution metadata, but every resolvable caller/callee/candidate/definition ID points to the normalized logical definition symbol. Query code must not need a later stable-key string filter to remove constructed/reduced rows.

Determine role from Roslyn part links plus source syntax: an implementation symbol has `PartialDefinitionPart`; a definition with an implementation has `PartialImplementationPart`; a `partial` bodyless declaration without an implementation is `PartialDefinition`; every other direct declaration is `Ordinary`. Never infer role from file ordering, source text after persistence, or display suffixes.

- [ ] **Step 6: Make preferred-declaration data authoritative**

After extraction, derive the logical symbol's direct `AsyncRole`, generated flag, and display-only source choices (generic placeholder names, retained nullable-reference/dynamic spelling) from the preferred declaration analysis while preserving the shared semantic identity fields. Keep source availability represented by `PreferredDeclarationKey`, and make locals/anonymous functions in a partial body contain the normalized logical method. Do not duplicate normalized source/hash back onto the logical symbol. Update async propagation dictionaries and interface/call relation creation to use logical keys only. Add a consistency check that every non-null `PreferredDeclarationKey` exists and points back to the same logical symbol.

- [ ] **Step 7: Run focused and full Core tests**

Run the Step 3 command, then run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore`

Expected: PASS, zero warnings, no duplicate logical callable in any snapshot assertion.

- [ ] **Step 8: Commit logical declaration extraction**

```powershell
rtk git add src/CsIndex.Core/Model/IndexEnums.cs src/CsIndex.Core/Model/IndexData.cs src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs src/CsIndex.Core/Analysis/SemanticExtractor.cs src/CsIndex.Core/Analysis/AnalysisState.cs src/CsIndex.Core/Analysis/AsyncInvolvementPropagator.cs tests/CsIndex.Core.Tests/LogicalDeclarationExtractionTests.cs tests/CsIndex.Core.Tests/InterfaceMethodBindingExtractorTests.cs tests/CsIndex.Core.Tests/ProjectScopedSourceSymbolIdentityTests.cs
rtk git commit -m "feat: normalize logical callable declarations"
```

**Positive cases:** ordinary declaration, two-part partial, definition-only partial, preferred implementation, per-row source/hash/role, logical calls/relations/graphs, project/profile isolation.

**Negative cases:** duplicate partial logical symbols, body edges from definition-only rows, role suffixes in paths, path-order preference, logical self-edges, absolute path in declaration keys.

**Out of scope:** SQLite schema/projection and command-specific definition/source behavior.

### Task 4: Portable Index Path Context and Preflight Validation

**Normative sections:** 15, 19.3, 20.7, 21, 22.7 (path mechanics except SQLite persistence).

**Files:**
- Create: `src/CsIndex.Core/Input/IndexPathResolver.cs`
- Create: `src/CsIndex.Core/Analysis/PreparedAnalysis.cs`
- Modify: `src/CsIndex.Core/Input/PathNormalizer.cs`
- Modify: `src/CsIndex.Core/Analysis/AnalysisCoordinator.cs`
- Modify: `src/CsIndex.Core/Analysis/SemanticExtractor.cs`
- Modify: `src/CsIndex.Core/Model/IndexData.cs`
- Modify: `src/CsIndex.Core/Caching/ProjectFingerprintBuilder.cs`
- Modify: `src/CsIndex.Core/Caching/InputFingerprintBuilder.cs`
- Create: `tests/CsIndex.Core.Tests/IndexPathResolverTests.cs`
- Create: `tests/CsIndex.Core.Tests/PortableAnalysisPathTests.cs`
- Modify: `tests/CsIndex.Core.Tests/TempDirectory.cs`

**Interfaces:**
- Produces: `PathDisplayStyle` with `Absolute = 1` and `Relative = 2`.
- Produces: `IndexPathResolver.CreateForIndex(string databasePath, string storageRoot)` and `CreateForQuery(string databasePath, string storedIndexRootAnchor, string? baseDirectory)`.
- Produces: `IndexRootAnchor`, `EffectiveBaseDirectory`, `ToStoredPath`, `ToAbsolutePath`, `ToDisplayPath`, and `NormalizeLocationInputToStoredPath`.
- Produces: `AnalysisCoordinator.PrepareAsync(ResolvedInput, IndexOptions, IndexPathResolver, CancellationToken)` returning an owned `PreparedAnalysis : IDisposable`, and `AnalyzeAsync(PreparedAnalysis, IndexOptions, byte[], byte[], CancellationToken)`.
- Changes: `IndexSnapshot.InputRoot` becomes the stored relative input-root representation and adds `IndexRootAnchor`; all project/document paths passed into `SemanticExtractor` are already storage-relative forward-slash paths.
- Consumes: the existing input resolver's `ResolvedInput.RootPath` as the storage root; custom `--db` never changes that root.

- [ ] **Step 1: Write failing lexical path-resolution tests**

Cover the standard and custom examples exactly:

```csharp
var standard = IndexPathResolver.CreateForIndex(
    @"D:\Work\Game\.csindex\index.sqlite",
    @"D:\Work\Game");
Assert.Equal("..", standard.IndexRootAnchor);
Assert.Equal("src/play.cs", standard.ToStoredPath(@"D:\Work\Game\src\play.cs"));

var custom = IndexPathResolver.CreateForIndex(
    @"D:\Indexes\Game\index.sqlite",
    @"D:\Work\Game");
Assert.Equal("../../Work/Game", custom.IndexRootAnchor);
```

Use temporary real paths for relative/absolute reconstruction and lexical synthetic roots for drive/UNC cases. Assert `../Shared/Generated/Bindings.cs` round-trips, input backslashes normalize to `/`, casing is preserved, `.`/`..` are normalized, and `--base-dir` changes only `EffectiveBaseDirectory`/absolute reconstruction.

- [ ] **Step 2: Add failing cross-volume/share and location-input tests**

Assert `CreateForIndex` or `ToStoredPath` rejects different drive letters, different UNC servers, and different shares on one server with an `InputResolutionException` naming both offending paths and the same-volume/share rule. Assert regular drive paths and their accepted `\\?\`/`\\.\` spellings compare as the same root. Assert absolute and effective-base-relative location inputs become the same stored relative path. A location on another volume/share must fail; there is no absolute-string fallback.

- [ ] **Step 3: Run resolver tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~IndexPathResolverTests`

Expected: FAIL because `IndexPathResolver` and `PathDisplayStyle` do not exist.

- [ ] **Step 4: Implement the index/query path resolver**

Use this exact API surface:

```csharp
public enum PathDisplayStyle
{
    Absolute = 1,
    Relative = 2,
}

public sealed class IndexPathResolver
{
    public static IndexPathResolver CreateForIndex(string databasePath, string storageRoot);
    public static IndexPathResolver CreateForQuery(
        string databasePath,
        string storedIndexRootAnchor,
        string? baseDirectory);

    public string DatabasePath { get; }
    public string IndexRootAnchor { get; }
    public string EffectiveBaseDirectory { get; }
    public string ToStoredPath(string absolutePath);
    public string ToAbsolutePath(string storedPath);
    public string ToDisplayPath(string storedPath, PathDisplayStyle style);
    public string NormalizeLocationInputToStoredPath(string inputPath);
}
```

Normalize accepted Windows device/extended prefixes before comparing roots, but preserve the user's real path casing for stored relative text. A same-volume/share comparison is ordinal-ignore-case on Windows roots. `ToStoredPath` calls `Path.GetRelativePath`, then changes separators to `/`; `ToAbsolutePath` combines the effective base without a containment check so leading `../` remains valid. Query construction with a nonexistent base directory is allowed until an operation actually reads a file.

- [ ] **Step 5: Write failing analysis preflight tests**

Create a temporary solution/project with a normal source, a linked same-volume `../Shared/Linked.cs`, and project paths. Assert preparation creates a mapping where every project/document/source-derived path is relative and contains no drive/UNC root. Add a synthetic workspace seam that reports a cross-volume document and assert preparation fails before any supplied database-open callback is invoked.

- [ ] **Step 6: Introduce owned prepared analysis and relative extraction input**

Use this lifecycle:

```csharp
using var prepared = await coordinator.PrepareAsync(input, options, paths, cancellationToken);
var result = await coordinator.AnalyzeAsync(
    prepared,
    options,
    inputFingerprint,
    requestHash,
    cancellationToken);
```

`PrepareAsync` loads the workspace once, validates the database directory, storage root, every project path, and every document path, and retains the `LoadedWorkspace` until disposed. `AnalyzeAsync` reuses that workspace; it must not load it a second time. The snapshot stores `IndexRootAnchor` and `InputRoot = "."`, project/document paths from `ToStoredPath`, and declaration/synthetic stable-key path components from those same stored paths. Fingerprints may read absolute files at runtime but persisted values and hash serialization must not contain those paths as stored identities.

Set source project/document identity from stored paths, not runtime absolute paths:

```csharp
var projectKey = project.FilePath is null
    ? $"project-name:{project.Name}"
    : $"project-path:{paths.ToStoredPath(project.FilePath)}";
var documentKey = $"{projectKey}|document:{paths.ToStoredPath(document.FilePath!)}";
```

Project/input fingerprint builders must hash the stored relative path plus file content/metadata, so moving the database/source layout without changing relative paths does not inject a machine root into cache identity.

Replace `SymbolCanonicalizer.BuildFallbackIdentity`'s direct `SourceTree.FilePath` use with an injected stored-path lookup from `PreparedAnalysis`. Metadata fallback remains path-free; any source fallback uses the mapped stored path plus span. Fail analysis if a source location has no validated mapping instead of falling back to its absolute path.

- [ ] **Step 7: Run focused portable-analysis tests and full Core tests**

Run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~IndexPathResolverTests|FullyQualifiedName~PortableAnalysisPathTests"`

Then run: `rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore`

Expected: PASS, zero warnings. Inspect the produced snapshot and assert no `ProjectData.ProjectPath`, `DocumentData.NormalizedPath`, `IndexSnapshot.InputRoot`, or source-derived stable/declaration key contains an absolute machine path.

- [ ] **Step 8: Commit portable path foundations**

```powershell
rtk git add src/CsIndex.Core/Input/IndexPathResolver.cs src/CsIndex.Core/Analysis/PreparedAnalysis.cs src/CsIndex.Core/Input/PathNormalizer.cs src/CsIndex.Core/Analysis/AnalysisCoordinator.cs src/CsIndex.Core/Analysis/SemanticExtractor.cs src/CsIndex.Core/Model/IndexData.cs src/CsIndex.Core/Caching/ProjectFingerprintBuilder.cs src/CsIndex.Core/Caching/InputFingerprintBuilder.cs tests/CsIndex.Core.Tests/IndexPathResolverTests.cs tests/CsIndex.Core.Tests/PortableAnalysisPathTests.cs tests/CsIndex.Core.Tests/TempDirectory.cs
rtk git commit -m "feat: add portable index path context"
```

**Positive cases:** standard/custom anchor, relocation, base override, absolute/relative display, same-volume linked `../`, drive and UNC normalization, absolute/relative location input.

**Negative cases:** cross-drive/share paths, absolute persisted values, hidden absolute fallback, symlink/junction resolution requirement, database location redefining storage root.

**Out of scope:** SQLite schema write/read and CLI option parsing.

### Task 5: Schema v5 Persistence and Shared-Formatter Model Cutover

**Normative sections:** 14-16.2, 18, 20.5-20.7, 21, 22.1 (formatter foundation), 22.4, 22.7.

**Files:**
- Modify: `src/CsIndex.Core/Caching/RequestHasher.cs`
- Create: `src/CsIndex.Core/Symbols/SymbolPathFormatter.cs`
- Modify: `src/CsIndex.Storage/Schema/SchemaMigrator.cs`
- Modify: `src/CsIndex.Storage/SqliteIndex.cs`
- Modify: `src/CsIndex.Storage/QueryModels.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Modify: `src/CsIndex.Query/QueryResults.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs` (endpoint hydration only)
- Modify: `src/CsIndex.Cli/Program.cs` (the `index` flow only)
- Modify: `src/CsIndex.Cli/SymbolSignatureFormatter.cs`
- Delete: `src/CsIndex.Cli/SymbolNameShortener.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `src/CsIndex.Cli/GraphOutputFormatter.cs`
- Modify: `tests/CsIndex.Core.Tests/RequestHasherTests.cs`
- Create: `tests/CsIndex.Core.Tests/SymbolPathFormatterTests.cs`
- Modify: `tests/CsIndex.Storage.Tests/SqliteIndexTests.cs`
- Create: `tests/CsIndex.Storage.Tests/SchemaFiveLogicalSymbolTests.cs`
- Create: `tests/CsIndex.Storage.Tests/PortablePathPersistenceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs`

**Interfaces:**
- Changes: `RequestHasher.SchemaVersion = 5` and `RequestHasher.AnalysisCacheVersion = 3`.
- Produces: `StoredDeclaration` with declaration ID/key, logical symbol ID, stored document path, role, span, normalized source/hash, and generated flag.
- Changes: `StoredProfile` exposes stored relative `InputRoot` and `IndexRootAnchor`; `QueryRepository.DatabasePath` exposes the normalized runtime DB path needed to build a query path resolver.
- Changes: `StoredSymbol` carries semantic `SymbolPathData`, signature display/identity, `PreferredDeclarationId`, and preferred declaration path/start/generated convenience fields; normalized source/hash exist only on `StoredDeclaration` and are never loaded by a name-only symbol projection.
- Produces: `SymbolPathStyle`, `SymbolPathFormatOptions`, and `SymbolPathFormatter.Format(SymbolPathData, SymbolPathFormatOptions)` in Core as the only human-facing symbol-path generator; default formatting is csharp/full.
- Changes: `StoredCall` and `StoredRelation` retain endpoint logical IDs and relation/call data but drop caller/callee/source/target display-name strings. `CallResult.SymbolsById` and `RelationResult.SymbolsById` are batch-hydrated for every named endpoint in the same task, so the solution never commits an uncompilable intermediate model.
- Changes: `StoredSymbol.DisplayName` and `FullyQualifiedName`, while still required by the unconverted legacy matcher/tests, become non-persisted forwarding properties that delegate to the shared csharp/full formatter. No CLI/output code may use them after this task. Task 9 deletes both properties after converting the remaining consumers; the final product retains neither compatibility member.
- Produces: `QueryRepository.FindLogicalSymbolCandidatesAsync`, `GetDeclarationsAsync`, `GetPreferredDeclarationsAsync`, and batched `GetSymbolsByIdsAsync`; source text is selected only when the caller requests it.
- Changes: the CLI `index` flow uses Task 4 prepared analysis/path preflight before opening the SQLite database, then cache-checks/saves the same relative snapshot.
- Consumes: Task 3 logical/declaration snapshot and Task 4 stored paths/anchor.

- [ ] **Step 1: Write failing schema-shape and round-trip tests**

Assert a new database contains schema version 5 and these exact logical changes:

```text
index_runs.index_root_anchor
index_runs.input_root                    # relative
projects.project_path                    # relative
documents.normalized_path                # relative, '/'
symbols.path_segment_kind
symbols.path_segment_display
symbols.path_segment_identity
symbols.type_display_path
symbols.type_identity_path
symbols.executable_display_path
symbols.executable_identity_path
symbols.preferred_declaration_id
symbols.return_type_display
symbols.conversion_type_key
symbols.conversion_type_display
method_parameters.type_key
method_parameters.type_display
symbol_declarations.id
symbol_declarations.declaration_key
symbol_declarations.symbol_id
symbol_declarations.document_id
symbol_declarations.declaration_role
symbol_declarations.source_start
symbol_declarations.source_length
symbol_declarations.normalized_source
symbol_declarations.normalized_source_hash
symbol_declarations.is_generated
```

Assert `symbols` has no `fully_qualified_name`, `display_name`, `normalized_source`, `normalized_source_hash`, `source_document_id`, `source_start`, or `source_length` column. Save an ordinary method, a two-row partial method, a definition-only partial method, a lambda, initializer, top-level node, and type; round-trip exact logical/declaration fields and preferred IDs, with no callable-declaration row for the type.

- [ ] **Step 2: Write failing transactional invariant tests**

Construct invalid snapshots and assert `SaveAsync` rolls back without replacing the prior valid run when: a preferred key is missing, a preferred row belongs to another logical symbol, a partial implementation is preferred over no matching definition key, an unknown declaration role is supplied, a call/relation references a physical declaration key, or duplicate logical stable keys occur. Assert a valid partial pair produces one logical row, two declaration rows, and no partial self-edge.

- [ ] **Step 3: Write failing formatter/model-cutover tests**

In Core, build `SymbolPathData` for nested types, local/lambda paths, global/literal-global namespaces, a non-alias `System.Guid` parameter, and an explicit-interface payload. Assert exact csharp/explicit plus full/short output and prove short mode removes only the owner namespace.

In Integration tests, construct calls/relations with endpoint IDs and a `SymbolsById` dictionary but no endpoint display strings. Assert table and JSON call/relation names, graph labels, signatures, and short-name output come only from `SymbolPathFormatter`. Remove one required endpoint from the dictionary and assert a deterministic internal invariant exception rather than an ID, empty name, or stored-string fallback. Update existing tests that construct `StoredCall`/`StoredRelation` so the test project will compile only after the complete model cutover.

- [ ] **Step 4: Run focused schema/formatter tests and confirm RED**

Run:

```powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SchemaFiveLogicalSymbolTests|FullyQualifiedName~PortablePathPersistenceTests"
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SymbolPathFormatterTests
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~OutputFormatterTests
```

Expected: FAIL because schema v4 has no declaration table/semantic path columns/root anchor, the shared formatter API is absent, and result models still depend on stored endpoint display strings.

- [ ] **Step 5: Replace schema creation with version 5**

Rename the creation method to `CreateVersionFiveAsync` and insert `5` into `schema_info`. Define `symbol_declarations.declaration_role` with `CHECK (declaration_role IN (1,2,3))`, a unique declaration key, and a unique `(symbol_id, document_id, source_start, source_length, declaration_role)` constraint. Add indexes for profile plus namespace/type/executable identity, containment, preferred declaration, declaration document/location/role, and the existing call/relation traversals.

Define both sides of the logical/declaration relationship: `symbol_declarations.symbol_id REFERENCES symbols(id) ON DELETE CASCADE`, and `symbols.preferred_declaration_id REFERENCES symbol_declarations(id) ON DELETE SET NULL`. The preferred row is nullable only while the transaction is being assembled or for metadata-only symbols.

Create symbols first with `preferred_declaration_id = NULL`, insert declarations, then update each preferred ID. Run these checks inside the same transaction before commit:

```sql
SELECT COUNT(*)
FROM symbols s
JOIN symbol_declarations d ON d.id = s.preferred_declaration_id
WHERE d.symbol_id <> s.id;

PRAGMA foreign_key_check;
```

Both must return no violation. Do not create a schema-4-to-5 migration path.

- [ ] **Step 6: Persist and project semantic data**

Update `SqliteIndex.SaveAsync` in this order: profile/run, projects, documents, logical symbols, containment/async links, parameters, declaration rows, preferred IDs, calls/relations/interface bindings/conditions, consistency checks, commit. Every call/relation/binding lookup uses the logical `symbolIds` dictionary; declaration IDs are held in a separate dictionary and never accepted by graph tables.

Change only `Program.RunIndexAsync` at this stage: resolve input and DB path, create the index path resolver, prepare/validate the workspace, calculate relative-path fingerprints, and only then construct/open `SqliteIndex` for cache lookup or save. Dispose `PreparedAnalysis` on cache reuse and on every failure. Keep all query-command option grammar unchanged until Task 11.

Update query projections so a normal logical result joins its preferred declaration only for path/start/generated data. `GetDeclarationsAsync(profileId, symbolIds, includeSourceText, token)` returns all associated rows ordered by role (`partial-definition`, `partial-implementation`, `ordinary` only where applicable), stored path, start, and ID. `GetPreferredDeclarationsAsync` loads preferred source only when requested. `FindLogicalSymbolCandidatesAsync` must not select normalized source blobs.

Change call/relation SQL projections to return logical endpoint IDs rather than formatting fields. `QueryRepository` must contain no symbol-path formatter and must not synthesize a presentation string from semantic columns.

- [ ] **Step 7: Implement the final shared formatter API and complete the model cutover**

Create these final Core interfaces; later tasks add consumers/tests, not replacement types:

```csharp
public enum SymbolPathStyle { CSharp = 1, Explicit = 2 }

public readonly record struct SymbolPathFormatOptions(
    SymbolPathStyle Style = SymbolPathStyle.CSharp,
    bool ShortNames = false);

public sealed class SymbolPathFormatter
{
    public string Format(SymbolPathData path, SymbolPathFormatOptions options);
}
```

For csharp/full, join nonempty namespace and type display path with `.`, then append `::` and executable display path. Explicit/full uses `global` for the empty namespace and otherwise emits namespace `::` type `::` executable. Csharp/short omits only the namespace. Explicit/short emits `**::` type `::` executable. Never shorten signature/payload types.

Make the two temporary `StoredSymbol` forwarding properties call this formatter with csharp/full and add a code comment naming Task 9's required deletion; neither value is read from SQLite. Add one batched `GetSymbolsByIdsAsync` repository call per result, populate `CallResult.SymbolsById`/`RelationResult.SymbolsById`, and update CLI signature/output/graph formatters to resolve every name through the shared formatter. Delete `SymbolNameShortener`; do not reimplement namespace shortening. Update the direct result-model test consumers named in this task. Missing endpoint IDs are invariant failures, except an unresolved call may use its documented unresolved source token.

- [ ] **Step 8: Add portable persistence and relocation assertions**

Save standard/custom DB snapshots and inspect every filesystem-derived text column plus all declaration/stable keys. Reject any rooted path. Move the database and source tree together in a test while preserving relative layout; create a query resolver from the stored anchor and assert it reconstructs the new source path. Create a query resolver with a different `baseDirectory`, read results, then compare a before/after SQL dump of all rows to prove no mutation.

- [ ] **Step 9: Enforce incompatible-schema preservation and rebuild guidance**

Build real schema 1-4 fixture files. Query and `index`-open attempts must throw `IndexDatabaseException`, preserve the complete original bytes/rows, and include all of these instructions in the message: unsupported version, database not modified, delete or rename the old database or select a new `--db`, then run `csindex index`. `--rebuild` must not bypass this check.

- [ ] **Step 10: Update cache version tests**

Assert `SchemaVersion == 5` and `AnalysisCacheVersion == 3`, both values participate in `RequestHasher.Build`, and a hash built with the former analysis cache version differs even when all other input/options are identical. Do not fold `AnalysisCacheVersion` into the schema constant.

- [ ] **Step 11: Run focused tests, compile every consumer, and confirm GREEN**

Run:

```powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj -c Release --no-restore
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RequestHasherTests|FullyQualifiedName~SymbolPathFormatterTests"
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~OutputFormatterTests
rtk dotnet build CsIndex.sln -c Release --no-restore
```

Expected: PASS, zero warnings. Inspect a saved DB using test SQL and confirm no absolute persisted path and no old display/source columns. Search CLI code and assert there is no `SymbolNameShortener`, endpoint display-string property access, or direct formatting of the temporary `StoredSymbol` forwarding properties.

- [ ] **Step 12: Commit the atomic schema/model/formatter cutover**

```powershell
rtk git add -A src/CsIndex.Core/Caching/RequestHasher.cs src/CsIndex.Core/Symbols/SymbolPathFormatter.cs src/CsIndex.Storage/Schema/SchemaMigrator.cs src/CsIndex.Storage/SqliteIndex.cs src/CsIndex.Storage/QueryModels.cs src/CsIndex.Storage/QueryRepository.cs src/CsIndex.Query/QueryResults.cs src/CsIndex.Query/SemanticQueryService.cs src/CsIndex.Cli/Program.cs src/CsIndex.Cli/SymbolSignatureFormatter.cs src/CsIndex.Cli/SymbolNameShortener.cs src/CsIndex.Cli/OutputFormatter.cs src/CsIndex.Cli/GraphOutputFormatter.cs tests/CsIndex.Core.Tests/RequestHasherTests.cs tests/CsIndex.Core.Tests/SymbolPathFormatterTests.cs tests/CsIndex.Storage.Tests/SqliteIndexTests.cs tests/CsIndex.Storage.Tests/SchemaFiveLogicalSymbolTests.cs tests/CsIndex.Storage.Tests/PortablePathPersistenceTests.cs tests/CsIndex.IntegrationTests/OutputFormatterTests.cs tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs
rtk git commit -m "feat: persist schema v5 semantic paths"
```

**Positive cases:** ordinary/partial declaration round-trip, preferred row, logical graph references, relative anchor/paths, relocation, lazy source load, all four shared path presentations, endpoint hydration, and compile-safe cross-layer cutover.

**Negative cases:** schema migration, auto deletion/rebuild, absolute path escape hatch, invalid preferred row, declaration ID in graph tables, partial self-edge, mutation on validation failure.

**Out of scope:** new selector parser, typed conditions, new CLI switches, canonical ordering, declaration/path presentation layouts beyond the existing default output surface.

### Task 6: Balanced Structured Symbol-Path Parser

**Normative sections:** 6-9, 19.1, 20.1, 22.1 (parser half), 22.3 (selector states).

**Files:**
- Create: `src/CsIndex.Query/Symbols/SymbolPathSyntax.cs`
- Create: `src/CsIndex.Query/Symbols/BalancedTextScanner.cs`
- Create: `src/CsIndex.Query/Symbols/SymbolPathParser.cs`
- Create: `tests/CsIndex.Query.Tests/BalancedTextScannerTests.cs`
- Create: `tests/CsIndex.Query.Tests/SymbolPathParserTests.cs`
- Retain temporarily but do not modify: `src/CsIndex.Query/Symbols/SymbolQueryParser.cs`, `SymbolQuery.cs`, and `TypeNameNormalizer.cs`; Task 9 removes them when every consumer is converted.

**Interfaces:**
- Produces: immutable `SymbolPathSelector` AST using Task 5's `SymbolPathStyle`, optional explicit namespace hierarchy, type hierarchy, and one-or-more executable segment selectors.
- Produces: distinct `GenericListState.Omitted/Present` and `ParameterListState.Omitted/Present`; present empty parameter list means exactly zero parameters.
- Produces: `SymbolPathParser.Parse(string)` for complete positional selectors, `ParseExecutable(string, PatternMode)` for method conditions, and `ParseHierarchy(string, PatternMode)` for namespace/type conditions.
- Consumes: Task 1's `ParseSelectorType` for parameter and conversion types and Task 5's shared `SymbolPathStyle` discriminator.
- Does not access SQLite, enumerate candidates, apply case modes, or format output.

- [ ] **Step 1: Write failing form/balancing tests**

Assert csharp and explicit ASTs for:

```text
Game.Core.Player.Inventory::Load(int).Validate(string).<lambda#1>
Game.Core::Player.Inventory::Load(int).Validate(string).<lambda#1>
A::B::C()
global::Program::<top-level-statements>
@global::Program::Run()
N::Outer<T>.Inner<U>::M(System.Collections.Generic.List<string?>,int[,],delegate*<int,void>)
N::T::[explicit:System.IDisposable.Dispose]()
```

Assert `global::` inside a parameter type, dots/`::` in square brackets, tuple punctuation, nested generics, arrays, pointers, nullable markers, function pointers, and escaped identifiers do not create structural separators. Assert exactly one top-level separator selects csharp and exactly two selects explicit.

- [ ] **Step 2: Write failing segment/state tests**

For each segment in `Outer.Local`, `Outer(int).Local`, `Outer.Local(string)`, and `Outer(int).Local(string)`, assert parameter omission/presence independently. Assert the five generic callable states from section 8.3:

```text
Method
Method<T>
Method()
Method<T>()
Method<T>(T)
```

Separately assert type components have no broad omitted-arity state: `Repository` is exact arity zero, `Repository<T>` is arity one, and `Repository<T,U>` is arity two. Multiple type arities require an explicit type glob.

Parse every exact special tag and synthetic marker from section 9. Assert lambda/anonymous ordinal accepts a positive integer or `*`, while initializer/top-level markers have no parameter list. Assert `@class` is a valid identifier, concrete output escapes C# keywords, and `Name`/`@Name` normalize to the same semantic identifier when lexically valid.

Assert `N::T::[operator:*](T,T)` parses `*` as the C# operator token inside the atomic bracket segment, not as a glob wildcard.

- [ ] **Step 3: Write failing malformed-input tests**

Assert `SymbolQueryParseException` for zero/three top-level separators, empty namespace/type/executable fields, empty child segments, old `Method()::Local()` and `Method()::<lambda#1>`, unmatched/misordered `<>()[]`, invalid generic placeholders, duplicate placeholders, ordinal 0/negative/non-number, unknown/incorrect-case tags, missing conversion target, wildcard in parameter types, and constructed generic argument input.

- [ ] **Step 4: Run parser tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~BalancedTextScannerTests|FullyQualifiedName~SymbolPathParserTests"`

Expected: FAIL because the AST/scanner/parser are absent.

- [ ] **Step 5: Implement the immutable AST**

Use these exact discriminators and omission states:

```csharp
public enum PatternMode { Glob = 1, Literal = 2 }
public enum GenericListState { Omitted = 0, Present = 1 }
public enum ParameterListState { Omitted = 0, Present = 1 }

public sealed record SymbolPathSelector(
    SymbolPathStyle Style,
    HierarchySelector? Namespace,
    HierarchySelector Type,
    IReadOnlyList<ExecutableSegmentSelector> ExecutableSegments);

public sealed record HierarchySelector(IReadOnlyList<HierarchySegmentSelector> Segments);

public sealed record CallableAritySelector(
    GenericListState GenericState,
    IReadOnlyList<string> GenericPlaceholders,
    ParameterListState ParameterState,
    IReadOnlyList<CanonicalTypeSelector> Parameters,
    IReadOnlyList<int> ParameterRefKinds);
```

`ExecutableSegmentSelector` is a closed record hierarchy for named, special, lambda, anonymous-method, initializer, and top-level segments. A special record carries the normalized lowercase tag, payload fields, conversion target selector where required, and `CallableAritySelector`.

- [ ] **Step 6: Implement one-pass balanced scanning**

Scan Unicode text left-to-right while tracking angle, parenthesis, square-bracket, and function-pointer/generic nesting. Record top-level `::` only when every nesting depth is zero and the token is not a `global::` qualifier inside type syntax. Scan executable `.` boundaries only at zero depth. Reject negative depth immediately and non-zero depth at end. Do not call `Split('.')`, `Split("::")`, use a last-dot ownership heuristic, or use regex as the structural parser.

- [ ] **Step 7: Implement form/segment parsing and exact errors**

Normalize identifier escapes only after validating C# identifier syntax. Keep grammar keywords/tags lowercase and case-sensitive regardless of later case options. When a parameter list is present and the generic list is omitted, set exact generic arity zero; when both are omitted, retain any-arity/any-signature. Pass each parameter/conversion type through Task 1 selector parsing with the segment's placeholder ordinal map.

- [ ] **Step 8: Run parser tests and mutation checks**

Run the Step 4 command. Then temporarily replace top-level scanning with a naive split and verify cases containing `global::`, brackets, and function pointers fail; restore the implementation and rerun GREEN. Temporarily allow old child `::` and verify its rejection test fails; restore and rerun GREEN.

Expected final result: PASS, zero warnings.

- [ ] **Step 9: Commit the structured parser**

```powershell
rtk git add src/CsIndex.Query/Symbols/SymbolPathSyntax.cs src/CsIndex.Query/Symbols/BalancedTextScanner.cs src/CsIndex.Query/Symbols/SymbolPathParser.cs tests/CsIndex.Query.Tests/BalancedTextScannerTests.cs tests/CsIndex.Query.Tests/SymbolPathParserTests.cs
rtk git commit -m "feat: parse structured symbol paths"
```

**Positive cases:** both forms, global/literal-global, omitted namespace via csharp, nested/generic types, all signature states, all exact special/synthetic markers, anonymous wildcard ordinal, balanced C# type syntax.

**Negative cases:** legacy grammar/child separator, malformed delimiter/order, empty fields/segments, invalid placeholder/ordinal/tag/type syntax, implicit form reinterpretation.

**Out of scope:** candidate resolution, case comparison, typed option conditions, formatting, CLI help.

### Task 7: Canonical Ordering and Parser/Formatter Round-Trip Closure

**Normative sections:** 7, 8.4, 9, 16, 20.6, 21, 22.1 (formatter half), 22.8 (ordering).

**Files:**
- Modify: `src/CsIndex.Core/Symbols/SymbolPathFormatter.cs`
- Create: `src/CsIndex.Query/Symbols/SymbolCanonicalComparer.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs` (canonical result ordering only)
- Modify: `src/CsIndex.Query/CallerTreeBuilder.cs`
- Modify: `src/CsIndex.Query/AsyncPathResolver.cs`
- Modify: `tests/CsIndex.Core.Tests/SymbolPathFormatterTests.cs`
- Create: `tests/CsIndex.Query.Tests/SymbolCanonicalComparerTests.cs`
- Modify: `tests/CsIndex.Query.Tests/SymbolPathParserTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`

**Interfaces:**
- Retains: Task 5's final `SymbolPathFormatter.Format(SymbolPathData, SymbolPathFormatOptions)` API; no second formatter or stored-display adapter is introduced.
- Produces: `SymbolCanonicalComparer.Instance` and canonical key helpers for logical symbols, declarations, graph nodes, and edges.
- Changes: all Query-layer ordering sites use semantic identity/location keys, never formatted names, reconstructed absolute paths, selected style, or selected case mode.
- Consumes: Task 5 semantic stored fields/formatter and Task 6 parser.
- Does not add CLI option parsing, presentation settings, accessibility/modifier/return-type composition, locations, or source text.

- [ ] **Step 1: Extend exact-style formatter coverage**

Build paths for nested generic types and a local/lambda chain and retain these exact assertions:

```csharp
Assert.Equal(
    "Game.Core.Player.Inventory::Load(int).Validate().<lambda#1>",
    Format(SymbolPathStyle.CSharp, shortNames: false));
Assert.Equal(
    "Game.Core::Player.Inventory::Load(int).Validate().<lambda#1>",
    Format(SymbolPathStyle.Explicit, shortNames: false));
Assert.Equal(
    "Player.Inventory::Load(System.Guid)",
    FormatGuid(SymbolPathStyle.CSharp, shortNames: true));
Assert.Equal(
    "**::Player.Inventory::Load(System.Guid)",
    FormatGuid(SymbolPathStyle.Explicit, shortNames: true));
```

Add every section 9 marker, global namespace (`Program::...` versus `global::Program::...`), literal `@global`, nested generic types, explicit-interface payloads, conversions, and operator `*`. These tests are regression guards for the Task 5 API and may already be GREEN; do not alter correct formatting merely to manufacture a RED.

- [ ] **Step 2: Add parser/formatter round-trip tests**

For every special/anonymous/synthetic category and both nested generic type forms, format csharp/full, explicit/full, csharp/short, and explicit/short, then parse with `SymbolPathParser`. Assert the parsed concrete segment/signature data equals the stored semantic constraints. For explicit-short output, assert the namespace selector is exactly `**` and is deliberately multi-match at resolution time.

- [ ] **Step 3: Add failing canonical-order invariance tests**

Create symbols that differ at each key level: namespace, type generic arity/path, executable signature, preferred stored path, source start, stable key. Shuffle repeatedly and assert `SymbolCanonicalComparer` produces the exact section 16.3 order. Format the sorted values under every style/short combination and reconstruct absolute paths under two base directories; assert ordered stable-key sequences remain identical. Add graph siblings and edges whose display order differs from semantic order so current `DisplayName` sorting is observably RED.

- [ ] **Step 4: Run formatter/parser/order tests and confirm the accepted RED**

Run:

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SymbolPathFormatterTests
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolPathParserTests|FullyQualifiedName~SymbolCanonicalComparerTests"
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~GraphQueryTests
```

Accepted RED: missing canonical comparer and old display-based ordering. Formatter/round-trip failures indicate a Task 5/6 defect exposed by stronger coverage; fix that defect through the same RED-GREEN loop rather than weakening the assertion.

- [ ] **Step 5: Close any formatter/parser semantic gaps**

Use only semantic `SymbolPathData`; never read the Task 5 forwarding display properties. Fix any exact or round-trip failure in the existing shared formatter/parser while keeping their public records unchanged. Concrete formatter output contains no wildcard except the intentional explicit-short namespace `**`; it never adds declaration role or location text.

- [ ] **Step 6: Implement canonical comparers**

Compare ordinal, case-sensitive values in this order: namespace path, `TypeIdentityPath`, `ExecutableIdentityPath`, preferred declaration stored path (empty last only for metadata), preferred source start, logical stable key. Definition declaration comparer orders partial definition before partial implementation, then stored path/start/ID. Source-match comparer orders logical canonical key, declaration path/start/role/ID. Graph ordering is parent-first and uses logical canonical keys for siblings/nodes/edges.

- [ ] **Step 7: Replace Query-layer display/path sorting**

Change root lists, caller trees, async paths, graph siblings/nodes/edges, and endpoint result rows to use the shared comparers. Preserve graph parent-before-child topology; use semantic keys only as sibling/tie-break ordering. Remove every Query-layer `DisplayName` sort and every absolute-path sort. Do not change traversal membership or filtering in this task.

- [ ] **Step 8: Run tests and mutation checks**

Run the Step 4 commands. Temporarily shorten dotted signature/payload types and verify formatter/round-trip tests fail; restore. Temporarily sort one graph family on formatted display name and verify style-invariance/canonical-order tests fail; restore. Temporarily order on reconstructed absolute path and verify the two-base test fails; restore. Final runs must PASS with zero warnings.

- [ ] **Step 9: Commit canonical formatting closure and ordering**

```powershell
rtk git add src/CsIndex.Core/Symbols/SymbolPathFormatter.cs src/CsIndex.Query/Symbols/SymbolCanonicalComparer.cs src/CsIndex.Query/SemanticQueryService.cs src/CsIndex.Query/CallerTreeBuilder.cs src/CsIndex.Query/AsyncPathResolver.cs tests/CsIndex.Core.Tests/SymbolPathFormatterTests.cs tests/CsIndex.Query.Tests/SymbolCanonicalComparerTests.cs tests/CsIndex.Query.Tests/SymbolPathParserTests.cs tests/CsIndex.IntegrationTests/GraphQueryTests.cs
rtk git commit -m "feat: canonicalize symbol result ordering"
```

**Positive cases:** csharp/explicit, global/literal-global, short/full, every special marker, generic nesting, parser round-trip, canonical sort at every tie-breaker, graph parent-first order.

**Negative cases:** shortening parameter/payload types, style/case/base-dependent identity or order, formatting from forwarding display properties, role/location suffix in paths.

**Out of scope:** CLI option parsing, portable path presentation, declaration-role layouts, and new accessibility/return-type composition.

### Task 8: Independently Typed and Cased Condition Compiler

**Normative sections:** 12, 19.1, 19.3, 20.3, 21, 22.5.

**Files:**
- Create: `src/CsIndex.Query/Symbols/TypedCondition.cs`
- Create: `src/CsIndex.Query/Symbols/TypedConditionCompiler.cs`
- Create: `src/CsIndex.Query/Symbols/StructuralGlobMatcher.cs`
- Create: `src/CsIndex.Query/Symbols/SymbolSelectionRequest.cs`
- Modify: `src/CsIndex.Query/Symbols/SourceTextFilter.cs`
- Create: `tests/CsIndex.Query.Tests/TypedConditionCompilerTests.cs`
- Create: `tests/CsIndex.Query.Tests/StructuralGlobMatcherTests.cs`
- Modify: `tests/CsIndex.Query.Tests/SourceTextFilterTests.cs`

**Interfaces:**
- Produces: `ConditionSyntax { Glob, Literal, Regex }`, `CaseMode { Strict, Ignore }`, and `ConditionCategory { Namespace, Type, Method, File, Include, Exclude }`.
- Produces: `TypedCondition(ConditionCategory Category, ConditionSyntax Syntax, string Value)` and `SymbolCaseOptions` with five independently default-strict categories.
- Produces: `SymbolSelectionRequest(string? Selector, IReadOnlyList<TypedCondition> Conditions, SymbolCaseOptions Case, FunctionTargetFilter FunctionFilter, bool KindSpecified, bool AsyncStatusSpecified)`.
- Produces: `TypedConditionCompiler.Compile(SymbolSelectionRequest, TimeSpan? regexTimeout)` returning logical and declaration-scoped compiled predicates.
- Consumes: Task 6 hierarchy/executable parsing for namespace/type/method glob/literal and Task 5 `StoredDeclaration`.
- Does not retrieve candidates or perform graph/relation traversal.

- [ ] **Step 1: Write failing option-domain and composition tests**

Construct mixed conditions in one request and assert glob, literal, and regex coexist. Verify repeated namespace/type/method/file alternatives are OR within category; categories are AND; every include is AND; any matching exclude rejects. Verify all file/source predicates for a partial logical symbol must pass on the same declaration row and cannot combine evidence from definition and implementation.

Use these representative assertions:

```csharp
Assert.True(Matches(namespaceGlob: "Game.**", typeLiteral: "Player.Controller"));
Assert.True(Matches(methodGlob: "Run.**.<lambda#1>"));
Assert.False(Matches(fileGlob: "src/*/Player.cs", file: "src/a/b/Player.cs"));
Assert.True(Matches(fileGlob: "src/**/Player.cs", file: "src/a/b/Player.cs"));
Assert.True(MatchesSource(includeGlob: "await*ConfigureAwait", source: "await\r\nConfigureAwait"));
```

- [ ] **Step 2: Add failing structural glob tests**

For namespace/type/method hierarchies, assert embedded `*` stays inside one component, whole `*` consumes exactly one level, whole `**` consumes zero-or-more levels, and embedded `**` equals `*`. For files, normalize input `\` to `/`, keep `*` inside one path component, and let whole `**` span directories. For source glob, assert `*` and `**` are equivalent, unanchored, and cross CR, LF, CRLF, NEL, U+2028, and U+2029.

Within structured special segments, apply glob tokens only to identifier/member payload positions. The `*` in `[operator:*]` is always the literal C# operator token, and the `*` in `<lambda#*>`/`<anonymous-method#*>` is the dedicated ordinal wildcard; neither is a generic identifier glob.

Assert `--method` glob/literal retains parser omission semantics (`Method` all overloads; `Method()` exact zero/non-generic), while method regex is a raw whole-value regex against `ExecutableDisplayPath` and performs no omission expansion.

- [ ] **Step 3: Add failing independent-case tests**

Test all five categories independently with names that differ only by case. For a csharp dotted suffix that overlaps namespace and type, align it to candidate components first, then assert namespace components use namespace case and type components use type case. For explicit-interface payloads, assert interface namespace/type identifiers use their cases and member identifiers use method case. Grammar keywords, aliases, tags, and markers remain lowercase/case-sensitive.

For structured positional/method glob/literal input, retain those semantic subcategory case rules inside explicit-interface payload and parameter types. For `--method-regex`, apply only `--method-case` to the entire raw canonical executable string; namespace/type case modes must not rewrite regex subranges.

- [ ] **Step 4: Add failing regex validation/timeout tests**

Assert name/file regex is wrapped as `\A(?:expression)\z`; source regex is unanchored. Assert every regex is `CultureInvariant`, optional `IgnoreCase`, and uses the production two-second timeout. Inject a millisecond timeout and a deterministic catastrophic input; timeout must throw `SymbolQueryParseException` and return no partial matches. Invalid regex syntax must fail at compile time.

- [ ] **Step 5: Run condition tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TypedConditionCompilerTests|FullyQualifiedName~StructuralGlobMatcherTests|FullyQualifiedName~SourceTextFilterTests"`

Expected: FAIL because matching still uses global `UseRegex`/`IgnoreCase` and flat wildcard conversion.

- [ ] **Step 6: Implement typed condition models and non-backtracking structural glob**

Use dynamic programming over pattern/value component indices for whole `**`, and a linear/star-backtracking-with-bounds matcher inside a component. Do not translate hierarchy glob to an unbounded regex. Parse namespace/type/method glob/literal structurally; literal disables wildcard tokens but still uses semantic signature omission. Normalize file pattern separators before component matching.

The compiled boundary must expose these operations:

```csharp
public sealed class CompiledTypedConditions
{
    public bool MatchesLogical(StoredSymbol symbol, CancellationToken cancellationToken);
    public bool MatchesDeclaration(
        StoredSymbol symbol,
        StoredDeclaration declaration,
        CancellationToken cancellationToken);
    public bool HasDeclarationConditions { get; }
    public bool RequiresSourceText { get; }
}
```

`MatchesLogical` applies namespace/type/method conditions and direct kind/async. `MatchesDeclaration` applies file/include/exclude to one row after `MatchesLogical`; it must never load or inspect source unless `RequiresSourceText` is true.

- [ ] **Step 7: Implement source literal/glob/regex search**

Literal uses ordinal `IndexOf` with the configured comparison. Glob treats the pattern as an unanchored wildcard where either star token crosses all supported newline code points. Regex uses `Regex.IsMatch`. Check cancellation before and after each condition evaluation and after regex execution; propagate timeout as a query error.

- [ ] **Step 8: Run focused tests and mutation checks**

Run the Step 5 command. Mutate same-category OR to AND and verify mixed-alternative tests fail; restore. Mutate declaration evaluation to combine two partial rows and verify the same-row test fails; restore. Mutate source regex to whole-value anchoring and verify substring tests fail; restore. Final run must PASS with zero warnings.

- [ ] **Step 9: Commit typed condition compilation**

```powershell
rtk git add src/CsIndex.Query/Symbols/TypedCondition.cs src/CsIndex.Query/Symbols/TypedConditionCompiler.cs src/CsIndex.Query/Symbols/StructuralGlobMatcher.cs src/CsIndex.Query/Symbols/SymbolSelectionRequest.cs src/CsIndex.Query/Symbols/SourceTextFilter.cs tests/CsIndex.Query.Tests/TypedConditionCompilerTests.cs tests/CsIndex.Query.Tests/StructuralGlobMatcherTests.cs tests/CsIndex.Query.Tests/SourceTextFilterTests.cs
rtk git commit -m "feat: compile typed symbol conditions"
```

**Positive cases:** mixed modes, independent case categories, candidate-aware csharp case split, all hierarchy/file/source glob rules, method omission, regex anchoring/timeout, exact boolean composition, same declaration row.

**Negative cases:** punctuation-triggered regex, global case switch, embedded recursive `**`, source whole-value matching, partial cross-row evidence, timeout-as-no-match.

**Out of scope:** CLI token names/scope, candidate retrieval, cardinality, traversal, presentation.

### Task 9: Candidate-Aware Symbol Path Resolution and Declaration Projection

**Normative sections:** 6-9, 12.2-12.5, 14.3, 20.4, 21, 22.2, 22.5.

**Files:**
- Create: `src/CsIndex.Query/Symbols/SymbolPathResolver.cs`
- Modify: `src/CsIndex.Query/ExecutableTargetResolver.cs`
- Modify: `src/CsIndex.Query/MethodTargetResolver.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs` (candidate diagnostics only)
- Modify: `src/CsIndex.Storage/QueryModels.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Delete after consumer conversion: `src/CsIndex.Query/Symbols/SymbolQuery.cs`
- Delete after consumer conversion: `src/CsIndex.Query/Symbols/SymbolQueryParser.cs`
- Delete after consumer conversion: `src/CsIndex.Query/Symbols/SymbolMatcher.cs`
- Delete after consumer conversion: `src/CsIndex.Query/Symbols/SymbolPatternMatcher.cs`
- Delete after consumer conversion: `src/CsIndex.Query/Symbols/SymbolSearchRequest.cs`
- Delete after consumer conversion: `src/CsIndex.Query/Symbols/TypeNameNormalizer.cs`
- Create: `tests/CsIndex.IntegrationTests/SymbolPathResolverTests.cs`
- Create: `tests/CsIndex.IntegrationTests/SymbolResolutionFixture.cs`
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/ProjectScopedSourceSymbolPersistenceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs`
- Delete after replacement coverage is GREEN: `tests/CsIndex.Query.Tests/SymbolQueryParserTests.cs`
- Delete after replacement coverage is GREEN: `tests/CsIndex.Query.Tests/SymbolPatternMatcherTests.cs`

**Interfaces:**
- Produces: `ResolvedLogicalRoot(StoredSymbol Symbol, IReadOnlyList<StoredDeclaration> MatchingDeclarations)`.
- Produces: `SymbolPathResolver.ResolveLogicalRootsAsync(long, SymbolSelectionRequest, bool sourceOnly, CancellationToken)` and `ResolveDeclarationRowsAsync(long, SymbolSelectionRequest, CancellationToken)`.
- Produces: repository candidate hints and containment-chain reads that do not select normalized source unless required.
- Removes: Task 5's temporary non-persisted `StoredSymbol.DisplayName`/`FullyQualifiedName` forwarding properties after every remaining consumer is converted to semantic fields or the shared formatter.
- Consumes: Task 6 parser, Task 8 compiled conditions, Task 5 logical/declaration repository, Task 7 comparer.
- Does not apply `--include-overrides`, caller/callee/relation/graph traversal, or command cardinality; Task 10 owns those phases.

- [ ] **Step 1: Build a failing ambiguity/containment fixture**

Index these deliberately confusing owners:

```text
Namespace1.Namespace2::Class1.Class2::Method1().Local().<lambda#1>
Namespace1.Namespace2.Namespace3::Class2::Method1().Local().<lambda#1>
Company.Namespace1.Namespace2::Class1.Class2::Method1().Other().<lambda#1>
global::Class1.Class2::Method1()
@global::Class1.Class2::Method1()
```

Include same-named locals under different methods, a same-named grandchild separated by another local, mixed project/profile copies, overloads/generic arities, partial definition/implementation rows, and lambda/anonymous ordinal siblings.

- [ ] **Step 2: Add failing csharp/explicit resolution tests**

Assert `Class1.Class2::Method1()` returns every namespace suffix match, `Namespace1.Namespace2.Class1.Class2::Method1()` may also match a longer `Company.` prefix, and `Namespace1.Namespace2::Class1.Class2::Method1()` is exact. Assert `A::B::C()` never falls back. Assert `global` and `@global` remain distinct, and explicit namespace `**` includes the global namespace by consuming zero components. Assert csharp and explicit paths formatted from the same symbol resolve to the same logical ID; short explicit may intentionally return multiple namespaces.

- [ ] **Step 3: Add failing executable/signature/containment tests**

Assert each bare/generic/empty/typed parameter state at every local segment returns the exact overload set. Assert aliases/framework names, generic placeholder renaming, ref modes, nullable rules, arrays, pointers, tuples, function pointers, and conversion targets use Task 1 identity. Assert `**` can span executable levels but every accepted result's actual parent chain contains each matched segment. A same-named grandchild without its intervening owner must not match a direct-child selector.

Assert concrete and wildcard lambda/anonymous ordinals, initializers, top-level statements, and every special segment resolve. Assert namespace-like/type-like capitalization never determines a boundary.

- [ ] **Step 4: Add failing declaration-filter/projection tests**

For a partial pair, make only the definition pass one file/source condition and only the implementation pass a different condition. Assert each single condition selects the logical symbol once; their conjunction selects none. `ResolveDeclarationRowsAsync` returns every passing physical row, while logical resolution de-duplicates. Assert no source blob is read for namespace/type/method/file-only searches using an injected repository read counter.

- [ ] **Step 5: Run resolver tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~SymbolPathResolverTests`

Expected: FAIL because current resolution guesses a namespace from the last dot, handles only method/lambda legacy text, and lacks declaration projection.

- [ ] **Step 6: Add semantic candidate and containment repository reads**

`FindLogicalSymbolCandidatesAsync` accepts optional exact leaf-name/kind hints derived safely from a selector; if a wildcard or case-ignore prevents a safe SQL equality hint, pass null rather than narrowing incorrectly. Return logical semantic fields and preferred location but no source. Add one batched containment query that loads the candidate and all ancestors to its containing type, preserving IDs/parent IDs. Add a declaration query whose projection includes normalized source only when `includeSourceText` is true.

- [ ] **Step 7: Implement csharp and explicit owner matching**

For explicit form, compare the namespace hierarchy and outer-to-inner type hierarchy independently with exact boundary semantics. For csharp form, align the selector hierarchy to every possible suffix of the candidate's complete namespace-component plus type-component sequence. After alignment, apply namespace case rules to candidate namespace positions and type case rules to type positions; never pre-split the input by capitalization or a simple-name lookup.

An unqualified csharp type suffix is equivalent to `**::<type>` but remains represented as csharp in the AST. Preserve distinct project/profile logical candidates and sort them with `SymbolCanonicalComparer`.

- [ ] **Step 8: Implement executable-chain matching and projection**

Walk each candidate's stored immediate containment chain from the type-owned root segment to the leaf. Match structured glob/literal segment patterns with dynamic programming for `**`; match a non-recursive segment against semantic kind/name/generic/parameter/special data. Do not accept a textual `ExecutableDisplayPath` match unless the parent-ID chain agrees.

Apply predicates in this order:

```text
structured selector (when present)
AND logical namespace/type/method typed conditions
AND direct kind/async
AND one associated declaration satisfying file/include/exclude together
```

Return one `ResolvedLogicalRoot` per logical ID. `sourceOnly` requires at least one declaration and preferred source availability. `ResolveDeclarationRowsAsync` emits every passing declaration row in Task 7 source-match order.

- [ ] **Step 9: Remove the legacy parser/matcher path and forwarding members**

Convert `ExecutableTargetResolver` and `MethodTargetResolver` internal consumers to `SymbolPathResolver`; then delete the six legacy parser/query/matcher files and their obsolete tests. Change `SemanticQueryService` candidate descriptions and the named test fixtures/assertions to use `SymbolPathFormatter` or explicit semantic fields. Delete the two Task 5 forwarding properties from `StoredSymbol`. Build errors, not another forwarding property, identify any missed consumer. Do not retain an adapter that accepts old child `::`, last-dot namespace guessing, global `UseRegex`, or global `IgnoreCase`.

- [ ] **Step 10: Run resolver/parser/condition suites and mutation checks**

Run:

```powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~SymbolPathResolverTests
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore
rtk dotnet build CsIndex.sln -c Release --no-restore
```

Mutate explicit matching to csharp suffix and verify exact-boundary tests fail; restore. Skip one containment edge and verify grandchild leakage tests fail; restore. Merge partial row predicates and verify same-row tests fail; restore. Final commands must PASS with zero warnings.

- [ ] **Step 11: Commit semantic resolution and legacy removal**

```powershell
rtk git add -A src/CsIndex.Query/Symbols src/CsIndex.Query/ExecutableTargetResolver.cs src/CsIndex.Query/MethodTargetResolver.cs src/CsIndex.Query/SemanticQueryService.cs src/CsIndex.Storage/QueryModels.cs src/CsIndex.Storage/QueryRepository.cs tests/CsIndex.Query.Tests tests/CsIndex.IntegrationTests/SymbolPathResolverTests.cs tests/CsIndex.IntegrationTests/SymbolResolutionFixture.cs tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs tests/CsIndex.IntegrationTests/CliCommandTests.cs tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs tests/CsIndex.IntegrationTests/GraphQueryTests.cs tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs tests/CsIndex.IntegrationTests/ProjectScopedSourceSymbolPersistenceTests.cs tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs
rtk git commit -m "feat: resolve canonical symbol paths"
```

**Positive cases:** exact explicit, csharp suffix/namespace omission, global/literal-global, copied styles, all signature states/specials, structural wildcards, immediate containment, project/profile determinism, same-row declaration filtering.

**Negative cases:** fallback form reinterpretation, capitalization boundary guessing, descendant text-only match, partial duplication/cross-row evidence, source scan for name-only query, any legacy parser/matcher behavior.

**Out of scope:** command option matrix/cardinality, post-root traversal, help, final presentation wiring.

### Task 10: Two-Phase Query Orchestration and Root-Only Traversal

**Normative sections:** 11, 13, 14.3, 15.4-15.5, 19.2-19.3, 20.8, 21, 22.6.

**Files:**
- Modify: `src/CsIndex.Query/QueryResults.cs`
- Modify: `src/CsIndex.Query/SemanticQueryService.cs`
- Modify: `src/CsIndex.Query/ExecutableTargetResolver.cs`
- Modify: `src/CsIndex.Query/MethodTargetResolver.cs`
- Modify: `src/CsIndex.Query/SourcePositionResolver.cs`
- Modify: `src/CsIndex.Query/AsyncPathResolver.cs`
- Modify: `src/CsIndex.Query/CallerTreeBuilder.cs`
- Modify: `src/CsIndex.Storage/QueryRepository.cs`
- Create: `tests/CsIndex.IntegrationTests/RootSelectionOrchestrationTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs`

**Interfaces:**
- Produces: `RootSelection(StoredProfile Profile, IReadOnlyList<ResolvedLogicalRoot> Roots)` and `DeclarationResultRow(StoredSymbol Symbol, StoredDeclaration Declaration)`.
- Produces: `LogicalSymbolResultRow(StoredSymbol Symbol, StoredDeclaration? PreferredDeclaration)` and `LoadLogicalRowsAsync(RootSelection, bool includeSourceText, CancellationToken)` for preferred-location/source projection after selection.
- Produces: `SemanticQueryService.SelectRootsAsync(SymbolSelectionRequest, string? profileName, bool sourceOnly, GeneratedFilter rootGeneratedFilter, CancellationToken)` and `SelectSourceRowsAsync(SymbolSelectionRequest, string? profileName, CancellationToken)`; `GeneratedFilter.Include` is the default root behavior.
- Produces: `SemanticQueryService.ExpandOverrideRootsAsync(RootSelection, CancellationToken)` returning the canonical original-plus-expanded logical roots without reapplying root predicates.
- Produces: traversal/result methods that consume an already selected `RootSelection`; they do not rerun filters.
- Retains: Task 5's ordinally keyed `CallResult.SymbolsById` and `RelationResult.SymbolsById`; all new two-phase service paths must populate those dictionaries for every endpoint that a formatter may name.
- Changes: `DefinitionResult` contains declaration rows; `SourceSearchResult` contains declaration rows; `source show` returns the preferred declaration for one selected logical root.
- Produces: query-time `IndexPathResolver` creation from repository DB path, stored anchor, and optional base directory for source reads and `--at` normalization.
- Removes: public/internal service overloads whose only purpose is the legacy query string/global matcher contract.
- Consumes: Task 9 root/declaration resolution and Task 7 ordering.

- [ ] **Step 1: Write failing command-neutral two-phase tests**

Instrument repository traversal methods with counters. For definition, references, callers, callees, overrides, async tree, and callers tree, perform root selection with namespace/type/method/file/source/kind/async filters and assert no call/relation/graph repository method runs during selection. After explicitly invoking the traversal method, assert secondary results that do not match root file/source/case conditions remain present.

Assert filter placement examples:

```csharp
var roots = await service.SelectRootsAsync(requestForRootInFileA, profile, sourceOnly: true, token);
Assert.Single(roots.Roots);
var callers = await service.FindCallersAsync(roots, generated, dispatch, callerScope, token);
Assert.Contains(callers.Calls, call => call.DocumentPath == "src/FileB.cs");
```

- [ ] **Step 2: Add failing cardinality/projection tests**

Assert filters reduce logical roots before any cardinality decision. A partial pair always counts as one root. `source show` selects one logical root and its preferred implementation row; a definition-only partial uses its definition. `definition` returns all associated declaration rows even when only one row satisfied the root file/source filter. `source search` returns every passing declaration row. Graph selection exposes zero/multiple roots without traversing so the caller can classify no-match/ambiguity before traversal.

- [ ] **Step 3: Add failing kind/async coverage tests**

Verify `all`/omission includes method, lambda, initializer, and top-level nodes but excludes type-only symbols; `method` includes every source method/special/local category but not anonymous/synthetic owners; `lambda` includes lambda and anonymous method only. Verify async/sync uses each node's direct `AsyncRole`, including sync initializer with async child, async child under sync owner, and directly-awaiting top-level statements. Combine with `--async-involved` for list without replacing its derived-depth predicate.

On command rows that already accept `--exclude-generated`/`--only-generated`, apply the preferred declaration's generated flag as a root predicate before require-single. Preserve the established call/document generated filter during the later reference/caller/callee traversal as well; root filtering must not silently replace result filtering.

- [ ] **Step 4: Add failing source-position/path-context tests**

With a stored relative document and moved tree, assert `definition --at` using reconstructed absolute path and effective-base-relative path locates the same call. Apply a query-time base override and assert source show/line-column resolution reads the override tree. A missing reconstructed source produces the normal source/input failure only when the operation reads it; a formatting-only logical search succeeds.

- [ ] **Step 5: Run focused orchestration tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~RootSelectionOrchestrationTests|FullyQualifiedName~FunctionTargetFilterTests|FullyQualifiedName~GraphQueryTests|FullyQualifiedName~SymbolSourceQueryTests"`

Expected: FAIL because current one-shot service methods can traverse before CLI cardinality checks and source results are logical-symbol rows rather than declaration rows.

- [ ] **Step 6: Introduce root/declaration result models**

Use these exact result boundaries:

```csharp
public sealed record RootSelection(
    StoredProfile Profile,
    IReadOnlyList<ResolvedLogicalRoot> Roots);

public sealed record DeclarationResultRow(
    StoredSymbol Symbol,
    StoredDeclaration Declaration);

public sealed record LogicalSymbolResultRow(
    StoredSymbol Symbol,
    StoredDeclaration? PreferredDeclaration);

public sealed record SourceSearchResult(
    StoredProfile Profile,
    IReadOnlyList<DeclarationResultRow> Matches);

public sealed record DefinitionResult(
    RootSelection Selection,
    IReadOnlyList<DeclarationResultRow> Definitions);
```

All relation/call/graph result contexts retain the original `RootSelection`, enabling CLI cardinality/error reporting without re-resolving. Retain Task 5's `SymbolsById` result dictionaries and populate them on every rewritten path by one batched `GetSymbolsByIdsAsync` over every caller/callee/definition/relation endpoint ID, then canonical-sort result records using those semantic symbols.

- [ ] **Step 7: Implement selection-first service APIs**

`SelectRootsAsync` loads profile, invokes `SymbolPathResolver.ResolveLogicalRootsAsync`, applies the requested preferred-declaration generated predicate, de-duplicates by logical ID, and canonical-sorts. `SelectSourceRowsAsync` invokes declaration resolution and source-match-sorts. It does not know command cardinality or exit codes. `LoadLogicalRowsAsync` batches preferred declaration reads after cardinality and includes normalized source only when output/source execution requests it; a logical row always projects its preferred declaration even when a different declaration satisfied root filters.

Traversal APIs accept `RootSelection`. `FindDefinitionsAsync` loads all declarations for selected logical IDs. References/callers use selected IDs as callees; callees use selected IDs as callers; overrides read selected method relation roots; graph builders accept one selected root. Existing `--include-overrides`, dispatch, caller scope, lambda-call inclusion, and call/document generated-source filtering occur after root selection in their established semantic phase. `--async-involved` and preferred-declaration generated filtering are root predicates and must already have run before cardinality.

Delete the current post-query `StableKey.Contains("|constructed:")`/`"|reduced:"` cleanup; Task 3 guarantees those non-logical rows are never persisted.

- [ ] **Step 8: Normalize include-overrides and partial behavior**

Run override/interface expansion only after roots pass typed conditions. Expansion results are not re-filtered by root conditions. Explicit `Kind=Lambda` is rejected for override expansion/`overrides`; `Kind=All`/omission and `Kind=Method` are allowed where currently documented. Calls, caller/callee IDs, override relations, bindings, and graph roots remain logical IDs, so no declaration duplicate enters a result.

`--include-overrides` still requires an exact, wildcard-free positional structured selector that resolves method-kind roots. New typed conditions may AND-refine those roots before expansion. After cardinality succeeds, `ExpandOverrideRootsAsync` adds descendant overrides/interface implementations once; symbol find outputs the expanded roots, while definition/reference/caller commands use the expanded IDs for their command-specific operation.

- [ ] **Step 9: Make source-position reads path-context aware**

Change `SourcePositionResolver.ResolveOffset`/`ResolveLineColumn` callers to pass the absolute path returned by Task 4. `FindDocumentsAsync` compares the complete normalized stored relative identity, not an absolute/suffix SQL heuristic; use platform path identity (`OrdinalIgnoreCase` on Windows) independently of the query `--file-case` matcher. Build the query resolver from `QueryRepository.DatabasePath`, `StoredProfile.IndexRootAnchor`, and the service's optional base directory; never write this context back to SQLite.

- [ ] **Step 10: Run focused and full Query/Integration tests**

Run the Step 5 command, then:

```powershell
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~GraphQueryTests|FullyQualifiedName~SymbolSourceQueryTests|FullyQualifiedName~FunctionTargetFilterTests"
```

Expected: PASS, zero warnings. Mutation: move traversal before the cardinality boundary in a test seam and verify the traversal counter fails; restore and rerun GREEN.

- [ ] **Step 11: Commit two-phase query orchestration**

```powershell
rtk git add src/CsIndex.Query/QueryResults.cs src/CsIndex.Query/SemanticQueryService.cs src/CsIndex.Query/ExecutableTargetResolver.cs src/CsIndex.Query/MethodTargetResolver.cs src/CsIndex.Query/SourcePositionResolver.cs src/CsIndex.Query/AsyncPathResolver.cs src/CsIndex.Query/CallerTreeBuilder.cs src/CsIndex.Storage/QueryRepository.cs tests/CsIndex.IntegrationTests/RootSelectionOrchestrationTests.cs tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs tests/CsIndex.IntegrationTests/GraphQueryTests.cs tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs
rtk git commit -m "feat: separate root selection from traversal"
```

**Command rows covered:** `symbol find`, `symbol list`, `source search`, `source show`, `definition <query>`, `definition --at`, `references`, `callers`, `callees`, `overrides`, `async tree`, `callers tree`.

**Positive cases:** root predicates before cardinality, command-specific traversal after selection, preferred partial projection, all-definition/source declaration rows, override expansion after filtering, logical endpoint hydration, relative-path resolution with query-only base override.

**Negative cases:** filtering secondary results, traversing before cardinality, partial double count, owner async leakage, path override mutation, legacy absolute document matching.

**Out of scope:** CLI token/scope validation, exit-code selection, help, presentation/output destination.

### Task 11: Breaking CLI Option Matrix and Verbose Help

**Normative sections:** 12.1-12.2, 13, 17, 19, 20.8, 22.6, 22.8.

**Files:**
- Modify: `src/CsIndex.Cli/CliArguments.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Create: `tests/CsIndex.IntegrationTests/CliSymbolPathOptionMatrixTests.cs`
- Create: `tests/CsIndex.IntegrationTests/VerboseHelpTests.cs`

**Interfaces:**
- Produces: CLI parsing of every typed condition/case option, `--symbol-path-style`, `--base-dir`, `--path-style`, `--help-verbose`, and `--help --verbose`.
- Produces: one `CreateSelectionRequest(CliArguments, string? selector)` mapper; `Program` does not parse symbol-path strings or implement match logic.
- Produces: per-command allowed-option sets assembled from immutable option-family arrays, not one permissive global set.
- Consumes: Task 10 two-phase APIs and enforces cardinality before traversal/output-destination creation.

- [ ] **Step 1: Add failing tokenizer and removed-option tests**

Assert each value option accepts `--name value` and `--name=value`, repeatable condition options retain order, and each case/style/base/path option may occur only in documented scope. Add explicit failure assertions for query `--regex`, query `--ignore-case`, old child `::`, zero/three separator selector text, duplicate case options, invalid case/style values, and `index --base-dir`/`index --path-style`/`index --symbol-path-style`.

Add `help-verbose` to flags. `--verbose` remains runtime progress only for `index`; on query commands it is valid only when combined with `--help`, otherwise unknown.

- [ ] **Step 2: Add the failing exact command matrix**

For every option family, loop through every command row rather than sampling. The allowed matrix is:

| Command | Selector | Root conditions | Presentation/path |
| --- | --- | --- | --- |
| `symbol find` | optional | all typed conditions, case modes, kind, async | symbol style, short names, base/path style |
| `symbol list` | forbidden | all typed conditions, case modes, kind, async, existing async-involved | symbol style, short names, base/path style |
| `source search` | forbidden | all typed conditions, case modes, kind, async | symbol style, short names, base/path style |
| `source show` | required | all typed conditions as AND refinement | symbol style, short names, base/path style |
| `definition <query>` | required | all typed conditions as AND refinement | symbol style, short names, base/path style |
| `references` / `callers` / `callees` | required | all typed conditions as AND refinement | symbol style, short names, base/path style |
| `overrides` | required | applicable method root conditions; explicit lambda kind rejected | symbol style, short names, base/path style |
| `async tree` / `callers tree` | required | all typed conditions as AND refinement | symbol style, short names, base/path style |
| `definition --at` | forbidden | forbidden | symbol style, short names, base/path style |
| `conditions` | forbidden | forbidden | base/path style only; no symbol style/short names |
| `index` | input path | only existing index-local options | no query presentation/path options |

Retain each existing command-specific option only on its current row: include-overrides, dispatch, caller-scope, exclude-lambda-calls, async-involved, generated filters, show-source/source-layout, output formats/files, profile, diagnostics.

- [ ] **Step 3: Add failing selection-minimum/cardinality tests**

`symbol find` and `source search` require a positional selector or at least one explicit selection condition. Explicit `--kind all` or `--async-status all` counts as a selection condition; DB/profile/output/help/style/base/path options do not. `source show` and graph commands require a selector and exactly one filtered logical root. Multi-root commands allow many unless `--require-single`, which returns exit 5 after filtering but before override expansion/traversal/output file creation. A single selected root may therefore expand to several `--include-overrides` results without retroactively failing require-single. `definition --at` rejects every selector/root filter.

A syntactically valid zero-match `symbol find`, `symbol list`, or `source search` returns exit 0 with its normal empty payload. A command that requires an actual root reports its documented no-match query error; it must not turn zero candidates into a parse error or compatibility fallback.

For references/callers/callees, assert `--exclude-generated`/`--only-generated` filters the preferred root declaration before require-single and still retains its established filtering of later call/document rows.

For each command that already supports `--include-overrides`, require one wildcard-free positional structured selector whose selected roots are method-kind; reject condition-only, lambda, initializer/top-level, and wildcard selectors. Allow new typed conditions as root refinements. Assert require-single counts the filtered pre-expansion roots and expansion starts only after it passes.

- [ ] **Step 4: Add failing normal/verbose help synchronization tests**

For global help and every recognized command path, assert:

```text
--help --verbose == --verbose --help == --help-verbose
```

Verbose help must contain all design section 17 topics and copyable examples for constructors, static constructors, destructor, operators/conversions, accessors, explicit interface members, initializer, top-level statements, lambda/anonymous method, generic/overload omission, glob `*`/`**`, csharp suffix warning, exact explicit/global namespace, partial roles, base/path styles, and invalid examples.

Inject DB/source/output factories that throw if opened. Help with missing positional input and known operational options must return success without invoking any factory. Unknown command/unknown option still returns exit 2 before help output.

- [ ] **Step 5: Run CLI matrix/help tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CliSymbolPathOptionMatrixTests|FullyQualifiedName~VerboseHelpTests|FullyQualifiedName~CliCommandTests"`

Expected: FAIL because new option families/styles/path context/verbose help are not wired and old global matcher flags remain.

- [ ] **Step 6: Implement option-family constants and request mapping**

Define these exact option-name families in `Program` and compose command-specific allowed sets without widening other commands:

```csharp
private static readonly string[] NamespaceConditions =
    ["namespace", "namespace-literal", "namespace-regex", "namespace-case"];
private static readonly string[] TypeConditions =
    ["type", "type-literal", "type-regex", "type-case"];
private static readonly string[] MethodConditions =
    ["method", "method-literal", "method-regex", "method-case"];
private static readonly string[] FileConditions =
    ["file", "file-literal", "file-regex", "file-case"];
private static readonly string[] SourceConditions =
    ["include", "include-literal", "include-regex",
     "exclude", "exclude-literal", "exclude-regex", "source-case"];
private static readonly string[] QueryPathOptions = ["base-dir", "path-style"];
private static readonly string[] SymbolPresentationOptions =
    ["symbol-path-style", "short-names"];
```

`CreateSelectionRequest` tags each condition with its option-derived syntax, parses each case mode once, preserves explicit kind/async presence, and passes raw selector text to Task 9. Remove `UseRegex`/`IgnoreCase` request fields and remove the legacy flags from `CliArguments.Flags`.

- [ ] **Step 7: Wire two-phase command execution and exit categories**

For each command: parse/validate scope; terminate help; create service with database/base-dir; select roots/rows; apply mandatory or optional cardinality; only then traverse, create output destination, render, flush, and commit. Keep numeric exits exactly 0/2/3/4/5. Regex syntax/path query errors are exit 2; cancellation/source/output failures exit 3; schema/SQLite exit 4; explicit require-single mismatch exit 5.

Preserve Task 5's `index` ordering: create `IndexPathResolver` from resolved root and database path, prepare/validate analysis before opening/checking the database, then perform cache lookup/save. This guarantees cross-volume/share failure cannot mutate an existing DB. Custom DB changes the anchor only; Task 11 adds only the final option-scope/help validation around that established flow.

- [ ] **Step 8: Implement terminal help dispatch**

Validate the recognized command and its allowed option names first. If normal or verbose help is requested, skip positional/cardinality/DB/source/output checks and write stdout. Store verbose sections as shared structured help data reused by global/command help; do not maintain divergent heredoc copies. `--help-verbose` selects the same content as help plus verbose.

- [ ] **Step 9: Run focused CLI tests and mutation checks**

Run the Step 5 command. Temporarily add root options to `conditions` and verify forbidden-matrix tests fail; restore. Temporarily omit explicit `--kind all` from the selection-condition count and verify minimum tests fail; restore. Temporarily open the output destination before cardinality and verify sentinel/no-open tests fail; restore. Final run must PASS with zero warnings.

- [ ] **Step 10: Commit CLI grammar and help**

```powershell
rtk git add src/CsIndex.Cli/CliArguments.cs src/CsIndex.Cli/Program.cs tests/CsIndex.IntegrationTests/CliCommandTests.cs tests/CsIndex.IntegrationTests/CliSymbolPathOptionMatrixTests.cs tests/CsIndex.IntegrationTests/VerboseHelpTests.cs
rtk git commit -m "feat: expose canonical symbol query grammar"
```

**Positive cases:** every allowed option/command form, mixed typed modes, independent case switches, explicit all selection, help spellings/order, terminal help, two-phase cardinality, standard/custom index path setup.

**Negative cases:** every forbidden option row, removed flags/grammar, invalid values/duplicates, definition-at filters, verbose without help on query commands, DB/source/output open during help/cardinality failure.

**Out of scope:** exact table/JSON/tree presentation payloads; Task 12 owns them.

### Task 12: Shared Symbol/Path Presentation and Atomic Payload Integration

**Normative sections:** 14.3, 15.5, 16, 19.2-19.3, 20.6-20.8, 22.7-22.8.

**Files:**
- Modify: `src/CsIndex.Cli/SymbolSignatureFormatter.cs`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs`
- Modify: `src/CsIndex.Cli/GraphOutputFormatter.cs`
- Modify: `src/CsIndex.Cli/Program.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Create: `tests/CsIndex.IntegrationTests/SymbolPathOutputAcceptanceTests.cs`
- Create: `tests/CsIndex.IntegrationTests/PortablePathOutputTests.cs`

**Interfaces:**
- Changes: formatter settings include `SymbolPathFormatOptions`, `IndexPathResolver`, and `PathDisplayStyle` in addition to format/source layout.
- Changes: `SymbolSignatureFormatter` composes modifiers/return type with `SymbolPathFormatter.Format`; it never performs regex namespace shortening.
- Produces: definition/source declaration rows with explicit `declarationRole` field/column; logical rows remain one-per-symbol and role-free in path text.
- Consumes: Task 5 formatter/endpoint dictionaries, Task 7 comparers, Task 4 query path resolver, Task 10 declaration result models, current atomic `OutputDestination`.

- [ ] **Step 1: Add failing all-output symbol-style tests**

For every symbol-bearing payload family—symbol find/list, source show/search, definition, references, callers, callees, overrides, async tree, callers tree—capture table/text, JSON, tree/line/Mermaid as applicable. Assert each human-facing name uses the shared formatter and exact csharp/explicit plus short/full combinations. Copy each emitted concrete full path back into a search and assert it resolves; explicit-short may intentionally return all matching namespaces.

Assert JSON semantic fields (IDs, stable keys, namespace, type/signature data) do not change with display style. Only human-facing `displayName`, signature/label/header/candidate text changes.

Use a non-alias fully qualified return type and assert `--short-names` leaves that return type fully qualified; the option omits only the symbol owner's namespace.

- [ ] **Step 2: Add failing path-style/base-dir tests for every consumer**

Using a moved fixture and base override, assert `--path-style absolute` (default) and `relative` exactly affect table/text locations, JSON `location.path`, graph locations, source headers, and diagnostics. Relative output is the stored `/` path and ignores base-dir. Absolute output uses the effective base. Re-run each format under both bases and assert canonical logical/declaration order and non-path payload bytes remain stable.

- [ ] **Step 3: Add failing declaration-role/projection tests**

For a partial pair, assert symbol find/list/source show/call/relation/graph logical rows appear once at the preferred implementation location. `definition` emits two rows/records ordered definition then implementation, each with dedicated role text. `source search` emits each matching declaration row with its role. JSON result units representing a declaration contain exactly one `declarationRole` value from the three allowed strings. No symbol path contains a role suffix.

- [ ] **Step 4: Add failing ambiguity and ordering tests**

Force multi-root graph/source-show ambiguity and assert candidate lists use the selected shared symbol style and enough selected-style location information to distinguish equal semantic display paths. Assert parents precede canonically ordered graph children and all table/JSON/tree/line/Mermaid representations preserve the same logical node order.

- [ ] **Step 5: Add failing atomic-output boundary tests**

For formatting error, regex timeout, reconstructed missing source, SQLite error, cancellation during last record, flush failure, cancellation after flush/before commit, and replace failure: seed an output sentinel, run the command with `--output-file`, assert sentinel bytes survive and no sibling temp remains. For help, argument error, root no-match/ambiguity, and require-single failure, assert the output file/temp is never opened. Success writes UTF-8 without BOM and replaces atomically.

- [ ] **Step 6: Run output tests and confirm RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolPathOutputAcceptanceTests|FullyQualifiedName~PortablePathOutputTests|FullyQualifiedName~OutputFormatterTests|FullyQualifiedName~CliCommandTests"`

Expected: FAIL because explicit/short presentation options and portable path context are not yet injected into every consumer, declaration rows lack role-specific layouts, and the current result projection cannot satisfy the complete output matrix.

- [ ] **Step 7: Complete option-aware human-facing name formatting**

Extend the Task 5 formatter injection so every command passes the selected `SymbolPathFormatOptions`, including ambiguity/help-adjacent candidate rendering. `SymbolSignatureFormatter.Format` composes canonical `ReturnTypeDisplay` plus the shared path; access/static/async prefixes remain outside the path. Assert `SymbolNameShortener` is already absent, keep graph/JSON/call/relation endpoint names on the Task 5 shared path, and never format an ID using a repository-supplied string. Search the CLI project for direct `DisplayName` formatting and eliminate every remaining human-facing bypass.

- [ ] **Step 8: Route every path through `IndexPathResolver`**

Before resolving line/column or emitting a location, convert stored relative path to absolute with the effective resolver. Emit the resolver-selected display path, not the file-open path. JSON canonical stored fields remain stored relative semantic data; `location.path` is presentation. Missing-file exceptions include the reconstructed path but never rewrite DB state.

- [ ] **Step 9: Render logical versus declaration units explicitly**

Logical symbol table/JSON consumes `LogicalSymbolResultRow`, uses preferred-declaration source/location, and has no declaration role. Definition/source-search use `DeclarationResultRow` and add a fixed `declarationRole` column/property. Source show reads the preferred row. Sort each family with Task 7 comparers before rendering; do not sort on formatted names or absolute paths.

- [ ] **Step 10: Preserve atomic destination state machine**

Keep output creation lazy and call `WritePayload` only after validation/cardinality/traversal succeeds. Verify without changing `OutputDestination` that its existing `Unopened -> Opening -> Open -> Committed/Faulted/Disposed` protections, same-directory unique temp creation, flush/close/cancellation gates, and commit token cover the new flows. If a new focused RED demonstrates an actual destination defect, stop and return the required `OutputDestination.cs` ownership expansion to the primary agent before editing it; do not reopen after commit or mask the primary exception with cleanup failure.

- [ ] **Step 11: Run focused tests and mutation checks**

Run the Step 6 command. Mutate one graph label to `StoredSymbol.DisplayName` and verify all-family style tests fail; restore. Mutate one JSON location to the stored relative path under absolute mode and verify path matrix fails; restore. Remove the pre-commit cancellation check and verify final-record cancellation fails; restore. Final run must PASS with zero warnings.

- [ ] **Step 12: Commit shared output integration**

```powershell
rtk git add -A src/CsIndex.Cli tests/CsIndex.IntegrationTests/OutputFormatterTests.cs tests/CsIndex.IntegrationTests/CliCommandTests.cs tests/CsIndex.IntegrationTests/SymbolPathOutputAcceptanceTests.cs tests/CsIndex.IntegrationTests/PortablePathOutputTests.cs
rtk git commit -m "feat: render canonical symbol and source paths"
```

**Positive cases:** every name-bearing payload/style/short combination, copied-path queries, every path consumer/style/base, declaration roles, preferred logical rows, deterministic ordering, atomic success.

**Negative cases:** legacy display/shortener bypass, shortened parameter/payload type, absolute stored identity output in relative mode, base-dir mutation/reordering, role suffix, partial logical duplicate, any partial committed payload.

**Out of scope:** normative docs and final cross-project acceptance closure.

### Task 13: End-to-End Acceptance Closure and Legacy Rejection

**Normative sections:** complete design section 22 matrix and acceptance summary section 24.

**Files:**
- Create: `tests/CsIndex.IntegrationTests/CSharpSymbolPathAcceptanceTests.cs`
- Create: `tests/CsIndex.IntegrationTests/TypedSearchAcceptanceTests.cs`
- Create: `tests/CsIndex.IntegrationTests/PortableIndexAcceptanceTests.cs`
- Create: `tests/CsIndex.IntegrationTests/CSharpSymbolPathAcceptanceFixture.cs`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs` to add reusable base-directory query-service construction and read-only database snapshot helpers
- Modify: `tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs`
- Modify: `tests/CsIndex.Storage.Tests/SqliteIndexTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/ProjectScopedSourceSymbolPersistenceTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/GraphQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`

**Interfaces:**
- Produces: no new production API; this task closes observable behavior and removes stale test expectations.
- Consumes: Tasks 1-12 as one schema-v5 product.
- Requires: no feature-owned `Assert.Skip`, environment-conditioned omission, or compatibility expectation.

- [ ] **Step 1: Build one comprehensive real indexed fixture**

Create a temporary multi-project solution whose source includes:

- namespace/type ambiguity (`Namespace1.Namespace2.Class1.Class2` versus `Namespace1.Namespace2.Namespace3.Class2`);
- global and literal `global` namespaces;
- nested/generic types and methods/locals with every omission/arity state;
- every section 10.1 callable and representative section 10.2 compiler-only exclusions;
- mixed lambda/anonymous numbering and nested immediate owners;
- partial definition/implementation plus definition-only partial;
- overload types covering aliases, fully qualified names, reference/value nullability, arrays/ranks, pointers, tuples, function pointers, ref modes, conversion target;
- calls, references, caller/callee, overrides, interface bindings, async path, and caller-tree edges;
- normalized source containing CR, LF, CRLF, NEL, U+2028, U+2029, TAB, and searchable text;
- same-volume linked `../` source and movable standard/custom DB layouts.

The fixture indexes through the real CLI/Core/Storage path. Test-only direct SQL is permitted only for inspection or deterministic corruption/old-schema setup, never to manufacture the behavior being accepted.

- [ ] **Step 2: Add direct parser/formatter/resolver/signature acceptance rows**

Create one named test or theory row for every bullet in design sections 22.1-22.4. Assertions must check ordered logical IDs, concrete displayed paths, declaration roles/locations, or exact error category—not merely exit success. Include all four output path styles and copy/re-query every concrete full path.

- [ ] **Step 3: Add direct typed matcher and command-matrix acceptance rows**

Create one named test or theory row for every bullet in sections 22.5-22.6. Exercise mixed literal/glob/regex modes and each case category in one invocation. Loop all allowed/forbidden options across every command. Use traversal counters or secondary-result assertions to prove root-only placement and pre-cardinality filtering.

- [ ] **Step 4: Add direct portable path/schema/output acceptance rows**

Create one named test or theory row for every bullet in sections 22.7-22.8. Inspect schema values/keys for rooted paths; relocate real temp directories; apply base-dir without mutation; compare every output family under absolute/relative paths; exercise old schema 4 preservation/rebuild guidance; compare canonical order across all style/case/format choices; and test terminal normal/verbose help plus output failure atomicity.

Use synthetic deterministic drive/UNC root comparison seams for cross-volume/share cases so the tests always execute on a single-drive host. Do not replace them with skipped live UNC tests.

- [ ] **Step 5: Add explicit legacy-removal searches and executable tests**

Run executable CLI cases that reject:

```text
Old.Type::Method()::Local()
Old.Type::Method()::<lambda#1>
--regex
--ignore-case
schema version 4
absolute-path fallback persistence
```

Search production code/tests for old parser/matcher types and old `display_name`/`fully_qualified_name` schema dependencies. Historical design/plan documents may retain historical text; active code, active normative docs after Task 14, and current tests may mention old forms only in explicit rejection assertions.

- [ ] **Step 6: Run the new acceptance set and confirm any uncovered RED**

Run: `rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CSharpSymbolPathAcceptanceTests|FullyQualifiedName~TypedSearchAcceptanceTests|FullyQualifiedName~PortableIndexAcceptanceTests"`

Expected before closure: any uncovered requirement fails for its actual missing behavior. If the set is immediately GREEN, perform the mutation checks in Step 7 to prove sensitivity before accepting it.

- [ ] **Step 7: Perform required mutation sensitivity checks**

Temporarily make and fully restore each mutation, recording the targeted failing test:

1. Treat csharp full dotted input as exact instead of suffix.
2. Allow a grandchild without its immediate containment edge.
3. Collapse nullable value/reference identity.
4. Give anonymous methods a separate ordinal counter.
5. Persist partial parts as two logical symbols.
6. Change one condition category from OR to AND or merge partial declaration evidence.
7. Traverse before root filtering/cardinality.
8. Persist/reconstruct one absolute document path.
9. Sort on formatted display/absolute path.
10. Remove the final pre-commit cancellation gate.

Each mutation must make at least one focused test fail for the intended reason. Restore production code and rerun the focused test GREEN before the next mutation.

- [ ] **Step 8: Run all project test suites**

Run sequentially:

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj -c Release --no-restore
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj -c Release --no-restore
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj -c Release --no-restore
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj -c Release --no-restore
```

Expected: every project PASS, zero warnings, zero feature-owned skips. Investigate any failure with systematic debugging; do not update a stale expected value until comparing it to the approved breaking contract.

- [ ] **Step 9: Commit acceptance closure**

```powershell
rtk git add tests/CsIndex.IntegrationTests/CSharpSymbolPathAcceptanceTests.cs tests/CsIndex.IntegrationTests/TypedSearchAcceptanceTests.cs tests/CsIndex.IntegrationTests/PortableIndexAcceptanceTests.cs tests/CsIndex.IntegrationTests/CSharpSymbolPathAcceptanceFixture.cs tests/CsIndex.IntegrationTests/CliCommandTests.cs tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs tests/CsIndex.Core.Tests/ExecutableSymbolExtractionTests.cs tests/CsIndex.Storage.Tests/SqliteIndexTests.cs tests/CsIndex.IntegrationTests/ProjectScopedSourceSymbolPersistenceTests.cs tests/CsIndex.IntegrationTests/FunctionTargetFilterTests.cs tests/CsIndex.IntegrationTests/GraphQueryTests.cs tests/CsIndex.IntegrationTests/SymbolSourceQueryTests.cs tests/CsIndex.IntegrationTests/OutputFormatterTests.cs
rtk git commit -m "test: close symbol path acceptance matrix"
```

**Positive cases:** every design section 22 bullet has a direct observable assertion and a traceable test name.

**Negative cases:** every removed grammar/option/schema fallback, cross-row partial evidence, traversal-before-filtering, absolute persistence, formatting-dependent identity/order, atomic failure leak.

**Out of scope:** editing normative docs and declaring the branch complete.

### Task 14: Normative Documentation, Final Verification, and Review Closure

**Normative sections:** entire approved design, especially 17-19, 22.9, and 24.

**Files:**
- Modify: `docs/SPEC.md`
- Modify: `docs/CLI.md`
- Modify: `docs/DB_SCHEMA.md`
- Modify: `docs/DECISIONS.md`
- Modify: `docs/TEST_PLAN.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`
- Modify: `docs/KNOWN_LIMITATIONS.md`
- Do not modify: `docs/superpowers/specs/2026-08-16-csharp-symbol-path-design.md` unless the primary agent first approves a genuine discovered contradiction
- Do not modify: historical specs/plans merely to erase old text

**Interfaces:**
- Produces: synchronized active documentation and final verification evidence only.
- Consumes: the implemented CLI help, schema, tests, and approved design.
- Requires: independent spec-compliance review and independent code-quality review by the primary agent or designated `gpt-5.6-sol` reviewer before branch integration.

- [ ] **Step 1: Update active normative documentation from implemented behavior**

Document the complete grammar, suffix/exact distinction, every special segment/example, overload/generic omission, typed matcher families/cases/composition, command matrix/root-only phases, logical/declaration roles, schema v5 tables/invariants, relative anchor/storage rules, base/path/symbol styles, verbose help, exit behavior, schema rejection/rebuild instructions, and atomic output guarantees.

`DB_SCHEMA.md` must list the actual v5 columns/indexes/FKs/check constraints and explain preferred declarations. `KNOWN_LIMITATIONS.md` must retain genuine limitations: no old grammar/migration, no constructed generic invocation query, no cross-volume/share multi-root model, no compiler-only callable names, csharp suffix may broaden copied results, anonymous ordinals are snapshot-local.

- [ ] **Step 2: Verify documentation and help synchronization**

Capture global and every command's normal/verbose help. Compare option spellings/scope against `CLI.md` and the matrix. Run searches for all new option families, schema version 5, declaration roles, `--base-dir`, path/symbol styles, and verbose help. Search active code/docs for stale `--regex`, query `--ignore-case`, old child `::`, schema 4 as current, absolute-path persistence, and legacy display columns; only explicit rejection/history statements may remain.

- [ ] **Step 3: Inspect actual schema and persisted path data**

Create one standard and one custom fresh index through the CLI. Query `schema_info`, `pragma_table_info`, FKs/indexes, root anchor, input/project/document/declaration path columns, stable/declaration keys, and partial rows. Assert schema 5, expected constraints, one logical partial/two role rows/preferred implementation, and zero rooted persisted paths. Relocate the fixture and execute one absolute-default, relative, and base-dir override query.

- [ ] **Step 4: Run fresh representative CLI probes**

Execute positive probes for csharp suffix, explicit exact, namespace omission, global namespace, nested local/lambda, a special segment, mixed typed conditions/cases, source substring, root-only callers/graph behavior, partial definition/source show, path styles, and copy/re-query output. Execute negative probes for legacy grammar/options, malformed delimiters, invalid regex/case/style, forbidden command option, ambiguous graph root, require-single, schema 4, cross-volume seam, and output failure sentinel preservation.

- [ ] **Step 5: Run formatting, build, and full solution tests**

Run sequentially from the worktree:

```powershell
rtk dotnet format CsIndex.sln --verify-no-changes --no-restore
rtk dotnet build CsIndex.sln -c Release --no-restore
rtk dotnet test CsIndex.sln -c Release --no-build --no-restore
```

Expected: exit 0, zero build errors/warnings, all tests passed, no feature-owned skips. If the sandbox alone denies Microsoft SDK discovery, rerun the identical build with the approved escalation and record both results; do not change code or bypass verification.

- [ ] **Step 6: Inspect diff, scope, and temporary artifacts**

Run:

```powershell
rtk git diff --check
rtk git status --short
rtk rg -n "SymbolQueryParser|SymbolPatternMatcher|fully_qualified_name|display_name" src tests
rtk rg -n -e "--regex" -e "--ignore-case" src tests docs
```

Classify any search hit. Production references to removed parser/matcher/schema fields are failures. Test/doc hits are allowed only for explicit rejection or immutable historical context. Inspect every changed file and confirm no unrelated refactor, generated artifact, output temp, copied DB, absolute fixture path, or user change is included.

- [ ] **Step 7: Commit active documentation**

```powershell
rtk git add docs/SPEC.md docs/CLI.md docs/DB_SCHEMA.md docs/DECISIONS.md docs/TEST_PLAN.md docs/IMPLEMENTATION_STATUS.md docs/KNOWN_LIMITATIONS.md
rtk git commit -m "docs: specify canonical symbol path implementation"
```

- [ ] **Step 8: Request independent spec-compliance review**

Give the reviewer the approved design, this plan, full branch diff, test evidence, schema inspection, and command/help captures. Require a severity-ranked report with exact file/line references and a row for every bullet in the design section 22 traceability audit. Return every Critical/Important finding to the responsible implementation task; use `superpowers:receiving-code-review`, reproduce the issue, add a focused failing test, implement the minimal fix, rerun the task gates, update its report, and commit the fix.

- [ ] **Step 9: Request independent code-quality review**

After spec compliance is clean, review parser/backtracking bounds, cancellation, SQLite transactions/FKs, path canonicalization/device/UNC handling, partial state invariants, lazy source loading, ordering purity, formatter bypasses, and atomic destination lifecycle. Resolve all valid Critical/Important findings through the same RED-GREEN fix loop. Record any accepted Minor with rationale; do not silently ignore it.

- [ ] **Step 10: Re-run final gates after the last review fix**

Repeat Steps 2-6 from the final commit, not from an earlier SHA. Confirm `rtk git status --short` is empty and record the final commit range, exact pass/fail/warning/skip counts, build/format exits, schema/path inspection, help probes, and any escalation limitation.

- [ ] **Step 11: Finish the development branch**

Use `superpowers:finishing-a-development-branch`. Present the verified integration choices to the user; do not merge, push, delete a worktree/branch, or create a PR without the user's selected action.

**Completion claim requires:** all design section 22 rows mapped and passing; zero Critical/Important review findings; clean worktree; no omitted required check except one explicitly reported with reason/risk.

## Design-to-Task Traceability

| Design verification section | Primary task(s) | Direct test suites |
| --- | --- | --- |
| 22.1 parser and formatter | 5-7, 12, 13 | `SymbolPathParserTests`, `SymbolPathFormatterTests`, `SymbolPathOutputAcceptanceTests`, `CSharpSymbolPathAcceptanceTests` |
| 22.2 resolution and containment | 9, 10, 13 | `SymbolPathResolverTests`, `RootSelectionOrchestrationTests`, `CSharpSymbolPathAcceptanceTests` |
| 22.3 signature identity | 1, 6, 9, 13 | `SymbolSignatureCanonicalizerTests`, `SymbolPathParserTests`, `SymbolPathResolverTests` |
| 22.4 callable extraction and partial identity | 2, 3, 5, 10, 13 | `CallablePathExtractionTests`, `LogicalDeclarationExtractionTests`, `SchemaFiveLogicalSymbolTests`, acceptance fixture tests |
| 22.5 typed matchers | 8, 9, 13 | `TypedConditionCompilerTests`, `StructuralGlobMatcherTests`, `TypedSearchAcceptanceTests` |
| 22.6 command matrix and traversal | 10, 11, 13 | `RootSelectionOrchestrationTests`, `CliSymbolPathOptionMatrixTests`, `CliCommandTests` |
| 22.7 portable paths and schema | 4, 5, 10-13 | `IndexPathResolverTests`, `PortablePathPersistenceTests`, `PortablePathOutputTests`, `PortableIndexAcceptanceTests` |
| 22.8 ordering, help, output safety | 7, 11-13 | `SymbolCanonicalComparerTests`, `VerboseHelpTests`, `OutputFormatterTests`, atomic CLI tests |
| 22.9 final gates | 14 | fresh format/build/full test, CLI probes, schema/path inspection, diff/scope/temp audit |

## Per-Task Agent and Review Contract

For each implementation task, the primary agent creates a fresh sequential task brief containing:

1. the exact task number, owned files, interfaces, normative section references, positive/negative cases, command rows, and out-of-scope list copied from this plan;
2. the current base commit and instruction that other agents' changes exist and must not be reverted;
3. mandatory `superpowers:test-driven-development`, `superpowers:systematic-debugging` for unexpected failures, and `superpowers:verification-before-completion` before a completion claim;
4. a requirement to report the exact RED failure, GREEN counts, warning/skip counts, mutation evidence, changed files, commit SHA, and clean status;
5. a stop condition: any conflict with the design, missing required interface, platform ambiguity that affects behavior, or need to expand owned files must be sent to the primary agent before code changes.

Before Task 1, use `superpowers:using-git-worktrees` to create and verify one isolated feature worktree from the final plan commit, establish a fresh build/test baseline, and record it in the Task 1 report. Reuse that same worktree sequentially; do not create parallel implementation worktrees that can diverge on schema/interfaces.

Dispatch the configured `implementer` first with `gpt-5.6-luna` and maximum reasoning. Only an actual Luna availability/entitlement failure permits one retry with `gpt-5.6-terra` and the identical brief; record the original error and fallback. Do not substitute models for sandbox denial, test failure, implementation difficulty, or ambiguity.

Each completed task receives two fresh reviews before the next implementation begins:

- spec-compliance review against the cited design sections and task cases;
- code-quality review for correctness, maintainability, cancellation, performance, persistence/path safety, and test sensitivity.

The primary agent adjudicates findings with evidence. Valid findings return to the original implementer for a focused RED-GREEN fix when practical. A task is not accepted merely because its implementer reports success.

## Execution Reports

Store ignored execution reports under:

```text
.superpowers/sdd/2026-08-17-csharp-symbol-path-portable-index/task-<n>-report.md
```

Each report records base/final SHA, owned diff, RED/GREEN commands and outputs, mutation checks, reviewer findings/fixes, final focused/full gates, known limitations, and clean status. Reports are evidence, not a substitute for the primary agent's fresh verification.
