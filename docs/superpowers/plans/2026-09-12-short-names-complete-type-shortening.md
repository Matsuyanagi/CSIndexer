# Complete `--short-names` Type Shortening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `--short-names` remove namespaces from every displayed owner and type, including JSON type fields, without changing stored identity, search, ordering, or schema.

**Architecture:** Add an identity-aware C# type display formatter to the existing canonical signature component, then use a dedicated paired display/identity path shortener for callable segments. Route all CLI signatures and JSON type fields through those two shared presentation entry points so table, graph, tree, Mermaid, and JSON output cannot diverge.

**Tech Stack:** C# 14, .NET 10, Roslyn `Microsoft.CodeAnalysis.CSharp`, xUnit v3, SQLite-backed integration fixtures.

**Spec:** `docs/superpowers/specs/2026-09-12-short-names-complete-type-shortening-design.md`

## Global Constraints

- Read the spec completely before modifying production code.
- Prefix every shell command with `rtk`. If invoking PowerShell explicitly, use `powershell.exe -NoProfile` or `pwsh.exe -NoProfile`.
- Follow RED-GREEN-REFACTOR for each behavior task: focused failing test, observed expected failure, minimum implementation, focused passing test, then refactor.
- `--short-names` is presentation-only. Do not mutate query inputs, selected symbols, ordering keys, stored rows, stable keys, or IDs.
- Do not add SQLite columns, bump the schema version, or require reindexing.
- Preserve the complete JSON `namespaceName` and `stableKey` values under `--short-names`.
- Shorten JSON `displayName`, `signature`, `fullyQualifiedName`, `parameters`, and `returnType` under `--short-names`.
- `fullyQualifiedName` remains csharp-style regardless of `--symbol-path-style`; only its namespace qualification responds to `--short-names`.
- Preserve the full outer-to-inner containing-type path. Remove semantic namespace components only.
- Preserve the output byte-for-byte when `--short-names` is absent.
- Do not use regular expressions, capitalization, or last-dot truncation to guess namespace/type boundaries.
- Preserve C# aliases, generic placeholder names, nullable syntax, arrays, pointers, tuples, function pointers, ref kinds, escaped identifiers, and non-ASCII identifiers.
- Preserve unrelated repository changes. Do not weaken or delete tests to obtain green output.

## Execution setup

Use `superpowers:using-git-worktrees` before Task 1. Create an isolated worktree from the commit that contains this plan, using branch `codex/short-names-complete-type-shortening`. Confirm the worktree starts clean and record its absolute path in the execution commentary. Run every command below from that worktree.

## File structure

- Modify `src/CsIndex.Core/Symbols/SymbolSignatureCanonicalizer.cs`: expose identity-aware formatting for one canonical C# type and keep its parser/type-node grammar authoritative.
- Create `src/CsIndex.Core/Symbols/SymbolPathDisplayShortener.cs`: structurally walk paired executable display/identity paths and shorten only their type-bearing positions.
- Modify `src/CsIndex.Core/Symbols/SymbolPathFormatter.cs`: invoke the shared path shortener only when `ShortNames` is true.
- Modify `src/CsIndex.Cli/SymbolSignatureFormatter.cs`: shorten return types using their canonical key/display pair.
- Modify `src/CsIndex.Cli/OutputFormatter.cs`: shorten JSON presentation/type fields while preserving identity and namespace fields.
- Modify `src/CsIndex.Cli/Program.cs`: make concise and verbose help describe complete owner/type shortening.
- Modify `tests/CsIndex.Core.Tests/SymbolSignatureCanonicalizerTests.cs`: cover the complete structural type matrix and malformed pair errors.
- Modify `tests/CsIndex.Core.Tests/SymbolPathFormatterTests.cs`: cover ordinary, nested, special, conversion, explicit-interface, and malformed executable paths.
- Modify `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`: cover Roslyn-style signatures and the exact JSON field contract.
- Modify `tests/CsIndex.IntegrationTests/CSharpSymbolPathAcceptanceTests.cs`: cover indexed canonical data in all four presentation combinations and short-path round trips.
- Modify `tests/CsIndex.IntegrationTests/CliCommandTests.cs` and `tests/CsIndex.IntegrationTests/VerboseHelpTests.cs`: synchronize exact help and verify the public CLI description.
- Modify `README.md`, `README_ja.md`, `EXAMPLE.md`, `docs/CLI.md`, `docs/SPEC.md`, `docs/DECISIONS.md`, and `docs/IMPLEMENTATION_STATUS.md`: replace owner-only wording and document JSON behavior.

