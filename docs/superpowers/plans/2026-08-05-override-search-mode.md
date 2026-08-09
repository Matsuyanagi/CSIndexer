# Override-Aware Method Search Implementation Plan

> Historical implementation plan. Its schema-version-3 steps describe the
> intermediate implementation at that date; the current formal schema is
> version 4 in `docs/DB_SCHEMA.md` and DEC-0022.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Add an opt-in descendant override/interface-implementation search mode, including real inherited-method alias resolution, across all five method-query commands.

**Architecture:** Persist Roslyn type kind and per-type interface implementation bindings in schema version 3. Resolve exact and inherited method roots in a shared Query-layer resolver, then use profile-scoped, cycle-safe SQLite graph queries to expand only the receiver type's descendant branch. Keep all output tied to real method symbols and leave exact lookup as the default.

**Tech Stack:** C#/.NET 10 (net10.0-windows), Roslyn, SQLite through Microsoft.Data.Sqlite, xUnit v3, existing CSIndexer CLI/query/storage layers.

## Global Constraints

- The option name is --include-overrides and its default is off.
- Accept the option only for symbol find, definition with a method query, references, callers, and callees.
- A type-only query or definition --at combined with the option returns the invalid-arguments exit code.
- Expansion is descendant-only. Never expand a concrete method toward a base class, interface contract, or sibling implementation.
- A concrete-method search does not include a call whose static target is only an interface method.
- Inherited aliases resolve to real declarations; never persist or output a synthetic D1::Play() symbol.
- A new method hides rather than overrides its base method.
- Preserve profile isolation, GeneratedFilter behavior, call reference-kind filters, caller dispatch presentation, short-name presentation, and lambda-callee options.
- ID-only closures use UNION; distance-bearing ancestor traversal uses an explicit visited-path guard. Every recursive query must terminate on malformed self- and multi-node cycles.
- Increase both database and request-hash schema versions from 2 to 3. Reject version 2 databases without modifying them.
- Do not add external dependencies.
- Follow RED-GREEN-REFACTOR for every testable task and use RTK-prefixed shell commands.

---

### Task 1: Extract type kinds and contextual interface method bindings

**Files:**
- Modify: src/CsIndex.Core/Model/IndexEnums.cs
- Modify: src/CsIndex.Core/Model/IndexData.cs
- Modify: src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs
- Modify: src/CsIndex.Core/Analysis/SemanticExtractor.cs
- Create: tests/CsIndex.Core.Tests/InterfaceMethodBindingExtractorTests.cs

**Interfaces:**
- Consumes: Roslyn INamedTypeSymbol.AllInterfaces and FindImplementationForInterfaceMember.
- Produces: SymbolData.TypeKind, InterfaceMethodBindingData, and IndexSnapshot.InterfaceMethodBindings.
- Stable-key fields are named ImplementingTypeKey, InterfaceMethodKey, and ImplementationMethodKey.

- [ ] **Step 1: Write the failing extraction tests.**

Create InterfaceMethodBindingExtractorTests with a compiling source that covers implicit, explicit, inherited, abstract, derived-override, default-interface, and partial-type cases:

~~~csharp
private const string Source = """
    public interface IPlayable { void Play(); }
    public interface IDefaultPlayable { void Play() { } }

    public abstract class AbstractPlayer : IPlayable
    {
        public abstract void Play();
    }

    public class BasePlayer
    {
        public virtual void Play() { }
    }

    public class InheritedPlayer : BasePlayer, IPlayable { }

    public class ProInheritedPlayer : InheritedPlayer
    {
        public override void Play() { }
    }

    public class ExplicitPlayer : IPlayable
    {
        void IPlayable.Play() { }
    }

    public class DefaultPlayer : IDefaultPlayable { }

    public partial class PartialPlayer : IPlayable
    {
        public void Play() { }
    }
    public partial class PartialPlayer { }
    """;

[Fact]
public async Task AnalyzeAsync_RecordsContextualInterfaceMethodBindings()
{
    var snapshot = await AnalyzeAsync(Source);
    var bindings = snapshot.InterfaceMethodBindings;

    AssertBinding(snapshot, bindings, "AbstractPlayer", "IPlayable", "AbstractPlayer");
    AssertBinding(snapshot, bindings, "InheritedPlayer", "IPlayable", "BasePlayer");
    AssertBinding(snapshot, bindings, "ProInheritedPlayer", "IPlayable", "ProInheritedPlayer");
    AssertBinding(snapshot, bindings, "ExplicitPlayer", "IPlayable", "ExplicitPlayer");
    AssertBinding(snapshot, bindings, "DefaultPlayer", "IDefaultPlayable", "IDefaultPlayable");
    Assert.Single(bindings.Where(binding =>
        snapshot.Symbols[binding.ImplementingTypeKey].TypeSimpleName == "PartialPlayer" &&
        snapshot.Symbols[binding.InterfaceMethodKey].TypeSimpleName == "IPlayable"));
}

