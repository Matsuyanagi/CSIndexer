using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsIndex.Core.Tests;

public sealed class SymbolSignatureCanonicalizerTests
{
    [Theory]
    [InlineData("int", "System.Int32")]
    [InlineData("int", "global::System.Int32")]
    [InlineData("object", "dynamic")]
    [InlineData("string", "string?")]
    [InlineData("(int,string)", "(int Left,string Right)")]
    public void CanonicalizeType_EquivalentSpellingsShareIdentity(string left, string right)
    {
        var leftType = GetParameterType(left);
        var rightType = GetParameterType(right);

        var canonicalLeft = SymbolSignatureCanonicalizer.CanonicalizeType(leftType);
        var canonicalRight = SymbolSignatureCanonicalizer.CanonicalizeType(rightType);

        Assert.Equal(canonicalLeft.IdentityKey, canonicalRight.IdentityKey);
    }

    [Theory]
    [InlineData("int", "int?")]
    [InlineData("int[]", "int[,]")]
    [InlineData("int*", "int")]
    [InlineData("delegate*<int,void>", "delegate* unmanaged<int,void>")]
    public void CanonicalizeType_DistinctSpellingsHaveDistinctIdentity(string left, string right)
    {
        var leftType = GetParameterType(left);
        var rightType = GetParameterType(right);

        var canonicalLeft = SymbolSignatureCanonicalizer.CanonicalizeType(leftType);
        var canonicalRight = SymbolSignatureCanonicalizer.CanonicalizeType(rightType);

        Assert.NotEqual(canonicalLeft.IdentityKey, canonicalRight.IdentityKey);
    }

    [Fact]
    public void CanonicalizeType_GenericPlaceholderNamesNormalizeByOrdinal()
    {
        var leftType = GetParameterType("T", genericParameters: ["T"]);
        var rightType = GetParameterType("U", genericParameters: ["U"]);

        var canonicalLeft = SymbolSignatureCanonicalizer.CanonicalizeType(leftType);
        var canonicalRight = SymbolSignatureCanonicalizer.CanonicalizeType(rightType);

        Assert.Equal(canonicalLeft.IdentityKey, canonicalRight.IdentityKey);
    }

    [Fact]
    public void CanonicalizeParameter_PreservesRefModeInSignature()
    {
        var refParameter = GetParameter("ref int");
        var outParameter = GetParameter("out int");
        var inParameter = GetParameter("in int");
        var refReadonlyParameter = GetParameter("ref readonly int");

        Assert.NotEqual(
            SymbolSignatureCanonicalizer.CanonicalizeParameter(refParameter).RefKind,
            SymbolSignatureCanonicalizer.CanonicalizeParameter(outParameter).RefKind);
        Assert.NotEqual(
            SymbolSignatureCanonicalizer.CanonicalizeParameter(inParameter).RefKind,
            SymbolSignatureCanonicalizer.CanonicalizeParameter(refReadonlyParameter).RefKind);
    }

