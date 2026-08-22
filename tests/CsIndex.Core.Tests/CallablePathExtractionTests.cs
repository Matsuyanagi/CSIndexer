using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class CallablePathExtractionTests
{
    [Fact]
    public async Task AnalyzeAsync_ExtractsCompleteCanonicalCallableCatalogAndImmediatePaths()
    {
        const string topLevelSource = """
            using System;

            void Local()
            {
                Action callback = delegate { };
                callback();
            }

            Local();
            """;
        const string catalogSource = """
            using System;
            using System.Threading.Tasks;

            namespace Game;

            public interface IContract
            {
                void Bodyless();
                T Map<T>(T value);
                int Value { get; set; }
            }

            public abstract partial class BodylessCases
            {
                public abstract void AbstractMember();
                [System.Runtime.InteropServices.DllImport("native")]
                public static extern void ExternalMember();
                partial void DefinitionOnly();
            }

            public sealed class Primary(int value)
            {
                public int Value => value;
            }

            public readonly struct Number
            {
                public Number(int value) => Value = value;
                public int Value { get; }

                public static Number operator +(Number left, Number right) => left;
                public static Number operator checked +(Number left, Number right) => left;
                public static implicit operator int(Number value) => value.Value;
            }

            public readonly struct CheckedNumber
            {
                public static explicit operator int(CheckedNumber value) => 0;
                public static explicit operator checked int(CheckedNumber value) => 0;
            }

            public sealed class Host : IContract, IDisposable
            {
                public Host(int number, string text) { }
                static Host() { }
                ~Host() { }

                public Func<int> Factory = () => 1;
                public Func<int> PropertyFactory { get; } = () => 2;
                public event Action Initialized = delegate { };
                public event EventHandler? FieldLike;

                public string Name { get; init; } = string.Empty;

                public int Value
                {
                    get { return 1; }
                    set { _ = value; }
                }

                public int Expression => 1;

                public string this[int index]
                {
                    get => index.ToString();
                    set { _ = value; }
                }

                public event EventHandler Changed
                {
                    add { }
                    remove { }
                }

                void IContract.Bodyless() { }

                T IContract.Map<T>(T value) => value;

                int IContract.Value
                {
                    get => Value;
                    set => Value = value;
                }

                void IDisposable.Dispose() { }

                public void Run(int number)
                {
                    void Local(string text)
                    {
                        Action leaf = () => _ = text;
                        leaf();
                    }

                    Action owner = () =>
                    {
                        void InsideLambda() { }
                        Action nested = delegate { InsideLambda(); };
                        nested();
                    };

                    Local(number.ToString());
                    owner();
                }

                public void LambdaForms()
                {
                    Func<int, int> simple = value => value;
                    Func<int, int> parenthesized = (value) => value;
                    _ = simple(1) + parenthesized(2);
                }
            }

            public sealed class CustomIndexer
            {
                [System.Runtime.CompilerServices.IndexerName("Lookup")]
                public string this[string key]
                {
                    get => key;
                    set { }
                }
            }

            """;
        const string nestedTypeSource = """
            namespace Game.Nested;

            public sealed class Outer<T>
            {
                public sealed class Inner<U>
                {
                    public void Pair(T left, U right) { }
                }
            }
            """;
        const string keywordSource = """
            namespace Game.@namespace;

            public interface @class
            {
                void @event();
            }

            public sealed class KeywordImplementation : @class
            {
                void @class.@event() { }
            }
            """;

        var snapshot = await AnalyzeAsync(
            ("TopLevel.cs", topLevelSource),
            ("Catalog.cs", catalogSource),
            ("NestedTypes.cs", nestedTypeSource),
            ("Keywords.cs", keywordSource));

        Assert.Equal(6, Enum.GetValues<CallablePathSegmentKind>().Length);
        Assert.Equal(1, (int)CallablePathSegmentKind.Named);
        Assert.Equal(2, (int)CallablePathSegmentKind.Special);
        Assert.Equal(3, (int)CallablePathSegmentKind.Lambda);
        Assert.Equal(4, (int)CallablePathSegmentKind.AnonymousMethod);
        Assert.Equal(5, (int)CallablePathSegmentKind.Initializer);
        Assert.Equal(6, (int)CallablePathSegmentKind.TopLevelStatements);

        AssertSegment(snapshot, "[constructor](int,string)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[static-constructor]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[destructor]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[operator:+](Game.Number,Game.Number)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[checked-operator:+](Game.Number,Game.Number)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[conversion:implicit:int](Game.Number)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[checked-conversion:explicit:int](Game.CheckedNumber)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[get:Item](int)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[set:Item](int,string)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[init:Name](string)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[add:Changed](System.EventHandler)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[remove:Changed](System.EventHandler)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[explicit:System.IDisposable.Dispose]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[explicit:Game.IContract.Bodyless]()", CallablePathSegmentKind.Special);
        var explicitGeneric = AssertSegment(
            snapshot,
            "[explicit:Game.IContract.Map]<T>(T)",
            CallablePathSegmentKind.Special);
        Assert.Contains("^0", explicitGeneric.Path!.SegmentIdentity, StringComparison.Ordinal);
        AssertSegment(snapshot, "[get:Game.IContract.Value]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[set:Game.IContract.Value](int)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[explicit:Game.@namespace.@class.@event]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[get:Lookup](string)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[set:Lookup](string,string)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "Host", "[get:Value]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "Host", "[set:Value](int)", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[get:Name]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[get:Expression]()", CallablePathSegmentKind.Special);

        AssertPath(snapshot, "Bodyless()");
        AssertPath(snapshot, "AbstractMember()");
        AssertPath(snapshot, "ExternalMember()");
        AssertPath(snapshot, "DefinitionOnly()");
        AssertPath(snapshot, "LambdaForms().<lambda#1>");
        AssertPath(snapshot, "LambdaForms().<lambda#2>");

        AssertPath(snapshot, "<initializer:Factory>.<lambda#1>");
        AssertPath(snapshot, "<initializer:PropertyFactory>.<lambda#1>");
        AssertPath(snapshot, "<initializer:Initialized>.<anonymous-method#1>");
        AssertPath(snapshot, "<top-level-statements>.Local().<anonymous-method#1>");
        AssertPath(snapshot, "Run(int).Local(string).<lambda#1>");
        Assert.DoesNotContain(snapshot.Symbols.Values, symbol =>
            symbol.Path?.ExecutableDisplayPath.Contains("Run(int)::Local(string)", StringComparison.Ordinal) == true);

        var run = FindPath(snapshot, "Run(int)");
        var local = FindPath(snapshot, "Run(int).Local(string)");
        var localLambda = FindPath(snapshot, "Run(int).Local(string).<lambda#1>");
        var ownerLambda = FindPath(snapshot, "Run(int).<lambda#1>");
        var insideLambda = FindPath(snapshot, "Run(int).<lambda#1>.InsideLambda()");
        var nestedAnonymous = FindPath(snapshot, "Run(int).<lambda#1>.<anonymous-method#1>");
        var runType = snapshot.Symbols[Assert.IsType<string>(run.ContainingSymbolKey)];
        Assert.Equal(IndexedSymbolKind.Type, runType.Kind);
        Assert.Equal("Host", runType.TypeSimpleName);
        Assert.Equal(run.StableKey, local.ContainingSymbolKey);
        Assert.Equal(local.StableKey, localLambda.ContainingSymbolKey);
        Assert.Equal(run.StableKey, ownerLambda.ContainingSymbolKey);
        Assert.Equal(ownerLambda.StableKey, insideLambda.ContainingSymbolKey);
        Assert.Equal(ownerLambda.StableKey, nestedAnonymous.ContainingSymbolKey);

        var topLevel = FindPath(snapshot, "<top-level-statements>");
        var topLevelLocal = FindPath(snapshot, "<top-level-statements>.Local()");
        var topLevelAnonymous = FindPath(snapshot, "<top-level-statements>.Local().<anonymous-method#1>");
        Assert.Equal(topLevel.StableKey, topLevelLocal.ContainingSymbolKey);
        Assert.Equal(topLevelLocal.StableKey, topLevelAnonymous.ContainingSymbolKey);
        var programType = snapshot.Symbols[Assert.IsType<string>(topLevel.ContainingSymbolKey)];
        Assert.Equal(IndexedSymbolKind.Type, programType.Kind);
        Assert.Equal("Program", programType.TypeSimpleName);

        var factoryInitializer = FindPath(snapshot, "<initializer:Factory>");
        var factoryType = snapshot.Symbols[Assert.IsType<string>(factoryInitializer.ContainingSymbolKey)];
        Assert.Equal(IndexedSymbolKind.Type, factoryType.Kind);
        Assert.Equal("Host", factoryType.TypeSimpleName);

        var pair = FindPath(snapshot, "Pair(T,U)");
        var pairPath = Assert.IsType<SymbolPathData>(pair.Path);
        Assert.Equal("Game.Nested", pairPath.NamespacePath);
        Assert.Equal("Outer<T>.Inner<U>", pairPath.TypeDisplayPath);
        Assert.Equal("Outer`1.Inner`1", pairPath.TypeIdentityPath);
        Assert.Contains("!0", pairPath.SegmentIdentity, StringComparison.Ordinal);
        Assert.Contains("!1", pairPath.SegmentIdentity, StringComparison.Ordinal);

        Assert.All(
            snapshot.Symbols.Values.Where(symbol => symbol.Kind == IndexedSymbolKind.Type),
            symbol =>
            {
                var typePath = Assert.IsType<SymbolPathData>(symbol.Path);
                Assert.Equal(symbol.NamespaceName, typePath.NamespacePath);
                Assert.False(string.IsNullOrEmpty(typePath.TypeDisplayPath));
                Assert.False(string.IsNullOrEmpty(typePath.TypeIdentityPath));
                Assert.Equal(string.Empty, typePath.ExecutableDisplayPath);
                Assert.Equal(string.Empty, typePath.ExecutableIdentityPath);
                Assert.Equal(string.Empty, typePath.SegmentDisplay);
                Assert.Equal(string.Empty, typePath.SegmentIdentity);
                Assert.Equal(CallablePathSegmentKind.Named, typePath.SegmentKind);
            });
        Assert.All(
            snapshot.Symbols.Values.Where(symbol => symbol.Kind != IndexedSymbolKind.Type),
            symbol => Assert.IsType<SymbolPathData>(symbol.Path));
    }

    [Fact]
    public async Task AnalyzeAsync_MapsExactCheckedConversionExampleInAValidPairedDeclaration()
    {
        const string source = """
            namespace Game;

            public readonly struct Number
            {
                public static explicit operator int(Number value) => 0;
                public static explicit operator checked int(Number value) => 0;
            }
            """;

        var snapshot = await AnalyzeAsync(("CheckedConversion.cs", source));

        Assert.All(snapshot.CompilationSummaries, summary => Assert.Equal(0, summary.Errors));
        AssertSegment(
            snapshot,
            "[checked-conversion:explicit:int](Game.Number)",
            CallablePathSegmentKind.Special);
    }

    [Fact]
    public async Task AnalyzeAsync_NumbersMixedAnonymousFunctionsPerImmediateOwnerInSourceOrder()
    {
        const string source = """
            using System;

            public sealed class Ordinals
            {
                public void Run()
                {
                    Action first = () => { };
                    Action second = delegate { };
                    Action third = () =>
                    {
                        Action nestedFirst = delegate { };
                        Action nestedSecond = () => { };
                    };
                }
            }
            """;

        var snapshot = await AnalyzeAsync(("Ordinals.cs", source));

        AssertPath(snapshot, "Run().<lambda#1>");
        AssertPath(snapshot, "Run().<anonymous-method#2>");
        AssertPath(snapshot, "Run().<lambda#3>");
        AssertPath(snapshot, "Run().<lambda#3>.<anonymous-method#1>");
        AssertPath(snapshot, "Run().<lambda#3>.<lambda#2>");
    }

    [Fact]
    public async Task AnalyzeAsync_InsertingAnonymousFunctionRenumbersOnlyLaterSiblingsOfItsImmediateOwner()
    {
        const string before = """
            using System;

            public sealed class Ordinals
            {
                public void Changed()
                {
                    Action first = () => FirstMarker();
                    Action later = delegate { LaterMarker(); };
                }

                public void Unchanged()
                {
                    Action first = delegate { OtherFirstMarker(); };
                    Action second = () => OtherSecondMarker();
                }

                private static void FirstMarker() { }
                private static void InsertedMarker() { }
                private static void LaterMarker() { }
                private static void OtherFirstMarker() { }
                private static void OtherSecondMarker() { }
            }
            """;
        const string after = """
            using System;

            public sealed class Ordinals
            {
                public void Changed()
                {
                    Action first = () => FirstMarker();
                    Action inserted = delegate { InsertedMarker(); };
                    Action later = delegate { LaterMarker(); };
                }

                public void Unchanged()
                {
                    Action first = delegate { OtherFirstMarker(); };
                    Action second = () => OtherSecondMarker();
                }

                private static void FirstMarker() { }
                private static void InsertedMarker() { }
                private static void LaterMarker() { }
                private static void OtherFirstMarker() { }
                private static void OtherSecondMarker() { }
            }
            """;

        var beforeSnapshot = await AnalyzeAsync(("Ordinals.cs", before));
        var afterSnapshot = await AnalyzeAsync(("Ordinals.cs", after));

        var beforeFirst = FindNormalizedSource(beforeSnapshot, "()=>FirstMarker()");
        var beforeLater = FindNormalizedSource(beforeSnapshot, "delegate{LaterMarker();}");
        var beforeOtherFirst = FindNormalizedSource(beforeSnapshot, "delegate{OtherFirstMarker();}");
        var beforeOtherSecond = FindNormalizedSource(beforeSnapshot, "()=>OtherSecondMarker()");
        var afterFirst = FindNormalizedSource(afterSnapshot, "()=>FirstMarker()");
        var afterInserted = FindNormalizedSource(afterSnapshot, "delegate{InsertedMarker();}");
        var afterLater = FindNormalizedSource(afterSnapshot, "delegate{LaterMarker();}");
        var afterOtherFirst = FindNormalizedSource(afterSnapshot, "delegate{OtherFirstMarker();}");
        var afterOtherSecond = FindNormalizedSource(afterSnapshot, "()=>OtherSecondMarker()");

        Assert.Equal("Changed().<lambda#1>", beforeFirst.Path!.ExecutableDisplayPath);
        Assert.Equal(beforeFirst.Path.ExecutableDisplayPath, afterFirst.Path!.ExecutableDisplayPath);
        Assert.Equal("Changed().<anonymous-method#2>", beforeLater.Path!.ExecutableDisplayPath);
        Assert.Equal("Changed().<anonymous-method#2>", afterInserted.Path!.ExecutableDisplayPath);
        Assert.Equal("Changed().<anonymous-method#3>", afterLater.Path!.ExecutableDisplayPath);
        Assert.Equal(beforeOtherFirst.Path!.ExecutableDisplayPath, afterOtherFirst.Path!.ExecutableDisplayPath);
        Assert.Equal(beforeOtherSecond.Path!.ExecutableDisplayPath, afterOtherSecond.Path!.ExecutableDisplayPath);
    }

    [Fact]
    public async Task AnalyzeAsync_ExcludesCompilerOnlyCallablesButKeepsWrittenAutoAccessorsAndPrimaryConstructor()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            public sealed class ImplicitConstructor
            {
            }

            public sealed class WrittenMembers
            {
                public event Action? FieldLike;
                public int Auto { get; set; }
                public object CreateImplicitConstructor() => new ImplicitConstructor();

                public async Task<int> AsyncMethod()
                {
                    await Task.Yield();
                    return 1;
                }

                public IEnumerable<int> Iterator()
                {
                    yield return 1;
                }

                public void ClosureHost()
                {
                    var value = 1;
                    Action closure = () => _ = value;
                    closure();
                }
            }

            public sealed class PrimaryConstructor(int value)
            {
                public int Value => value;
            }

            public record PositionalRecord(int Value);
            """;

        var snapshot = await AnalyzeAsync(("Exclusions.cs", source));

        Assert.DoesNotContain(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method &&
            symbol.TypeSimpleName == "ImplicitConstructor" &&
            symbol.MethodKind == (int)Microsoft.CodeAnalysis.MethodKind.Constructor);
        var creator = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "WrittenMembers" &&
            symbol.Name == "CreateImplicitConstructor");
        var implicitConstructorCall = Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == creator.StableKey &&
            call.ReferenceKind == ReferenceKind.ObjectCreation);
        Assert.Equal("new ImplicitConstructor()", implicitConstructorCall.UnresolvedName);
        Assert.DoesNotContain(implicitConstructorCall.CalleeSymbolKey, snapshot.Symbols.Keys);
        Assert.DoesNotContain(implicitConstructorCall.CalleeDefinitionKey, snapshot.Symbols.Keys);
        Assert.DoesNotContain(snapshot.Symbols.Values, symbol =>
            symbol.Name is "add_FieldLike" or "remove_FieldLike");
        Assert.DoesNotContain(snapshot.Symbols.Values, symbol =>
            symbol.Name.Contains("BackingField", StringComparison.Ordinal) ||
            symbol.Name.Contains("MoveNext", StringComparison.Ordinal) ||
            symbol.Name.Contains("DisplayClass", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "PositionalRecord" &&
            symbol.Name is ("Equals" or "GetHashCode" or "PrintMembers" or "<Clone>$" or "get_Value" or "set_Value"));

        AssertSegment(snapshot, "[get:Auto]()", CallablePathSegmentKind.Special);
        AssertSegment(snapshot, "[set:Auto](int)", CallablePathSegmentKind.Special);
        var primaryConstructor = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "PrimaryConstructor" &&
            symbol.Path?.SegmentDisplay == "[constructor](int)");
        Assert.NotNull(PreferredDeclaration(snapshot, primaryConstructor).DocumentKey);
        Assert.True(PreferredDeclaration(snapshot, primaryConstructor).SourceLength > 0);
        var recordPrimaryConstructor = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "PositionalRecord" &&
            symbol.Path?.SegmentDisplay == "[constructor](int)");
        Assert.NotNull(PreferredDeclaration(snapshot, recordPrimaryConstructor).DocumentKey);
    }

    [Fact]
    public async Task AnalyzeAsync_RetainsExactSourceTokensForEveryResolvedCallShape()
    {
        const string source = """
            using System;

            public sealed class TokenTarget
            {
            }

            public sealed class TokenCaller
            {
                private static void Target() { }

                public void Run()
                {
                    Target();
                    _ = new TokenTarget();
                    Action callback = Target;
                    _ = callback;
                    _ = nameof(Target);
                }
            }
            """;

        var snapshot = await AnalyzeAsync(("ResolvedCallTokens.cs", source));
        var caller = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "TokenCaller" && symbol.Name == "Run");
        var calls = snapshot.Calls.Where(call => call.CallerSymbolKey == caller.StableKey).ToArray();

        Assert.Equal("Target", Assert.Single(calls, call =>
            call.ReferenceKind == ReferenceKind.Invocation).UnresolvedName);
        Assert.Equal("new TokenTarget()", Assert.Single(calls, call =>
            call.ReferenceKind == ReferenceKind.ObjectCreation).UnresolvedName);
        Assert.Equal("Target", Assert.Single(calls, call =>
            call.ReferenceKind == ReferenceKind.DelegateCreation).UnresolvedName);
        Assert.Equal("Target", Assert.Single(calls, call =>
            call.ReferenceKind == ReferenceKind.NameOf).UnresolvedName);
    }

    [Fact]
    public async Task AnalyzeAsync_AttachesPrimaryConstructorBaseArgumentsToTheConstructorOwner()
    {
        const string source = """
            using System;

            public class Base(int direct, Func<int> lambda, Func<int> anonymous)
            {
            }

            public sealed class Derived()
                : Base(Create(), () => Helper(), delegate { return Helper(); })
            {
                private static int Create() => 1;
                private static int Helper() => 2;
            }
            """;

        var snapshot = await AnalyzeAsync(("PrimaryConstructorBaseArguments.cs", source));

        Assert.All(snapshot.CompilationSummaries, summary => Assert.Equal(0, summary.Errors));
        var constructor = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "Derived" &&
            symbol.Path?.ExecutableDisplayPath == "[constructor]()");
        var lambda = FindPath(snapshot, "[constructor]().<lambda#1>");
        var anonymous = FindPath(snapshot, "[constructor]().<anonymous-method#2>");
        Assert.Equal(constructor.StableKey, lambda.ContainingSymbolKey);
        Assert.Equal(constructor.StableKey, anonymous.ContainingSymbolKey);

        var create = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "Derived" && symbol.Name == "Create");
        var helper = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "Derived" && symbol.Name == "Helper");
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == constructor.StableKey &&
            call.CalleeDefinitionKey == create.StableKey);
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == lambda.StableKey &&
            call.CalleeDefinitionKey == helper.StableKey);
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == anonymous.StableKey &&
            call.CalleeDefinitionKey == helper.StableKey);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotAttachPrimaryConstructorDeclarationMetadataToTheConstructorOwner()
    {
        const string source = """
            using System;

            public sealed class LabelAttribute(string text) : Attribute
            {
                public string Text { get; } = text;
            }

            public static class DeclarationSymbols
            {
                public static int Helper() => 1;
            }

            public class Base(Func<int> factory)
            {
            }

            [Label(nameof(DeclarationSymbols.Helper))]
            public sealed class Derived(string description = nameof(DeclarationSymbols.Helper))
                : Base(() => DeclarationSymbols.Helper())
            {
                public string Description { get; } = description;
            }
            """;

        var snapshot = await AnalyzeAsync(("PrimaryConstructorDeclarationMetadata.cs", source));

        Assert.All(snapshot.CompilationSummaries, summary => Assert.Equal(0, summary.Errors));
        var constructor = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "Derived" &&
            symbol.Path?.ExecutableDisplayPath == "[constructor](string)");
        var lambda = FindPath(snapshot, "[constructor](string).<lambda#1>");
        var helper = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == "DeclarationSymbols" && symbol.Name == "Helper");
        Assert.Equal(constructor.StableKey, lambda.ContainingSymbolKey);
        Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == lambda.StableKey &&
            call.CalleeDefinitionKey == helper.StableKey);

        const string referencedExpression = "DeclarationSymbols.Helper";
        var attributeReferenceStart = source.IndexOf(referencedExpression, StringComparison.Ordinal);
        var defaultReferenceStart = source.IndexOf(
            referencedExpression,
            attributeReferenceStart + referencedExpression.Length,
            StringComparison.Ordinal);
        Assert.True(attributeReferenceStart >= 0);
        Assert.True(defaultReferenceStart >= 0);
        Assert.DoesNotContain(snapshot.Calls, call =>
            call.ReferenceKind == ReferenceKind.NameOf &&
            call.SourceStart == attributeReferenceStart);
        Assert.DoesNotContain(snapshot.Calls, call =>
            call.ReferenceKind == ReferenceKind.NameOf &&
            call.SourceStart == defaultReferenceStart);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesLogicalKeysForConstructedAndReducedTargets()
    {
        const string source = """
            public static class Extensions
            {
                public static T Echo<T>(this T value) => value;
            }

            public sealed class TargetHost
            {
                public T Member<T>(T value) => value;

                public void Run()
                {
                    _ = Member(1);

                    T Local<T>(T value) => value;
                    _ = Local(2);
                    _ = 3.Echo();
                }
            }
            """;

        var snapshot = await AnalyzeAsync(("ActualTargets.cs", source));

        Assert.All(snapshot.CompilationSummaries, summary => Assert.Equal(0, summary.Errors));
        var memberDefinition = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Name == "Member");
        var localDefinition = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Name == "Local");
        var extensionDefinition = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Name == "Echo");
        var run = FindPath(snapshot, "Run()");

        AssertLogicalTarget(snapshot, memberDefinition, "Member<T>(T)");
        AssertLogicalTarget(snapshot, localDefinition, "Run().Local<T>(T)");
        AssertLogicalTarget(snapshot, extensionDefinition, "Echo<T>(T)");
        Assert.Equal(run.StableKey, localDefinition.ContainingSymbolKey);

        Assert.DoesNotContain(snapshot.Symbols.Values, symbol =>
            symbol.StableKey.Contains("|constructed:", StringComparison.Ordinal) ||
            symbol.StableKey.Contains("|reduced:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_NormalizesInitializerDisplayIdentifiersSemantically()
    {
        const string source = """
            using System;

            public sealed class InitializerNames
            {
                public static Func<int> @Factory = () => 1;
                public static Func<int> \u0050roperty { get; } = () => 2;
                public static Func<int> @class { get; } = () => 3;
            }
            """;

        var snapshot = await AnalyzeAsync(("InitializerNames.cs", source));

        Assert.All(snapshot.CompilationSummaries, summary => Assert.Equal(0, summary.Errors));
        var factory = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Initializer && symbol.Name == "<initializer:Factory>");
        var property = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Initializer && symbol.Name == "<initializer:Property>");
        var keyword = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Initializer && symbol.Name == "<initializer:class>");
        Assert.Equal("<initializer:Factory>", factory.Path?.SegmentDisplay);
        Assert.Equal("<initializer:Factory>", factory.Path?.SegmentIdentity);
        Assert.Equal("<initializer:Property>", property.Path?.SegmentDisplay);
        Assert.Equal("<initializer:Property>", property.Path?.SegmentIdentity);
        Assert.Equal("<initializer:@class>", keyword.Path?.SegmentDisplay);
        Assert.Equal("<initializer:class>", keyword.Path?.SegmentIdentity);
    }

    private static void AssertLogicalTarget(
        IndexSnapshot snapshot,
        SymbolData definition,
        string expectedExecutableDisplayPath)
    {
        var call = Assert.Single(snapshot.Calls, candidate =>
            candidate.CalleeDefinitionKey == definition.StableKey);
        Assert.Equal(definition.StableKey, call.CalleeSymbolKey);
        Assert.Equal(definition.StableKey, call.CalleeDefinitionKey);
        Assert.DoesNotContain("|constructed:", call.CalleeSymbolKey!, StringComparison.Ordinal);
        Assert.DoesNotContain("|reduced:", call.CalleeSymbolKey!, StringComparison.Ordinal);
        Assert.Equal(expectedExecutableDisplayPath, definition.Path?.ExecutableDisplayPath);
    }

    private static SymbolData AssertSegment(
        IndexSnapshot snapshot,
        string segmentDisplay,
        CallablePathSegmentKind segmentKind)
    {
        var symbol = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Path?.SegmentDisplay == segmentDisplay);
        Assert.Equal(segmentKind, symbol.Path!.SegmentKind);
        Assert.Equal(segmentDisplay, symbol.Path.ExecutableDisplayPath);
        return symbol;
    }

    private static SymbolData AssertSegment(
        IndexSnapshot snapshot,
        string typeSimpleName,
        string segmentDisplay,
        CallablePathSegmentKind segmentKind)
    {
        var symbol = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.TypeSimpleName == typeSimpleName &&
            symbol.Path?.SegmentDisplay == segmentDisplay);
        Assert.Equal(segmentKind, symbol.Path!.SegmentKind);
        Assert.Equal(segmentDisplay, symbol.Path.ExecutableDisplayPath);
        return symbol;
    }

    private static void AssertPath(IndexSnapshot snapshot, string executableDisplayPath) =>
        _ = FindPath(snapshot, executableDisplayPath);

    private static SymbolData FindPath(IndexSnapshot snapshot, string executableDisplayPath) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Path?.ExecutableDisplayPath == executableDisplayPath);

    private static SymbolData FindNormalizedSource(IndexSnapshot snapshot, string normalizedSource) =>
        Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Lambda &&
            symbol.PreferredDeclarationKey is { } declarationKey &&
            snapshot.Declarations.TryGetValue(declarationKey, out var declaration) &&
            declaration.NormalizedSource == normalizedSource);

    private static SymbolDeclarationData PreferredDeclaration(IndexSnapshot snapshot, SymbolData symbol) =>
        snapshot.Declarations[symbol.PreferredDeclarationKey!];

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