[Fact]
public async Task AnalyzeAsync_PersistsTypeKindForClassesAndInterfaces()
{
    var snapshot = await AnalyzeAsync(Source);
    Assert.Equal(
        (int)Microsoft.CodeAnalysis.TypeKind.Interface,
        snapshot.Symbols.Values.Single(symbol => symbol.TypeSimpleName == "IPlayable" &&
                                                 symbol.Kind == IndexedSymbolKind.Type).TypeKind);
    Assert.Equal(
        (int)Microsoft.CodeAnalysis.TypeKind.Class,
        snapshot.Symbols.Values.Single(symbol => symbol.TypeSimpleName == "InheritedPlayer" &&
                                                 symbol.Kind == IndexedSymbolKind.Type).TypeKind);
}
~~~

Use the same temporary-directory AnalysisCoordinator pattern as AsyncSemanticExtractorTests.AnalyzeAsync. AssertBinding must compare the three referenced symbols' TypeSimpleName values so the test does not depend on unstable numeric IDs.

- [ ] **Step 2: Run the focused tests and confirm the RED state.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~InterfaceMethodBindingExtractorTests
~~~

Expected: compilation fails because InterfaceMethodBindings, InterfaceMethodBindingData, and SymbolData.TypeKind do not exist.

- [ ] **Step 3: Add the snapshot model and type-kind extraction.**

Add these model members:

~~~csharp
public sealed record InterfaceMethodBindingData
{
    public required string ImplementingTypeKey { get; init; }
    public required string InterfaceMethodKey { get; init; }
    public required string ImplementationMethodKey { get; init; }
}
~~~

Add int? TypeKind to SymbolData and this collection to IndexSnapshot:

~~~csharp
public List<InterfaceMethodBindingData> InterfaceMethodBindings { get; } = [];
~~~

In SymbolCanonicalizer.CreateType set:

~~~csharp
TypeKind = (int)type.TypeKind,
~~~

Add IndexedTypeKind and IndexedAccessibility enums to IndexEnums.cs with integer values matching Microsoft.CodeAnalysis.TypeKind and Microsoft.CodeAnalysis.Accessibility. Include every current Roslyn enum value so Storage never relies on unexplained numeric literals.

- [ ] **Step 4: Implement contextual binding extraction.**

Add a deduplication set to SemanticExtractor:

~~~csharp
private readonly HashSet<(string ImplementingType, string InterfaceMethod, string ImplementationMethod)>
    _interfaceMethodBindingKeys = [];
~~~

For every declared type processed by ExtractRelations, enumerate type.AllInterfaces and each IMethodSymbol member. Resolve the implementation and add this exact binding:

~~~csharp
private void ExtractInterfaceMethodBindings(INamedTypeSymbol type)
{
    var implementingTypeKey = EnsureType(type);
    foreach (var interfaceType in type.AllInterfaces)
    {
        foreach (var interfaceMethod in interfaceType.GetMembers().OfType<IMethodSymbol>())
        {
            if (type.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol implementation)
            {
                continue;
            }

            var interfaceMethodKey = EnsureMethod(
                _canonicalizer.NormalizeMethod(interfaceMethod),
                actualTarget: false);
            var implementationMethodKey = EnsureMethod(
                _canonicalizer.NormalizeMethod(implementation),
                actualTarget: false);
            var key = (implementingTypeKey, interfaceMethodKey, implementationMethodKey);
            if (_interfaceMethodBindingKeys.Add(key))
            {
                _snapshot.InterfaceMethodBindings.Add(new InterfaceMethodBindingData
                {
                    ImplementingTypeKey = implementingTypeKey,
                    InterfaceMethodKey = interfaceMethodKey,
                    ImplementationMethodKey = implementationMethodKey,
                });
            }
        }
    }
}
~~~

Call this once for each declared class, struct, or record type. Do not add bindings for interface declarations themselves. If Roslyn returns null, append one deterministic diagnostic containing the implementing type and interface member display names, then continue.

- [ ] **Step 5: Run core tests and refactor only with a green suite.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj --filter FullyQualifiedName~InterfaceMethodBindingExtractorTests
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj
~~~

Expected: the focused tests and the complete Core project pass with zero failures.

- [ ] **Step 6: Commit Task 1.**

~~~powershell
rtk git add src/CsIndex.Core/Model/IndexEnums.cs src/CsIndex.Core/Model/IndexData.cs src/CsIndex.Core/Symbols/SymbolCanonicalizer.cs src/CsIndex.Core/Analysis/SemanticExtractor.cs tests/CsIndex.Core.Tests/InterfaceMethodBindingExtractorTests.cs
rtk git commit -m "feat(core): extract interface method bindings"
~~~

### Task 2: Persist schema version 3 and interface bindings

