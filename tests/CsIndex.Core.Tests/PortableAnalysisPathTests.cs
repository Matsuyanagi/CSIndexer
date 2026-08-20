using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Profiles;
using CsIndex.Core.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CsIndex.Core.Tests;

public sealed class PortableAnalysisPathTests
{
    private const string MainSource = """
        using System;

        namespace Demo;

        public sealed class Main
        {
            private readonly Action _callback = () => { };

            public void Run()
            {
                Action local = () => { };
                local();
            }
        }
        """;

    [Fact]
    public async Task PreparedAnalysis_LoadsWorkspaceOnceAndAnalyzeReusesIt()
    {
        using var temporary = new TempDirectory();
        var sourcePath = temporary.Write(@"src\Main.cs", MainSource);
        var input = new ResolvedInput(InputMode.Directory, temporary.Path, temporary.Path, []);
        var options = new IndexOptions { InputPath = temporary.Path, ForcedMode = InputMode.Directory };
        var paths = CreateStandardPaths(temporary.Path);
        var loaded = CreateLoadedWorkspace(null, (sourcePath, MainSource));
        var loadCount = 0;
        var coordinator = AnalysisCoordinator.CreateForTesting((_, _, _) =>
        {
            loadCount++;
            return Task.FromResult(loaded);
        });

        using var prepared = await coordinator.PrepareAsync(
            input,
            options,
            paths,
            TestContext.Current.CancellationToken);
        var result = await coordinator.AnalyzeAsync(
            prepared,
            options,
            [],
            [],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, loadCount);
        Assert.Equal(".", result.Snapshot.InputRoot);
        Assert.Equal("..", result.Snapshot.IndexRootAnchor);
        Assert.Contains(result.Snapshot.Documents, document => document.NormalizedPath == "src/Main.cs");

        prepared.Dispose();
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = prepared.Projects.Count;
        });
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.AnalyzeAsync(
            prepared,
            options,
            [],
            [],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrepareAndAnalyze_UseStoredPathsForProjectsDocumentsDeclarationsAndSyntheticKeys()
    {
        using var temporary = new TempDirectory();
        var storageRoot = Path.Combine(temporary.Path, "Game");
        var projectPath = temporary.Write(@"Game\Game.csproj", "<Project />");
        var sourcePath = temporary.Write(@"Game\src\Main.cs", MainSource);
        var linkedPath = temporary.Write(
            @"Shared\Linked.cs",
            "namespace Shared; public sealed class Linked { public void Run() { } }");
        var input = new ResolvedInput(InputMode.Project, projectPath, storageRoot, [projectPath]);
        var options = new IndexOptions { InputPath = projectPath, ForcedMode = InputMode.Project };
        var paths = CreateStandardPaths(storageRoot);
        var loaded = CreateLoadedWorkspace(
            projectPath,
            (sourcePath, MainSource),
            (linkedPath, File.ReadAllText(linkedPath)));
        var coordinator = AnalysisCoordinator.CreateForTesting((_, _, _) => Task.FromResult(loaded));

        using var prepared = await coordinator.PrepareAsync(
            input,
            options,
            paths,
            TestContext.Current.CancellationToken);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(
            input,
            options,
            paths,
            TestContext.Current.CancellationToken);
        var snapshot = (await coordinator.AnalyzeAsync(
            prepared,
            options,
            fingerprint,
            [],
            TestContext.Current.CancellationToken)).Snapshot;

        var project = Assert.Single(snapshot.Projects);
        Assert.Equal("project-path:Game.csproj", project.Key);
        Assert.Equal("Game.csproj", project.ProjectPath);
        Assert.Equal(".", snapshot.InputRoot);
        Assert.Equal("..", snapshot.IndexRootAnchor);
        Assert.Collection(
            snapshot.Documents.OrderBy(document => document.NormalizedPath, StringComparer.Ordinal),
            document =>
            {
                Assert.Equal("../Shared/Linked.cs", document.NormalizedPath);
                Assert.Equal("project-path:Game.csproj|document:../Shared/Linked.cs", document.Key);
            },
            document =>
            {
                Assert.Equal("src/Main.cs", document.NormalizedPath);
                Assert.Equal("project-path:Game.csproj|document:src/Main.cs", document.Key);
            });

        var run = Assert.Single(snapshot.Symbols.Values, symbol => symbol.DisplayName == "Demo.Main::Run()");
        var declaration = snapshot.Declarations[Assert.IsType<string>(run.PreferredDeclarationKey)];
        Assert.Equal("project-path:Game.csproj|document:src/Main.cs", declaration.DocumentKey);
        Assert.Contains($"{run.StableKey}|declaration:src/Main.cs:", declaration.Key, StringComparison.Ordinal);
        Assert.Null(run.SourceDocumentKey);
        Assert.Null(run.SourceStart);
        Assert.Null(run.SourceLength);
        Assert.Null(run.NormalizedSource);
        Assert.Null(run.NormalizedSourceHash);
        Assert.Contains(snapshot.Symbols.Keys, key =>
            key.Contains("|document:src/Main.cs|", StringComparison.Ordinal));

        AssertPortableSnapshotPaths(snapshot, temporary.Path);
    }

    [Fact]
    public async Task Prepare_RejectsCrossVolumeDocumentDisposesWorkspaceAndSkipsContinuation()
    {
        using var temporary = new TempDirectory();
        var projectPath = temporary.Write(@"Game\Game.csproj", "<Project />");
        var crossVolumePath = CreateDifferentDrivePath(temporary.Path, @"Outside\Linked.cs");
        var loaded = CreateLoadedWorkspace(
            projectPath,
            (crossVolumePath, "namespace Outside; public sealed class Linked { }"));
        var input = new ResolvedInput(InputMode.Project, projectPath, temporary.Path, [projectPath]);
        var options = new IndexOptions { InputPath = projectPath, ForcedMode = InputMode.Project };
        var paths = CreateStandardPaths(temporary.Path);
        var loadCount = 0;
        var databaseOpenSentinel = false;
        var coordinator = AnalysisCoordinator.CreateForTesting((_, _, _) =>
        {
            loadCount++;
            return Task.FromResult(loaded);
        });

        async Task PrepareThenOpenDatabaseAsync()
        {
            using var prepared = await coordinator.PrepareAsync(
                input,
                options,
                paths,
                TestContext.Current.CancellationToken);
            databaseOpenSentinel = true;
        }

        var exception = await Assert.ThrowsAsync<InputResolutionException>(PrepareThenOpenDatabaseAsync);

        Assert.Equal(1, loadCount);
        Assert.False(databaseOpenSentinel);
        Assert.Contains(crossVolumePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same-volume/share", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(loaded.IsDisposed);
    }

    [Fact]
    public void SymbolCanonicalizer_UnmappedSourceFallbackFailsWithoutEmbeddingRuntimePath()
    {
        using var temporary = new TempDirectory();
        var unmappedPath = Path.Combine(temporary.Path, "Unmapped.cs");
        var tree = CSharpSyntaxTree.ParseText(
            "public sealed class Host { public void Run() { System.Action a = () => { }; } }",
            new CSharpParseOptions(LanguageVersion.Latest),
            unmappedPath,
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "Fallback",
            [tree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var local = Assert.Single(tree.GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>());
        var localSymbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(
            local,
            TestContext.Current.CancellationToken));
        Assert.Null(localSymbol.GetDocumentationCommentId());
        var canonicalizer = new SymbolCanonicalizer(
            CreateProfile(),
            _ => throw new InputResolutionException("Source tree was not present in the validated path map."));

        var exception = Assert.Throws<InputResolutionException>(() =>
        {
            _ = canonicalizer.GetDefinitionStableKey(localSymbol, "project-name:Fallback");
        });

        Assert.DoesNotContain(unmappedPath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelocatedIdenticalLayoutsProduceEquivalentPortableIdentitiesAndFingerprints()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        var firstResult = await AnalyzeLayoutAsync(first);
        var secondResult = await AnalyzeLayoutAsync(second);

        Assert.Equal(firstResult.InputFingerprint, secondResult.InputFingerprint);
        Assert.Equal(".", firstResult.Snapshot.InputRoot);
        Assert.Equal(".", secondResult.Snapshot.InputRoot);
        Assert.Equal(
            firstResult.Snapshot.Projects.Select(ProjectIdentity),
            secondResult.Snapshot.Projects.Select(ProjectIdentity));
        Assert.Equal(
            firstResult.Snapshot.Documents.Select(DocumentIdentity),
            secondResult.Snapshot.Documents.Select(DocumentIdentity));
        Assert.Equal(
            firstResult.Snapshot.Symbols.Keys.Order(StringComparer.Ordinal),
            secondResult.Snapshot.Symbols.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            firstResult.Snapshot.Declarations.Keys.Order(StringComparer.Ordinal),
            secondResult.Snapshot.Declarations.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringValidationAndFingerprintEnumerationIsObserved()
    {
        using var temporary = new TempDirectory();
        temporary.Write(@"src\First.cs", "public sealed class First { }");
        temporary.Write(@"src\Second.cs", "public sealed class Second { }");
        var input = new ResolvedInput(InputMode.Directory, temporary.Path, temporary.Path, []);
        var options = new IndexOptions { InputPath = temporary.Path, ForcedMode = InputMode.Directory };
        var paths = CreateStandardPaths(temporary.Path);
        var inputFingerprintBuilder = new InputFingerprintBuilder();
        var coordinator = new AnalysisCoordinator(
            new InputModeResolver(),
            new WorkspaceLoader(new SourceFileEnumerator()),
            inputFingerprintBuilder,
            new AnalysisProfileBuilder(),
            new SemanticExtractor(new ProjectFingerprintBuilder()));

        using var validationCancellation = new CancellationTokenSource();
        var validationItems = 0;
        coordinator.AfterPathValidationItem = () =>
        {
            validationItems++;
            validationCancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.PrepareAsync(
            input,
            options,
            paths,
            validationCancellation.Token));
        Assert.Equal(1, validationItems);

        using var fingerprintCancellation = new CancellationTokenSource();
        var fingerprintItems = 0;
        inputFingerprintBuilder.AfterFileFingerprintItem = () =>
        {
            fingerprintItems++;
            fingerprintCancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.BuildInputFingerprintAsync(
            input,
            options,
            paths,
            fingerprintCancellation.Token));
        Assert.Equal(1, fingerprintItems);
    }

    private static async Task<(byte[] InputFingerprint, IndexSnapshot Snapshot)> AnalyzeLayoutAsync(
        TempDirectory temporary)
    {
        var storageRoot = Path.Combine(temporary.Path, "Layout");
        var projectPath = temporary.Write(@"Layout\App.csproj", "<Project />");
        var sourcePath = temporary.Write(@"Layout\src\Main.cs", MainSource);
        var relocatedCoreLibrary = temporary.Copy(
            @"Layout\references\System.Private.CoreLib.dll",
            typeof(object).Assembly.Location);
        var input = new ResolvedInput(InputMode.Project, projectPath, storageRoot, [projectPath]);
        var options = new IndexOptions { InputPath = projectPath, ForcedMode = InputMode.Project };
        var paths = CreateStandardPaths(storageRoot);
        var loaded = CreateLoadedWorkspaceWithCoreLibraryOverride(
            projectPath,
            relocatedCoreLibrary,
            (sourcePath, MainSource));
        var coordinator = AnalysisCoordinator.CreateForTesting((_, _, _) => Task.FromResult(loaded));
        using var prepared = await coordinator.PrepareAsync(
            input,
            options,
            paths,
            TestContext.Current.CancellationToken);
        var fingerprint = await coordinator.BuildInputFingerprintAsync(
            input,
            options,
            paths,
            TestContext.Current.CancellationToken);
        var snapshot = (await coordinator.AnalyzeAsync(
            prepared,
            options,
            fingerprint,
            [],
            TestContext.Current.CancellationToken)).Snapshot;
        return (fingerprint, snapshot);
    }

    private static string ProjectIdentity(ProjectData project) =>
        $"{project.Key}|{project.ProjectPath}|{Convert.ToHexString(project.Fingerprint)}";

    private static string DocumentIdentity(DocumentData document) =>
        $"{document.Key}|{document.NormalizedPath}|{Convert.ToHexString(document.ContentHash)}";

    private static void AssertPortableSnapshotPaths(IndexSnapshot snapshot, string runtimeRoot)
    {
        Assert.Equal(".", snapshot.InputRoot);
        Assert.False(Path.IsPathRooted(snapshot.IndexRootAnchor));
        Assert.All(snapshot.Projects, project =>
        {
            Assert.DoesNotContain(runtimeRoot, project.Key, StringComparison.OrdinalIgnoreCase);
            if (project.ProjectPath is not null)
            {
                Assert.False(Path.IsPathRooted(project.ProjectPath));
                Assert.DoesNotContain(runtimeRoot, project.ProjectPath, StringComparison.OrdinalIgnoreCase);
            }
        });
        Assert.All(snapshot.Documents, document =>
        {
            Assert.False(Path.IsPathRooted(document.NormalizedPath));
            Assert.DoesNotContain(runtimeRoot, document.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runtimeRoot, document.NormalizedPath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(snapshot.Symbols.Values, symbol =>
        {
            Assert.DoesNotContain(runtimeRoot, symbol.StableKey, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runtimeRoot, symbol.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(snapshot.Declarations.Values, declaration =>
        {
            Assert.DoesNotContain(runtimeRoot, declaration.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runtimeRoot, declaration.DocumentKey, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IndexPathResolver CreateStandardPaths(string storageRoot) =>
        IndexPathResolver.CreateForIndex(
            Path.Combine(storageRoot, ".csindex", "index.sqlite"),
            storageRoot);

    private static string CreateDifferentDrivePath(string referencePath, string relativePath)
    {
        var currentDrive = char.ToUpperInvariant(Path.GetPathRoot(referencePath)![0]);
        var otherDrive = currentDrive == 'Z' ? 'Y' : 'Z';
        return $"{otherDrive}:\\{relativePath}";
    }

    private static LoadedWorkspace CreateLoadedWorkspace(
        string? projectPath,
        params (string Path, string Source)[] documents) =>
        CreateLoadedWorkspace(projectPath, null, documents);

    private static LoadedWorkspace CreateLoadedWorkspaceWithCoreLibraryOverride(
        string? projectPath,
        string coreLibraryPath,
        params (string Path, string Source)[] documents) =>
        CreateLoadedWorkspace(projectPath, coreLibraryPath, documents);

    private static LoadedWorkspace CreateLoadedWorkspace(
        string? projectPath,
        string? coreLibraryPath,
        IReadOnlyList<(string Path, string Source)> documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var metadataReferences = GetPlatformReferences();
        if (coreLibraryPath is not null)
        {
            metadataReferences = metadataReferences
                .Where(reference => !string.Equals(
                    (reference as PortableExecutableReference)?.FilePath,
                    typeof(object).Assembly.Location,
                    StringComparison.OrdinalIgnoreCase))
                .Append(MetadataReference.CreateFromFile(coreLibraryPath))
                .ToArray();
        }

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            filePath: projectPath,
            outputFilePath: null,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: metadataReferences);
        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        foreach (var (path, source) in documents)
        {
            var documentId = DocumentId.CreateNewId(projectId, Path.GetFileName(path));
            solution = solution.AddDocument(
                DocumentInfo.Create(
                    documentId,
                    Path.GetFileName(path),
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create(), path)),
                    filePath: path));
        }

        Assert.True(workspace.TryApplyChanges(solution));
        var project = workspace.CurrentSolution.GetProject(projectId)!;
        return new LoadedWorkspace(
            workspace,
            [project],
            metadataReferences
                .OfType<PortableExecutableReference>()
                .Select(reference => reference.FilePath!)
                .ToArray(),
            [],
            0);
    }

    private static AnalysisProfileData CreateProfile() => new()
    {
        Name = "test",
        InputMode = InputMode.Directory,
        OperatingSystem = "Windows",
        Architecture = "x64",
        PreprocessorSymbols = [],
        ProfileHash = [],
    };

    private static IReadOnlyList<MetadataReference> GetPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
}