---

### Task 1: Identity-aware canonical type display shortening

**Files:**
- Modify: `src/CsIndex.Core/Symbols/SymbolSignatureCanonicalizer.cs:68-191,998-1166`
- Test: `tests/CsIndex.Core.Tests/SymbolSignatureCanonicalizerTests.cs`

**Interfaces:**
- Consumes: existing `CanonicalTypeSignature(string IdentityKey, string DisplayText)` and the private `ParseIdentityNode`/`TypeNode` grammar.
- Produces: `public static string FormatTypeDisplay(CanonicalTypeSignature type, bool shortNames)`.
- Produces internally: structural rewrite helpers that pair one parsed identity node with one Roslyn `TypeSyntax` display tree.

- [ ] **Step 1: Add failing tests for ordinary and nested type shortening**

Add a theory named `FormatTypeDisplay_ShortNamesRemoveOnlySemanticNamespaces` with exact identity/display/expected triples. Include these minimum rows:

```csharp
[Theory]
[InlineData("System::Guid", "System.Guid", "Guid")]
[InlineData(
    "System.Collections.Generic::Dictionary<System::String,System.Collections.Generic::List<Game.Models::Widget>>",
    "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Game.Models.Widget>>",
    "Dictionary<string, List<Widget>>")]
[InlineData(
    "Game.Models::Outer<System::Int32>.Inner<System::String>",
    "Game.Models.Outer<int>.Inner<string>",
    "Outer<int>.Inner<string>")]
[InlineData("Game::@class", "Game.@class", "@class")]
[InlineData("会社.モデル::入力", "会社.モデル.入力", "入力")]
public void FormatTypeDisplay_ShortNamesRemoveOnlySemanticNamespaces(
    string identity,
    string display,
    string expected)
{
    var canonical = new CanonicalTypeSignature(identity, display);

    Assert.Equal(expected, SymbolSignatureCanonicalizer.FormatTypeDisplay(canonical, shortNames: true));
    Assert.Equal(display, SymbolSignatureCanonicalizer.FormatTypeDisplay(canonical, shortNames: false));
}
```

- [ ] **Step 2: Add failing tests for recursive C# type shapes and mismatch diagnostics**

Use the existing Roslyn compilation helpers to canonicalize source types rather than hand-writing fragile function-pointer identities. Assert exact short displays for:

```csharp
"(System.DateTime, Game.Models.Widget?[])"     // => "(DateTime, Widget? [])"
"delegate* unmanaged[Cdecl]<System.Int32, Game.Models.Widget, System.Void>"
"System.Collections.Generic.List<Game.Models.Outer<int>.Inner<string?>[]>*"
"dynamic"
"T"
```

Include both nullable cases that the canonical pair represents differently:

- a nullable value type whose identity is `System::Nullable<T>` but whose display is `T?`;
- a nullable reference type whose identity intentionally omits the annotation but whose display retains `?`.

Also include every C# predefined alias (`bool` through `void`, plus `nint` and `nuint`) so alias preservation is verified rather than inferred.

Add `FormatTypeDisplay_RejectsStructurallyMismatchedIdentityAndDisplay`:

```csharp
var exception = Assert.Throws<InvalidOperationException>(() =>
    SymbolSignatureCanonicalizer.FormatTypeDisplay(
        new CanonicalTypeSignature("Game::Outer.Inner", "Game.Outer"),
        shortNames: true));
Assert.Contains("does not match display", exception.Message, StringComparison.Ordinal);
```

- [ ] **Step 3: Run the focused type tests and verify RED**

Run:

```text
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FormatTypeDisplay_"
```

Expected: compilation or assertion failure because `FormatTypeDisplay(CanonicalTypeSignature, bool)` does not exist or still returns qualified type text. Record the failing test count and first relevant failure.

- [ ] **Step 4: Implement the canonical type display API**

Add the public entry point next to `CanonicalizeType`:

```csharp
public static string FormatTypeDisplay(CanonicalTypeSignature type, bool shortNames)
{
    ArgumentNullException.ThrowIfNull(type);
    if (!shortNames)
    {
        return type.DisplayText;
    }

    var syntax = SyntaxFactory.ParseTypeName(type.DisplayText);
    if (syntax.ContainsDiagnostics)
    {
        throw CreateDisplayMismatch(type);
    }

    var identity = ParseIdentityNode(type.IdentityKey);
    return RewriteShortDisplay(identity, syntax, type).NormalizeWhitespace().ToFullString();
}
```