**Files:**
- Modify: src/CsIndex.Core/Caching/RequestHasher.cs
- Modify: src/CsIndex.Storage/Schema/SchemaMigrator.cs
- Modify: src/CsIndex.Storage/SqliteIndex.cs
- Modify: src/CsIndex.Storage/QueryModels.cs
- Modify: src/CsIndex.Storage/QueryRepository.cs
- Modify: tests/CsIndex.Storage.Tests/SqliteIndexTests.cs

**Interfaces:**
- Consumes: IndexSnapshot.InterfaceMethodBindings and SymbolData.TypeKind from Task 1.
- Produces: schema version 3, StoredInterfaceMethodBinding, StoredSymbol.TypeKind/Accessibility, and GetInterfaceMethodBindingsAsync.

- [ ] **Step 1: Write failing schema and persistence tests.**

Update the existing version assertions to 3 and add tests equivalent to:

~~~csharp
[Fact]
public async Task Save_PersistsTypeKindAndInterfaceMethodBindings()
{
    using var temporary = new TempDirectory();
    var index = new SqliteIndex(Path.Combine(temporary.Path, "index.sqlite"));
    await index.SaveAsync(CreateOverrideSearchSnapshot(temporary.Path), TestContext.Current.CancellationToken);

    var repository = index.CreateQueryRepository();
    var profile = await repository.GetProfileAsync(cancellationToken: TestContext.Current.CancellationToken);
    var contract = Assert.Single(await repository.FindSymbolCandidatesAsync(
        profile.Id, name: "Play", typeSimpleName: "IPlayable",
        kind: IndexedSymbolKind.Method,
        cancellationToken: TestContext.Current.CancellationToken));
    var bindings = await repository.GetInterfaceMethodBindingsAsync(
        profile.Id, [contract.Id], TestContext.Current.CancellationToken);

    Assert.Equal(3, SchemaMigrator.CurrentVersion);
    Assert.Equal(3, RequestHasher.SchemaVersion);
    Assert.NotEmpty(bindings);
    Assert.All(bindings, binding => Assert.Equal(contract.Id, binding.InterfaceMethodId));
}
~~~

Add VersionTwoDatabase_ProducesExplicitErrorWithoutModification by adapting the existing version-one test. Preserve a marker table and DELETE journal mode, invoke EnsureCreatedAsync, and assert version 2, the marker, and journal mode remain unchanged.

- [ ] **Step 2: Run the focused Storage tests and confirm the RED state.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --filter "FullyQualifiedName~Save_PersistsTypeKindAndInterfaceMethodBindings|FullyQualifiedName~VersionTwoDatabase"
~~~

Expected: failures report schema version 2 and missing binding persistence/query APIs.

- [ ] **Step 3: Create schema version 3.**

Set RequestHasher.SchemaVersion to 3. Rename CreateVersionTwoAsync to CreateVersionThreeAsync, insert schema_info value 3, add nullable type_kind INTEGER after accessibility in symbols, and add the exact interface_method_bindings table and two indexes from the approved design specification.

Keep the existing fail-fast version check. Do not add an UPDATE, ALTER migration, DROP, or delete-on-mismatch path.

- [ ] **Step 4: Persist type kind and bindings transactionally.**

Add type_kind to InsertSymbolAsync and bind it as:

~~~csharp
command.Parameters.AddWithValue("$type_kind", (object?)symbol.TypeKind ?? DBNull.Value);
~~~

Add this persistence method and call it after symbol IDs are known:

