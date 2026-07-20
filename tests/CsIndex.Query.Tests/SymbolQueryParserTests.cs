using CsIndex.Query.Symbols;

namespace CsIndex.Query.Tests;

public sealed class SymbolQueryParserTests
{
    [Fact]
    public void Parse_NormalizesKeywordParameterTypes()
    {
        var query = new SymbolQueryParser().Parse("Player::Play(string)");

        Assert.Equal("Player", query.TypeSimpleName);
        Assert.Equal("Play", query.MethodName);
        Assert.Equal(["System.String"], query.ParameterTypes);
    }

    [Fact]
    public void Parse_DistinguishesOmittedAndEmptyParameterLists()
    {
        var omitted = new SymbolQueryParser().Parse("Player::Play");
        var empty = new SymbolQueryParser().Parse("Player::Play()");

        Assert.Null(omitted.ParameterTypes);
        Assert.Empty(empty.ParameterTypes!);
    }

    [Fact]
    public void Parse_RejectsReservedGenericTypeSyntax()
    {
        Assert.Throws<SymbolQueryParseException>(() =>
            new SymbolQueryParser().Parse("Container<int>::Run()"));
    }
}