Implement these private helpers in the same file so they reuse the existing private `TypeNode` hierarchy:

```csharp
private static TypeSyntax RewriteShortDisplay(
    TypeNode identity,
    TypeSyntax display,
    CanonicalTypeSignature pair);

private static NameSyntax RewriteNamedTypeDisplay(
    NamedTypeNode identity,
    NameSyntax display,
    CanonicalTypeSignature pair);

private static InvalidOperationException CreateDisplayMismatch(
    CanonicalTypeSignature pair);
```

`RewriteShortDisplay` must use an exhaustive type-pattern switch:

- `NamedTypeNode` with `NameSyntax`: flatten the qualified display name into ordered `SimpleNameSyntax` components, validate component and generic-argument counts against `NamedTypeNode.Segments`, drop exactly `NamespaceSegmentCount` components, recursively rewrite every generic argument, then rebuild the remaining outer-to-inner name.
- `NamedTypeNode` with `PredefinedTypeSyntax`: validate the keyword against `PredefinedTypeNames` and the identity's qualified metadata name, then preserve the C# keyword unchanged.
- the `System.Object` identity paired with the `dynamic` display identifier: preserve `dynamic`; do not turn it into `object`.
- a `NullableTypeSyntax` display: preserve `?` while recursively rewriting its element. Accept the two canonical identity shapes deliberately used by this repository: `System.Nullable<T>` for nullable value types, and the same reference/placeholder identity as the unannotated form for nullable reference types. Reject all other identity/display combinations.
- `PlaceholderTypeNode`: accept an identifier/type-parameter display unchanged.
- `ArrayTypeNode` + `ArrayTypeSyntax`: recursively rewrite `ElementType` and preserve rank specifiers.
- `PointerTypeNode` + `PointerTypeSyntax`: recursively rewrite `ElementType`.
- `NullableTypeNode` + `NullableTypeSyntax`: recursively rewrite `ElementType`; retain this case for valid parsed canonical pairs even though Roslyn-persisted nullable value types normally use the `System.Nullable<T>` identity shape above.
- `TupleTypeNode` + `TupleTypeSyntax`: validate element count, recursively rewrite every element type, and preserve the already-canonical omission of tuple element names.
- `FunctionPointerTypeNode` + `FunctionPointerTypeSyntax`: validate parameter/return counts, recursively rewrite each type, and preserve calling convention and ref-kind tokens.
- the canonical dynamic pair: preserve the `dynamic` display token.
- every incompatible pair: throw `CreateDisplayMismatch`.

Do not render from identity alone: placeholder names and C# aliases come from `DisplayText`. Do not modify `IdentityKey` or the existing canonicalization/matching methods.

- [ ] **Step 5: Run focused and surrounding canonicalizer tests and verify GREEN**

Run:

```text
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolSignatureCanonicalizerTests"
```

Expected: all `SymbolSignatureCanonicalizerTests` pass with zero warnings.

- [ ] **Step 6: Refactor only duplicated syntax helpers and rerun the same test class**

Keep flatten/rebuild logic private to `SymbolSignatureCanonicalizer`. Reuse its existing top-level splitter and predefined-type knowledge. Do not expose `TypeNode` or add a second identity parser. Rerun the Step 5 command after refactoring.

- [ ] **Step 7: Commit Task 1**

```text
rtk git add src\CsIndex.Core\Symbols\SymbolSignatureCanonicalizer.cs tests\CsIndex.Core.Tests\SymbolSignatureCanonicalizerTests.cs
rtk git commit -m "feat: shorten canonical type displays"
```

---

### Task 2: Structural shortening of every callable path segment

**Files:**
- Create: `src/CsIndex.Core/Symbols/SymbolPathDisplayShortener.cs`
- Modify: `src/CsIndex.Core/Symbols/SymbolPathFormatter.cs:21-52`
- Test: `tests/CsIndex.Core.Tests/SymbolPathFormatterTests.cs`

**Interfaces:**
- Consumes: Task 1 `SymbolSignatureCanonicalizer.FormatTypeDisplay(CanonicalTypeSignature, bool)`.
- Consumes: paired `SymbolPathData.ExecutableIdentityPath` and `ExecutableDisplayPath`.
- Produces: `internal static string SymbolPathDisplayShortener.ShortenExecutablePath(string identityPath, string displayPath)`.
- Produces: `SymbolPathFormatter.Format` that applies the new helper only for `options.ShortNames`.

- [ ] **Step 1: Convert owner-only path expectations into failing complete-shortening expectations**

