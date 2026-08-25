using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;

namespace CsIndex.IntegrationTests;

public sealed class ProjectScopedSourceSymbolPersistenceTests
{
    [Fact]
    public async Task SaveAndQuery_KeepSameAssemblySourceDefinitionsProjectScoped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "csindex-project-identity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projects = CreateProjects(root, out var firstProjectPath, out var secondProjectPath);
            using var workspace = projects.Workspace;
            var snapshot = CreateSnapshot(root);

            await new SemanticExtractor(new ProjectFingerprintBuilder()).ExtractAsync(
                projects.Items,
                snapshot,
                includeDiagnostics: true,
                cancellationToken);

            Assert.Equal(2, snapshot.Projects.Count);
            Assert.Equal(2, snapshot.Projects.Select(project => project.Key).Distinct(StringComparer.Ordinal).Count());
            Assert.All(snapshot.Projects, project =>
            {
                Assert.Equal("DuplicateAssembly", project.AssemblyName);
                Assert.Equal("net10.0", project.TargetFramework);
            });
            var extractedRuns = snapshot.Symbols.Values
                .Where(symbol => symbol.Kind == IndexedSymbolKind.Method && symbol.DisplayName == "Shared.Twin::Run()")
                .ToArray();
            Assert.Equal(2, extractedRuns.Length);
            Assert.Equal(2, extractedRuns.Select(symbol => symbol.StableKey).Distinct(StringComparer.Ordinal).Count());

            var databasePath = Path.Combine(root, "index.sqlite");
            var index = new SqliteIndex(databasePath);
            await index.SaveAsync(snapshot, cancellationToken);

            var repository = index.CreateQueryRepository();
            var profile = await repository.GetProfileAsync(snapshot.Profile.Name, cancellationToken);
            var runs = await repository.FindSymbolCandidatesAsync(
                profile.Id,
                name: "Run",
                typeSimpleName: "Twin",
                kind: IndexedSymbolKind.Method,
                sourceOnly: true,
                cancellationToken: cancellationToken);
            var duplicateRuns = runs
                .Where(symbol => FormatPath(symbol) == "Shared.Twin::Run()")
                .ToArray();
            var preferredRuns = (await repository.GetPreferredDeclarationsAsync(
                    profile.Id,
                    duplicateRuns.Select(symbol => symbol.Id),
                    includeSourceText: true,
                    cancellationToken))
                .ToDictionary(declaration => declaration.SymbolId);
            var firstRun = Assert.Single(duplicateRuns, symbol =>
                preferredRuns[symbol.Id].NormalizedSource!.Contains("LocalFirst", StringComparison.Ordinal));
            var secondRun = Assert.Single(duplicateRuns, symbol =>
                preferredRuns[symbol.Id].NormalizedSource!.Contains("LocalSecond", StringComparison.Ordinal));
            Assert.Equal(2, duplicateRuns.Length);
            Assert.Equal(2, duplicateRuns.Select(symbol => symbol.Id).Distinct().Count());

            var firstLocal = Assert.Single(await repository.FindSymbolCandidatesAsync(
                profile.Id,
                name: "LocalFirst",
                typeSimpleName: "Twin",
                kind: IndexedSymbolKind.Method,
                sourceOnly: true,
                cancellationToken: cancellationToken));
            var secondLocal = Assert.Single(await repository.FindSymbolCandidatesAsync(
                profile.Id,
                name: "LocalSecond",
                typeSimpleName: "Twin",
                kind: IndexedSymbolKind.Method,
                sourceOnly: true,
                cancellationToken: cancellationToken));

