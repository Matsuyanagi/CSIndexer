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
        ExecutableIdentityPath: "Run(System.Guid).<lambda#1>",
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
            ExecutableIdentityPath = "Load(System.Guid)",
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
            "Player.Inventory::Load(System.Guid)",
            formatter.Format(guidPath, new(SymbolPathStyle.CSharp, ShortNames: true)));
        Assert.Equal(
            "**::Player.Inventory::Load(System.Guid)",
            formatter.Format(guidPath, new(SymbolPathStyle.Explicit, ShortNames: true)));
    }

    [Fact]
    public void Format_CoversEveryConcreteCallableCategoryWithCanonicalPayloads()
    {
        var cases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[constructor](int,string)"] = "[constructor](int,string)",
            ["[static-constructor]()"] = "[static-constructor]()",
            ["[destructor]()"] = "[destructor]()",
            ["[operator:+](Game.Number,Game.Number)"] = "[operator:+](Game.Number,Game.Number)",
            ["[checked-operator:+](Game.Number,Game.Number)"] = "[checked-operator:+](Game.Number,Game.Number)",
            ["[operator:*](Game.Number*,Game.Number)"] = "[operator:*](Game.Number*,Game.Number)",
            ["[conversion:implicit:int](Game.Number)"] = "[conversion:implicit:int](Game.Number)",
            ["[conversion:explicit:System.Guid](Game.Number)"] = "[conversion:explicit:System.Guid](Game.Number)",
            ["[checked-conversion:explicit:int](Game.Number)"] = "[checked-conversion:explicit:int](Game.Number)",
            ["[get:Name]()"] = "[get:Name]()",
            ["[set:Name](string)"] = "[set:Name](string)",
            ["[init:Name](string)"] = "[init:Name](string)",
            ["[add:Changed](System.EventHandler)"] = "[add:Changed](System.EventHandler)",
            ["[remove:Changed](System.EventHandler)"] = "[remove:Changed](System.EventHandler)",
            ["[explicit:System.IDisposable.Dispose]()"] = "[explicit:System.IDisposable.Dispose]()",
            ["[get:Game.Contracts.IPlayer.Name]()"] = "[get:Game.Contracts.IPlayer.Name]()",
            ["[set:Game.Contracts.IPlayer.Name](string)"] = "[set:Game.Contracts.IPlayer.Name](string)",
            ["[explicit:Game.Contracts.IMapper.Map]<T>(T)"] = "[explicit:Game.Contracts.IMapper.Map]<T>(T)",
            ["Run(int).Local(string).<lambda#1>"] = "Run(int).Local(string).<lambda#1>",
            ["<initializer:member>.<lambda#1>"] = "<initializer:member>.<lambda#1>",
            ["<anonymous-method#1>"] = "<anonymous-method#1>",
            ["<top-level-statements>"] = "<top-level-statements>",
        };
        var formatter = new SymbolPathFormatter();

        foreach (var (executable, expectedExecutable) in cases)
        {
            var path = new SymbolPathData(
                "Game.Core",
                "Player.Inventory",
                "Player.Inventory",
                executable,
                executable,
                executable,
                executable,
                CallablePathSegmentKind.Named);
            Assert.Equal(
                $"Game.Core.Player.Inventory::{expectedExecutable}",
                formatter.Format(path, new(SymbolPathStyle.CSharp)));
        }
    }

    [Theory]
    [InlineData(SymbolPathStyle.CSharp, false, "Game.Core.Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>")]
    [InlineData(SymbolPathStyle.CSharp, true, "Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>")]
    [InlineData(SymbolPathStyle.Explicit, false, "Game.Core::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>")]
    [InlineData(SymbolPathStyle.Explicit, true, "**::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>")]
    public void Format_UsesStyleAndOmitsOnlyOwnerNamespace(
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
            "Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(global, new(SymbolPathStyle.CSharp, ShortNames: true)));
        Assert.Equal(
            "global::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(global, new(SymbolPathStyle.Explicit)));
        Assert.Equal(
            "**::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(global, new(SymbolPathStyle.Explicit, ShortNames: true)));

        Assert.Equal(
            "@global.Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.CSharp)));
        Assert.Equal(
            "Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.CSharp, ShortNames: true)));
        Assert.Equal(
            "@global::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.Explicit)));
        Assert.Equal(
            "**::Outer<T>.Inner<U>::Run(System.Guid).<lambda#1>",
            formatter.Format(literal, new(SymbolPathStyle.Explicit, ShortNames: true)));
    }

    [Fact]
    public void Format_ShortNamesPreserveConversionAndExplicitInterfacePayloads()
    {
        var payload = NestedMethod with
        {
            ExecutableDisplayPath =
                "[conversion:implicit:System.Guid](System.Guid).[explicit:System.IDisposable.Dispose]()",
        };

        Assert.Equal(
            "Outer<T>.Inner<U>::[conversion:implicit:System.Guid](System.Guid).[explicit:System.IDisposable.Dispose]()",
            new SymbolPathFormatter().Format(
                payload,
                new(SymbolPathStyle.CSharp, ShortNames: true)));
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
