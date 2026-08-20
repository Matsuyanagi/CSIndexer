using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        var preferredDeclaration = DeclarationByKey(snapshot, load.PreferredDeclarationKey!);
        Assert.Contains("await", preferredDeclaration.NormalizedSource);
        Assert.Equal(load.StableKey, preferredDeclaration.SymbolKey);
        Assert.Null(load.SourceDocumentKey);
        Assert.Null(load.SourceStart);
        Assert.Null(load.SourceLength);
        Assert.Null(load.NormalizedSource);
        Assert.Null(load.NormalizedSourceHash);
        var testCancellationToken = TestContext.Current.CancellationToken;
        var implementationTree = CSharpSyntaxTree.ParseText(
            implementation,
            cancellationToken: testCancellationToken);
        var implementationMethod = Assert.Single(
            implementationTree.GetRoot(testCancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "LoadAsync");
        var expectedDeclaration = SourceNormalizer.Normalize(implementationMethod, testCancellationToken);
        Assert.Equal(implementationMethod.SpanStart, preferredDeclaration.SourceStart);
        Assert.Equal(implementationMethod.Span.Length, preferredDeclaration.SourceLength);
        Assert.Equal(expectedDeclaration.Text, preferredDeclaration.NormalizedSource);
        Assert.Equal(expectedDeclaration.Hash, preferredDeclaration.NormalizedSourceHash);
        Assert.True(load.AsyncRole.HasFlag(AsyncRole.DeclaredAsync));

        var validate = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == "Validate");
        Assert.Equal(
            DeclarationRole.PartialDefinition,
            DeclarationByKey(snapshot, validate.PreferredDeclarationKey!).Role);
        Assert.Single(DeclarationsFor(snapshot, validate));
        Assert.Contains(snapshot.Calls, call => call.CallerSymbolKey == load.StableKey);
        Assert.DoesNotContain(snapshot.Calls, call => call.CallerSymbolKey == validate.StableKey);
        Assert.DoesNotContain(snapshot.Relations, relation => relation.SourceSymbolKey == validate.StableKey);
        Assert.DoesNotContain(
            snapshot.Symbols.Values,
            symbol => symbol.ContainingSymbolKey == validate.StableKey);
        Assert.Equal(AsyncRole.None, validate.AsyncRole);
        Assert.Null(validate.AsyncNextSymbolKey);

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
    }

    [Fact]
    public async Task AnalyzeAsync_NormalizesAmbiguousCandidatesToUniqueLogicalKeys()
    {
        const string source = """
            public sealed class AmbiguousHost
            {
                private static void Target(int first, object second) { }
                private static void Target(object first, int second) { }

                public void Run()
                {
                    Target(1, 1);
                }
            }
            """;

        var snapshot = await AnalyzeAsync(("Ambiguous.cs", source));

        var call = Assert.Single(snapshot.Calls, candidate =>
            candidate.ResolutionStatus == ResolutionStatus.Ambiguous &&
            candidate.UnresolvedName == "Target");
        Assert.Equal(2, call.CandidateSymbolKeys.Count);
        Assert.Equal(
            call.CandidateSymbolKeys.Count,
            call.CandidateSymbolKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(call.CandidateSymbolKeys, key =>
        {
            Assert.DoesNotContain("|constructed:", key, StringComparison.Ordinal);
            Assert.DoesNotContain("|reduced:", key, StringComparison.Ordinal);
            Assert.True(snapshot.Symbols.ContainsKey(key));
        });
    }

    [Fact]
    public async Task AnalyzeAsync_PrefersImplementationRegardlessOfDocumentAndInputOrder()
    {
        const string definition = """
            using System.Threading.Tasks;

            public partial class OrderedWorker
            {
                public partial Task LoadAsync();
            }
            """;
        const string implementation = """
            using System.Threading.Tasks;

            public partial class OrderedWorker
            {
                public partial async Task LoadAsync()
                {
                    await Task.Yield();
                }
            }
            """;

        var implementationFirst = await AnalyzeAsync(
            ("A_Implementation.cs", implementation),
            ("Z_Definition.cs", definition));
        var definitionFirst = await AnalyzeAsync(
            ("A_Definition.cs", definition),
            ("Z_Implementation.cs", implementation));

        AssertPreferredImplementation(implementationFirst, "A_Implementation.cs");
        AssertPreferredImplementation(definitionFirst, "Z_Implementation.cs");
    }

    private static void AssertPreferredImplementation(IndexSnapshot snapshot, string expectedFileName)
    {
        var load = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == "LoadAsync");
        var declaration = DeclarationByKey(snapshot, load.PreferredDeclarationKey!);
        Assert.Equal(DeclarationRole.PartialImplementation, declaration.Role);
        Assert.Contains("await", declaration.NormalizedSource, StringComparison.Ordinal);
        var document = Assert.Single(snapshot.Documents, candidate => candidate.Key == declaration.DocumentKey);
        Assert.Equal(expectedFileName, Path.GetFileName(document.NormalizedPath));
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
