using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    public void CanonicalizeType_FlattensContainingTypeOrdinalsAndPreservesScope()
    {
        var (outerType, innerType, methodType) = GetScopedPlaceholderParameterTypes();

        Assert.Equal("!0", SymbolSignatureCanonicalizer.CanonicalizeType(outerType).IdentityKey);
        Assert.Equal("!1", SymbolSignatureCanonicalizer.CanonicalizeType(innerType).IdentityKey);
        Assert.Equal("^0", SymbolSignatureCanonicalizer.CanonicalizeType(methodType).IdentityKey);
    }

    [Fact]
    public void ParseSelectorType_MatchesPlaceholderScopeAndOrdinalExactly()
    {
        var (outerType, innerType, methodType) = GetScopedPlaceholderParameterTypes();
        var candidates = new[]
        {
            SymbolSignatureCanonicalizer.CanonicalizeType(outerType),
            SymbolSignatureCanonicalizer.CanonicalizeType(innerType),
            SymbolSignatureCanonicalizer.CanonicalizeType(methodType),
        };
        var placeholders = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
        {
            ["TOuter"] = new(CanonicalGenericPlaceholderScope.Type, 0),
            ["TInner"] = new(CanonicalGenericPlaceholderScope.Type, 1),
            ["TMethod"] = new(CanonicalGenericPlaceholderScope.Method, 0),
        };
        var selectors = new[]
        {
            SymbolSignatureCanonicalizer.ParseSelectorType("TOuter", placeholders),
            SymbolSignatureCanonicalizer.ParseSelectorType("TInner", placeholders),
            SymbolSignatureCanonicalizer.ParseSelectorType("TMethod", placeholders),
        };

        for (var selectorIndex = 0; selectorIndex < selectors.Length; selectorIndex++)
        {
            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                Assert.Equal(
                    selectorIndex == candidateIndex,
                    SymbolSignatureCanonicalizer.IsMatch(selectors[selectorIndex], candidates[candidateIndex]));
            }
        }
    }

    [Fact]
    public void ParseSelectorType_SnapshotsPlaceholderMapAndExposesReadOnlyView()
    {
        var placeholders = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
        {
            ["T"] = new(CanonicalGenericPlaceholderScope.Type, 0),
        };
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType("T", placeholders);
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(
            GetParameterType("T", genericParameters: ["T"]));

        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));

        placeholders.Clear();
        placeholders["T"] = new(CanonicalGenericPlaceholderScope.Type, 99);

        Assert.True(selector.GenericPlaceholders.ContainsKey("T"));
        Assert.Equal(new(CanonicalGenericPlaceholderScope.Type, 0), selector.GenericPlaceholders["T"]);
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));

        var dictionary = Assert.IsAssignableFrom<IDictionary<string, CanonicalGenericPlaceholder>>(
            selector.GenericPlaceholders);
        Assert.Throws<NotSupportedException>(() => dictionary.Add(
            "U",
            new CanonicalGenericPlaceholder(CanonicalGenericPlaceholderScope.Type, 1)));
        Assert.Throws<NotSupportedException>(() => dictionary["T"] =
            new CanonicalGenericPlaceholder(CanonicalGenericPlaceholderScope.Type, 1));
        Assert.Throws<NotSupportedException>(() => dictionary.Remove("T"));
        Assert.Throws<NotSupportedException>(() => dictionary.Clear());
    }

    [Fact]
    public void CanonicalTypeSelector_HasNoPublicInstanceConstructor()
    {
        Assert.Empty(typeof(CanonicalTypeSelector).GetConstructors());
    }

    [Fact]
    public void CanonicalizeType_FlattensNestedExecutableMethodOrdinals()
    {
        var (outerType, middleType, innerType) = GetNestedExecutablePlaceholderParameterTypes();

        Assert.Equal("^0", SymbolSignatureCanonicalizer.CanonicalizeType(outerType).IdentityKey);
        Assert.Equal("^1", SymbolSignatureCanonicalizer.CanonicalizeType(middleType).IdentityKey);
        Assert.Equal("^2", SymbolSignatureCanonicalizer.CanonicalizeType(innerType).IdentityKey);
    }

    [Fact]
    public void ParseSelectorType_MatchesNestedExecutableMethodOrdinalsExactly()
    {
        var (outerType, middleType, innerType) = GetNestedExecutablePlaceholderParameterTypes();
        var candidates = new[]
        {
            SymbolSignatureCanonicalizer.CanonicalizeType(outerType),
            SymbolSignatureCanonicalizer.CanonicalizeType(middleType),
            SymbolSignatureCanonicalizer.CanonicalizeType(innerType),
        };
        var placeholders = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
        {
            ["TOuter"] = new(CanonicalGenericPlaceholderScope.Method, 0),
            ["TMiddle"] = new(CanonicalGenericPlaceholderScope.Method, 1),
            ["TInner"] = new(CanonicalGenericPlaceholderScope.Method, 2),
        };
        var selectors = new[]
        {
            SymbolSignatureCanonicalizer.ParseSelectorType("TOuter", placeholders),
            SymbolSignatureCanonicalizer.ParseSelectorType("TMiddle", placeholders),
            SymbolSignatureCanonicalizer.ParseSelectorType("TInner", placeholders),
        };

        for (var selectorIndex = 0; selectorIndex < selectors.Length; selectorIndex++)
        {
            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                Assert.Equal(
                    selectorIndex == candidateIndex,
                    SymbolSignatureCanonicalizer.IsMatch(selectors[selectorIndex], candidates[candidateIndex]));
            }
        }
    }

    [Fact]
    public void CanonicalizeParameter_PreservesAllRefModesPairwise()
    {
        string[] parameterTexts = ["int", "ref int", "out int", "in int", "ref readonly int"];
        var refKinds = parameterTexts
            .Select(parameterText =>
                SymbolSignatureCanonicalizer.CanonicalizeParameter(GetParameter(parameterText)).RefKind)
            .ToArray();

        Assert.Equal(parameterTexts.Length, refKinds.Distinct().Count());
        for (var leftIndex = 0; leftIndex < refKinds.Length; leftIndex++)
        {
            for (var rightIndex = 0; rightIndex < refKinds.Length; rightIndex++)
            {
                Assert.Equal(leftIndex == rightIndex, refKinds[leftIndex] == refKinds[rightIndex]);
            }
        }
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
    public void CanonicalizeType_EncodesNamespaceAndTypeBoundary()
    {
        var (nested, _) = GetNestedGenericParameterTypes();
        var (_, valueType, _) = GetCustomNullableParameterTypes();

        Assert.Equal(
            "System::Int32",
            SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("int")).IdentityKey);
        Assert.Equal(
            "::C",
            SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("C")).IdentityKey);
        Assert.Equal(
            "Game.Models::Outer<System::Int32>.Inner<System::String>",
            SymbolSignatureCanonicalizer.CanonicalizeType(nested).IdentityKey);
        Assert.Equal(
            "valuetype:Game.Models::ValueType",
            SymbolSignatureCanonicalizer.CanonicalizeType(valueType).IdentityKey);
    }

    [Theory]
    [InlineData("valuetype")]
    [InlineData("reftype")]
    public void IsMatch_RoundTripsNamespacesThatResembleClassificationPrefixes(string namespaceName)
    {
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(
            GetClassificationPrefixNamespaceParameterType(namespaceName));
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            $"{namespaceName}.Marker",
            EmptyPlaceholders());

        Assert.Equal($"{namespaceName}::Marker", candidate.IdentityKey);
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));
    }

    [Fact]
    public void CanonicalizeType_OmitsTupleElementNamesFromDisplay()
    {
        var canonical = SymbolSignatureCanonicalizer.CanonicalizeType(
            GetParameterType("(int Left,string Right)"));

        Assert.Equal("(int, string)", canonical.DisplayText);
    }

    [Fact]
    public void CanonicalizeType_NormalizesTupleAndValueTupleSpellings()
    {
        var tuple = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("(int,string)"));
        var valueTuple = SymbolSignatureCanonicalizer.CanonicalizeType(
            GetParameterType("System.ValueTuple<int,string>"));
        var tupleSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "(int,string)",
            EmptyPlaceholders());
        var valueTupleSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "System.ValueTuple<int,string>",
            EmptyPlaceholders());

        Assert.Equal("(System::Int32,System::String)", tuple.IdentityKey);
        Assert.Equal(tuple.IdentityKey, valueTuple.IdentityKey);
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(tupleSelector, valueTuple));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(valueTupleSelector, tuple));
    }

    [Theory]
    [InlineData("(int,int)", "System.ValueTuple<int,int>")]
    [InlineData("(int,int,int)", "System.ValueTuple<int,int,int>")]
    [InlineData("(int,int,int,int)", "System.ValueTuple<int,int,int,int>")]
    [InlineData("(int,int,int,int,int)", "System.ValueTuple<int,int,int,int,int>")]
    [InlineData("(int,int,int,int,int,int)", "System.ValueTuple<int,int,int,int,int,int>")]
    [InlineData("(int,int,int,int,int,int,int)", "System.ValueTuple<int,int,int,int,int,int,int>")]
    [InlineData(
        "(int,int,int,int,int,int,int,int)",
        "System.ValueTuple<int,int,int,int,int,int,int,System.ValueTuple<int>>")]
    [InlineData(
        "(int,int,int,int,int,int,int,int,int,int,int,int,int,int,int)",
        "System.ValueTuple<int,int,int,int,int,int,int,System.ValueTuple<int,int,int,int,int,int,int,System.ValueTuple<int>>>")]
    public void CanonicalizeType_NormalizesFrameworkValueTupleChains(
        string tupleText,
        string frameworkText)
    {
        var tuple = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(tupleText));
        var framework = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(frameworkText));
        var tupleSelector = SymbolSignatureCanonicalizer.ParseSelectorType(tupleText, EmptyPlaceholders());
        var frameworkSelector = SymbolSignatureCanonicalizer.ParseSelectorType(frameworkText, EmptyPlaceholders());

        Assert.Equal(tuple.IdentityKey, framework.IdentityKey);
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(tupleSelector, framework));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(frameworkSelector, tuple));
    }

    [Fact]
    public void CanonicalizeType_DoesNotCollapseNonTupleArityEightValueTuple()
    {
        const string tupleText = "(int,int,int,int,int,int,int,int)";
        const string nonTupleText = "System.ValueTuple<int,int,int,int,int,int,int,int>";
        var tuple = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(tupleText));
        var nonTuple = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(nonTupleText));
        var tupleSelector = SymbolSignatureCanonicalizer.ParseSelectorType(tupleText, EmptyPlaceholders());
        var nonTupleSelector = SymbolSignatureCanonicalizer.ParseSelectorType(nonTupleText, EmptyPlaceholders());

        Assert.NotEqual(tuple.IdentityKey, nonTuple.IdentityKey);
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(tupleSelector, nonTuple));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(nonTupleSelector, tuple));
    }

    [Fact]
    public void CanonicalizeType_KeepsTopLevelArityOneValueTupleNamed()
    {
        const string typeText = "System.ValueTuple<int>";
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(typeText));
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(typeText, EmptyPlaceholders());

        Assert.Equal("valuetype:System::ValueTuple<System::Int32>", candidate.IdentityKey);
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));
    }

    [Theory]
    [InlineData(
        "(int,int)",
        "system.ValueTuple<int,int>",
        "System.valuetuple<int,int>")]
    [InlineData(
        "(int,int,int,int,int,int,int,int)",
        "system.ValueTuple<int,int,int,int,int,int,int,system.ValueTuple<int>>",
        "System.valuetuple<int,int,int,int,int,int,int,System.valuetuple<int>>")]
    public void IsMatch_AppliesCasePoliciesToFrameworkValueTupleRecognition(
        string tupleText,
        string namespaceCaseSelectorText,
        string typeCaseSelectorText)
    {
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(tupleText));
        var namespaceCaseSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            namespaceCaseSelectorText,
            EmptyPlaceholders());
        var typeCaseSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            typeCaseSelectorText,
            EmptyPlaceholders());

        Assert.False(SymbolSignatureCanonicalizer.IsMatch(namespaceCaseSelector, candidate));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            namespaceCaseSelector,
            candidate,
            StringComparison.OrdinalIgnoreCase,
            StringComparison.Ordinal));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            namespaceCaseSelector,
            candidate,
            StringComparison.Ordinal,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(typeCaseSelector, candidate));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            typeCaseSelector,
            candidate,
            StringComparison.Ordinal,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            typeCaseSelector,
            candidate,
            StringComparison.OrdinalIgnoreCase,
            StringComparison.Ordinal));
    }

    [Fact]
    public void ParseSelectorType_DoesNotMatchNullableTupleToNonNullableTuple()
    {
        var tuple = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("(int,int)"));
        var nullableTuple = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("(int,int)?"));
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "(int,int)?",
            EmptyPlaceholders());

        Assert.False(SymbolSignatureCanonicalizer.IsMatch(selector, tuple));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, nullableTuple));
    }

    [Fact]
    public void ParseSelectorType_RequiresQualifiedNonAliasNames()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "Customer",
                EmptyPlaceholders()));

        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.Customer",
            EmptyPlaceholders());

        Assert.Equal("Game.Models.Customer", selector.SyntaxText);
    }

    [Theory]
    [InlineData("@int")]
    [InlineData("@dynamic")]
    [InlineData("@nint")]
    public void ParseSelectorType_DoesNotTreatEscapedKeywordsAsAliases(string typeText)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                typeText,
                EmptyPlaceholders()));

        Assert.Contains("must be fully qualified", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSelectorType_DoesNotTreatEscapedGlobalAsGlobalAlias()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "@global::System.Int32",
                EmptyPlaceholders()));

        Assert.Contains("Unsupported type alias", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Game.Models.@int", "int")]
    [InlineData("Game.Models.@dynamic", "dynamic")]
    [InlineData("Game.Models.@nint", "nint")]
    public void ParseSelectorType_RetainsFullyQualifiedEscapedIdentifiers(
        string selectorText,
        string typeName)
    {
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(
            GetEscapedIdentifierParameterType(typeName));
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            selectorText,
            EmptyPlaceholders());

        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));
    }

    [Fact]
    public void ParseSelectorType_RejectsConstructedGenericMethodNotation()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "Method<System.String>",
                EmptyPlaceholders()));
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
                EmptyPlaceholders()));
    }

    [Fact]
    public void ParseSelectorType_RejectsInvalidPlaceholderEntries()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "T",
                new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
                {
                    [string.Empty] = new(CanonicalGenericPlaceholderScope.Type, 0),
                }));
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "T",
                new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
                {
                    ["T"] = new(CanonicalGenericPlaceholderScope.Type, -1),
                }));
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "T",
                new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
                {
                    ["T"] = new((CanonicalGenericPlaceholderScope)int.MaxValue, 0),
                }));
        Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "T",
                new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
                {
                    ["T"] = new(CanonicalGenericPlaceholderScope.Type, 0),
                    ["U"] = new(CanonicalGenericPlaceholderScope.Type, 0),
                }));
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
            EmptyPlaceholders());

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
            EmptyPlaceholders());

        Assert.True(
            SymbolSignatureCanonicalizer.IsMatch(selector, candidate),
            $"Candidate identity: {canonical.IdentityKey}");
    }

    [Theory]
    [InlineData(
        "delegate* unmanaged[Cdecl]<int,void>",
        "delegate* unmanaged[ /* leading */ Cdecl /* trailing */ ] < int, void >")]
    [InlineData(
        "delegate* unmanaged[Cdecl,SuppressGCTransition]<int,void>",
        "delegate* unmanaged[ /* first */ Cdecl, /* second */ SuppressGCTransition ] < int, void >")]
    public void ParseSelectorType_IgnoresFunctionPointerCallingConventionTrivia(
        string candidateText,
        string selectorText)
    {
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(candidateText));
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType(
            selectorText,
            EmptyPlaceholders());

        Assert.True(SymbolSignatureCanonicalizer.IsMatch(selector, candidate));
    }

    [Fact]
    public void ParseSelectorType_DistinguishesFunctionPointerCallingConventionsPairwise()
    {
        AssertFunctionPointerMatrix(
        [
            "delegate*<int,void>",
            "delegate* unmanaged<int,void>",
            "delegate* unmanaged[Cdecl]<int,void>",
            "delegate* unmanaged[Stdcall]<int,void>",
            "delegate* unmanaged[Cdecl,SuppressGCTransition]<int,void>",
            "delegate* unmanaged[Stdcall,SuppressGCTransition]<int,void>",
        ]);
    }

    [Fact]
    public void ParseSelectorType_DistinguishesFunctionPointerParameterRefKindsPairwise()
    {
        AssertFunctionPointerMatrix(
        [
            "delegate*<int,void>",
            "delegate*<ref int,void>",
            "delegate*<out int,void>",
            "delegate*<in int,void>",
            "delegate*<ref readonly int,void>",
        ]);
    }

    [Fact]
    public void ParseSelectorType_DistinguishesFunctionPointerReturnRefKindsPairwise()
    {
        AssertFunctionPointerMatrix(
        [
            "delegate*<int,int>",
            "delegate*<int,ref int>",
            "delegate*<int,ref readonly int>",
        ]);
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
            EmptyPlaceholders());

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
            EmptyPlaceholders());
        var valueSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.ValueType?",
            EmptyPlaceholders());

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
    public void CanonicalizeType_PreservesPlaceholderConstraintClassification()
    {
        var (value, nullableValue, reference, nullableReference) = GetConstrainedPlaceholderParameterTypes();
        var valueCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(value);
        var nullableValueCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(nullableValue);
        var referenceCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(reference);
        var nullableReferenceCanonical = SymbolSignatureCanonicalizer.CanonicalizeType(nullableReference);

        Assert.Equal("valuetype:!0", valueCanonical.IdentityKey);
        Assert.NotEqual(valueCanonical.IdentityKey, nullableValueCanonical.IdentityKey);
        Assert.Equal("reftype:!1", referenceCanonical.IdentityKey);
        Assert.Equal(referenceCanonical.IdentityKey, nullableReferenceCanonical.IdentityKey);
    }

    [Fact]
    public void ParseSelectorType_UsesPlaceholderConstraintsForNullableMatching()
    {
        var (value, nullableValue, reference, _) = GetConstrainedPlaceholderParameterTypes();
        var placeholders = new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
        {
            ["TValue"] = new(CanonicalGenericPlaceholderScope.Type, 0),
            ["TReference"] = new(CanonicalGenericPlaceholderScope.Type, 1),
        };
        var valueSelector = SymbolSignatureCanonicalizer.ParseSelectorType("TValue?", placeholders);
        var referenceSelector = SymbolSignatureCanonicalizer.ParseSelectorType("TReference?", placeholders);

        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            valueSelector,
            SymbolSignatureCanonicalizer.CanonicalizeType(value)));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            valueSelector,
            SymbolSignatureCanonicalizer.CanonicalizeType(nullableValue)));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            referenceSelector,
            SymbolSignatureCanonicalizer.CanonicalizeType(reference)));
    }

    [Fact]
    public void IsMatch_AppliesNamespaceAndTypeCasePoliciesIndependently()
    {
        var (simpleType, genericType) = GetCasePolicyParameterTypes();
        var simpleCandidate = SymbolSignatureCanonicalizer.CanonicalizeType(simpleType);
        var genericCandidate = SymbolSignatureCanonicalizer.CanonicalizeType(genericType);
        var namespaceCaseSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "game.models.Customer",
            EmptyPlaceholders());
        var typeCaseSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.customer",
            EmptyPlaceholders());
        var genericNamespaceCaseSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "game.models.Box<other.space.Customer>",
            EmptyPlaceholders());
        var genericTypeCaseSelector = SymbolSignatureCanonicalizer.ParseSelectorType(
            "Game.Models.box<Other.Space.customer>",
            EmptyPlaceholders());

        Assert.False(SymbolSignatureCanonicalizer.IsMatch(namespaceCaseSelector, simpleCandidate));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            namespaceCaseSelector,
            simpleCandidate,
            StringComparison.OrdinalIgnoreCase,
            StringComparison.Ordinal));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            namespaceCaseSelector,
            simpleCandidate,
            StringComparison.Ordinal,
            StringComparison.OrdinalIgnoreCase));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            typeCaseSelector,
            simpleCandidate,
            StringComparison.Ordinal,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            typeCaseSelector,
            simpleCandidate,
            StringComparison.OrdinalIgnoreCase,
            StringComparison.Ordinal));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            genericNamespaceCaseSelector,
            genericCandidate,
            StringComparison.OrdinalIgnoreCase,
            StringComparison.Ordinal));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            genericNamespaceCaseSelector,
            genericCandidate,
            StringComparison.Ordinal,
            StringComparison.OrdinalIgnoreCase));
        Assert.True(SymbolSignatureCanonicalizer.IsMatch(
            genericTypeCaseSelector,
            genericCandidate,
            StringComparison.Ordinal,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(SymbolSignatureCanonicalizer.IsMatch(
            genericTypeCaseSelector,
            genericCandidate,
            StringComparison.OrdinalIgnoreCase,
            StringComparison.Ordinal));
    }

    [Fact]
    public void IsMatch_RejectsCultureSensitiveCasePolicies()
    {
        var candidate = SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType("int"));
        var selector = SymbolSignatureCanonicalizer.ParseSelectorType("int", EmptyPlaceholders());

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SymbolSignatureCanonicalizer.IsMatch(
                selector,
                candidate,
                StringComparison.CurrentCulture,
                StringComparison.Ordinal));

        Assert.Equal("namespaceComparison", exception.ParamName);
    }

    [Fact]
    public void ParseSelectorType_ReportsStableDiagnosticCategories()
    {
        var invalidSyntax = Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType("int[", EmptyPlaceholders()));
        Assert.Equal("syntaxText", invalidSyntax.ParamName);
        Assert.Contains("Invalid C# type syntax", invalidSyntax.Message, StringComparison.Ordinal);

        var unqualifiedName = Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType("Customer", EmptyPlaceholders()));
        Assert.Equal("name", unqualifiedName.ParamName);
        Assert.Contains("must be fully qualified", unqualifiedName.Message, StringComparison.Ordinal);

        var invalidPlaceholder = Assert.Throws<ArgumentException>(() =>
            SymbolSignatureCanonicalizer.ParseSelectorType(
                "T",
                new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal)
                {
                    ["T"] = new(CanonicalGenericPlaceholderScope.Type, -1),
                }));
        Assert.Equal("genericPlaceholders", invalidPlaceholder.ParamName);
        Assert.Contains("non-empty and non-negative", invalidPlaceholder.Message, StringComparison.Ordinal);
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
        }).CreateMethod(method);

        Assert.Equal("System::Int32", signature.ConversionTargetType?.IdentityKey);
        Assert.Equal("int", signature.ConversionTargetType?.DisplayText);
        Assert.Equal("System::Int32", symbol.ConversionTypeKey);
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

    private static (ITypeSymbol Outer, ITypeSymbol Inner, ITypeSymbol Method)
        GetScopedPlaceholderParameterTypes()
    {
        const string source = """
            namespace Game.Models;

            public class Outer<TOuter>
            {
                public class Inner<TInner>
                {
                    public void Method<TMethod>(TOuter outer, TInner inner, TMethod method) { }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "ScopedPlaceholderSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var method = compilation.Assembly.GlobalNamespace
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Game")
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Models")
            .GetTypeMembers("Outer").Single()
            .GetTypeMembers("Inner").Single()
            .GetMembers("Method").OfType<IMethodSymbol>().Single();
        return (method.Parameters[0].Type, method.Parameters[1].Type, method.Parameters[2].Type);
    }

    private static (ITypeSymbol Outer, ITypeSymbol Middle, ITypeSymbol Inner)
        GetNestedExecutablePlaceholderParameterTypes()
    {
        const string source = """
            public static class Container
            {
                public static void Outer<TOuter>(TOuter value)
                {
                    void Middle<TMiddle>(TOuter outer, TMiddle middle)
                    {
                        void Inner<TInner>(TOuter outer, TMiddle middle, TInner inner) { }
                    }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "NestedExecutablePlaceholderSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var semanticModel = compilation.GetSemanticModel(tree);
        var localFunctions = tree.GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .ToDictionary(
                syntax => syntax.Identifier.ValueText,
                syntax => (IMethodSymbol)semanticModel.GetDeclaredSymbol(
                    syntax,
                    TestContext.Current.CancellationToken)!);
        var middle = localFunctions["Middle"];
        var inner = localFunctions["Inner"];
        return (middle.Parameters[0].Type, middle.Parameters[1].Type, inner.Parameters[2].Type);
    }

    private static (ITypeSymbol Simple, ITypeSymbol Generic) GetCasePolicyParameterTypes()
    {
        const string source = """
            namespace Other.Space
            {
                public class Customer { }
            }

            namespace Game.Models
            {
                public class Customer { }
                public class Box<T> { }

                public class Consumer
                {
                    public void Simple(Customer value) { }
                    public void Generic(Box<Other.Space.Customer> value) { }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "CasePolicySignatureTests",
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
            consumer.GetMembers("Simple").OfType<IMethodSymbol>().Single().Parameters[0].Type,
            consumer.GetMembers("Generic").OfType<IMethodSymbol>().Single().Parameters[0].Type);
    }

    private static (ITypeSymbol Value, ITypeSymbol NullableValue, ITypeSymbol Reference, ITypeSymbol NullableReference)
        GetConstrainedPlaceholderParameterTypes()
    {
        const string source = """
            #nullable enable

            public class Constraints<TValue, TReference>
                where TValue : struct
                where TReference : class
            {
                public void Method(
                    TValue value,
                    TValue? nullableValue,
                    TReference reference,
                    TReference? nullableReference) { }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "ConstrainedPlaceholderSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var parameters = compilation.Assembly.GlobalNamespace
            .GetTypeMembers("Constraints").Single()
            .GetMembers("Method").OfType<IMethodSymbol>().Single()
            .Parameters;
        return (parameters[0].Type, parameters[1].Type, parameters[2].Type, parameters[3].Type);
    }

    private static ITypeSymbol GetEscapedIdentifierParameterType(string typeName)
    {
        var source = $$"""
            namespace Game.Models;

            public sealed class @{{typeName}} { }
            public sealed class Consumer
            {
                public void Method(@{{typeName}} value) { }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "EscapedIdentifierSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var models = compilation.Assembly.GlobalNamespace
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Game")
            .GetNamespaceMembers().Single(candidate => candidate.Name == "Models");
        Assert.Single(models.GetTypeMembers(typeName));
        return models
            .GetTypeMembers("Consumer").Single()
            .GetMembers("Method").OfType<IMethodSymbol>().Single().Parameters[0].Type;
    }

    private static ITypeSymbol GetClassificationPrefixNamespaceParameterType(string namespaceName)
    {
        var source = $$"""
            namespace {{namespaceName}};

            public sealed class Marker { }
            public sealed class Consumer
            {
                public void Method(Marker value) { }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "ClassificationPrefixNamespaceSignatureTests",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return compilation.Assembly.GlobalNamespace
            .GetNamespaceMembers().Single(candidate => candidate.Name == namespaceName)
            .GetTypeMembers("Consumer").Single()
            .GetMembers("Method").OfType<IMethodSymbol>().Single().Parameters[0].Type;
    }

    private static void AssertFunctionPointerMatrix(IReadOnlyList<string> typeTexts)
    {
        var candidates = typeTexts
            .Select(typeText => SymbolSignatureCanonicalizer.CanonicalizeType(GetParameterType(typeText)))
            .ToArray();
        var selectors = typeTexts
            .Select(typeText => SymbolSignatureCanonicalizer.ParseSelectorType(typeText, EmptyPlaceholders()))
            .ToArray();

        for (var selectorIndex = 0; selectorIndex < selectors.Length; selectorIndex++)
        {
            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                Assert.Equal(
                    selectorIndex == candidateIndex,
                    SymbolSignatureCanonicalizer.IsMatch(selectors[selectorIndex], candidates[candidateIndex]));
            }
        }
    }

    private static IReadOnlyDictionary<string, CanonicalGenericPlaceholder> EmptyPlaceholders() =>
        new Dictionary<string, CanonicalGenericPlaceholder>(StringComparer.Ordinal);

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
