using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsIndex.Core.Tests;

public sealed class ExecutableSymbolExtractionTests
{
    [Fact]
    public void GetDefinitionStableKey_DisambiguatesFunctionPointerOverloadsWithoutChangingOrdinaryDocumentationIds()
    {
        const string source = """
            namespace Acceptance.FunctionPointers;

            public unsafe sealed class CollisionHost
            {
                public void Shape(delegate*<int, void> callback) { }
                public void Shape(delegate*<long, void> callback) { }
                public void Shape(delegate*<ref int, void> callback) { }
                public void Keep(int value) { }
            }

            public unsafe partial class PartialHost
            {
                public partial void Pair(delegate*<ref int, void> callback);
            }

            public unsafe partial class PartialHost
            {
                public partial void Pair(delegate*<ref int, void> callback) { }
            }
            """;
        var cancellationToken = TestContext.Current.CancellationToken;
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: "C:/machine-specific/FunctionPointerStableKeys.cs",
            cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            "FunctionPointerStableKeys",
            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(cancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var collisionHost = Assert.IsAssignableFrom<INamedTypeSymbol>(
            compilation.GetTypeByMetadataName("Acceptance.FunctionPointers.CollisionHost"));
        var overloads = collisionHost.GetMembers("Shape").OfType<IMethodSymbol>().ToArray();
        Assert.Equal(3, overloads.Length);
        Assert.All(overloads, method => Assert.Equal(
            "M:Acceptance.FunctionPointers.CollisionHost.Shape()",
            method.GetDocumentationCommentId()));

        var profile = new AnalysisProfileData
        {
            Name = "function-pointer-stable-key-test",
            InputMode = InputMode.Solution,
            TargetFramework = "net10.0",
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        };
        var canonicalizer = new SymbolCanonicalizer(profile);
        const string projectKey = "project-name:FunctionPointers";
        const string overloadOwnerKey =
            "profile:function-pointer-stable-key-test|assembly:FunctionPointerStableKeys|" +
            "project:project-name:FunctionPointers|tfm:net10.0|" +
            "M:Acceptance.FunctionPointers.CollisionHost.Shape()";
        string[] expectedCanonicalParameterIdentities =
        [
            "delegate*<0:System::Int32,0:System::Void>",
            "delegate*<0:System::Int64,0:System::Void>",
            "delegate*<1:System::Int32,0:System::Void>",
        ];
        var stableKeys = overloads
            .Select(method => canonicalizer.GetDefinitionStableKey(method, projectKey))
            .ToArray();

        Assert.Equal(3, stableKeys.Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < stableKeys.Length; index++)
        {
            Assert.StartsWith(overloadOwnerKey, stableKeys[index], StringComparison.Ordinal);
            Assert.Contains(expectedCanonicalParameterIdentities[index], stableKeys[index], StringComparison.Ordinal);
            Assert.Equal(stableKeys[index], canonicalizer.GetDefinitionStableKey(overloads[index], projectKey));
            Assert.DoesNotContain("machine-specific", stableKeys[index], StringComparison.OrdinalIgnoreCase);
        }

        var ordinary = Assert.Single(collisionHost.GetMembers("Keep").OfType<IMethodSymbol>());
        Assert.Equal(
            "profile:function-pointer-stable-key-test|assembly:FunctionPointerStableKeys|" +
            "project:project-name:FunctionPointers|tfm:net10.0|" +
            "M:Acceptance.FunctionPointers.CollisionHost.Keep(System.Int32)",
            canonicalizer.GetDefinitionStableKey(ordinary, projectKey));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var partialSymbols = syntaxTree.GetRoot(cancellationToken)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "Pair")
            .Select(method => Assert.IsAssignableFrom<IMethodSymbol>(
                semanticModel.GetDeclaredSymbol(method, cancellationToken)))
            .ToArray();
        var partialDefinition = Assert.Single(partialSymbols, method => method.PartialImplementationPart is not null);
        var partialImplementation = Assert.Single(partialSymbols, method => method.PartialDefinitionPart is not null);
        var definitionKey = canonicalizer.GetDefinitionStableKey(partialDefinition, projectKey);
        var implementationKey = canonicalizer.GetDefinitionStableKey(partialImplementation, projectKey);
        Assert.Equal(definitionKey, implementationKey);
        Assert.StartsWith(
            "profile:function-pointer-stable-key-test|assembly:FunctionPointerStableKeys|" +
            "project:project-name:FunctionPointers|tfm:net10.0|" +
            "M:Acceptance.FunctionPointers.PartialHost.Pair()",
            definitionKey,
            StringComparison.Ordinal);
        Assert.Contains("delegate*<1:System::Int32,0:System::Void>", definitionKey, StringComparison.Ordinal);
        Assert.DoesNotContain("machine-specific", definitionKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_ExtractsReturnTypesAndNormalizedSourceForExecutableSymbols()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            namespace Test;

            public sealed class A
            {
                static A() { }
                public A() { }

                public async Task<int> ExecuteAsync()
                {
                    await Task.Yield();
                    return 1;
                }

                public int Value
                {
                    get { return 7; }
                    set { _ = value; }
                }

                public static A operator +(A left, A right) => left;
                public static implicit operator int(A value) => 0;

                public void Host()
                {
                    int Local() => 2;
                    Func<int, int> callback = static value => value + 2;
                    _ = callback(1);
                }
            }
            """;

        var snapshot = await AnalyzeAsync(("Executables.cs", source));

        Assert.Equal("System.Threading.Tasks::Task<System::Int32>", Find(snapshot, "ExecuteAsync").ReturnTypeKey);
        Assert.Null(Find(snapshot, ".ctor").ReturnTypeKey);
        Assert.Null(Find(snapshot, ".cctor").ReturnTypeKey);
        Assert.Equal((int)IndexedAccessibility.NotApplicable, Find(snapshot, ".cctor").Accessibility);
        Assert.True(Find(snapshot, ".cctor").IsStatic);
        Assert.Equal("System::Int32", FindLambda(snapshot).ReturnTypeKey);
        Assert.Equal("int", FindLambda(snapshot).ReturnTypeDisplay);
        var lambdaParameter = Assert.Single(FindLambda(snapshot).Parameters);
        Assert.Equal("System::Int32", lambdaParameter.TypeKey);
        Assert.Equal("int", lambdaParameter.TypeDisplay);
        Assert.Equal((int)IndexedAccessibility.NotApplicable, FindLambda(snapshot).Accessibility);
        Assert.True(FindLambda(snapshot).IsStatic);
        Assert.Equal("System::Int32", Find(snapshot, "get_Value").ReturnTypeKey);
        Assert.Equal(
            "public async Task<int>ExecuteAsync(){await Task.Yield();return 1;}",
            NormalizedText(snapshot, PreferredDeclaration(snapshot, Find(snapshot, "ExecuteAsync"))));
        Assert.NotNull(NormalizedText(snapshot, PreferredDeclaration(snapshot, FindLambda(snapshot))));
        Assert.NotNull(NormalizedText(snapshot, PreferredDeclaration(snapshot, Find(snapshot, "op_Addition"))));
        Assert.NotNull(NormalizedText(snapshot, PreferredDeclaration(snapshot, Find(snapshot, "op_Implicit"))));
        Assert.NotNull(NormalizedText(snapshot, PreferredDeclaration(snapshot, Find(snapshot, "Local"))));

        Assert.Null(typeof(SymbolDeclarationData).GetProperty("NormalizedSource"));
        Assert.Null(typeof(SymbolDeclarationData).GetProperty("NormalizedSourceHash"));
        Assert.Null(typeof(SymbolData).GetProperty("NormalizedSource"));
        Assert.Null(typeof(SymbolData).GetProperty("NormalizedSourceHash"));

        var local = Find(snapshot, "Local");
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.LocalFunction, local.MethodKind);
        Assert.Equal((int)IndexedAccessibility.NotApplicable, local.Accessibility);
        Assert.False(local.IsStatic);
        Assert.Equal("System::Int32", local.ReturnTypeKey);

        var getter = Find(snapshot, "get_Value");
        var setter = Find(snapshot, "set_Value");
        Assert.Equal(IndexedSymbolKind.Method, getter.Kind);
        Assert.Equal(IndexedSymbolKind.Method, setter.Kind);
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.PropertyGet, getter.MethodKind);
        Assert.Equal((int)IndexedAccessibility.Public, getter.Accessibility);
        Assert.False(getter.IsStatic);
        Assert.Equal("System::Int32", getter.ReturnTypeKey);
        var getterDeclaration = PreferredDeclaration(snapshot, getter);
        var setterDeclaration = PreferredDeclaration(snapshot, setter);
        Assert.Equal("get{return 7;}", NormalizedText(snapshot, getterDeclaration));
        Assert.Equal("set{_=value;}", NormalizedText(snapshot, setterDeclaration));
        Assert.Equal("get { return 7; }", source.Substring(
            getterDeclaration.SourceStart,
            getterDeclaration.SourceLength));
        Assert.Equal("set { _ = value; }", source.Substring(
            setterDeclaration.SourceStart,
            setterDeclaration.SourceLength));

        var addition = Find(snapshot, "op_Addition");
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.UserDefinedOperator, addition.MethodKind);
        Assert.Equal((int)IndexedAccessibility.Public, addition.Accessibility);
        Assert.True(addition.IsStatic);
        Assert.Equal("Test::A", addition.ReturnTypeKey);

        var conversion = Find(snapshot, "op_Implicit");
        Assert.Equal((int)Microsoft.CodeAnalysis.MethodKind.Conversion, conversion.MethodKind);
        Assert.Equal((int)IndexedAccessibility.Public, conversion.Accessibility);
        Assert.True(conversion.IsStatic);
        Assert.Equal("System::Int32", conversion.ReturnTypeKey);
    }

    [Fact]
    public async Task AnalyzeAsync_PersistsNormalizedArrayRankTextInDocumentRange()
    {
        const string source = """
            namespace Test;

            public sealed class ArrayHost
            {
                public string?[] Build(string?[] items) => items;
            }
            """;

        var snapshot = await AnalyzeAsync(("ArrayHost.cs", source));

        var build = Find(snapshot, "Build");
        var declaration = PreferredDeclaration(snapshot, build);
        var normalizedSource = NormalizedText(snapshot, declaration);
        Assert.Contains("string?[]Build(string?[]items)", normalizedSource, StringComparison.Ordinal);
        var document = Assert.Single(snapshot.Documents, value => value.Key == declaration.DocumentKey);
        Assert.Equal(HashUtilities.Sha256(document.NormalizedSource), document.NormalizedSourceHash);
    }

    [Fact]
    public async Task AnalyzeAsync_NamesLambdasByImmediateOwnerAndKeepsImmediateContainment()
    {
        const string declarations = """
            using System;

            namespace Test;

            public partial class A
            {
                public Action member1 = () => { };
                public Action member2 = () => { };
                public Func<int> Property { get; } = () => 1;
                public event Action Changed = () => { };
            }
            """;
        const string implementation = """
            using System;

            namespace Test;

            public partial class A
            {
                public void Run()
                {
                    Action first = () => { };
                    Action second = () =>
                    {
                        Action nested = () => Target();
                        nested();
                    };
                }

                private static void Target() { }
            }
            """;

        var snapshot = await AnalyzeAsync(("Declarations.cs", declarations), ("Implementation.cs", implementation));

        var expectedPaths = new[]
        {
            "<initializer:member1>.<lambda#1>",
            "<initializer:member2>.<lambda#1>",
            "<initializer:Property>.<lambda#1>",
            "<initializer:Changed>.<lambda#1>",
            "Run().<lambda#1>",
            "Run().<lambda#2>",
            "Run().<lambda#2>.<lambda#1>",
        };
        var actualPaths = snapshot.Symbols.Values
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Lambda)
            .Select(symbol => symbol.Path!.ExecutableDisplayPath)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPaths.OrderBy(name => name, StringComparer.Ordinal), actualPaths);

        var runSecond = FindLambda(snapshot, "Run().<lambda#2>");
        var nested = FindLambda(snapshot, "Run().<lambda#2>.<lambda#1>");
        Assert.Equal(runSecond.StableKey, nested.ContainingSymbolKey);

        var target = Find(snapshot, "Target");
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == nested.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
    }

    [Fact]
    public async Task AnalyzeAsync_AssignsDocumentRangesToDeclarationsAndCalls()
    {
        const string source = """
            namespace Test;

            public sealed class Host
            {
                public void Run()
                {
                    var value = new Nested();
                    Local();
                    void Local() { Target(); }
                }

                private void Target() { }
                private sealed class Nested { }
            }
            """;

        var snapshot = await AnalyzeAsync(("Ranges.cs", source));
        var run = Find(snapshot, "Run");
        var local = Find(snapshot, "Local");
        var target = Find(snapshot, "Target");
        var runDeclaration = PreferredDeclaration(snapshot, run);
        var localDeclaration = PreferredDeclaration(snapshot, local);

        Assert.Equal("public void Run(){var value=new Nested();Local();void Local(){Target();}}", NormalizedText(snapshot, runDeclaration));
        Assert.Equal("void Local(){Target();}", NormalizedText(snapshot, localDeclaration));
        Assert.InRange(localDeclaration.NormalizedStart, runDeclaration.NormalizedStart + 1, runDeclaration.NormalizedStart + runDeclaration.NormalizedLength - localDeclaration.NormalizedLength);

        var invocation = Assert.Single(snapshot.Calls, call =>
            call.ReferenceKind == ReferenceKind.Invocation && call.CalleeDefinitionKey == target.StableKey);
        Assert.Equal("Target()", NormalizedText(snapshot, invocation));

        var objectCreation = Assert.Single(snapshot.Calls, call => call.ReferenceKind == ReferenceKind.ObjectCreation);
        Assert.Equal("new Nested()", NormalizedText(snapshot, objectCreation));
    }

    [Fact]
    public async Task AnalyzeAsync_InsertingLambdaRenumbersOnlyLaterLambdasOfSameOwner()
    {
        const string before = """
            using System;

            namespace Test;

            public sealed class Owners
            {
                public void SameOwner()
                {
                    Action earlier = () => EarlierMarker();
                    Action later = () => LaterMarker();
                }

                public void OtherOwner()
                {
                    Action first = () => OtherFirstMarker();
                    Action second = () => OtherSecondMarker();
                }

                private static void EarlierMarker() { }
                private static void LaterMarker() { }
                private static void OtherFirstMarker() { }
                private static void OtherSecondMarker() { }
                private static void InsertedMarker() { }
            }
            """;
        const string after = """
            using System;

            namespace Test;

            public sealed class Owners
            {
                public void SameOwner()
                {
                    Action earlier = () => EarlierMarker();
                    Action inserted = () => InsertedMarker();
                    Action later = () => LaterMarker();
                }

                public void OtherOwner()
                {
                    Action first = () => OtherFirstMarker();
                    Action second = () => OtherSecondMarker();
                }

                private static void EarlierMarker() { }
                private static void LaterMarker() { }
                private static void OtherFirstMarker() { }
                private static void OtherSecondMarker() { }
                private static void InsertedMarker() { }
            }
            """;

        var beforeSnapshot = await AnalyzeAsync(("Owners.cs", before));
        var afterSnapshot = await AnalyzeAsync(("Owners.cs", after));

        var beforeEarlier = FindLambdaWithNormalizedSource(beforeSnapshot, "()=>EarlierMarker()");
        var beforeLater = FindLambdaWithNormalizedSource(beforeSnapshot, "()=>LaterMarker()");
        var beforeOtherFirst = FindLambdaWithNormalizedSource(beforeSnapshot, "()=>OtherFirstMarker()");
        var beforeOtherSecond = FindLambdaWithNormalizedSource(beforeSnapshot, "()=>OtherSecondMarker()");
        var afterEarlier = FindLambdaWithNormalizedSource(afterSnapshot, "()=>EarlierMarker()");
        var afterInserted = FindLambdaWithNormalizedSource(afterSnapshot, "()=>InsertedMarker()");
        var afterLater = FindLambdaWithNormalizedSource(afterSnapshot, "()=>LaterMarker()");
        var afterOtherFirst = FindLambdaWithNormalizedSource(afterSnapshot, "()=>OtherFirstMarker()");
        var afterOtherSecond = FindLambdaWithNormalizedSource(afterSnapshot, "()=>OtherSecondMarker()");

        Assert.Equal("SameOwner().<lambda#1>", beforeEarlier.Path!.ExecutableDisplayPath);
        Assert.Equal(beforeEarlier.Path.ExecutableDisplayPath, afterEarlier.Path!.ExecutableDisplayPath);
        Assert.Equal("SameOwner().<lambda#2>", beforeLater.Path!.ExecutableDisplayPath);
        Assert.Equal("SameOwner().<lambda#3>", afterLater.Path!.ExecutableDisplayPath);
        Assert.Equal("SameOwner().<lambda#2>", afterInserted.Path!.ExecutableDisplayPath);
        Assert.Equal(beforeOtherFirst.Path!.ExecutableDisplayPath, afterOtherFirst.Path!.ExecutableDisplayPath);
        Assert.Equal(beforeOtherSecond.Path!.ExecutableDisplayPath, afterOtherSecond.Path!.ExecutableDisplayPath);
    }

    [Fact]
    public async Task AnalyzeAsync_IndexesInitializersInLaterPartialDocument()
    {
        const string firstPart = """
            namespace Test;

            public partial class A
            {
                private static void Target() { }
            }
            """;
        const string laterPart = """
            using System;

            namespace Test;

            public partial class A
            {
                public Action LaterField = () => Target();
                public Func<int> LaterProperty { get; } = () => { Target(); return 1; };
                public event Action LaterEvent = () => Target();
            }
            """;

        var snapshot = await AnalyzeAsync(("First.cs", firstPart), ("Later.cs", laterPart));

        var expectedPaths = new[]
        {
            "<initializer:LaterField>.<lambda#1>",
            "<initializer:LaterProperty>.<lambda#1>",
            "<initializer:LaterEvent>.<lambda#1>",
        };
        var actualPaths = snapshot.Symbols.Values
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Lambda)
            .Select(symbol => symbol.Path!.ExecutableDisplayPath)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPaths.OrderBy(name => name, StringComparer.Ordinal), actualPaths);

        var eventLambda = FindLambda(snapshot, "<initializer:LaterEvent>.<lambda#1>");
        var target = Find(snapshot, "Target");
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == eventLambda.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
    }

    [Fact]
    public async Task AnalyzeAsync_IndexesExpressionBodiedPropertyAndIndexerGettersAsLambdaOwners()
    {
        const string source = """
            using System;

            namespace Test;

            public sealed class A
            {
                public Func<int> Factory => () => Target();
                public Func<int> this[int index] => () => Target();

                private static int Target() => 1;
            }
            """;

        var snapshot = await AnalyzeAsync(("ExpressionBodiedMembers.cs", source));

        var factoryGetter = Find(snapshot, "get_Factory");
        var indexerGetter = Find(snapshot, "get_Item");
        Assert.Equal(IndexedSymbolKind.Method, factoryGetter.Kind);
        Assert.Equal(IndexedSymbolKind.Method, indexerGetter.Kind);
        Assert.Equal("System::Func<System::Int32>", factoryGetter.ReturnTypeKey);
        Assert.Equal("System::Func<System::Int32>", indexerGetter.ReturnTypeKey);
        var factoryDeclaration = PreferredDeclaration(snapshot, factoryGetter);
        var indexerDeclaration = PreferredDeclaration(snapshot, indexerGetter);
        Assert.Equal("public Func<int>Factory=>()=>Target();", NormalizedText(snapshot, factoryDeclaration));
        Assert.Equal("public Func<int>this[int index]=>()=>Target();", NormalizedText(snapshot, indexerDeclaration));
        Assert.Equal(
            "public Func<int> Factory => () => Target();",
            source.Substring(factoryDeclaration.SourceStart, factoryDeclaration.SourceLength));
        Assert.Equal(
            "public Func<int> this[int index] => () => Target();",
            source.Substring(indexerDeclaration.SourceStart, indexerDeclaration.SourceLength));

        var factoryLambda = FindLambdaOwnedBy(snapshot, factoryGetter);
        var indexerLambda = FindLambdaOwnedBy(snapshot, indexerGetter);
        Assert.Equal("()=>Target()", NormalizedText(snapshot, PreferredDeclaration(snapshot, factoryLambda)));
        Assert.Equal("()=>Target()", NormalizedText(snapshot, PreferredDeclaration(snapshot, indexerLambda)));
        Assert.Equal("System::Int32", factoryLambda.ReturnTypeKey);
        Assert.Equal("System::Int32", indexerLambda.ReturnTypeKey);
        Assert.Equal("int", factoryLambda.ReturnTypeDisplay);
        Assert.Equal("int", indexerLambda.ReturnTypeDisplay);
        Assert.Equal("[get:Factory]().<lambda#1>", factoryLambda.Path!.ExecutableDisplayPath);
        Assert.Equal("[get:Item](int).<lambda#1>", indexerLambda.Path!.ExecutableDisplayPath);
        Assert.Equal(factoryGetter.StableKey, factoryLambda.ContainingSymbolKey);
        Assert.Equal(indexerGetter.StableKey, indexerLambda.ContainingSymbolKey);

        var target = Find(snapshot, "Target");
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == factoryLambda.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == indexerLambda.StableKey &&
            call.CalleeDefinitionKey == target.StableKey);
    }

    private static SymbolData Find(IndexSnapshot snapshot, string name) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.Name == name);

    private static SymbolData FindLambda(IndexSnapshot snapshot) =>
        Assert.Single(snapshot.Symbols.Values, symbol => symbol.Kind == IndexedSymbolKind.Lambda);

    private static SymbolData FindLambda(IndexSnapshot snapshot, string executableDisplayPath) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda &&
            symbol.Path?.ExecutableDisplayPath == executableDisplayPath);

    private static SymbolData FindLambdaWithNormalizedSource(IndexSnapshot snapshot, string normalizedSource) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda &&
            NormalizedText(snapshot, PreferredDeclaration(snapshot, symbol)) == normalizedSource);

    private static SymbolDeclarationData PreferredDeclaration(IndexSnapshot snapshot, SymbolData symbol) =>
        snapshot.Declarations[symbol.PreferredDeclarationKey!];

    private static string NormalizedText(IndexSnapshot snapshot, SymbolDeclarationData declaration)
    {
        var document = Assert.Single(snapshot.Documents, value => value.Key == declaration.DocumentKey);
        Assert.True(declaration.NormalizedStart >= 0);
        Assert.True(declaration.NormalizedLength > 0);
        Assert.True(declaration.NormalizedStart <= document.NormalizedSource.Length - declaration.NormalizedLength);
        return document.NormalizedSource.AsSpan(
            declaration.NormalizedStart,
            declaration.NormalizedLength).ToString();
    }

    private static string NormalizedText(IndexSnapshot snapshot, CallData call)
    {
        var document = Assert.Single(snapshot.Documents, value => value.Key == call.DocumentKey);
        Assert.True(call.NormalizedStart >= 0);
        Assert.True(call.NormalizedLength > 0);
        Assert.True(call.NormalizedStart <= document.NormalizedSource.Length - call.NormalizedLength);
        return document.NormalizedSource.AsSpan(call.NormalizedStart, call.NormalizedLength).ToString();
    }

    private static SymbolData FindLambdaOwnedBy(IndexSnapshot snapshot, SymbolData owner) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda && symbol.ContainingSymbolKey == owner.StableKey);

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
