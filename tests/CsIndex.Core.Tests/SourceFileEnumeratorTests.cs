using CsIndex.Core.Input;

namespace CsIndex.Core.Tests;

public sealed class SourceFileEnumeratorTests
{
    [Fact]
    public void Enumerate_AlwaysExcludesObj_ButIncludesBinAndGenerated()
    {
        using var temporary = new TempDirectory();
        var source = temporary.Write("src/Main.cs");
        var bin = temporary.Write("bin/Bin.cs");
        var generated = temporary.Write("Generated/Generated.g.cs");
        temporary.Write("obj/Root.cs");
        temporary.Write("src/Obj/Nested.cs");

        var result = new SourceFileEnumerator().Enumerate(temporary.Path, []);

        Assert.Contains(result.IncludedFiles, path =>
            path.EndsWith(Path.Combine("src", "Main.cs"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.IncludedFiles, path =>
            path.EndsWith(Path.Combine("bin", "Bin.cs"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.IncludedFiles, path =>
            path.EndsWith(Path.Combine("Generated", "Generated.g.cs"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.ExcludedFiles.Count);
    }

    [Fact]
    public void Enumerate_AppliesRecursiveGlobExclusions()
    {
        using var temporary = new TempDirectory();
        temporary.Write("Assets/Game.cs");
        temporary.Write("Library/PackageCache/Package.cs");
        temporary.Write("nested/Temp/Temporary.cs");

        var result = new SourceFileEnumerator().Enumerate(
            temporary.Path,
            ["**/Library/PackageCache/**", "**/Temp/**"]);

        Assert.Single(result.IncludedFiles);
        Assert.EndsWith(Path.Combine("Assets", "Game.cs"), result.IncludedFiles[0]);
    }

    [Fact]
    public void Enumerate_HandlesMoreThanFiveThousandFiles()
    {
        using var temporary = new TempDirectory();
        for (var index = 0; index < 5001; index++)
        {
            temporary.Write($"Source/F{index:D4}.cs", $"class C{index};");
        }

        var result = new SourceFileEnumerator().Enumerate(temporary.Path, []);

        Assert.Equal(5001, result.IncludedFiles.Count);
    }
}
