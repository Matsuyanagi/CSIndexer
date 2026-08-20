using CsIndex.Core.Analysis;
using CsIndex.Core.Input;
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

    [Fact]
    public async Task ProfileHash_UnavailableReferenceIdentityIgnoresRuntimeRoot()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var firstReference = Path.Combine(first.Path, "missing", "Shared.Reference.dll");
        var secondReference = Path.Combine(second.Path, "missing", "Shared.Reference.dll");

        var firstProfile = await BuildProfileAsync(first, firstReference);
        var secondProfile = await BuildProfileAsync(second, secondReference);

        Assert.Equal(firstProfile.ProfileHash, secondProfile.ProfileHash);
    }

    [Fact]
    public async Task ProfileHash_SameNamedReferenceContentChangesIdentity()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var firstReference = first.Write(@"references\Shared.Reference.dll", "first content");
        var secondReference = second.Write(@"references\Shared.Reference.dll", "second content");

        var firstProfile = await BuildProfileAsync(first, firstReference);
        var secondProfile = await BuildProfileAsync(second, secondReference);

        Assert.NotEqual(firstProfile.ProfileHash, secondProfile.ProfileHash);
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

    private static Task<AnalysisProfileData> BuildProfileAsync(
        TempDirectory temporary,
        string metadataReference)
    {
        var input = new ResolvedInput(InputMode.Directory, temporary.Path, temporary.Path, []);
        var options = new IndexOptions
        {
            InputPath = temporary.Path,
            ForcedMode = InputMode.Directory,
            ProfileName = "portable-reference-test",
        };
        return new AnalysisProfileBuilder().BuildAsync(
            input,
            options,
            [],
            [metadataReference],
            TestContext.Current.CancellationToken);
    }
}