~~~csharp
private static async Task InsertInterfaceMethodBindingsAsync(
    SqliteConnection connection,
    SqliteTransaction transaction,
    long profileId,
    IEnumerable<InterfaceMethodBindingData> bindings,
    IReadOnlyDictionary<string, long> symbolIds,
    CancellationToken cancellationToken)
{
    await using var command = CreateCommand(connection, transaction, """
        INSERT OR IGNORE INTO interface_method_bindings(
            analysis_profile_id, implementing_type_id,
            interface_method_id, implementation_method_id)
        VALUES($profile_id, $type_id, $interface_id, $implementation_id);
        """);
    foreach (var binding in bindings)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$type_id", symbolIds[binding.ImplementingTypeKey]);
        command.Parameters.AddWithValue("$interface_id", symbolIds[binding.InterfaceMethodKey]);
        command.Parameters.AddWithValue("$implementation_id", symbolIds[binding.ImplementationMethodKey]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
~~~

Delete binding rows before prior profile symbols, or rely on their symbol foreign keys with ON DELETE CASCADE and verify that behavior in the replacement-save test.

- [ ] **Step 5: Add DB-only read models.**

Append optional int? TypeKind and int? Accessibility values to StoredSymbol, update every symbol SELECT projection and ReadSymbolsAsync consistently, and add:

~~~csharp
public sealed record StoredInterfaceMethodBinding(
    long ImplementingTypeId,
    long InterfaceMethodId,
    long ImplementationMethodId);
~~~

Implement:

~~~csharp
public Task<IReadOnlyList<StoredInterfaceMethodBinding>> GetInterfaceMethodBindingsAsync(
    long profileId,
    IEnumerable<long> interfaceMethodIds,
    CancellationToken cancellationToken = default);
~~~

The SQL must filter both analysis_profile_id and interface_method_id, parameterize every ID, and order by implementing_type_id then implementation_method_id.

- [ ] **Step 6: Run Storage tests and verify transactional replacement.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj
~~~

Expected: all Storage tests pass, including schema rejection, rollback, replacement, and DB-only binding reads.

- [ ] **Step 7: Commit Task 2.**

~~~powershell
rtk git add src/CsIndex.Core/Caching/RequestHasher.cs src/CsIndex.Storage/Schema/SchemaMigrator.cs src/CsIndex.Storage/SqliteIndex.cs src/CsIndex.Storage/QueryModels.cs src/CsIndex.Storage/QueryRepository.cs tests/CsIndex.Storage.Tests/SqliteIndexTests.cs
rtk git commit -m "feat(storage): persist override search bindings"
~~~

### Task 3: Add cycle-safe inherited and descendant graph queries

**Files:**
- Modify: src/CsIndex.Storage/QueryModels.cs
- Modify: src/CsIndex.Storage/QueryRepository.cs
- Modify: tests/CsIndex.Storage.Tests/SqliteIndexTests.cs

**Interfaces:**
- Consumes: schema version 3 symbols, type_kind/accessibility, symbol_relations, and interface_method_bindings.
- Produces: StoredInheritedMethodCandidate, MethodSearchSeed, InterfaceSearchSeed, FindInheritedMethodCandidatesAsync, ExpandOverrideMethodIdsAsync, and FindInterfaceImplementationMethodIdsAsync.

- [ ] **Step 1: Add failing graph-query tests and a persisted graph fixture.**

Add these records to the expected test API:

~~~csharp
public sealed record StoredInheritedMethodCandidate(long ReceiverTypeId, long MethodId, int Depth);
public sealed record MethodSearchSeed(long MethodId, long ReceiverTypeId);
public sealed record InterfaceSearchSeed(long InterfaceMethodId, long InterfaceScopeTypeId);
~~~

CreateOverrideSearchSnapshot must contain type/method symbols and relations for:

~~~text
IPlayable
Pianist implements IPlayable
ProPianist inherits Pianist
Game implements IPlayable
Base
D1 inherits Base and implements IPlayable
D2 inherits D1
OtherBranch inherits Base
~~~

Persist method overrides ProPianist.Play -> Pianist.Play, D2.Play -> Base.Play, and OtherBranch.Play -> Base.Play. Persist interface bindings for Pianist, ProPianist, Game, D1, and D2, but none for OtherBranch.

Write these assertions:

~~~csharp
var inherited = await repository.FindInheritedMethodCandidatesAsync(
    profile.Id, [d1.Id], "Play", cancellationToken);
Assert.Contains(inherited, row => row.ReceiverTypeId == d1.Id &&
                                  row.MethodId == basePlay.Id &&
                                  row.Depth == 1);

var branchMethods = await repository.ExpandOverrideMethodIdsAsync(
    profile.Id, [new MethodSearchSeed(basePlay.Id, d1.Id)], cancellationToken);
Assert.Contains(basePlay.Id, branchMethods);
Assert.Contains(d2Play.Id, branchMethods);
Assert.DoesNotContain(otherPlay.Id, branchMethods);

var interfaceMethods = await repository.FindInterfaceImplementationMethodIdsAsync(
    profile.Id, [new InterfaceSearchSeed(interfacePlay.Id, iPlayable.Id)], cancellationToken);
Assert.Contains(pianistPlay.Id, interfaceMethods);
Assert.Contains(proPlay.Id, interfaceMethods);
Assert.Contains(gamePlay.Id, interfaceMethods);
Assert.Contains(basePlay.Id, interfaceMethods);
Assert.Contains(d2Play.Id, interfaceMethods);
Assert.DoesNotContain(otherPlay.Id, interfaceMethods);
~~~

Add malformed type and override cycles to a separate snapshot and assert each API completes, returns distinct IDs, and does not throw a recursion-depth error.

Add accessibility and profile-isolation assertions: private inherited methods are absent; internal and private-protected methods are absent when the receiver is in another assembly; and the same graph APIs return no rows for a different analysis_profile_id.

- [ ] **Step 2: Run the graph-query tests and confirm the RED state.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --filter "FullyQualifiedName~InheritedMethod|FullyQualifiedName~OverrideMethod|FullyQualifiedName~InterfaceImplementation|FullyQualifiedName~Cycle"
~~~

Expected: compilation fails because the records and repository methods are absent.

- [ ] **Step 3: Implement inherited ancestor lookup.**

Add:

~~~csharp
public Task<IReadOnlyList<StoredInheritedMethodCandidate>> FindInheritedMethodCandidatesAsync(
    long profileId,
    IEnumerable<long> receiverTypeIds,
    string methodName,
    CancellationToken cancellationToken = default);
~~~

Use a path-bearing recursive CTE. The anchor path is ,<receiver-id>, and the recursive predicate must reject target IDs already present in that comma-delimited path. For class/struct receivers follow only Inherits; for interface receivers follow only base-interface Implements edges. Return all accessible same-name methods with depth greater than zero. Public, protected, and protected-internal methods are eligible; internal and private-protected methods require equal assembly names; private and not-applicable methods are excluded.

- [ ] **Step 4: Implement branch-scoped override expansion.**

Add:

~~~csharp
public Task<IReadOnlyList<long>> ExpandOverrideMethodIdsAsync(
    long profileId,
    IEnumerable<MethodSearchSeed> seeds,
    CancellationToken cancellationToken = default);
~~~

Build parameterized seed VALUES rows. One UNION CTE walks reverse Inherits edges from each receiver type, and a second UNION CTE walks reverse Overrides edges from each root method. Return only methods whose containing type is in the matching receiver branch. Include every root method ID and return distinct IDs in stable numeric order.

- [ ] **Step 5: Implement interface implementation expansion with interface scope.**

Add:

~~~csharp
public Task<IReadOnlyList<long>> FindInterfaceImplementationMethodIdsAsync(
    long profileId,
    IEnumerable<InterfaceSearchSeed> seeds,
    CancellationToken cancellationToken = default);
~~~

For each interface scope, use a UNION closure over reverse Implements and Inherits type relations to find types assignable to that exact interface. Join interface_method_bindings on both the contract method and the assignable implementing type. This scope filter is what prevents an IBase method query through IDerived from including a class that implements only IBase.

- [ ] **Step 6: Run focused and complete Storage tests.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj --filter "FullyQualifiedName~SqliteIndexTests"
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj
~~~

Expected: every graph assertion passes and malformed cycles terminate.

- [ ] **Step 7: Commit Task 3.**

~~~powershell
rtk git add src/CsIndex.Storage/QueryModels.cs src/CsIndex.Storage/QueryRepository.cs tests/CsIndex.Storage.Tests/SqliteIndexTests.cs
rtk git commit -m "feat(storage): resolve override search graphs"
~~~

### Task 4: Resolve exact, inherited, interface, and override method targets

**Files:**
- Create: src/CsIndex.Query/MethodTargetResolver.cs
- Modify: src/CsIndex.Query/Symbols/SymbolMatcher.cs
- Modify: src/CsIndex.Query/SemanticQueryService.cs
- Modify: tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs
- Modify: tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs

**Interfaces:**
- Consumes: repository graph APIs from Task 3 and existing SymbolQueryParser.
- Produces: MethodTargetResolver.ResolveAsync and includeOverrides support in FindSymbolsAsync and FindDefinitionsAsync.

- [ ] **Step 1: Extend the integration fixture with the approved inheritance graph.**

Add compiling public members under namespace Alpha:

~~~csharp
public interface IPlayable { void Play(); }
public interface IAdvancedPlayable : IPlayable { }

public class Pianist : IPlayable
{
    public virtual void Play() => PianistBody();
    private void PianistBody() { }
}

public class ProPianist : Pianist
{
    public override void Play() => ProPianistBody();
    private void ProPianistBody() { }
}

public class Game : IPlayable
{
    public void Play() => GameBody();
    private void GameBody() { }
}

public class Baseball
{
    public void Play() => BaseballBody();
    private void BaseballBody() { }
}

public class InheritedBase
{
    public virtual void Play() => BaseBody();
    private void BaseBody() { }
}

public class D1 : InheritedBase, IAdvancedPlayable { }

public class D2 : D1
{
    public override void Play() => D2Body();
    private void D2Body() { }
}

public class OtherBranch : InheritedBase
{
    public override void Play() => OtherBody();
    private void OtherBody() { }
}

public class HidingPlayer : D1
{
    public new void Play() => HiddenBody();
    private void HiddenBody() { }
}

public class HidingBase
{
    public void Select(int value) { }
}

public class HidingMiddle : HidingBase
{
    public void Select(string value) { }
}

public class HidingLeaf : HidingMiddle { }
~~~

- [ ] **Step 2: Write failing target-resolution acceptance tests.**

Add:

~~~csharp
[Fact]
public async Task IncludeOverridesExpandsInterfaceAndConcreteTargetsDownward()
{
    await fixture.BuildTask;
    var interfaceResult = await fixture.Query.FindSymbolsAsync(
        "Alpha.IPlayable::Play()", includeOverrides: true,
        cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal(
        ["Alpha.D2::Play()", "Alpha.Game::Play()", "Alpha.IPlayable::Play()",
         "Alpha.InheritedBase::Play()", "Alpha.Pianist::Play()", "Alpha.ProPianist::Play()"],
        interfaceResult.MatchedSymbols.Select(symbol => symbol.DisplayName).Order());

    var concreteResult = await fixture.Query.FindSymbolsAsync(
        "Alpha.Pianist::Play()", includeOverrides: true,
        cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal(
        ["Alpha.Pianist::Play()", "Alpha.ProPianist::Play()"],
        concreteResult.MatchedSymbols.Select(symbol => symbol.DisplayName).Order());
}

[Fact]
public async Task IncludeOverridesResolvesInheritedAliasWithinReceiverBranch()
{
    await fixture.BuildTask;
    var result = await fixture.Query.FindDefinitionsAsync(
        "Alpha.D1::Play()", includeOverrides: true,
        cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal(
        ["Alpha.D2::Play()", "Alpha.InheritedBase::Play()"],
        result.Definitions.Select(symbol => symbol.DisplayName).Order());
    Assert.DoesNotContain(result.Definitions, symbol => symbol.TypeSimpleName == "D1");
    Assert.DoesNotContain(result.Definitions, symbol => symbol.TypeSimpleName == "OtherBranch");
}

[Fact]
public async Task DerivedInterfaceAliasDoesNotIncludeBaseInterfaceSiblingImplementations()
{
    await fixture.BuildTask;
    var result = await fixture.Query.FindSymbolsAsync(
        "Alpha.IAdvancedPlayable::Play()", includeOverrides: true,
        cancellationToken: TestContext.Current.CancellationToken);
    Assert.Contains(result.MatchedSymbols, symbol => symbol.TypeSimpleName == "InheritedBase");
    Assert.Contains(result.MatchedSymbols, symbol => symbol.TypeSimpleName == "D2");
    Assert.DoesNotContain(result.MatchedSymbols, symbol => symbol.TypeSimpleName == "Game");
}
~~~

Also assert that the same D1 query returns zero exact matches without the flag and that HidingPlayer::Play resolves only the real hiding method.

Add name-hiding assertions for HidingLeaf: Select(string) resolves to HidingMiddle.Select(string), while Select(int) returns zero results because the same-name declaration on HidingMiddle suppresses lookup of HidingBase.Select(int).

- [ ] **Step 3: Run the focused integration tests and confirm the RED state.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter "FullyQualifiedName~IncludeOverrides|FullyQualifiedName~DerivedInterfaceAlias"
~~~

Expected: API calls fail to compile because includeOverrides and MethodTargetResolver do not exist.

- [ ] **Step 4: Extract signature-only matching.**

Add this public helper and make IsMatch call it after receiver-type checks:

~~~csharp
public static bool IsMethodSignatureMatch(SymbolQuery query, StoredSymbol symbol)
{
    if (!query.IsMethodQuery || symbol.Kind != IndexedSymbolKind.Method ||
        symbol.Name != query.MethodName)
    {
        return false;
    }

    if (query.ParameterTypes is null)
    {
        return true;
    }

    return symbol.Parameters.Count == query.ParameterTypes.Count &&
           symbol.Parameters.Select(parameter => TypeNameNormalizer.Normalize(parameter.TypeKey))
               .SequenceEqual(query.ParameterTypes);
}
~~~

- [ ] **Step 5: Implement MethodTargetResolver.**

Create:

~~~csharp
internal sealed class MethodTargetResolver(QueryRepository repository)
{
    public Task<IReadOnlyList<StoredSymbol>> ResolveAsync(
        long profileId,
        SymbolQuery query,
        bool includeOverrides,
        bool sourceOnly,
        CancellationToken cancellationToken = default);
}
~~~

Resolve receiver type symbols first. For each receiver, collect declared same-name methods before applying the optional parameter signature. If any declared same-name method exists, never fall back to a base member even when its signatures do not match. Otherwise group inherited candidates by receiver, select the minimum depth, and apply the signature only at that depth.

Represent each resolved root internally as:

~~~csharp
private sealed record ResolvedRoot(StoredSymbol Method, StoredSymbol ReceiverType);
~~~

Split roots by ReceiverType.TypeKind. Interface roots use InterfaceSearchSeed(Method.Id, ReceiverType.Id); all other roots use MethodSearchSeed(Method.Id, ReceiverType.Id). Add root IDs, concrete override IDs, and interface implementation IDs to one HashSet<long>, then load real symbols with GetSymbolsByIdsAsync.

- [ ] **Step 6: Thread the resolver through symbol and definition queries.**

Use these signatures:

~~~csharp
public Task<QueryContext> FindSymbolsAsync(
    string queryText,
    string? profileName = null,
    bool sourceOnly = false,
    bool includeOverrides = false,
    CancellationToken cancellationToken = default);

public Task<DefinitionResult> FindDefinitionsAsync(
    string queryText,
    string? profileName = null,
    bool includeOverrides = false,
    CancellationToken cancellationToken = default);
~~~

Type queries keep the existing path when includeOverrides is false. When it is true, throw SymbolQueryParseException with the exact message "--include-overrides requires a method query." FindTargetSymbolsAsync must pass includeOverrides through both its source-first and metadata-fallback attempts.

- [ ] **Step 7: Run Query/Integration tests and refactor.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter "FullyQualifiedName~PhaseOneAcceptanceTests"
~~~

Expected: exact lookup tests remain unchanged; all new target-set tests pass.

- [ ] **Step 8: Commit Task 4.**

~~~powershell
rtk git add src/CsIndex.Query/MethodTargetResolver.cs src/CsIndex.Query/Symbols/SymbolMatcher.cs src/CsIndex.Query/SemanticQueryService.cs tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs
rtk git commit -m "feat(query): resolve override-aware method targets"
~~~

### Task 5: Apply override-aware targets to calls and expose the CLI option

**Files:**
- Modify: src/CsIndex.Query/SemanticQueryService.cs
- Modify: src/CsIndex.Cli/CliArguments.cs
- Modify: src/CsIndex.Cli/Program.cs
- Modify: tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs
- Modify: tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs
- Modify: tests/CsIndex.IntegrationTests/CliCommandTests.cs

**Interfaces:**
- Consumes: expanded QueryContext from Task 4.
- Produces: includeOverrides support in FindReferencesAsync, FindCallersAsync, FindCalleesAsync, and --include-overrides parsing for all five approved commands.

- [ ] **Step 1: Add call sites and failing service tests.**

Add this fixture caller:

~~~csharp
public class OverrideSearchCaller
{
    public void Execute(
        IPlayable contract,
        Pianist pianist,
        ProPianist professional,
        Game game,
        Baseball baseball,
        D1 d1,
        D2 d2,
        OtherBranch other)
    {
        contract.Play();
        pianist.Play();
        professional.Play();
        game.Play();
        baseball.Play();
        d1.Play();
        d2.Play();
        other.Play();
    }
}
~~~

Write tests that prove:

~~~csharp
var interfaceCallers = await fixture.Query.FindCallersAsync(
    "Alpha.IPlayable::Play()", GeneratedFilter.Include,
    DispatchSearchMode.Static, CallerScope.Direct,
    includeOverrides: true,
    cancellationToken: cancellationToken);
Assert.Contains(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("IPlayable"));
Assert.Contains(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("Pianist"));
Assert.Contains(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("Game"));
Assert.DoesNotContain(interfaceCallers.Calls, call => call.CalleeDefinitionDisplayName!.Contains("Baseball"));

var concreteCallers = await fixture.Query.FindCallersAsync(
    "Alpha.Pianist::Play()", GeneratedFilter.Include,
    DispatchSearchMode.Static, CallerScope.Direct,
    includeOverrides: true,
    cancellationToken: cancellationToken);
Assert.DoesNotContain(concreteCallers.Calls, call =>
    call.CalleeDefinitionDisplayName!.Contains("IPlayable"));
Assert.DoesNotContain(concreteCallers.Calls, call =>
    call.CalleeDefinitionDisplayName!.Contains("Game"));
~~~

Add references coverage for method groups or ordinary invocations, and a D1 callees assertion that includes BaseBody and D2Body but excludes OtherBody. Verify --exclude-lambda-calls still takes effect when combined with includeOverrides.

- [ ] **Step 2: Run the service tests and confirm the RED state.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter "FullyQualifiedName~OverrideAware|FullyQualifiedName~IncludeOverrides"
~~~

Expected: call-query APIs do not accept includeOverrides.

- [ ] **Step 3: Thread includeOverrides through call-query services.**

Append bool includeOverrides = false before CancellationToken in these APIs:

~~~csharp
FindReferencesAsync(
    string queryText, GeneratedFilter generatedFilter,
    string? profileName = null, bool includeOverrides = false,
    CancellationToken cancellationToken = default);

FindCallersAsync(
    string queryText, GeneratedFilter generatedFilter,
    DispatchSearchMode dispatchMode, CallerScope callerScope,
    string? profileName = null, bool includeOverrides = false,
    CancellationToken cancellationToken = default);

FindCalleesAsync(
    string queryText, GeneratedFilter generatedFilter,
    bool includeLambdaCalls = true, string? profileName = null,
    bool includeOverrides = false,
    CancellationToken cancellationToken = default);
~~~

Each method passes the flag to FindTargetSymbolsAsync and otherwise preserves its current generated/reference-kind/caller-scope/dispatch/lambda logic.

- [ ] **Step 4: Write failing end-to-end CLI tests.**

Add a theory invoking Program.Main with --db for symbol find, definition, references, callers, and callees. Run every command once with --output table and once with --output json. Assert each command accepts --include-overrides and includes ProPianist when rooted at Pianist. Parse JSON and assert the expanded method appears in the command's matched collection as well as its definitions, calls, or caller/callee-specific collection where applicable.

Add:

~~~csharp
[Theory]
[InlineData("symbol", "find", "Alpha.IPlayable")]
[InlineData("definition", "--at", "Source.cs:1:1")]
public async Task IncludeOverridesRejectsNonMethodQueries(params string[] args)
{
    await _fixture.BuildTask;
    var result = await RunAsync(args.Concat(["--include-overrides", "--db", _fixture.DatabasePath]).ToArray());
    Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
    Assert.Contains("--include-overrides requires a method query", result.StandardError);
}
~~~

Add one callers test combining --include-overrides --dispatch all --short-names and one callees test combining --include-overrides --exclude-lambda-calls.

- [ ] **Step 5: Implement CLI parsing and help.**

Add include-overrides to CliArguments.Flags. Add it to the allowed option list for exactly RunSymbolAsync, RunDefinitionAsync, RunReferencesAsync, RunCallersAsync, and RunCalleesAsync. Pass:

~~~csharp
includeOverrides: parsed.HasFlag("include-overrides")
~~~

to the matching query-service calls. In RunDefinitionAsync, reject --at plus --include-overrides with CliUsageException("--include-overrides requires a method query."). Do not accept the flag for symbol list, overrides, conditions, or index.

Update global and command-specific help with:

~~~text
--include-overrides         Include descendant overrides and interface implementations
~~~

- [ ] **Step 6: Run CLI, integration, and query tests.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj --filter FullyQualifiedName~CliCommandTests
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
~~~

Expected: all new command combinations pass, unsupported commands reject the flag, and default behavior remains exact.

- [ ] **Step 7: Commit Task 5.**

~~~powershell
rtk git add src/CsIndex.Query/SemanticQueryService.cs src/CsIndex.Cli/CliArguments.cs src/CsIndex.Cli/Program.cs tests/CsIndex.IntegrationTests/SemanticIndexFixture.cs tests/CsIndex.IntegrationTests/PhaseOneAcceptanceTests.cs tests/CsIndex.IntegrationTests/CliCommandTests.cs
rtk git commit -m "feat(cli): add override-aware search mode"
~~~

### Task 6: Document schema and search semantics, then verify the repository

**Files:**
- Modify: docs/SPEC.md
- Modify: docs/CLI.md
- Modify: docs/DECISIONS.md
- Modify: docs/DB_SCHEMA.md
- Modify: docs/TEST_PLAN.md
- Modify: docs/IMPLEMENTATION_STATUS.md
- Modify: docs/KNOWN_LIMITATIONS.md
- Verify: CsIndex.sln

**Interfaces:**
- Consumes: final option name, schema, query behavior, and limitations from Tasks 1-5.
- Produces: durable user/developer documentation and fresh full-suite verification evidence.

- [ ] **Step 1: Update user-facing CLI and specification documentation.**

Document the five accepted commands, default-off behavior, descendant-only direction, inherited aliases, real-symbol output, type-query error, and examples for IPlayable, Pianist, and D1. State explicitly that concrete searches exclude interface-statically-typed call sites.

- [ ] **Step 2: Update schema, decisions, test plan, status, and limitations.**

Set the documented schema/request-hash version to 3. Add symbols.type_kind, interface_method_bindings, both indexes, foreign keys, and rebuild requirements to DB_SCHEMA.md and SPEC.md. Add a decision recording branch-scoped interface bindings and query-time graph expansion. Record the metadata-only implementation enumeration limit and the absence of upward runtime-flow expansion in KNOWN_LIMITATIONS.md.

- [ ] **Step 3: Run focused project tests.**

Run:

~~~powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
~~~

Expected: every project reports zero failed tests.

- [ ] **Step 4: Run full Release verification with confirmed process exits.**

Run:

~~~powershell
rtk proxy dotnet test CsIndex.sln --configuration Release
rtk proxy dotnet build CsIndex.sln --configuration Release
~~~

Require confirmed exit code 0 for both commands, zero failed tests, zero build errors, and no warnings-as-errors failures. If the sandbox blocks Windows SDK discovery, rerun the same commands with approved elevated access and preserve the exit-code evidence.

- [ ] **Step 5: Inspect acceptance criteria and repository hygiene.**

Run:

~~~powershell
rtk git diff --check
rtk git status --short
rtk rg -n "Current schema version|Request hash schema version|include-overrides|interface_method_bindings|type_kind" docs src tests
~~~

Review every changed file against the approved design. Confirm no internal SDD reports, temporary verification scripts, generated binaries, or unrelated user changes are staged.

- [ ] **Step 6: Commit Task 6.**

~~~powershell
rtk git add docs/SPEC.md docs/CLI.md docs/DECISIONS.md docs/DB_SCHEMA.md docs/TEST_PLAN.md docs/IMPLEMENTATION_STATUS.md docs/KNOWN_LIMITATIONS.md
rtk git commit -m "docs: document override-aware method search"
~~~

After this task, run the required per-task review and final whole-branch review before using superpowers:finishing-a-development-branch.
