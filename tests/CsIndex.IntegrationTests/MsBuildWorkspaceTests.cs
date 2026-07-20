using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;

namespace CsIndex.IntegrationTests;

public sealed class MsBuildWorkspaceTests
{
    [Fact]
    public async Task ProjectMode_LoadsPhysicalDocumentsThroughMsBuildWorkspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var projectPath = Path.Combine(
            repositoryRoot,
            "tests",
            "CsIndex.Query.Tests",
            "CsIndex.Query.Tests.csproj");
        var options = new IndexOptions
        {
            InputPath = projectPath,
            ForcedMode = InputMode.Project,
        };
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(input, options, cancellationToken);

        var result = await coordinator.AnalyzeAsync(
            input,
            options,
            fingerprint,
            RequestHasher.Build(input, options),
            cancellationToken);

        Assert.Equal(InputMode.Project, result.Snapshot.Profile.InputMode);
        Assert.NotEmpty(result.Snapshot.Projects);
        Assert.NotEmpty(result.Snapshot.Documents);
        Assert.NotEmpty(result.Snapshot.Symbols);
        Assert.All(result.Snapshot.CompilationSummaries, summary => Assert.Equal(0, summary.Errors));
    }
}
