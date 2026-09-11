using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsIndex.Core.Tests;

public sealed class SymbolPathFormatterTests
{
    private static readonly SymbolPathData NestedMethod = new(
        NamespacePath: "Game.Core",
        TypeDisplayPath: "Outer<T>.Inner<U>",
        TypeIdentityPath: "Outer`1.Inner`1",
        ExecutableDisplayPath: "Run(System.Guid).<lambda#1>",
        ExecutableIdentityPath: "Run(System::Guid).<lambda#1>",
        SegmentDisplay: "<lambda#1>",
        SegmentIdentity: "<lambda#1>",
        SegmentKind: CallablePathSegmentKind.Lambda);

    [Fact]
    public void Format_UsesExactNestedGenericAndLocalLambdaExamples()
    {
        var path = new SymbolPathData(
            NamespacePath: "Game.Core",
            TypeDisplayPath: "Player.Inventory",
            TypeIdentityPath: "Player.Inventory",
            ExecutableDisplayPath: "Load(int).Validate().<lambda#1>",
            ExecutableIdentityPath: "Load(int).Validate().<lambda#1>",
            SegmentDisplay: "<lambda#1>",
            SegmentIdentity: "<lambda#1>",
            SegmentKind: CallablePathSegmentKind.Lambda);
        var guidPath = path with
        {
            ExecutableDisplayPath = "Load(System.Guid)",
            ExecutableIdentityPath = "Load(System::Guid)",
            SegmentDisplay = "Load(System.Guid)",
            SegmentIdentity = "Load(System.Guid)",
            SegmentKind = CallablePathSegmentKind.Named,
        };
        var formatter = new SymbolPathFormatter();

        Assert.Equal(
            "Game.Core.Player.Inventory::Load(int).Validate().<lambda#1>",
            formatter.Format(path, new(SymbolPathStyle.CSharp)));
        Assert.Equal(
            "Game.Core::Player.Inventory::Load(int).Validate().<lambda#1>",
            formatter.Format(path, new(SymbolPathStyle.Explicit)));
        Assert.Equal(
            "Player.Inventory::Load(Guid)",
            formatter.Format(guidPath, new(SymbolPathStyle.CSharp, ShortNames: true)));
        Assert.Equal(
            "**::Player.Inventory::Load(Guid)",
            formatter.Format(guidPath, new(SymbolPathStyle.Explicit, ShortNames: true)));
    }

