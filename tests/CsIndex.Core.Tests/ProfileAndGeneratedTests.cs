using CsIndex.Core.Analysis;
using CsIndex.Core.Model;
using CsIndex.Core.Profiles;
using Microsoft.CodeAnalysis.Text;

namespace CsIndex.Core.Tests;

public sealed class ProfileAndGeneratedTests
{
    [Fact]
    public void DirectoryProfile_DefinesWindowsButNotNetByDefault()
    {
        var symbols = AnalysisProfileBuilder.BuildDirectorySymbols(new IndexOptions { InputPath = "." });

        Assert.Contains("WINDOWS", symbols);
        Assert.DoesNotContain("NET10_0", symbols);
        Assert.DoesNotContain("NET10_0_OR_GREATER", symbols);
    }

    [Fact]
    public void DirectoryProfile_TargetFrameworkAddsFrameworkSymbols()
    {
        var symbols = AnalysisProfileBuilder.BuildDirectorySymbols(new IndexOptions
        {
            InputPath = ".",
            TargetFramework = "net10.0-windows",
        });

        Assert.Contains("NET10_0", symbols);
        Assert.Contains("NET10_0_OR_GREATER", symbols);
        Assert.Contains("WINDOWS", symbols);
    }

    [Theory]
    [InlineData("Thing.g.cs")]
    [InlineData("Thing.generated.cs")]
    [InlineData("Thing.Designer.cs")]
    public void GeneratedDetector_RecognizesPhysicalGeneratedFileNames(string fileName)
    {
        var result = GeneratedCodeDetector.Detect(fileName, SourceText.From("class Thing;"));

        Assert.True(result.IsGenerated);
    }
}
