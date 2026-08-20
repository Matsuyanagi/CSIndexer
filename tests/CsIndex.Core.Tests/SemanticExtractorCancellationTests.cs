using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CsIndex.Core.Tests;

public sealed class SemanticExtractorCancellationTests
{
    [Fact]
    public async Task ExtractAsync_ObservesCancellationDuringDeclarationFinalization()
    {
        const string source = """
            public sealed class Host
            {
                public void First() { }
                public void Second() { }
                public void Third() { }
            }
            """;
        using var temporary = new TempDirectory();
        var sourcePath = temporary.Write("Source.cs", source);
        var projectPath = temporary.Write("Host.csproj", "<Project />");
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("FinalizationCancellation");
        var references = GetPlatformReferences();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "FinalizationCancellation",
                "FinalizationCancellation",
                LanguageNames.CSharp,
                filePath: projectPath,
                outputFilePath: Path.ChangeExtension(projectPath, ".dll"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
                metadataReferences: references))
            .AddDocument(
                DocumentId.CreateNewId(projectId, "Source.cs"),
                "Source.cs",
                SourceText.From(source),
                filePath: sourcePath);
        Assert.True(workspace.TryApplyChanges(solution));

        using var cancellation = new CancellationTokenSource();
        var extractor = new SemanticExtractor(new ProjectFingerprintBuilder());
        var observedPhase = (DeclarationFinalizationPhase?)null;
        extractor.AfterDeclarationFinalizationItem = phase =>
        {
            if (phase == DeclarationFinalizationPhase.Projection)
            {
                observedPhase = phase;
                cancellation.Cancel();
            }
        };
        var snapshot = CreateSnapshot(temporary.Path);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        await Assert.ThrowsAsync<OperationCanceledException>(() => extractor.ExtractAsync(
            [project],
            snapshot,
            includeDiagnostics: true,
            cancellation.Token));

        Assert.Equal(DeclarationFinalizationPhase.Projection, observedPhase);
    }

    [Fact]
    public async Task ExtractAsync_ObservesCancellationDuringNestedExecutableTraversal()
    {
        const string source = """
            using System;

            public sealed class Host
            {
                public void Run()
                {
                    void Local() { }
                    Action callback = () => { };
                    callback();
                }
            }
            """;
        using var temporary = new TempDirectory();
        var sourcePath = temporary.Write("Source.cs", source);
        var projectPath = temporary.Write("Host.csproj", "<Project />");
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Cancellation");
        var references = GetPlatformReferences();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "Cancellation",
                "Cancellation",
                LanguageNames.CSharp,
                filePath: projectPath,
                outputFilePath: Path.ChangeExtension(projectPath, ".dll"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
                metadataReferences: references))
            .AddDocument(
                DocumentId.CreateNewId(projectId, "Source.cs"),
                "Source.cs",
                SourceText.From(source),
                filePath: sourcePath);
        Assert.True(workspace.TryApplyChanges(solution));

        using var cancellation = new CancellationTokenSource();
        var extractor = new SemanticExtractor(new ProjectFingerprintBuilder());
        var callbackCount = 0;
        extractor.AfterNestedExecutableOwner = () =>
        {
            callbackCount++;
            cancellation.Cancel();
        };
        var snapshot = CreateSnapshot(temporary.Path);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        await Assert.ThrowsAsync<OperationCanceledException>(() => extractor.ExtractAsync(
            [project],
            snapshot,
            includeDiagnostics: true,
            cancellation.Token));

        Assert.True(callbackCount > 0);
    }

    private static IndexSnapshot CreateSnapshot(string root) => new()
    {
        InputRoot = root,
        InputFingerprint = [],
        RequestHash = [],
        Profile = new AnalysisProfileData
        {
            Name = "test",
            InputMode = InputMode.Directory,
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        },
    };

    private static IReadOnlyList<MetadataReference> GetPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();
}