    [Fact]
    public void Format_CoversEveryConcreteCallableCategoryWithCanonicalPayloads()
    {
        var cases = new Dictionary<string, (string Display, string Identity, string ExpectedShort)>(StringComparer.Ordinal)
        {
            ["[constructor](int,string)"] = (
                "[constructor](int,string)",
                "[constructor](System::Int32,System::String)",
                "[constructor](int,string)"),
            ["[static-constructor]()"] = ("[static-constructor]()", "[static-constructor]()", "[static-constructor]()"),
            ["[destructor]()"] = ("[destructor]()", "[destructor]()", "[destructor]()"),
            ["[operator:+](Game.Number,Game.Number)"] = (
                "[operator:+](Game.Number,Game.Number)",
                "[operator:+](Game::Number,Game::Number)",
                "[operator:+](Number,Number)"),
            ["[checked-operator:+](Game.Number,Game.Number)"] = (
                "[checked-operator:+](Game.Number,Game.Number)",
                "[checked-operator:+](Game::Number,Game::Number)",
                "[checked-operator:+](Number,Number)"),
            ["[operator:<](Game.Number,Game.Number)"] = (
                "[operator:<](Game.Number,Game.Number)",
                "[operator:<](Game::Number,Game::Number)",
                "[operator:<](Number,Number)"),
            ["[operator:>](Game.Number,Game.Number)"] = (
                "[operator:>](Game.Number,Game.Number)",
                "[operator:>](Game::Number,Game::Number)",
                "[operator:>](Number,Number)"),
            ["[operator:<<](Game.Number,Game.Number)"] = (
                "[operator:<<](Game.Number,Game.Number)",
                "[operator:<<](Game::Number,Game::Number)",
                "[operator:<<](Number,Number)"),
            ["[operator:>>](Game.Number,Game.Number)"] = (
                "[operator:>>](Game.Number,Game.Number)",
                "[operator:>>](Game::Number,Game::Number)",
                "[operator:>>](Number,Number)"),
            ["[operator:*](Game.Number*,Game.Number)"] = (
                "[operator:*](Game.Number*,Game.Number)",
                "[operator:*](Game::Number*,Game::Number)",
                "[operator:*](Number*,Number)"),
            ["[conversion:implicit:int](Game.Number)"] = (
                "[conversion:implicit:int](Game.Number)",
                "[conversion:implicit:System::Int32](Game::Number)",
                "[conversion:implicit:int](Number)"),
            ["[conversion:explicit:System.Guid](Game.Number)"] = (
                "[conversion:explicit:System.Guid](Game.Number)",
                "[conversion:explicit:System::Guid](Game::Number)",
                "[conversion:explicit:Guid](Number)"),
            ["[checked-conversion:explicit:int](Game.Number)"] = (
                "[checked-conversion:explicit:int](Game.Number)",
                "[checked-conversion:explicit:System::Int32](Game::Number)",
                "[checked-conversion:explicit:int](Number)"),
            ["[get:Name]()"] = ("[get:Name]()", "[get:Name]()", "[get:Name]()"),
            ["[set:Name](string)"] = ("[set:Name](string)", "[set:Name](System::String)", "[set:Name](string)"),
            ["[init:Name](string)"] = ("[init:Name](string)", "[init:Name](System::String)", "[init:Name](string)"),
            ["[add:Changed](System.EventHandler)"] = (
                "[add:Changed](System.EventHandler)",
                "[add:Changed](System::EventHandler)",
                "[add:Changed](EventHandler)"),
            ["[remove:Changed](System.EventHandler)"] = (
                "[remove:Changed](System.EventHandler)",
                "[remove:Changed](System::EventHandler)",
                "[remove:Changed](EventHandler)"),
            ["[explicit:System.IDisposable.Dispose]()"] = (
                "[explicit:System.IDisposable.Dispose]()",
                "[explicit:System::IDisposable.Dispose]()",
                "[explicit:IDisposable.Dispose]()"),
            ["[get:Game.Contracts.IPlayer.Name]()"] = (
                "[get:Game.Contracts.IPlayer.Name]()",
                "[get:Game.Contracts::IPlayer.Name]()",
                "[get:IPlayer.Name]()"),
            ["[set:Game.Contracts.IPlayer.Name](string)"] = (
                "[set:Game.Contracts.IPlayer.Name](string)",
                "[set:Game.Contracts::IPlayer.Name](System::String)",
                "[set:IPlayer.Name](string)"),
            ["[explicit:Game.Contracts.IMapper.Map]<T>(T)"] = (
                "[explicit:Game.Contracts.IMapper.Map]<T>(T)",
                "[explicit:Game.Contracts::IMapper.Map]`1(^0)",
                "[explicit:IMapper.Map]<T>(T)"),
            ["Run(int).Local(string).<lambda#1>"] = (
                "Run(int).Local(string).<lambda#1>",
                "Run(System::Int32).Local(System::String).<lambda#1>",
                "Run(int).Local(string).<lambda#1>"),
            ["<initializer:member>.<lambda#1>"] = (
                "<initializer:member>.<lambda#1>",
                "<initializer:member>.<lambda#1>",
                "<initializer:member>.<lambda#1>"),
            ["<anonymous-method#1>"] = ("<anonymous-method#1>", "<anonymous-method#1>", "<anonymous-method#1>"),
            ["<top-level-statements>"] = ("<top-level-statements>", "<top-level-statements>", "<top-level-statements>"),
        };
        var formatter = new SymbolPathFormatter();

        foreach (var (executable, (display, identity, expectedShort)) in cases)
        {
            var path = new SymbolPathData(
                "Game.Core",
                "Player.Inventory",
                "Player.Inventory",
                display,
                identity,
                display,
                identity,
                CallablePathSegmentKind.Named);
            Assert.Equal(
                $"Game.Core.Player.Inventory::{display}",
                formatter.Format(path, new(SymbolPathStyle.CSharp)));
            Assert.Equal(
                $"Player.Inventory::{expectedShort}",
                formatter.Format(path, new(SymbolPathStyle.CSharp, ShortNames: true)));
        }
    }

    [Theory]
    [InlineData(SymbolPathStyle.CSharp, false, "Game.Core.Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>")]
    [InlineData(SymbolPathStyle.CSharp, true, "Outer<T>.Inner<U>::Run(Guid).<lambda#1>")]
    [InlineData(SymbolPathStyle.Explicit, false, "Game.Core::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>")]
    [InlineData(SymbolPathStyle.Explicit, true, "**::Outer<T>.Inner<U>::Run(Guid).<lambda#1>")]
    public void Format_UsesStyleAndShortensEveryTypeNamespace(
        SymbolPathStyle style,
        bool shortNames,
        string expected)
    {
        Assert.Equal(
            expected,
            new SymbolPathFormatter().Format(NestedMethod, new SymbolPathFormatOptions(style, shortNames)));
    }

