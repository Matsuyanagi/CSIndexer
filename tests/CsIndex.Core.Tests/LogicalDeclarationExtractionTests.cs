using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
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
        Assert.Contains("await", NormalizedText(snapshot, preferredDeclaration), StringComparison.Ordinal);
        Assert.Equal(load.StableKey, preferredDeclaration.SymbolKey);
        Assert.Null(load.SourceDocumentKey);
        Assert.Null(load.SourceStart);
        Assert.Null(load.SourceLength);
        Assert.Null(typeof(SymbolData).GetProperty("NormalizedSource"));
        Assert.Null(typeof(SymbolData).GetProperty("NormalizedSourceHash"));
        var testCancellationToken = TestContext.Current.CancellationToken;
        var implementationTree = CSharpSyntaxTree.ParseText(
            implementation,
            cancellationToken: testCancellationToken);
        var implementationMethod = Assert.Single(
            implementationTree.GetRoot(testCancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "LoadAsync");
        Assert.Equal(implementationMethod.SpanStart, preferredDeclaration.SourceStart);
        Assert.Equal(implementationMethod.Span.Length, preferredDeclaration.SourceLength);
        Assert.Equal(
            "public partial async Task LoadAsync(){await Task.Yield();void Local()=>Target();Local();Action nested=()=>Target();}",
            NormalizedText(snapshot, preferredDeclaration));
        Assert.True(preferredDeclaration.NormalizedStart >= 0);
        Assert.True(preferredDeclaration.NormalizedLength > 0);
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
                private static void Target<T>(T first, object second) { }
                private static void Target<T>(object first, T second) { }

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
        var expectedDefinitionKeys = snapshot.Symbols.Values
            .Where(symbol =>
                symbol.Kind == IndexedSymbolKind.Method &&
                symbol.Name == "Target" &&
                symbol.Arity == 1)
            .Select(symbol => symbol.StableKey)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2, expectedDefinitionKeys.Count);
        Assert.True(expectedDefinitionKeys.SetEquals(call.CandidateSymbolKeys));
        Assert.All(call.CandidateSymbolKeys, key =>
        {
            Assert.DoesNotContain("|constructed:", key, StringComparison.Ordinal);
            Assert.DoesNotContain("|reduced:", key, StringComparison.Ordinal);
            Assert.True(snapshot.Symbols.ContainsKey(key));
        });
    }

    [Fact]
    public void NormalizeCandidateKeys_CollapsesConstructedAndReducedSymbolsToOriginalDefinitions()
    {
        const string source = """
            public static class Extensions
            {
                public static T Echo<T>(this T value) => value;
            }

            public sealed class Host
            {
                public static T Target<T>(T value) => value;

                public void Run()
                {
                    var value = 1;
                    _ = value.Echo();
                }
            }
            """;
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            "CandidateNormalization",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var host = compilation.GetTypeByMetadataName("Host")!;
        var targetDefinition = Assert.Single(host.GetMembers("Target").OfType<IMethodSymbol>());
        var constructedTarget = targetDefinition.Construct(
            compilation.GetSpecialType(SpecialType.System_Int32));
        var extensionType = compilation.GetTypeByMetadataName("Extensions")!;
        var extensionDefinition = Assert.Single(extensionType.GetMembers("Echo").OfType<IMethodSymbol>());
        var invocation = Assert.Single(tree.GetRoot(cancellationToken)
            .DescendantNodes().OfType<InvocationExpressionSyntax>());
        var reducedExtension = Assert.IsAssignableFrom<IMethodSymbol>(
            model.GetSymbolInfo(invocation, cancellationToken).Symbol);
        Assert.NotNull(reducedExtension.ReducedFrom);

        var canonicalizer = new SymbolCanonicalizer(new AnalysisProfileData
        {
            Name = "test",
            InputMode = InputMode.Directory,
            OperatingSystem = "Windows",
            Architecture = "x64",
            TargetFramework = "net10.0",
            PreprocessorSymbols = [],
            ProfileHash = [],
        });
        var candidates = SemanticExtractor.NormalizeCandidateKeys(
            [constructedTarget, constructedTarget, reducedExtension],
            canonicalizer.NormalizeLogicalMethod,
            method => method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        var expected = new[] { targetDefinition, extensionDefinition }
            .Select(method => method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Count, candidates.Length);
        Assert.True(expected.SetEquals(candidates));
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
        Assert.Contains("await", NormalizedText(snapshot, declaration), StringComparison.Ordinal);
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

    private static string NormalizedText(IndexSnapshot snapshot, SymbolDeclarationData declaration)
    {
        var document = Assert.Single(snapshot.Documents, candidate => candidate.Key == declaration.DocumentKey);
        Assert.InRange(declaration.NormalizedStart, 0, document.NormalizedSource.Length - declaration.NormalizedLength);
        Assert.InRange(declaration.NormalizedLength, 1, document.NormalizedSource.Length);
        return document.NormalizedSource.AsSpan(
            declaration.NormalizedStart,
            declaration.NormalizedLength).ToString();
    }

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

    private static IReadOnlyList<MetadataReference> GetPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();
}
