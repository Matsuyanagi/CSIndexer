using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class LogicalDeclarationExtractionTests
{
    [Fact]
    public async Task AnalyzeAsync_StoresPartialPartsAsOneLogicalCallableWithPreferredImplementation()
    {
        const string definition = """
            using System.Threading.Tasks;

            namespace Sample;

            public interface IContract
            {
                void Execute();
            }

            public class Base
            {
                public virtual void Execute() { }
            }

            public partial class Worker : Base, IContract
            {
                public partial Task LoadAsync();
                partial void Validate();
            }
            """;
        const string implementation = """
            using System;
            using System.Threading.Tasks;

            namespace Sample;

            public partial class Worker
            {
                public override void Execute()
                {
                    LoadAsync();
                    Action nested = () => LoadAsync();
                }

                public partial async Task LoadAsync()
                {
                    await Task.Yield();
                    void Local() => Target();
                    Local();
                    Action nested = () => Target();
                }

                private static void Target() { }
            }
            """;
        var snapshot = await AnalyzeAsync(("Definition.cs", definition), ("Implementation.cs", implementation));

        var load = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == "LoadAsync");
        var loadDeclarations = DeclarationsFor(snapshot, load);
        Assert.Equal(2, loadDeclarations.Count);
        Assert.Equal(
            new[] { DeclarationRole.PartialDefinition, DeclarationRole.PartialImplementation },
            loadDeclarations.OrderBy(row => row.Role).Select(row => row.Role));
        Assert.Equal(
            DeclarationRole.PartialImplementation,
            DeclarationByKey(snapshot, load.PreferredDeclarationKey!).Role);
        Assert.Contains("await", DeclarationByKey(snapshot, load.PreferredDeclarationKey!).NormalizedSource);
        Assert.Equal(load.StableKey, DeclarationByKey(snapshot, load.PreferredDeclarationKey!).SymbolKey);
        Assert.True(load.AsyncRole.HasFlag(AsyncRole.DeclaredAsync));

        var validate = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == "Validate");
        Assert.Equal(
            DeclarationRole.PartialDefinition,
            DeclarationByKey(snapshot, validate.PreferredDeclarationKey!).Role);
        Assert.Single(DeclarationsFor(snapshot, validate));

        Assert.NotEqual(loadDeclarations[0].Key, loadDeclarations[1].Key);
        Assert.All(loadDeclarations, declaration =>
            Assert.DoesNotContain("|constructed:", declaration.Key, StringComparison.Ordinal));
        Assert.All(loadDeclarations, declaration =>
            Assert.DoesNotContain("|reduced:", declaration.Key, StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Relations, relation =>
            relation.SourceSymbolKey == load.StableKey &&
            relation.TargetSymbolKey == load.StableKey &&
            relation.RelationKind is SymbolRelationKind.PartialDefinition or SymbolRelationKind.PartialImplementation);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesLogicalKeysForCallsContainmentBindingsAndAsyncPropagation()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            namespace Sample;

            public interface IContract { void Execute(); }
            public class Base { public virtual void Execute() { } }

            public partial class Worker : Base, IContract
            {
                public partial Task LoadAsync();

                public override void Execute()
                {
                    LoadAsync();
                    Action nested = () => LoadAsync();
                }

                public partial async Task LoadAsync()
                {
                    await Task.Yield();
                    void Local() { Target(); }
                    Local();
                    Action nested = () => Target();
                }

                private static void Target() { }
            }
            """;
        var snapshot = await AnalyzeAsync(("Worker.cs", source));

        var load = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == "LoadAsync");
        var execute = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method &&
            symbol.Name == "Execute" &&
            symbol.TypeSimpleName == "Worker");
        Assert.Equal(load.StableKey, execute.AsyncNextSymbolKey);

        var resolvedCalls = snapshot.Calls.Where(call => call.ResolutionStatus == ResolutionStatus.Resolved).ToArray();
        Assert.NotEmpty(resolvedCalls);
        Assert.All(resolvedCalls, call =>
        {
            Assert.NotNull(call.CalleeDefinitionKey);
            Assert.Equal(call.CalleeDefinitionKey, call.CalleeSymbolKey);
            Assert.True(snapshot.Symbols.ContainsKey(call.CalleeDefinitionKey!));
            Assert.DoesNotContain("|constructed:", call.CalleeDefinitionKey!, StringComparison.Ordinal);
            Assert.DoesNotContain("|reduced:", call.CalleeDefinitionKey!, StringComparison.Ordinal);
        });

        var local = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == "Local");
        Assert.Equal(load.StableKey, local.ContainingSymbolKey);
        var lambda = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda && symbol.ContainingSymbolKey == load.StableKey);
        Assert.Contains(snapshot.Calls, call => call.CallerSymbolKey == local.StableKey);
        Assert.Contains(snapshot.Calls, call => call.CallerSymbolKey == lambda.StableKey);

        Assert.Contains(snapshot.Relations, relation =>
            relation.SourceSymbolKey == execute.StableKey &&
            relation.RelationKind == SymbolRelationKind.Overrides);
        var workerType = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Type && symbol.TypeSimpleName == "Worker");
        var binding = Assert.Single(snapshot.InterfaceMethodBindings, candidate =>
            candidate.ImplementingTypeKey == workerType.StableKey &&
            candidate.ImplementationMethodKey == execute.StableKey);
        Assert.True(snapshot.Symbols.ContainsKey(binding.InterfaceMethodKey));
        Assert.DoesNotContain(snapshot.Calls, call => call.CallerSymbolKey == "Validate");
    }

    private static IReadOnlyList<SymbolDeclarationData> DeclarationsFor(
        IndexSnapshot snapshot,
        SymbolData symbol) =>
        snapshot.Declarations.Values
            .Where(declaration => declaration.SymbolKey == symbol.StableKey)
            .ToArray();

    private static SymbolDeclarationData DeclarationByKey(IndexSnapshot snapshot, string key) =>
        snapshot.Declarations[key];

    private static async Task<IndexSnapshot> AnalyzeAsync(params (string Path, string Source)[] documents)
    {
        using var temporary = new TempDirectory();
        foreach (var (path, source) in documents)
        {
            temporary.Write(path, source);
        }

        var options = new IndexOptions
        {
            InputPath = temporary.Path,
            ForcedMode = InputMode.Directory,
        };
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(
            input,
            options,
            TestContext.Current.CancellationToken);
        var result = await coordinator.AnalyzeAsync(
            input,
            options,
            fingerprint,
            RequestHasher.Build(input, options),
            TestContext.Current.CancellationToken);
        return result.Snapshot;
    }
}
