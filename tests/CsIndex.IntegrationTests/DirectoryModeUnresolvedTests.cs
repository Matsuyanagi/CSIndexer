using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;

namespace CsIndex.IntegrationTests;

public sealed class DirectoryModeUnresolvedTests
{
    [Fact]
    public async Task MissingMetadataStillProducesUnresolvedCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "csindex-unresolved-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "Missing.cs"),
                "public class Caller { public void Execute(MissingType value) { value.Play(); } }",
                cancellationToken);
            var options = new IndexOptions { InputPath = root, ForcedMode = InputMode.Directory };
            var coordinator = AnalysisCoordinator.CreateDefault();
            var input = coordinator.ResolveInput(options);
            var fingerprint = await coordinator.BuildInputFingerprintAsync(input, options, cancellationToken);

            var result = await coordinator.AnalyzeAsync(
                input,
                options,
                fingerprint,
                RequestHasher.Build(input, options),
                cancellationToken);

            var call = Assert.Single(result.Snapshot.Calls);
            Assert.Equal(ResolutionStatus.Unresolved, call.ResolutionStatus);
            Assert.Equal(ResolutionReason.MissingMetadataReference, call.ResolutionReason);
            Assert.Equal("value.Play", call.UnresolvedName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