Update `NestedMethod.ExecutableIdentityPath` to a real canonical identity such as `Run(System::Guid).<lambda#1>`. Rename `Format_UsesStyleAndOmitsOnlyOwnerNamespace` to `Format_UsesStyleAndShortensEveryTypeNamespace`, and change the short expectations to:

```text
Outer<T>.Inner<U>::Run(Guid).<lambda#1>
**::Outer<T>.Inner<U>::Run(Guid).<lambda#1>
```

Replace `Format_ShortNamesPreserveConversionAndExplicitInterfacePayloads` with a test whose paired paths are:

```csharp
ExecutableDisplayPath =
    "[conversion:implicit:System.Guid](Game.Models.Number)." +
    "[explicit:System.IDisposable.Dispose]()." +
    "[get:Game.Contracts.IPlayer.Name]()." +
    "Local(System.Collections.Generic.List<Game.Models.Number>)";
ExecutableIdentityPath =
    "[conversion:implicit:System::Guid](Game.Models::Number)." +
    "[explicit:System::IDisposable.Dispose]()." +
    "[get:Game.Contracts::IPlayer.Name]()." +
    "Local(System.Collections.Generic::List<Game.Models::Number>)";
```

Assert the short executable text is exactly:

```text
[conversion:implicit:Guid](Number).[explicit:IDisposable.Dispose]().[get:IPlayer.Name]().Local(List<Number>)
```

- [ ] **Step 2: Add failing coverage for every concrete callable category and malformed pairs**

Change the existing concrete-category dictionary to carry `(Display, Identity, ExpectedShort)` values. Keep constructors, static constructors, destructors, all operator spellings including `<`, `>`, `<<`, and `>>`, conversions, property/event accessors, explicit interface methods, local functions, lambdas, anonymous methods, initializers, and top-level statements.

Add one nested path containing qualified types in both the root method and two local functions. Add tests that require deterministic `InvalidOperationException` messages for:

- display/identity executable segment count mismatch;
- parameter count mismatch;
- malformed delimiter nesting;
- a type-bearing special payload whose display and identity disagree.

- [ ] **Step 3: Run the focused path tests and verify RED**

Run:

```text
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolPathFormatterTests"
```

Expected: short-name assertions fail because the formatter still passes through executable type namespaces.

- [ ] **Step 4: Implement `SymbolPathDisplayShortener`**

Create an internal static class with this entry point:

```csharp
internal static class SymbolPathDisplayShortener
{
    public static string ShortenExecutablePath(string identityPath, string displayPath)
    {
        if (string.IsNullOrEmpty(identityPath) && string.IsNullOrEmpty(displayPath))
        {
            return displayPath;
        }

        if (string.IsNullOrEmpty(identityPath) || string.IsNullOrEmpty(displayPath))
        {
            throw CreateMismatch("empty executable path", identityPath, displayPath);
        }

        var identities = SplitSegments(identityPath);
        var displays = SplitSegments(displayPath);
        RequireSameCount(identities, displays, "executable segment");
        return string.Join('.', identities.Zip(displays, ShortenSegment));
    }
}
```

Implement the following private operations with balanced delimiter tracking:

```csharp
private static IReadOnlyList<string> SplitSegments(string value);
private static string ShortenSegment(string identity, string display);
private static string ShortenParameterList(string identity, string display);
private static string ShortenSpecialPayload(string identity, string display);
private static string ShortenType(string identity, string display) =>
    SymbolSignatureCanonicalizer.FormatTypeDisplay(
        new CanonicalTypeSignature(identity, display),
        shortNames: true);
```

The scanner must split `.` or `,` only at zero parenthesis, bracket, and generic-angle depth. Angle characters inside bracketed operator tags are literal operator tokens and must not alter angle depth. Preserve known parameter prefixes `ref readonly `, `ref `, `out `, and `in ` while shortening the following paired type.

For bracketed special segments:

- shorten the target after `[conversion:implicit:` / `[conversion:explicit:` / checked equivalents;
- for `[explicit:TYPE.Member]`, use the identity `::` boundary and the last top-level member dot to shorten `TYPE` while preserving `Member`;
- apply the same explicit-interface rule to `[get:]`, `[set:]`, `[init:]`, `[add:]`, and `[remove:]` payloads only when the identity payload contains `::`;
- leave constructor/destructor/operator/initializer/top-level/anonymous markers and ordinary member names unchanged;
- shorten the terminal parameter list for every callable category.

Every structural mismatch must throw `InvalidOperationException` containing both paired path fragments and the mismatch category.