    [Fact]
    public void Format_GlobalAndLiteralGlobalNamespacesRemainDistinct()
    {
        var global = NestedMethod with { NamespacePath = string.Empty };
        var literal = NestedMethod with { NamespacePath = "@global" };
        var formatter = new SymbolPathFormatter();

        Assert.Equal(
            "Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(global, new(SymbolPathStyle.CSharp)));
        Assert.Equal(
            "Outer<T>.Inner<U>::Run(Guid).<lambda#1>",
            formatter.Format(global, new(SymbolPathStyle.CSharp, ShortNames: true)));
        Assert.Equal(
            "global::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(global, new(SymbolPathStyle.Explicit)));
        Assert.Equal(
            "**::Outer<T>.Inner<U>::Run(Guid).<lambda#1>",
            formatter.Format(global, new(SymbolPathStyle.Explicit, ShortNames: true)));

        Assert.Equal(
            "@global.Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.CSharp)));
        Assert.Equal(
            "Outer<T>.Inner<U>::Run(Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.CSharp, ShortNames: true)));
        Assert.Equal(
            "@global::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.Explicit)));
        Assert.Equal(
            "**::Outer<T>.Inner<U>::Run(Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.Explicit, ShortNames: true)));
    }

    [Fact]
    public void Format_ShortNamesShortenConversionAndExplicitInterfacePayloads()
    {
        var payload = NestedMethod with
        {
            ExecutableDisplayPath =
                "[conversion:implicit:System.Guid](Game.Models.Number)." +
                "[explicit:System.IDisposable.Dispose]()." +
                "[get:Game.Contracts.IPlayer.Name]()." +
                "Local(System.Collections.Generic.List<Game.Models.Number>)",
            ExecutableIdentityPath =
                "[conversion:implicit:System::Guid](Game.Models::Number)." +
                "[explicit:System::IDisposable.Dispose]()." +
                "[get:Game.Contracts::IPlayer.Name]()." +
                "Local(System.Collections.Generic::List<Game.Models::Number>)",
        };

        Assert.Equal(
            "Outer<T>.Inner<U>::[conversion:implicit:Guid](Number).[explicit:IDisposable.Dispose]()." +
            "[get:IPlayer.Name]().Local(List<Number>)",
            new SymbolPathFormatter().Format(
                payload,
                new(SymbolPathStyle.CSharp, ShortNames: true)));
    }

    [Fact]
    public void Format_ShortNamesShortenQualifiedTypesAcrossNestedLocalFunctions()
    {
        var path = NestedMethod with
        {
            ExecutableDisplayPath =
                "Root(System.Collections.Generic.List<Game.Models.Number>)." +
                "LocalOne(System.Collections.Generic.Dictionary<Game.Models.Key,Game.Models.Value>)." +
                "LocalTwo(Game.Models.Wrapper<Game.Models.Number>)",
            ExecutableIdentityPath =
                "Root(System.Collections.Generic::List<Game.Models::Number>)." +
                "LocalOne(System.Collections.Generic::Dictionary<Game.Models::Key,Game.Models::Value>)." +
                "LocalTwo(Game.Models::Wrapper<Game.Models::Number>)",
        };

        Assert.Equal(
            "Outer<T>.Inner<U>::Root(List<Number>).LocalOne(Dictionary<Key, Value>).LocalTwo(Wrapper<Number>)",
            new SymbolPathFormatter().Format(
                path,
                new(SymbolPathStyle.CSharp, ShortNames: true)));
    }

    [Fact]
    public void Format_ShortNamesRejectExecutableSegmentCountMismatch()
    {
        var path = NestedMethod with
        {
            ExecutableDisplayPath = "Run(System.Int32)",
            ExecutableIdentityPath = "Run(System::Int32).Local()",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SymbolPathFormatter().Format(path, new(SymbolPathStyle.CSharp, ShortNames: true)));

        Assert.Equal(
            "Symbol executable path mismatch (executable segment count): identity 'Run(System::Int32).Local()', display 'Run(System.Int32)'.",
            exception.Message);
    }

    [Fact]
    public void Format_ShortNamesRejectParameterCountMismatch()
    {
        var path = NestedMethod with
        {
            ExecutableDisplayPath = "Run()",
            ExecutableIdentityPath = "Run(System::Int32)",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SymbolPathFormatter().Format(path, new(SymbolPathStyle.CSharp, ShortNames: true)));

        Assert.Equal(
            "Symbol executable path mismatch (parameter count): identity 'Run(System::Int32)', display 'Run()'.",
            exception.Message);
    }

    [Fact]
    public void Format_ShortNamesRejectMalformedDelimiterNesting()
    {
        var path = NestedMethod with
        {
            ExecutableDisplayPath = "Run(System.Int32",
            ExecutableIdentityPath = "Run(System::Int32)",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SymbolPathFormatter().Format(path, new(SymbolPathStyle.CSharp, ShortNames: true)));

        Assert.Equal(
            "Symbol executable path mismatch (delimiter nesting): identity 'Run(System::Int32)', display 'Run(System.Int32'.",
            exception.Message);
    }

    [Fact]
    public void Format_ShortNamesRejectMismatchedTypeBearingSpecialPayload()
    {
        var path = NestedMethod with
        {
            ExecutableDisplayPath = "[conversion:implicit:System.Guid]()",
            ExecutableIdentityPath = "[conversion:implicit:System::String]()",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new SymbolPathFormatter().Format(path, new(SymbolPathStyle.CSharp, ShortNames: true)));

        Assert.Equal(
            "Symbol executable path mismatch (special payload): identity '[conversion:implicit:System::String]()', display '[conversion:implicit:System.Guid]()'.",
            exception.Message);
    }

    [Fact]
    public void Format_TypeOnlyPathHasNoTrailingExecutableSeparator()
    {
        var type = NestedMethod with
        {
            ExecutableDisplayPath = string.Empty,
            ExecutableIdentityPath = string.Empty,
            SegmentDisplay = string.Empty,
            SegmentIdentity = string.Empty,
            SegmentKind = CallablePathSegmentKind.Named,
        };

        var formatter = new SymbolPathFormatter();
        Assert.Equal("Game.Core.Outer<T>.Inner<U>", formatter.Format(type, new()));
        Assert.Equal(
            "Outer<T>.Inner<U>",
            formatter.Format(type, new(SymbolPathStyle.CSharp, ShortNames: true)));
        Assert.Equal("Game.Core::Outer<T>.Inner<U>", formatter.Format(type, new(SymbolPathStyle.Explicit)));
        Assert.Equal(
            "**::Outer<T>.Inner<U>",
            formatter.Format(type, new(SymbolPathStyle.Explicit, ShortNames: true)));
    }

    [Fact]
    public void CreateType_ProducesOwnerOnlySemanticPath()
    {
        const string source = """
            namespace Game.Core;

            public class Outer<T>
            {
                public class @class<U> { }
            }
            """;
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "TypePathTests",
            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var innerType = compilation.Assembly.GlobalNamespace
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Game")
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Core")
            .GetTypeMembers("Outer").Single()
            .GetTypeMembers("class").Single();
        var symbol = new SymbolCanonicalizer(new AnalysisProfileData
        {
            Name = "formatter-test",
            InputMode = InputMode.Solution,
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        }).CreateType(innerType);

        var path = Assert.IsType<SymbolPathData>(symbol.Path);
        Assert.Equal("Game.Core", path.NamespacePath);
        Assert.Equal("Outer<T>.@class<U>", path.TypeDisplayPath);
        Assert.Equal("Outer`1.class`1", path.TypeIdentityPath);
        Assert.Equal(string.Empty, path.ExecutableDisplayPath);
        Assert.Equal(string.Empty, path.ExecutableIdentityPath);
        Assert.Equal(string.Empty, path.SegmentDisplay);
        Assert.Equal(string.Empty, path.SegmentIdentity);
        Assert.Equal(CallablePathSegmentKind.Named, path.SegmentKind);
        Assert.Equal("Game.Core.Outer<T>.@class<U>", new SymbolPathFormatter().Format(path, new()));
    }

    [Fact]
    public void Format_RejectsInvalidStyleAndStructurallyEmptyTypePath()
    {
        var formatter = new SymbolPathFormatter();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            formatter.Format(NestedMethod, new((SymbolPathStyle)99)));
        Assert.Throws<ArgumentException>(() =>
            formatter.Format(NestedMethod with { TypeDisplayPath = string.Empty }, new()));
        Assert.Throws<ArgumentException>(() =>
            formatter.Format(NestedMethod with { TypeDisplayPath = "   " }, new()));
    }

    private static IReadOnlyList<MetadataReference> GetPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
}