    [Fact]
    public void CanonicalizeType_UsesConcreteCSharpAliasesAndQualifiedNamedTypes()
    {
        Assert.Equal("bool", SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("bool")).DisplayText);
        Assert.Equal("int", SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("int")).DisplayText);
        Assert.Equal("nint", SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("nint")).DisplayText);
        Assert.Equal("nuint", SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("nuint")).DisplayText);
        Assert.Equal("string", SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("string")).DisplayText);
        Assert.Equal("object", SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("object")).DisplayText);

        var named = SymbolSignatureCanonicalizer.CanonicalizeType(
            GetParameterType("System.Collections.Generic.List<string>"));
        Assert.Equal("System.Collections.Generic.List<string>", named.DisplayText);
        Assert.DoesNotContain("global::", named.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizeType_OmitsTupleElementNamesFromDisplay()
    {
        var canonical = SymbolSignatureCanonicalizer.CanonicalizeType(
            GetParameterType("(int Left,string Right)"));

        Assert.Equal("(int, string)", canonical.DisplayText);
    }

    [Fact]
    public void ParseSelectorType_RequiresQualifiedNonAliasNames()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "Customer",
                new Dictionary<string, int>(StringComparer.Ordinal)));

        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.Customer",
            new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.Equal("Game.Models.Customer", selector.SyntaxText);
    }

    [Fact]
    public void ParseSelectorType_RejectsConstructedGenericMethodNotation()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "Method<System.String>",
                new Dictionary<string, int>(StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("int[")]
    [InlineData("*")]
    [InlineData("Game.Models.*")]
    public void ParseSelectorType_RejectsInvalidOrWildcardTypeSyntax(string typeText)
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                typeText,
                new Dictionary<string, int>(StringComparer.Ordinal)));
    }

    [Fact]
    public void ParseSelectorType_RejectsInvalidPlaceholderEntries()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "T",
                new Dictionary<string, int>(StringComparer.Ordinal) { [string.Empty] = 0 }));
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "T",
                new Dictionary<string, int>(StringComparer.Ordinal) { ["T"] = -1 }));
    }

    [Theory]
    [InlineData("int")]
    [InlineData("System.Int32")]
    [InlineData("global::System.Int32")]
    public void ParseSelectorType_MatchesAliasAndFrameworkSpellings(string typeText)
    {
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("int"));
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            typeText,
            new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));
    }

    [Theory]
    [InlineData("delegate*<int,void>")]
    [InlineData("delegate* unmanaged<int,void>")]
    [InlineData("delegate* unmanaged[Cdecl]<int,void>")]
    [InlineData("delegate* unmanaged[Cdecl,SuppressGCTransition]<int,void>")]
    [InlineData("delegate*<ref int,void>")]
    [InlineData("delegate*<out int,void>")]
    [InlineData("delegate*<in int,void>")]
    [InlineData("delegate*<int,ref int>")]
    [InlineData("delegate*<int,ref readonly int>")]
    public void ParseSelectorType_MatchesEquivalentFunctionPointer(string typeText)
    {
        var canonical = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(typeText));
        var candidate = new CanonicalTypeSignature(canonical.IdentityKey, canonical.DisplayText);
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            typeText,
            new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.True(
            SymbolSignatureCanonicalizer.IsMatch(selector, candidate),
            $"Candidate identity: {canonical.IdentityKey}");
    }

    [Fact]
    public void CanonicalizeType_PreservesContainingGenericArguments()
    {
        var (first, second) = GetNestedGenericParameterTypes();

        Assert.NotEqual(
            SymbolSignatureCanonicalizer.CanonicalizeType(first).IdentityKey,
            SymbolSignatureCanonicalizer.CanonicalizeType(second).IdentityKey);
    }

    [Fact]
    public void ParseSelectorType_MatchesNestedGenericArguments()
    {
        var (first, second) = GetNestedGenericParameterTypes();
        var canonical = SymbolSignatureCanonicalizer.CanonicalizeType(first);
        var candidate = new CanonicalTypeSignature(canonical.IdentityKey, canonical.DisplayText);
        var otherCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(second);
        var otherCandidate = new CanonicalTypeSignature(otherCanonical.IdentityKey, otherCanonical.DisplayText);
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.Outer<int>.Inner<string>",
            new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(selector, otherCandidate));
    }

    [Fact]
    public void ParseSelectorType_DistinguishesNullableCustomValueAndReferenceTypes()
    {
        var (referenceType, valueType, nullableValueType) = GetCustomNullableParameterTypes();
        var referenceCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(referenceType);
        var valueCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(valueType);
        var nullableValueCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(nullableValueType);
        var referenceSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.RefType?",
            new Dictionary<string, int>(StringComparer.Ordinal));
        var valueSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.ValueType?",
            new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            referenceSelector,
            new CanonicalTypeSignature(referenceCanonical.IdentityKey, referenceCanonical.DisplayText)));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            valueSelector,
            new CanonicalTypeSignature(valueCanonical.IdentityKey, valueCanonical.DisplayText)));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            valueSelector,
            new CanonicalTypeSignature(nullableValueCanonical.IdentityKey, nullableValueCanonical.DisplayText)));
    }

    [Fact]
    public void CanonicalizeMethod_StoresConversionTargetIdentityAndDisplaySeparately()
    {
        var method = GetConversionMethod();
        var signature = SymbolSignatureCanonicalizer.CanonicalizeMethod(method);
        var symbol = new SymbolCanonicalizer(new AnalysisProfileData
        {
            Name = "test",
            InputMode = InputMode.Solution,
            TargetFramework = "net10.0",
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        }).CreateMethod(method, actualTarget: false);

        Assert.Equal("System.Int32", signature.ConversionTargetType?.IdentityKey);
        Assert.Equal("int", signature.ConversionTargetType?.DisplayText);
        Assert.Equal("System.Int32", symbol.ConversionTypeKey);
        Assert.Equal("int", symbol.ConversionTypeDisplay);
    }

    private static (ITypeSymbol First, ITypeSymbol Second) GetNestedGenericParameterTypes()
    {
        const string source = """
            namespace Game.Models;

            public class Outer<T>
            {
                public class Inner<U> { }
            }

            public class Consumer
            {
                public void First(Outer<int>.Inner<string> value) { }
                public void Second(Outer<long>.Inner<string> value) { }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "NestedGenericSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var consumer = compilation.Assembly.GlobalNamespace
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Game")
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Models")
            .GetTypeMembers("Consumer").Single();
        var first = consumer.GetMembers("First").OfType<IMethodSymbol>().Single().Parameters[0].Type;
        var second = consumer.GetMembers("Second").OfType<IMethodSymbol>().Single().Parameters[0].Type;
        return (first, second);
    }

    private static (ITypeSymbol Reference, ITypeSymbol Value, ITypeSymbol NullableValue)
        GetCustomNullableParameterTypes()
    {
        const string source = """
            #nullable enable
            namespace Game.Models;

            public sealed class RefType { }
            public readonly struct ValueType { }

            public sealed class Consumer
            {
                public void Reference(RefType value) { }
                public void Value(ValueType value) { }
                public void NullableValue(ValueType? value) { }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "CustomNullableSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var consumer = compilation.Assembly.GlobalNamespace
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Game")
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Models")
            .GetTypeMembers("Consumer").Single();
        return (
            consumer.GetMembers("Reference").OfType<IMethodSymbol>().Single().Parameters[0].Type,
            consumer.GetMembers("Value").OfType<IMethodSymbol>().Single().Parameters[0].Type,
            consumer.GetMembers("NullableValue").OfType<IMethodSymbol>().Single().Parameters[0].Type);
    }

    private static IMethodSymbol GetConversionMethod()
    {
        const string source = """
            namespace Game.Models;

            public readonly struct Value
            {
                public static implicit operator int(Value value) => 0;
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "ConversionSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation.Assembly.GlobalNamespace
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Game")
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Models")
            .GetTypeMembers("Value").Single()
            .GetMembers("op_Implicit").OfType<IMethodSymbol>().Single();
    }

    private static ITypeSymbol GetParameterType(
        string typeText,
        IReadOnlyList<string>? genericParameters = null)
    {
        var parameter = GetParameter(typeText, genericParameters);
        return parameter.Type;
    }

    private static IParameterSymbol GetParameter(
        string typeText,
        IReadOnlyList<string>? genericParameters = null)
    {
        var genericList = genericParameters is null ? string.Empty : $"<{string.Join(",", genericParameters)}>";
        var body = typeText.StartsWith("out ", StringComparison.Ordinal) ? "value = default;" : string.Empty;
        var source = $"public unsafe class C{genericList} {{ public void M({typeText} value) {{ {body} }} }}";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "CanonicalSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var diagnostics = compilation.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var method = compilation
            .Assembly
            .GlobalNamespace
            .GetTypeMembers("C")
            .Single()
            .GetMembers("M")
            .OfType<IMethodSymbol>()
            .Single();
        return method.Parameters[0];
    }

    private static IReadOnlyList<MetadataReference> GetPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
}