- [ ] **Step 5: Wire `SymbolPathFormatter` to the shortener**

Before composing the final owner and executable text, choose the executable path once:

```csharp
var executable = options.ShortNames
    ? SymbolPathDisplayShortener.ShortenExecutablePath(
        path.ExecutableIdentityPath,
        path.ExecutableDisplayPath)
    : path.ExecutableDisplayPath;

return string.IsNullOrEmpty(executable)
    ? owner
    : $"{owner}::{executable}";
```

Do not change owner style handling or the explicit-style `**::` convention.

- [ ] **Step 6: Run focused and complete Core tests**

Run:

```text
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolPathFormatterTests|FullyQualifiedName~SymbolSignatureCanonicalizerTests"
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore
```

Expected: both commands pass with zero failures and zero warnings.

- [ ] **Step 7: Commit Task 2**

```text
rtk git add src\CsIndex.Core\Symbols\SymbolPathDisplayShortener.cs src\CsIndex.Core\Symbols\SymbolPathFormatter.cs tests\CsIndex.Core.Tests\SymbolPathFormatterTests.cs
rtk git commit -m "feat: shorten types in symbol paths"
```

---

### Task 3: CLI signatures and complete JSON short-name contract

**Files:**
- Modify: `src/CsIndex.Cli/SymbolSignatureFormatter.cs:7-56`
- Modify: `src/CsIndex.Cli/OutputFormatter.cs:12-13,437-480`
- Test: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`

**Interfaces:**
- Consumes: Tasks 1-2 shared Core formatters.
- Produces: `internal static string SymbolSignatureFormatter.FormatType(string typeKey, string typeDisplay, SymbolPathFormatOptions options)`.
- Produces: JSON field values that consistently respond to `ShortNames` without mutating the source `StoredSymbol`.

- [ ] **Step 1: Add a failing Roslyn-style table signature test**

Construct one `StoredSymbol` with valid paired data for:

```text
public static Microsoft.CodeAnalysis.CSharp.Syntax.ForStatementSyntax
Roslynator.CSharp.SyntaxRefactorings::ConvertWhileStatementToForStatement(
    Microsoft.CodeAnalysis.CSharp.Syntax.WhileStatementSyntax,
    Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax?,
    Microsoft.CodeAnalysis.SeparatedSyntaxList<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax>)
```

Use canonical identities with explicit namespace/type boundaries, for example `Microsoft.CodeAnalysis.CSharp.Syntax::WhileStatementSyntax`. Extend the local `CreateSymbol` helper to accept `returnTypeDisplay` and an optional complete `SymbolPathData`; do not fabricate identity paths by copying display paths in a short-name test.

Assert short table output equals one line containing:

```text
public static ForStatementSyntax SyntaxRefactorings::ConvertWhileStatementToForStatement(WhileStatementSyntax,VariableDeclarationSyntax?,SeparatedSyntaxList<ExpressionSyntax>)
```

Also assert the full formatter still emits the original qualified line.

- [ ] **Step 2: Add failing JSON assertions for every changed and preserved field**

Render the same symbol as JSON with `ShortNames: true` and assert:

```csharp
Assert.Equal("SyntaxRefactorings::ConvertWhileStatementToForStatement(WhileStatementSyntax,VariableDeclarationSyntax?,SeparatedSyntaxList<ExpressionSyntax>)",
    value.GetProperty("displayName").GetString());
Assert.Equal("public static ForStatementSyntax SyntaxRefactorings::ConvertWhileStatementToForStatement(WhileStatementSyntax,VariableDeclarationSyntax?,SeparatedSyntaxList<ExpressionSyntax>)",
    value.GetProperty("signature").GetString());
Assert.Equal(value.GetProperty("displayName").GetString(),
    value.GetProperty("fullyQualifiedName").GetString());
