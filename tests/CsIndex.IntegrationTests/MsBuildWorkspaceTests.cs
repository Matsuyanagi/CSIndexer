using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
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
            "src",
            "CsIndex.Query",
            "CsIndex.Query.csproj");
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

    [Fact]
    public async Task ProjectMode_KeepsExternalGeneratedReporterCompilationOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var projectPath = Path.Combine(
            repositoryRoot,
            "tests",
            "CsIndex.Core.Tests",
            "CsIndex.Core.Tests.csproj");
        var options = new IndexOptions
        {
            InputPath = projectPath,
            ForcedMode = InputMode.Project,
        };
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var paths = IndexPathResolver.CreateForIndex(
            Path.Combine(input.RootPath, ".csindex", "index.sqlite"),
            input.RootPath);

        using var prepared = await coordinator.PrepareAsync(
            input,
            options,
            paths,
            cancellationToken);
        var reporter = Assert.Single(
            prepared.Projects.SelectMany(project => project.Documents),
            document => document.FilePath is not null &&
                Path.GetFileName(document.FilePath!)
                    .Equals("DefaultRunnerReporters.cs", StringComparison.OrdinalIgnoreCase));

        var crossVolume = !PathNormalizer.SameVolumeShare(input.RootPath, reporter.FilePath!);
        if (crossVolume)
        {
            Assert.True(prepared.DocumentSelection.IsCompilationOnly(reporter));
            Assert.Equal(1, prepared.DocumentSelection.CompilationOnlyCount);
            Assert.Equal(1, prepared.DocumentsExcluded);
            Assert.Contains(prepared.Warnings,
                warning => warning.Contains(
                    "DefaultRunnerReporters.cs",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Throws<InputResolutionException>(() => prepared.Mappings.GetDocumentPath(reporter));
        }
        else
        {
            Assert.True(prepared.DocumentSelection.IsIndexable(reporter));
            Assert.Equal(
                PathNormalizer.RelativePath(input.RootPath, reporter.FilePath!),
                prepared.Mappings.GetDocumentPath(reporter));
        }

        var result = await coordinator.AnalyzeAsync(
            prepared,
            options,
            [],
            RequestHasher.Build(input, options),
            cancellationToken);
        if (crossVolume)
        {
            Assert.DoesNotContain(
                result.Snapshot.Documents,
                document => document.NormalizedPath.Contains(
                    "DefaultRunnerReporters.cs",
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(result.Snapshot.DocumentsExcluded >= prepared.DocumentsExcluded);
            Assert.Contains(
                result.Snapshot.Warnings,
                warning => warning.Contains(
                    "DefaultRunnerReporters.cs",
                    StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var reporterDocument = Assert.Single(
                result.Snapshot.Documents,
                document => document.NormalizedPath.EndsWith(
                    "DefaultRunnerReporters.cs",
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(reporterDocument.IsGenerated);
        }
    }
}
