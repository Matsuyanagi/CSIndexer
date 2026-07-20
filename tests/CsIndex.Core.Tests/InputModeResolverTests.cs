using CsIndex.Core.Input;
using CsIndex.Core.Model;

namespace CsIndex.Core.Tests;

public sealed class InputModeResolverTests
{
    [Fact]
    public void Resolve_DirectoryWithoutBuildFiles_UsesDirectoryMode()
    {
        using var temporary = new TempDirectory();
        temporary.Write("Source.cs", "class Source;");

        var result = new InputModeResolver().Resolve(new IndexOptions { InputPath = temporary.Path });

        Assert.Equal(InputMode.Directory, result.Mode);
    }

    [Fact]
    public void Resolve_MultipleSolutions_RequiresExplicitSelection()
    {
        using var temporary = new TempDirectory();
        temporary.Write("A.sln");
        temporary.Write("nested/B.slnx");

        var exception = Assert.Throws<InputResolutionException>(() =>
            new InputModeResolver().Resolve(new IndexOptions { InputPath = temporary.Path }));

        Assert.Contains("Multiple solutions", exception.Message);
        Assert.Contains("A.sln", exception.Message);
        Assert.Contains("B.slnx", exception.Message);
    }

    [Fact]
    public void Resolve_MultipleProjects_ReturnsAllInDeterministicOrder()
    {
        using var temporary = new TempDirectory();
        temporary.Write("Z/Z.csproj");
        temporary.Write("A/A.csproj");

        var result = new InputModeResolver().Resolve(new IndexOptions { InputPath = temporary.Path });

        Assert.Equal(InputMode.Project, result.Mode);
        Assert.Equal(2, result.EntryPaths.Count);
        Assert.EndsWith(Path.Combine("A", "A.csproj"), result.EntryPaths[0]);
    }
}