Assert.Equal(new[] { "WhileStatementSyntax", "VariableDeclarationSyntax?", "SeparatedSyntaxList<ExpressionSyntax>" },
    value.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
Assert.Equal("ForStatementSyntax", value.GetProperty("returnType").GetString());
Assert.Equal("Roslynator.CSharp", value.GetProperty("namespaceName").GetString());
Assert.Equal("symbol-1", value.GetProperty("stableKey").GetString());
```

Add a second assertion using `SymbolPathStyle.Explicit` to prove `displayName` follows explicit style while `fullyQualifiedName` remains csharp style. Add a no-option baseline asserting every qualified field remains unchanged.

- [ ] **Step 3: Run the focused formatter tests and verify RED**

Run:

```text
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~OutputFormatterTests"
```

Expected: the new table return-type and JSON type-field assertions fail while existing owner shortening still passes.

- [ ] **Step 4: Implement CLI type formatting**

Replace the unused no-op `FormatType(string, SymbolPathFormatOptions)` with:

```csharp
public static string FormatType(
    string typeKey,
    string typeDisplay,
    SymbolPathFormatOptions symbolPathOptions)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
    ArgumentException.ThrowIfNullOrWhiteSpace(typeDisplay);
    return SymbolSignatureCanonicalizer.FormatTypeDisplay(
        new CanonicalTypeSignature(typeKey, typeDisplay),
        symbolPathOptions.ShortNames);
}
```

In `Format`, choose the existing return display when no return type exists, but when a return type exists require the canonical pair and call `FormatType`. Produce a deterministic formatting error if one half of a persisted pair is missing rather than guessing a namespace/type boundary.

- [ ] **Step 5: Route JSON fields through the shared formatter**

In `OutputFormatter.ToSymbolObject`:

```csharp
var fullyQualifiedOptions = FullyQualifiedNameOptions with
{
    ShortNames = symbolPathOptions.ShortNames,
};
```

Use `fullyQualifiedOptions` for `fullyQualifiedName`. Map every parameter through `SymbolSignatureFormatter.FormatType(parameter.TypeKey, parameter.TypeDisplay, symbolPathOptions)`. Format `returnType` through the same method when both stored return values exist. Keep `stableKey`, `namespaceName`, and all non-presentation fields untouched.

Do not modify `StoredSymbol`, `StoredParameter`, or repository hydration.

- [ ] **Step 6: Run formatter, CLI build, and unchanged-default checks**

Run:

```text
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~OutputFormatterTests"
rtk dotnet build src\CsIndex.Cli\CsIndex.Cli.csproj -c Release --no-restore
```

Expected: formatter tests pass; four production projects build with zero errors and warnings.

- [ ] **Step 7: Commit Task 3**

```text
rtk git add src\CsIndex.Cli\SymbolSignatureFormatter.cs src\CsIndex.Cli\OutputFormatter.cs tests\CsIndex.IntegrationTests\OutputFormatterTests.cs
rtk git commit -m "feat: shorten CLI signature type fields"
```

---

### Task 4: Indexed path, graph, round-trip, and help acceptance

**Files:**
- Modify: `src/CsIndex.Cli/Program.cs:115-125,1780-1860,1890-1920`
- Modify: `tests/CsIndex.IntegrationTests/CSharpSymbolPathAcceptanceTests.cs:227-277,687-723`
- Modify: `tests/CsIndex.IntegrationTests/CliCommandTests.cs:1820-1875,2040-2150,2280-2330`
- Modify: `tests/CsIndex.IntegrationTests/OutputFormatterTests.cs`
- Modify: `tests/CsIndex.IntegrationTests/VerboseHelpTests.cs:180-205`

**Interfaces:**
- Consumes: shared formatting behavior from Tasks 1-3 and the existing indexed acceptance fixture.
- Produces: public CLI behavior consistent across csharp/explicit, table/JSON/tree/Mermaid, and copied short selectors.
- Produces: concise help text `Omit namespaces from displayed owners and types` and matching verbose explanation.

- [ ] **Step 1: Change indexed acceptance expectations and verify RED**

Extend `PF07_IndexedPathFormattingIsExactInAllFourStyles` with `NestedGeneric` and explicit-interface/conversion cases. Exact short csharp expectations must include:

```text
Outer<T>.Inner<U>::NestedGeneric(Dictionary<string, List<int? []>>)
SpecialHost::[conversion:explicit:Guid](SpecialHost)
SpecialHost::[explicit:ISpecial.Map]<T>(T)
```

The corresponding short explicit values must prefix `**::` and otherwise match.

Rename `SI04_NonAliasTypesAreFullyQualifiedInCanonicalOutput` to describe both full and short presentation. For full options, retain every existing namespace assertion. For short options, assert the exact short string for each case and assert the removed prefixes are absent.

Add at least one round-trip assertion that copies a short path containing `Dictionary<string,List<int?[]>>` back into the existing resolver and finds the original symbol ID.

Run:

```text
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~PF07_|FullyQualifiedName~SI04_"
```

Expected: failures show the existing owner-only behavior retains parameter/payload namespaces.

- [ ] **Step 2: Add graph/tree consistency tests before production help edits**

In `OutputFormatterTests`, create endpoints with canonical qualified return/parameter/path pairs and render caller tree as `tree`, `json`, and `mermaid` using short options. Assert each label contains `List<Widget>` and contains neither `System.Collections.Generic.` nor `Game.Models.`. Assert the equivalent full-options labels remain qualified.

In `CliCommandTests`, strengthen the existing caller-tree and query-command short-name tests with a fixture symbol that has a qualified type if the standard fixture already exposes one; otherwise rely on the indexed `CSharpSymbolPathAcceptanceTests` for end-to-end CLI type coverage and keep `CliCommandTests` focused on routing.

- [ ] **Step 3: Update help expectations first and verify RED**

Replace exact concise-help lines with:

```text
--short-names  Omit namespaces from displayed owners and types
```

Replace the verbose assertion `short names remove owner namespace only` with assertions containing:

```text
short names omit namespaces from displayed owners, return types, parameters, and nested type arguments
JSON short names preserve stableKey and namespaceName
```

Run:

```text
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~VerboseHelpTests|FullyQualifiedName~NewCommandHelpMatchesAcceptedGrammar"
```

Expected: exact help and verbose reference assertions fail against the old wording.

- [ ] **Step 4: Update the single shared help definitions**

Change `ShortNamesHelpOption` and the global concise help line to `Omit namespaces from displayed owners and types`. Replace the verbose owner-only sentence with the two precise lines from Step 3. Keep option scopes and accepted grammar unchanged.

- [ ] **Step 5: Run all focused presentation tests and verify GREEN**

Run:

```text
rtk dotnet test tests\CsIndex.Core.Tests\CsIndex.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SymbolPathFormatterTests|FullyQualifiedName~SymbolSignatureCanonicalizerTests"
rtk dotnet test tests\CsIndex.IntegrationTests\CsIndex.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~OutputFormatterTests|FullyQualifiedName~CSharpSymbolPathAcceptanceTests|FullyQualifiedName~VerboseHelpTests|FullyQualifiedName~CliCommandTests"
```

Expected: all focused tests pass with zero warnings. Confirm the no-option expected strings remain unchanged rather than updating them to short values.

- [ ] **Step 6: Commit Task 4**

```text
rtk git add src\CsIndex.Cli\Program.cs tests\CsIndex.IntegrationTests\CSharpSymbolPathAcceptanceTests.cs tests\CsIndex.IntegrationTests\CliCommandTests.cs tests\CsIndex.IntegrationTests\OutputFormatterTests.cs tests\CsIndex.IntegrationTests\VerboseHelpTests.cs
rtk git commit -m "test: cover complete short-name presentation"
```

---

### Task 5: Canonical documentation and decision records

**Files:**
- Modify: `docs/superpowers/specs/2026-09-12-short-names-complete-type-shortening-design.md`
- Modify: `README.md`
- Modify: `README_ja.md`
- Modify: `EXAMPLE.md`
- Modify: `docs/CLI.md:159-172,502-522`
- Modify: `docs/SPEC.md:2533-2540,3035-3045`
- Modify: `docs/DECISIONS.md`
- Modify: `docs/IMPLEMENTATION_STATUS.md`

**Interfaces:**
- Consumes: verified public behavior from Tasks 1-4.
- Produces: one non-contradictory public contract and an implementation status/decision trail.

- [ ] **Step 1: Update English and Japanese user documentation**

Use this exact semantic statement in English documentation:

```text
With --short-names, namespaces are omitted from displayed owners and every displayed type, including return types, parameters, generic arguments, conversion targets, and explicit-interface payloads. Nested containing types remain visible. In JSON, displayName, signature, fullyQualifiedName, parameters, and returnType shorten; stableKey and the complete namespaceName do not change.
```

Translate the same contract naturally in README_ja.md and the Japanese sections of docs/CLI.md/docs/SPEC.md. Add the Roslyn-style before/after example to EXAMPLE.md. State that omitting the option yields canonical machine-oriented JSON.

- [ ] **Step 2: Record the replacement decision and implementation status**

Append a dated decision to `docs/DECISIONS.md` that explicitly supersedes owner-only shortening and records:

- presentation-time identity-aware shortening;
- no schema/reindex;
- JSON shortened/preserved field lists;
- containing-type preservation;
- default output and ordering unchanged.

Update `docs/IMPLEMENTATION_STATUS.md` to remove its owner-only claim and describe the implemented complete behavior. Do not rewrite historical design files; the 2026-09-12 spec is the explicit superseding document.

- [ ] **Step 3: Scan for contradictory owner-only wording**

Run:

```text
rtk rg -n "owner namespace only|owner-only|only the owner namespace|戻り値型、引数型.*短縮しません|所有者namespaceだけ|short names remove owner namespace only" README.md README_ja.md EXAMPLE.md docs src tests
```

Expected: matches remain only where the new design describes the superseded historical behavior or a historical plan is intentionally immutable. Active README, CLI, SPEC, help, status, and decision text must have no owner-only claim.

- [ ] **Step 4: Validate documentation diff and commit**

Run:

```text
rtk git diff --check
```

Expected: exit code 0 and no output.

Commit:

```text
rtk git add README.md README_ja.md EXAMPLE.md docs\CLI.md docs\SPEC.md docs\DECISIONS.md docs\IMPLEMENTATION_STATUS.md docs\superpowers\specs\2026-09-12-short-names-complete-type-shortening-design.md
rtk git commit -m "docs: document complete short-name output"
```

---

### Task 6: Final regression and Roslynator acceptance verification

**Files:**
- Inspect only: all changed files and commits from Tasks 1-5.
- Temporary output only: use a disposable output path outside the repository if a captured CLI payload is needed; remove it after inspection.

**Interfaces:**
- Consumes: complete implementation and documentation.
- Produces: fresh evidence for every acceptance criterion; no additional feature behavior.

- [ ] **Step 1: Inspect the complete branch diff**

Run:

```text
rtk git status --short
rtk git diff --check
rtk git diff main...HEAD --stat
rtk git diff main...HEAD -- src tests README.md README_ja.md EXAMPLE.md docs
```

Confirm only planned files changed, no stored/canonical value is mutated for presentation, and no schema/version change exists.

- [ ] **Step 2: Run the complete test suite**

Run:

```text
rtk dotnet test CsIndex.sln -c Release --no-restore
```

Expected: all projects pass with zero failed tests and zero warnings. Record the exact test count.

- [ ] **Step 3: Run Release build and formatting gates**

Run:

```text
rtk dotnet build CsIndex.sln -c Release --no-restore
rtk dotnet format CsIndex.sln --verify-no-changes --no-restore
rtk git diff --check
```

Expected: build exit 0 with zero errors/warnings; format exit 0; diff check has no output. If sandboxed Windows SDK discovery is denied, rerun the same command with the required approval rather than changing build settings.

- [ ] **Step 4: Query the existing Roslynator database without rebuilding**

Locate the database path reported by the user's command without modifying it, then run the newly built CLI against that compatible database:

```text
rtk powershell.exe -NoProfile -Command "Test-Path -LiteralPath 'F:\WorksLab\roslynator\.csindex\roslynator.sqlite'"
rtk dotnet src\CsIndex.Cli\bin\Release\net10.0-windows\win-x64\csindex.dll symbol find --db F:\WorksLab\roslynator\.csindex\roslynator.sqlite "*::ConvertWhileStatementToForStatement" --short-names
```

The first command must print `True`. If it does not, stop this acceptance check and report that the external fixture moved; do not search broadly, guess another path, or rebuild the database. Expected signature:

```text
public static ForStatementSyntax SyntaxRefactorings::ConvertWhileStatementToForStatement(WhileStatementSyntax,VariableDeclarationSyntax?,SeparatedSyntaxList<ExpressionSyntax>)
```

Assert the payload contains none of these prefixes:

```text
Microsoft.CodeAnalysis.
Roslynator.CSharp.SyntaxRefactorings
```

Do not overwrite or rebuild the user's Roslynator database.

- [ ] **Step 5: Verify JSON against the same database**

Repeat Step 4 with `--output-format json`. Confirm:

- `displayName`, `signature`, `fullyQualifiedName`, `parameters`, and `returnType` are short;
- `namespaceName` remains `Roslynator.CSharp`;
- `stableKey` still contains the original stable identity;
- omitting `--short-names` returns the original qualified JSON strings.

- [ ] **Step 6: Final review checkpoint**

Use `superpowers:requesting-code-review` for an independent review if execution policy permits a reviewer. Otherwise perform the primary-agent diff review required by repository instructions and explicitly report that independent delegation was unavailable. Address every technically valid finding with a focused RED-GREEN fix and rerun affected gates.

- [ ] **Step 7: Record final verification without an empty commit**

If verification required no code/doc correction, do not create an empty commit. Report exact commands, exit codes, test counts, Roslynator output evidence, changed files, and commit list. Then use `superpowers:finishing-a-development-branch` to present integration options.