            var storedProjects = await ReadStoredProjectsAsync(databasePath, profile.Id, cancellationToken);
            Assert.Equal(2, storedProjects.Count);
            var projectIdsByPath = storedProjects.ToDictionary(project => project.ProjectPath, project => project.Id);
            var firstStoredProjectPath = Path.GetRelativePath(root, firstProjectPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var secondStoredProjectPath = Path.GetRelativePath(root, secondProjectPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            Assert.Equal(
                [firstStoredProjectPath, secondStoredProjectPath],
                storedProjects.Select(project => project.ProjectPath));

            var symbolProjectIds = await ReadSymbolProjectIdsAsync(
                databasePath,
                profile.Id,
                [firstRun.Id, secondRun.Id, firstLocal.Id, secondLocal.Id],
                cancellationToken);
            Assert.Equal(4, symbolProjectIds.Count);
            Assert.Equal(projectIdsByPath[firstStoredProjectPath], symbolProjectIds[firstRun.Id]);
            Assert.Equal(projectIdsByPath[secondStoredProjectPath], symbolProjectIds[secondRun.Id]);
            Assert.Equal(symbolProjectIds[firstRun.Id], symbolProjectIds[firstLocal.Id]);
            Assert.Equal(symbolProjectIds[secondRun.Id], symbolProjectIds[secondLocal.Id]);
            Assert.NotEqual(symbolProjectIds[firstRun.Id], symbolProjectIds[secondRun.Id]);

            var calls = await repository.GetCallsByCallerAsync(
                profile.Id,
                [firstRun.Id, secondRun.Id],
                GeneratedFilter.Include,
                cancellationToken: cancellationToken);
            var firstCall = Assert.Single(calls, call => call.CallerSymbolId == firstRun.Id);
            var secondCall = Assert.Single(calls, call => call.CallerSymbolId == secondRun.Id);
            Assert.Equal(ResolutionStatus.Resolved, firstCall.ResolutionStatus);
            Assert.Equal(ResolutionStatus.Resolved, secondCall.ResolutionStatus);
            Assert.Equal(firstLocal.Id, firstCall.CalleeDefinitionId);
            Assert.Equal(secondLocal.Id, secondCall.CalleeDefinitionId);
            Assert.NotEqual(secondLocal.Id, firstCall.CalleeDefinitionId);
            Assert.NotEqual(firstLocal.Id, secondCall.CalleeDefinitionId);

            var service = new SemanticQueryService(repository);
            var firstExact = await service.FindSymbolsAsync(
                "Shared.Twin::Run()",
                snapshot.Profile.Name,
                sourceOnly: true,
                cancellationToken: cancellationToken);
            var secondExact = await service.FindSymbolsAsync(
                "Shared.Twin::Run()",
                snapshot.Profile.Name,
                sourceOnly: true,
                cancellationToken: cancellationToken);

            Assert.Equal([firstRun.Id, secondRun.Id], firstExact.MatchedSymbols.Select(symbol => symbol.Id));
            Assert.Equal(firstExact.MatchedSymbols.Select(symbol => symbol.Id), secondExact.MatchedSymbols.Select(symbol => symbol.Id));
            var exactDeclarations = (await repository.GetPreferredDeclarationsAsync(
                    profile.Id,
                    firstExact.MatchedSymbols.Select(symbol => symbol.Id),
                    includeSourceText: true,
                    cancellationToken))
                .ToDictionary(declaration => declaration.SymbolId);
            Assert.Equal(
                ["public void Run(){LocalFirst();}", "public void Run(){LocalSecond();}"],
                firstExact.MatchedSymbols.Select(symbol => exactDeclarations[symbol.Id].NormalizedSource));
            Assert.Equal(2, firstExact.MatchedSymbols.Select(symbol => symbol.Id).Distinct().Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GraphRootAmbiguityDisambiguatesDuplicateCanonicalNamesByDocumentAndId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "csindex-project-identity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projects = CreateProjects(root, out _, out _);
            using var workspace = projects.Workspace;
            var snapshot = CreateSnapshot(root);
            await new SemanticExtractor(new ProjectFingerprintBuilder()).ExtractAsync(
                projects.Items,
                snapshot,
                includeDiagnostics: true,
                cancellationToken);

            var databasePath = Path.Combine(root, "index.sqlite");
            var index = new SqliteIndex(databasePath);
            await index.SaveAsync(snapshot, cancellationToken);
            var repository = index.CreateQueryRepository();
            var profile = await repository.GetProfileAsync(snapshot.Profile.Name, cancellationToken);
            var candidates = (await repository.FindSymbolCandidatesAsync(
                    profile.Id,
                    name: "Run",
                    typeSimpleName: "Twin",
                    kind: IndexedSymbolKind.Method,
                    sourceOnly: true,
                    cancellationToken: cancellationToken))
                .Where(symbol => FormatPath(symbol) == "Shared.Twin::Run()")
                .ToArray();
            Assert.Equal(2, candidates.Length);
            var service = new SemanticQueryService(repository);

            var first = await Assert.ThrowsAsync<SymbolQueryParseException>(() => service.FindAsyncPathAsync(
                "Shared.Twin::Run()",
                profileName: profile.Name,
                cancellationToken: cancellationToken));
            var second = await Assert.ThrowsAsync<SymbolQueryParseException>(() => service.FindAsyncPathAsync(
                "Shared.Twin::Run()",
                profileName: profile.Name,
                cancellationToken: cancellationToken));
            var expected = "Graph query is ambiguous for 'Shared.Twin::Run()'. Candidates: " + string.Join(
                ", ",
                candidates.Select(candidate =>
                    $"{FormatPath(candidate)} [document: {candidate.DocumentPath}; symbol ID: {candidate.Id}]"));

            Assert.Equal(expected, first.Message);
            Assert.Equal(first.Message, second.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ProjectFixture CreateProjects(
        string root,
        out string firstProjectPath,
        out string secondProjectPath)
    {
        const string firstSource = """
            namespace Shared;

            public sealed class Twin
            {
                public void Run()
                {
                    LocalFirst();
                }

                private static void LocalFirst() { }
            }
            """;
        const string secondSource = """
            namespace Shared;

            public sealed class Twin
            {
                public void Run()
                {
                    LocalSecond();
                }

                private static void LocalSecond() { }
            }
            """;

        firstProjectPath = WriteFile(root, "First/First.csproj", "<Project />");
        var firstSourcePath = WriteFile(root, "First/Twin.cs", firstSource);
        secondProjectPath = WriteFile(root, "Second/Second.csproj", "<Project />");
        var secondSourcePath = WriteFile(root, "Second/Twin.cs", secondSource);
        var firstProjectId = ProjectId.CreateNewId("First");
        var secondProjectId = ProjectId.CreateNewId("Second");
        var firstDocumentId = DocumentId.CreateNewId(firstProjectId, "Twin.cs");
        var secondDocumentId = DocumentId.CreateNewId(secondProjectId, "Twin.cs");
        var references = GetPlatformReferences();
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution
            .AddProject(CreateProjectInfo(firstProjectId, "First", firstProjectPath, references))
            .AddProject(CreateProjectInfo(secondProjectId, "Second", secondProjectPath, references))
            .AddDocument(firstDocumentId, "Twin.cs", SourceText.From(firstSource), filePath: firstSourcePath)
            .AddDocument(secondDocumentId, "Twin.cs", SourceText.From(secondSource), filePath: secondSourcePath);
        Assert.True(workspace.TryApplyChanges(solution));

        return new ProjectFixture(
            workspace,
            [
                workspace.CurrentSolution.GetProject(firstProjectId)!,
                workspace.CurrentSolution.GetProject(secondProjectId)!,
            ]);
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

    private static string WriteFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static async Task<IReadOnlyList<StoredProject>> ReadStoredProjectsAsync(
        string databasePath,
        long profileId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_path
            FROM projects
            WHERE analysis_profile_id = $profile_id
            ORDER BY project_path;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var projects = new List<StoredProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(new StoredProject(reader.GetInt64(0), reader.GetString(1)));
        }

        return projects;
    }

    private static async Task<IReadOnlyDictionary<long, long>> ReadSymbolProjectIdsAsync(
        string databasePath,
        long profileId,
        IReadOnlyList<long> symbolIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        var parameters = new List<string>(symbolIds.Count);
        for (var index = 0; index < symbolIds.Count; index++)
        {
            var name = $"$symbol_id_{index}";
            command.Parameters.AddWithValue(name, symbolIds[index]);
            parameters.Add(name);
        }

        command.CommandText = $"""
            SELECT id, project_id
            FROM symbols
            WHERE analysis_profile_id = $profile_id
              AND id IN ({string.Join(", ", parameters)});
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var projectIds = new Dictionary<long, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projectIds.Add(reader.GetInt64(0), reader.GetInt64(1));
        }

        return projectIds;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string FormatPath(StoredSymbol symbol) =>
        new SymbolPathFormatter().Format(
            Assert.IsType<SymbolPathData>(symbol.Path),
            new SymbolPathFormatOptions());

    private sealed record ProjectFixture(AdhocWorkspace Workspace, IReadOnlyList<Project> Items);

    private sealed record StoredProject(long Id, string ProjectPath);
}
