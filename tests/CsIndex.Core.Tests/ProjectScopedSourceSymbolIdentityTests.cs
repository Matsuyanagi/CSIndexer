using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CsIndex.Core.Tests;

public sealed class ProjectScopedSourceSymbolIdentityTests
{
    [Fact]
    public async Task ExtractAsync_RemovesLegacySourcePayloadFromEveryLogicalSymbol()
    {
        using var temporary = new TempDirectory();
        using var workspace = new AdhocWorkspace();
        var projects = CreateProjects(workspace, temporary);
        var snapshot = CreateSnapshot(temporary.Path);

        await new SemanticExtractor(new ProjectFingerprintBuilder()).ExtractAsync(
            projects,
            snapshot,
            includeDiagnostics: true,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(snapshot.Declarations);
        Assert.All(snapshot.Symbols.Values, symbol =>
        {
            Assert.Null(symbol.SourceDocumentKey);
            Assert.Null(symbol.SourceStart);
            Assert.Null(symbol.SourceLength);
        });
    }

    [Fact]
    public async Task ExtractAsync_ScopesSameAssemblySourceSymbolsByProjectKey()
    {
        using var temporary = new TempDirectory();
        using var workspace = new AdhocWorkspace();
        var projects = CreateProjects(workspace, temporary);
        var snapshot = CreateSnapshot(temporary.Path);

        await new SemanticExtractor(new ProjectFingerprintBuilder()).ExtractAsync(
            projects,
            snapshot,
            includeDiagnostics: true,
            TestContext.Current.CancellationToken);

        var firstProjectKey = Assert.Single(snapshot.Projects, project => project.Name == "First").Key;
        var secondProjectKey = Assert.Single(snapshot.Projects, project => project.Name == "Second").Key;
        var runs = snapshot.Symbols.Values
            .Where(symbol => symbol.Kind == IndexedSymbolKind.Method && symbol.DisplayName == "Shared.Twin::Run()")
            .ToArray();

        Assert.Equal(2, runs.Length);
        Assert.Equal(
            [firstProjectKey, secondProjectKey],
            runs.Select(symbol => symbol.ProjectKey).OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(2, runs.Select(symbol => symbol.StableKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(runs, symbol => NormalizedSource(snapshot, symbol).Contains(
            "LocalFirst",
            StringComparison.Ordinal));
        Assert.Contains(runs, symbol => NormalizedSource(snapshot, symbol).Contains(
            "LocalSecond",
            StringComparison.Ordinal));

        foreach (var run in runs)
        {
            var containingType = snapshot.Symbols[Assert.IsType<string>(run.ContainingSymbolKey)];
            Assert.Equal(run.ProjectKey, containingType.ProjectKey);
            var declaration = snapshot.Declarations[run.PreferredDeclarationKey!];
            Assert.Equal(run.StableKey, declaration.SymbolKey);
            Assert.Equal(DeclarationRole.Ordinary, declaration.Role);
            Assert.Null(run.SourceDocumentKey);
            Assert.Null(run.SourceStart);
            Assert.Null(run.SourceLength);
            Assert.Equal(run.IsGenerated, declaration.IsGenerated);
            Assert.NotEmpty(NormalizedSource(snapshot, run));

            var localCall = Assert.Single(snapshot.Calls, call =>
                call.CallerSymbolKey == run.StableKey &&
                call.CalleeDefinitionKey is { } definitionKey &&
                snapshot.Symbols[definitionKey].Name.StartsWith("Local", StringComparison.Ordinal));
            Assert.Equal(run.ProjectKey, snapshot.Symbols[localCall.CalleeDefinitionKey!].ProjectKey);

            Assert.Single(snapshot.Symbols.Values, symbol =>
                symbol.Kind == IndexedSymbolKind.Lambda &&
                symbol.ContainingSymbolKey == run.StableKey);
        }

        var consumer = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method && symbol.DisplayName == "Shared.Consumer::Execute()");
        var genericCall = Assert.Single(snapshot.Calls, call =>
            call.CallerSymbolKey == consumer.StableKey && call.CalleeDefinitionKey is not null);
        var genericDefinition = snapshot.Symbols[genericCall.CalleeDefinitionKey!];
        Assert.Equal("Generic", genericDefinition.Name);
        Assert.Equal("CrossProjectTarget", genericDefinition.TypeSimpleName);
        Assert.Equal(firstProjectKey, genericDefinition.ProjectKey);
        var genericParameter = Assert.Single(genericDefinition.Parameters);
        Assert.Equal("^0", genericParameter.TypeKey);
        Assert.Equal("T", genericParameter.TypeDisplay);
        Assert.Equal("System::Void", genericDefinition.ReturnTypeKey);
        Assert.Equal("void", genericDefinition.ReturnTypeDisplay);

        var consumerType = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Type &&
            symbol.DisplayName == "Shared.Consumer");
        var contractBinding = Assert.Single(snapshot.InterfaceMethodBindings, binding =>
            binding.ImplementingTypeKey == consumerType.StableKey &&
            binding.ImplementationMethodKey == consumer.StableKey);
        var contractMethod = snapshot.Symbols[contractBinding.InterfaceMethodKey];
        Assert.Equal("Shared.IContract::Execute()", contractMethod.DisplayName);
        Assert.Equal(firstProjectKey, contractMethod.ProjectKey);

        var objectToString = Assert.Single(snapshot.Symbols.Values, symbol =>
            symbol.Kind == IndexedSymbolKind.Method &&
            symbol.Name == "ToString" &&
            symbol.TypeMetadataName == "Object");
        Assert.Null(objectToString.ProjectKey);
        Assert.DoesNotContain("|project:", objectToString.StableKey, StringComparison.Ordinal);
    }

    private static string NormalizedSource(IndexSnapshot snapshot, SymbolData symbol)
    {
        var declaration = snapshot.Declarations[symbol.PreferredDeclarationKey!];
        var document = snapshot.Documents.Single(value => value.Key == declaration.DocumentKey);
        return document.NormalizedSource.AsSpan(
            declaration.NormalizedStart,
            declaration.NormalizedLength).ToString();
    }

    private static IReadOnlyList<Project> CreateProjects(AdhocWorkspace workspace, TempDirectory temporary)
    {
        const string firstSource = """
            using System;

            namespace Shared;

            public interface IContract
            {
                void Execute();
            }

            public static class CrossProjectTarget
            {
                public static void Generic<T>(T value) { }
            }

            public sealed class Twin
            {
                public void Run()
                {
                    LocalFirst();
                    Action nested = () => LocalFirst();
                    _ = new object().ToString();
                }

                private static void LocalFirst() { }
            }
            """;
        const string secondSource = """
            using System;

            namespace Shared;

            public sealed class Twin
            {
                public void Run()
                {
                    LocalSecond();
                    Action nested = () => LocalSecond();
                    _ = new object().ToString();
                }

                private static void LocalSecond() { }
            }

            public sealed class Consumer : IContract
            {
                public void Execute()
                {
                    CrossProjectTarget.Generic(1);
                }
            }
            """;

        var firstProjectPath = temporary.Write("First/First.csproj", "<Project />");
        var firstSourcePath = temporary.Write("First/Twin.cs", firstSource);
        var secondProjectPath = temporary.Write("Second/Second.csproj", "<Project />");
        var secondSourcePath = temporary.Write("Second/Twin.cs", secondSource);
        var firstProjectId = ProjectId.CreateNewId("First");
        var secondProjectId = ProjectId.CreateNewId("Second");
        var firstDocumentId = DocumentId.CreateNewId(firstProjectId, "Twin.cs");
        var secondDocumentId = DocumentId.CreateNewId(secondProjectId, "Twin.cs");
        var references = GetPlatformReferences();

        var solution = workspace.CurrentSolution
            .AddProject(CreateProjectInfo(firstProjectId, "First", firstProjectPath, references))
            .AddProject(CreateProjectInfo(secondProjectId, "Second", secondProjectPath, references))
            .AddDocument(firstDocumentId, "Twin.cs", SourceText.From(firstSource), filePath: firstSourcePath)
            .AddDocument(secondDocumentId, "Twin.cs", SourceText.From(secondSource), filePath: secondSourcePath)
            .AddProjectReference(secondProjectId, new ProjectReference(firstProjectId));
        Assert.True(workspace.TryApplyChanges(solution));

        return
        [
            workspace.CurrentSolution.GetProject(firstProjectId)!,
            workspace.CurrentSolution.GetProject(secondProjectId)!,
        ];
    }

    private static ProjectInfo CreateProjectInfo(
        ProjectId projectId,
        string name,
        string projectPath,
        IReadOnlyList<MetadataReference> references) =>
        ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            "DuplicateAssembly",
            LanguageNames.CSharp,
            filePath: projectPath,
            outputFilePath: Path.ChangeExtension(projectPath, ".dll"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: references);

    private static IReadOnlyList<MetadataReference> GetPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private static IndexSnapshot CreateSnapshot(string root) => new()
    {
        InputRoot = root,
        IndexRootAnchor = ".",
        InputFingerprint = [],
        RequestHash = [],
        Profile = new AnalysisProfileData
        {
            Name = "test",
            InputMode = InputMode.Solution,
            TargetFramework = "net10.0",
            OperatingSystem = "Windows",
            Architecture = "x64",
            PreprocessorSymbols = [],
            ProfileHash = [],
        },
    };
}
