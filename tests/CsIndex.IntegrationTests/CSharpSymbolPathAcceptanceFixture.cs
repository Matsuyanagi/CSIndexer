using System.Globalization;
using System.Text;
using CsIndex.Cli;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsIndex.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CSharpSymbolPathAcceptanceCollection :
    ICollectionFixture<CSharpSymbolPathAcceptanceFixture>
{
    public const string Name = "C# symbol-path acceptance";
}

public sealed class CSharpSymbolPathAcceptanceFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ContainerPath { get; } = Path.Combine(
        Path.GetTempPath(),
        "csindex-csharp-symbol-path-acceptance",
        Guid.NewGuid().ToString("N"));

    public string WorkspacePath => Path.Combine(ContainerPath, "workspace");
    public string SolutionPath => Path.Combine(WorkspacePath, "Acceptance.sln");
    public string SourcePath => Path.Combine(WorkspacePath, "src");
    public string CorpusPath => Path.Combine(SourcePath, "Corpus");
    public string SharedPath => Path.Combine(ContainerPath, "Shared");
    public string StandardDatabasePath => Path.Combine(WorkspacePath, ".csindex", "index.sqlite");
    public string CustomDatabasePath => Path.Combine(ContainerPath, "indexes", "custom", "index.sqlite");
    public string LinkedSourcePath => Path.Combine(SharedPath, "Linked.cs");
    public string CorpusSourcePath => Path.Combine(CorpusPath, "Ambiguity.cs");
    public Task Ready => _ready.Task;
    public CSharpSymbolPathCliResult StandardIndexResult { get; private set; } = default!;
    public CSharpSymbolPathCliResult CustomIndexResult { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            CreateSourceLayout();
            StandardIndexResult = await RunCliAsync("index", SolutionPath, "--profile-name", "primary");
            if (StandardIndexResult.ExitCode == ExitCodes.Success)
            {
                StandardIndexResult = await RunCliAsync("index", SolutionPath, "--profile-name", "secondary");
            }

            CustomIndexResult = await RunCliAsync(
                "index", SolutionPath, "--profile-name", "primary", "--db", CustomDatabasePath);
            if (CustomIndexResult.ExitCode == ExitCodes.Success)
            {
                CustomIndexResult = await RunCliAsync(
                    "index", SolutionPath, "--profile-name", "secondary", "--db", CustomDatabasePath);
            }
            if (StandardIndexResult.ExitCode != ExitCodes.Success || CustomIndexResult.ExitCode != ExitCodes.Success)
            {
                throw new InvalidOperationException(
                    "The acceptance fixture could not be indexed through the real CLI. " +
                    $"standard={StandardIndexResult.ExitCode}: {StandardIndexResult.StandardError}; " +
                    $"custom={CustomIndexResult.ExitCode}: {CustomIndexResult.StandardError}");
            }

            _ready.SetResult();
        }
        catch (Exception exception)
        {
            _ready.SetException(exception);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(ContainerPath))
        {
            Directory.Delete(ContainerPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    public async Task<CSharpSymbolPathCliResult> RunStandardCliAsync(params string[] arguments) =>
        await RunCliAsync([.. arguments, "--db", StandardDatabasePath]);

    public async Task<CSharpSymbolPathCliResult> RunCustomCliAsync(params string[] arguments) =>
        await RunCliAsync([.. arguments, "--db", CustomDatabasePath]);

    public async Task<CSharpSymbolPathCliResult> RunCliAsync(params string[] arguments)
    {
        await ConsoleGate.WaitAsync(TestContext.Current.CancellationToken);
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.Main(arguments);
            return new CSharpSymbolPathCliResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            ConsoleGate.Release();
        }
    }

    internal async Task<CSharpSymbolPathCliResult> RunCliAsync(
        ProgramDependencies dependencies,
        params string[] arguments)
        => await RunCliAsync(dependencies, TestContext.Current.CancellationToken, arguments);

    internal async Task<CSharpSymbolPathCliResult> RunCliAsync(
        ProgramDependencies dependencies,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        await ConsoleGate.WaitAsync(cancellationToken);
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.RunAsync(
                arguments,
                cancellationToken,
                dependencies);
            return new CSharpSymbolPathCliResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            ConsoleGate.Release();
        }
    }

    public async Task<CSharpSymbolPathIsolatedIndexResult> IndexFunctionPointerOverloadsAsync()
    {
        var workspacePath = Path.Combine(ContainerPath, "isolated", $"function-pointers-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(workspacePath, "src", "FunctionPointers");
        var solutionPath = Path.Combine(workspacePath, "FunctionPointers.sln");
        var databasePath = Path.Combine(workspacePath, "index.sqlite");
        Directory.CreateDirectory(projectPath);
        File.WriteAllText(solutionPath, FunctionPointerSolutionSource);
        File.WriteAllText(Path.Combine(projectPath, "FunctionPointers.csproj"), FunctionPointerProjectSource);
        File.WriteAllText(Path.Combine(projectPath, "FunctionPointerOverloads.cs"), FunctionPointerOverloadSource);

        var result = await RunCliAsync(
            "index", solutionPath,
            "--profile-name", "function-pointer-overloads",
            "--db", databasePath);
        return new CSharpSymbolPathIsolatedIndexResult(workspacePath, databasePath, result);
    }

    public SemanticQueryService CreateQueryService(string? databasePath = null, string? baseDirectory = null) =>
        new(new SqliteIndex(databasePath ?? StandardDatabasePath).CreateQueryRepository(), baseDirectory);

    public QueryRepository CreateRepository(string? databasePath = null) =>
        new SqliteIndex(databasePath ?? StandardDatabasePath).CreateQueryRepository();

    public SymbolPathFormatOptions FullCsharp => new(SymbolPathStyle.CSharp, ShortNames: false);
    public SymbolPathFormatOptions FullExplicit => new(SymbolPathStyle.Explicit, ShortNames: false);
    public SymbolPathFormatOptions ShortCsharp => new(SymbolPathStyle.CSharp, ShortNames: true);
    public SymbolPathFormatOptions ShortExplicit => new(SymbolPathStyle.Explicit, ShortNames: true);

    public string FormatPath(StoredSymbol symbol, SymbolPathFormatOptions? options = null) =>
        new SymbolPathFormatter().Format(
            Assert.IsType<SymbolPathData>(symbol.Path),
            options ?? FullCsharp);

    public async Task<StoredProfile> GetProfileAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default) =>
        await CreateRepository(databasePath).GetProfileAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<StoredSymbol>> GetExecutableSymbolsAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var repository = CreateRepository(databasePath);
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        return await repository.FindExecutableSymbolsAsync(profile.Id, sourceOnly: true, cancellationToken);
    }

    public async Task<StoredSymbol> GetSymbolAsync(
        string fullCsharpPath,
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var symbols = await GetExecutableSymbolsAsync(databasePath, cancellationToken);
        return Assert.Single(symbols, symbol =>
            FormatPath(symbol, FullCsharp).Equals(fullCsharpPath, StringComparison.Ordinal));
    }

    public async Task<(IMethodSymbol Definition, IMethodSymbol Implementation)> GetPairedPartialMethodsAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = new[]
        {
            Path.Combine(CorpusPath, "Partials.Definition.cs"),
            Path.Combine(CorpusPath, "Partials.Implementation.cs"),
        };
        var trees = new List<SyntaxTree>(paths.Length);
        foreach (var path in paths)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                await File.ReadAllTextAsync(path, cancellationToken),
                new CSharpParseOptions(LanguageVersion.Preview),
                path,
                cancellationToken: cancellationToken));
        }

        var compilation = CSharpCompilation.Create(
            "Acceptance.Partials.Normalization",
            trees,
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var declarations = new List<IMethodSymbol>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .Where(method => method.Identifier.ValueText == "PairedPartial"))
            {
                declarations.Add(Assert.IsAssignableFrom<IMethodSymbol>(
                    model.GetDeclaredSymbol(method, cancellationToken)));
            }
        }

        return (
            Assert.Single(declarations, method => method.PartialImplementationPart is not null),
            Assert.Single(declarations, method => method.PartialDefinitionPart is not null));
    }

    public async Task<IReadOnlyList<StoredDeclaration>> GetDeclarationsAsync(
        StoredSymbol symbol,
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var repository = CreateRepository(databasePath);
        var profile = await repository.GetProfileAsync(cancellationToken: cancellationToken);
        return await repository.GetDeclarationsAsync(profile.Id, [symbol.Id], includeSourceText: true, cancellationToken);
    }

    public string GetLocation(string sourceFileName, string marker)
    {
        var path = Path.Combine(CorpusPath, sourceFileName);
        var source = File.ReadAllText(path);
        var offset = source.IndexOf(marker, StringComparison.Ordinal);
        if (offset < 0)
        {
            throw new InvalidOperationException($"Marker '{marker}' was not found in '{sourceFileName}'.");
        }

        var point = SourcePositionResolver.ResolveOffset(path, offset);
        return $"{path}:{point.Line}:{point.Column}";
    }

    public IndexPathResolver CreatePathResolver(string? databasePath = null, string? baseDirectory = null)
    {
        var effectiveDatabasePath = databasePath ?? StandardDatabasePath;
        var profile = GetProfileAsync(effectiveDatabasePath).GetAwaiter().GetResult();
        return IndexPathResolver.CreateForQuery(
            effectiveDatabasePath,
            profile.IndexRootAnchor,
            baseDirectory);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string?>>> ReadAllTextColumnsAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, IReadOnlyList<string?>>(StringComparer.Ordinal);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? StandardDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var tables = new List<string>();
        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            var columns = new List<string>();
            await using (var columnCommand = connection.CreateCommand())
            {
                columnCommand.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
                await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(2).Contains("TEXT", StringComparison.OrdinalIgnoreCase))
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
            }

            foreach (var column in columns)
            {
                await using var valueCommand = connection.CreateCommand();
                valueCommand.CommandText = $"SELECT \"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\" FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ORDER BY rowid;";
                await using var reader = await valueCommand.ExecuteReaderAsync(cancellationToken);
                var columnValues = new List<string?>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    columnValues.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
                }

                values[$"{table}.{column}"] = columnValues;
            }
        }

        return values;
    }

    public async Task<byte[]> ReadDatabaseBytesAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default) =>
        await File.ReadAllBytesAsync(databasePath ?? StandardDatabasePath, cancellationToken);

    public static async Task CreateLegacyDatabaseAsync(
        string databasePath,
        int version,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE;";
        await command.ExecuteScalarAsync(cancellationToken);
        command.CommandText = $"""
            CREATE TABLE schema_info(version INTEGER NOT NULL);
            INSERT INTO schema_info(version) VALUES ({version});
            CREATE TABLE legacy_sentinel(value TEXT NOT NULL);
            INSERT INTO legacy_sentinel(value) VALUES ('preserve-me');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> ReadDatabaseSnapshotAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? StandardDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = new List<string>();
        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var snapshot = new StringBuilder();
        foreach (var table in tables)
        {
            var quotedTable = QuoteIdentifier(table);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {quotedTable} ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            snapshot.Append("table:").Append(ToBase64(table)).AppendLine();
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                snapshot.Append("column:").Append(ToBase64(reader.GetName(ordinal))).AppendLine();
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                snapshot.AppendLine("row:");
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    snapshot.Append(SnapshotValue(reader.GetValue(ordinal))).AppendLine();
                }
            }
        }

        return snapshot.ToString();
    }

    public string CopyContainerTo(string destinationParent)
    {
        var destination = Path.Combine(destinationParent, Path.GetFileName(ContainerPath));
        CopyDirectory(ContainerPath, destination);
        return destination;
    }

    public string CopyDatabaseTo(string destinationDirectory, string? databasePath = null)
    {
        Directory.CreateDirectory(destinationDirectory);
        var source = databasePath ?? StandardDatabasePath;
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    private static IReadOnlyList<MetadataReference> GetPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

    private void CreateSourceLayout()
    {
        Directory.CreateDirectory(CorpusPath);
        Directory.CreateDirectory(Path.Combine(SourcePath, "DuplicateA"));
        Directory.CreateDirectory(Path.Combine(SourcePath, "DuplicateB"));
        Directory.CreateDirectory(SharedPath);

        File.WriteAllText(SolutionPath, SolutionSource);
        File.WriteAllText(Path.Combine(CorpusPath, "Corpus.csproj"), CorpusProjectSource);
        File.WriteAllText(Path.Combine(SourcePath, "DuplicateA", "DuplicateA.csproj"), DuplicateProjectSource);
        File.WriteAllText(Path.Combine(SourcePath, "DuplicateB", "DuplicateB.csproj"), DuplicateProjectSource);
        File.WriteAllText(Path.Combine(SourcePath, "DuplicateA", "Twin.cs"), DuplicateSource("DuplicateA"));
        File.WriteAllText(Path.Combine(SourcePath, "DuplicateB", "Twin.cs"), DuplicateSource("DuplicateB"));
        File.WriteAllText(LinkedSourcePath, LinkedSource);
        File.WriteAllText(Path.Combine(CorpusPath, "Ambiguity.cs"), AmbiguitySource);
        File.WriteAllText(Path.Combine(CorpusPath, "Signatures.cs"), SignatureSource);
        File.WriteAllText(Path.Combine(CorpusPath, "SpecialCallables.cs"), SpecialCallablesSource);
        File.WriteAllText(Path.Combine(CorpusPath, "Partials.Definition.cs"), PartialDefinitionSource);
        File.WriteAllText(Path.Combine(CorpusPath, "Partials.Implementation.cs"), PartialImplementationSource);
        File.WriteAllText(Path.Combine(CorpusPath, "Graph.cs"), GraphSource);
        File.WriteAllText(Path.Combine(CorpusPath, "TopLevel.cs"), TopLevelSource);
        File.WriteAllText(Path.Combine(CorpusPath, "NewlineCorpus.cs"), NewlineCorpusSource);
        File.WriteAllText(Path.Combine(CorpusPath, "Generated.g.cs"), GeneratedSource);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string SnapshotValue(object value) => value switch
    {
        DBNull => "null",
        byte[] bytes => $"blob:{Convert.ToBase64String(bytes)}",
        string text => $"text:{ToBase64(text)}",
        long integer => $"integer:{integer.ToString(CultureInfo.InvariantCulture)}",
        double real => $"real:{real.ToString("R", CultureInfo.InvariantCulture)}",
        _ => $"{value.GetType().FullName}:{ToBase64(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}",
    };

    private static string ToBase64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private const string SolutionSource = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Corpus", "src\\Corpus\\Corpus.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "DuplicateA", "src\\DuplicateA\\DuplicateA.csproj", "{22222222-2222-2222-2222-222222222222}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "DuplicateB", "src\\DuplicateB\\DuplicateB.csproj", "{33333333-3333-3333-3333-333333333333}"
        EndProject
        Global
        \tGlobalSection(SolutionConfigurationPlatforms) = preSolution
        \t\tDebug|Any CPU = Debug|Any CPU
        \t\tRelease|Any CPU = Release|Any CPU
        \tEndGlobalSection
        \tGlobalSection(ProjectConfigurationPlatforms) = postSolution
        \t\t{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        \t\t{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        \t\t{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
        \t\t{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
        \t\t{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        \t\t{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
        \t\t{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
        \t\t{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
        \t\t{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        \t\t{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
        \t\t{33333333-3333-3333-3333-333333333333}.Release|Any CPU.ActiveCfg = Release|Any CPU
        \t\t{33333333-3333-3333-3333-333333333333}.Release|Any CPU.Build.0 = Release|Any CPU
        \tEndGlobalSection
        EndGlobal
        """;

    private const string CorpusProjectSource = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <LangVersion>preview</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="../../../Shared/Linked.cs" Link="Linked.cs" />
          </ItemGroup>
        </Project>
        """;

    private const string DuplicateProjectSource = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <LangVersion>preview</LangVersion>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string FunctionPointerSolutionSource = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FunctionPointers", "src\FunctionPointers\FunctionPointers.csproj", "{44444444-4444-4444-4444-444444444444}"
        EndProject
        Global
        \tGlobalSection(SolutionConfigurationPlatforms) = preSolution
        \t\tDebug|Any CPU = Debug|Any CPU
        \t\tRelease|Any CPU = Release|Any CPU
        \tEndGlobalSection
        \tGlobalSection(ProjectConfigurationPlatforms) = postSolution
        \t\t{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        \t\t{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.Build.0 = Debug|Any CPU
        \t\t{44444444-4444-4444-4444-444444444444}.Release|Any CPU.ActiveCfg = Release|Any CPU
        \t\t{44444444-4444-4444-4444-444444444444}.Release|Any CPU.Build.0 = Release|Any CPU
        \tEndGlobalSection
        EndGlobal
        """;

    private const string FunctionPointerProjectSource = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <LangVersion>preview</LangVersion>
            <Nullable>enable</Nullable>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
          </PropertyGroup>
        </Project>
        """;

    private const string FunctionPointerOverloadSource = """
        namespace Acceptance.FunctionPointers;

        public unsafe sealed class CollisionHost
        {
            public void Shape(delegate*<int, void> callback) { }
            public void Shape(delegate*<long, void> callback) { }
            public void Shape(delegate*<ref int, void> callback) { }
        }
        """;

    private static string DuplicateSource(string marker) => $$"""
        namespace Acceptance.Duplicates;

        public sealed class Twin
        {
            public void Same() { System.Console.WriteLine("{{marker}}"); }
        }
        """;

    private const string LinkedSource = """
        namespace Acceptance.Shared;

        public static class Linked
        {
            public static void LinkedMarker() { }
        }
        """;

    private const string AmbiguitySource = """
        using System;
        using Acceptance.Shared;

        namespace Namespace1.Namespace2
        {
            public class Class1
            {
                public class Class2 { public void Method() { } }
            }

            namespace Namespace3
            {
                public class Class2 { public void Method() { } }
            }
        }

        namespace A
        {
            public sealed class B { public void C() { } }
        }

        namespace Wider.Namespace1.Namespace2
        {
            public class Class1
            {
                public class Class2 { public void Method() { } }
            }
        }

        public sealed class GlobalOwner { public void GlobalMethod() { } }

        namespace @global
        {
            public sealed class LiteralGlobalOwner { public void LiteralGlobalMethod() { } }
        }

        namespace Acceptance.Corpus
        {
            public sealed class LocalOwners
            {
                public void RootOne()
                {
                    void SameLocal() { }
                    void NestedOwner()
                    {
                        void SameLocal() { }
                        void DeepOnly() { }
                        Action first = () => SameLocal();
                        Action second = delegate { SameLocal(); };
                        Action third = () => SameLocal();
                        DeepOnly();
                        _ = first; _ = second; _ = third;
                    }
                    SameLocal(); NestedOwner();
                }

                public void RootTwo()
                {
                    void SameLocal() { }
                    SameLocal();
                }

                public void CallsLinked() => Linked.LinkedMarker();
            }
        }
        """;

    private const string SignatureSource = """
        using System;
        using System.Collections.Generic;

        namespace Acceptance.Signatures;

        public unsafe class Outer<T>
        {
            public class Inner<U>
            {
                public void Bare() { }
                public void Bare(int value) { }
                public void Generic() { }
                public void Generic(int value) { }
                public void Generic<V>() { }
                public void Generic<V>(V value) { }
                public void @class() { }
                public void Alias(int value) { }
                public void Framework(System.Int32 value) { }
                public void NullableReference(string? value) { }
                public void NullableValue(int? value) { }
                public void RankOne(int[] value) { }
                public void RankTwo(int[,] value) { }
                public void Pointer(int* value) { }
                public void Tuple((int left, string right) value) { }
                public void FunctionPointer(delegate* managed<int, void> callback) { }
                public void NestedGeneric(Dictionary<string, List<int?[]>> value) { }
                public void NonAliasShapes(
                    (Acceptance.Special.SpecialHost, List<Acceptance.Special.SpecialHost>) tuple,
                    delegate*<Acceptance.Special.SpecialHost, Acceptance.Special.SpecialHost> callback) { }
                public void AliasMatrix(
                    bool boolean,
                    byte unsignedByte,
                    sbyte signedByte,
                    short signedShort,
                    ushort unsignedShort,
                    int signedInt,
                    uint unsignedInt,
                    long signedLong,
                    ulong unsignedLong,
                    nint nativeInt,
                    nuint nativeUInt,
                    char character,
                    float single,
                    double @double,
                    decimal @decimal,
                    string text,
                    object value) { }
                public void Scope<V>(T typeValue, V methodValue) { }
                public void Shape(int value) { }
                public void Shape(int? value) { }
                public void Shape(int[] value) { }
                public void Shape(int[,] value) { }
                public void Shape(int* value) { }
                public void Shape(int** value) { }
                public void Shape((int, string) value) { }
                public void Shape((int, (string, int)) value) { }
                public void Shape(delegate*<int, void> callback) { }
                public void ByValue(int value) { }
                public void ByRef(ref int value) { }
                public void ByOut(out int value) { value = 0; }
                public void ByIn(in int value) { }
                public void ByRefReadonly(ref readonly int value) { }

                public void LocalStates()
                {
                    void First<V>()
                    {
                        void Leaf() { }
                        Leaf();
                    }
                    void Second(int value) { }
                    First<int>(); Second(1);
                }

                public void LocalStates(int value)
                {
                    void First<V>(V item)
                    {
                        void Leaf(int leaf) { }
                        Leaf(value);
                    }
                    First(value);
                }
            }
        }

        public sealed class ConversionHost
        {
            public static implicit operator int(ConversionHost value) => 0;
            public static explicit operator string(ConversionHost value) => string.Empty;
        }
        """;

    private const string SpecialCallablesSource = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.InteropServices;
        using System.Threading.Tasks;

        namespace Acceptance.Special;

        public interface ISpecial
        {
            void Run();
            int Value { get; set; }
            T Map<T>(T value);
        }

        public class SpecialHost : ISpecial
        {
            public SpecialHost(int value) { }
            static SpecialHost() { }
            ~SpecialHost() { }
            public static SpecialHost operator +(SpecialHost left, SpecialHost right) => left;
            public static SpecialHost operator checked +(SpecialHost left, SpecialHost right) => left;
            public static implicit operator int(SpecialHost value) => 0;
            public static explicit operator string(SpecialHost value) => string.Empty;
            public static explicit operator Guid(SpecialHost value) => Guid.Empty;
            public static explicit operator double(SpecialHost value) => 0;
            public static explicit operator checked double(SpecialHost value) => 0;
            public int Auto { get; set; }
            public string InitOnly { get; init; } = string.Empty;
            public int Body { get { return 0; } set { } }
            public int Expression => 1;
            public int this[int index] { get => index; set { } }
            public event EventHandler? Custom { add { } remove { } }
            public event EventHandler? FieldLike;
            int ISpecial.Value { get => 0; set { } }
            void ISpecial.Run() { }
            T ISpecial.Map<T>(T value) => value;

            public void MixedAnonymous()
            {
                Action<int> first = value => Marker();
                Action second = delegate { Marker(); };
                Action third = () => Marker();
                _ = first; _ = second; _ = third;
            }

            public void NestedAnonymous()
            {
                Action parentLambda = () =>
                {
                    Action nestedLambda = () => Marker();
                    Action nestedAnonymous = delegate { Marker(); };
                    _ = nestedLambda; _ = nestedAnonymous;
                };
                Action parentAnonymous = delegate
                {
                    Action nestedLambda = () => Marker();
                    _ = nestedLambda;
                };
                void LocalOwner()
                {
                    Action localAnonymous = delegate { Marker(); };
                    _ = localAnonymous;
                }
                _ = parentLambda; _ = parentAnonymous;
                LocalOwner();
            }

            public void OwnerForInitializer() { }
            private static void Marker() { }
        }

        public sealed class InitializerHost
        {
            private readonly int field = Initialize();
            public int Property { get; set; } = Initialize();
            public event EventHandler? Changed = (sender, args) => _ = Initialize();
            private static int Initialize() => 1;
        }

        public sealed class ImplicitDefaultHost { }
        public abstract class AbstractHost { public abstract void Bodyless(); }
        public interface IBodylessHost { void Bodyless(); }
        public sealed class ExternHost { [DllImport("kernel32")] public static extern uint GetCurrentThreadId(); }
        public class PrimaryClass(int value) { public int Value => value; }
        public struct PrimaryStruct(int value) { public int Value => value; }
        public record PrimaryRecord(int Value);

        public sealed class AsyncAndIteratorHost
        {
            public async Task AsyncMember() { await Task.Yield(); }
            public IEnumerable<int> Iterator() { yield return 1; }
        }
        """;

    private const string PartialDefinitionSource = """
        using System;

        namespace Acceptance.Partials;

        [AttributeUsage(AttributeTargets.Method)]
        internal sealed class PartialDefinitionMarkerAttribute : Attribute
        {
            public PartialDefinitionMarkerAttribute(string marker) { }
        }

        [AttributeUsage(AttributeTargets.Method)]
        internal sealed class PartialImplementationMarkerAttribute : Attribute
        {
            public PartialImplementationMarkerAttribute(string marker) { }
        }

        public interface IPartialContract
        {
            void PairedPartial();
        }

        public abstract class PartialBase
        {
            public abstract void PairedPartial();
        }

        public partial class PartialHost : PartialBase, IPartialContract
        {
            [PartialDefinitionMarker("PARTIAL-DEFINITION-MARKER")]
            public override partial void PairedPartial();
            partial void DefinitionOnly(); // DEFINITION-ONLY-MARKER

            public void InvokePaired() => PairedPartial();
        }
        """;

    private const string PartialImplementationSource = """
        namespace Acceptance.Partials;

        public partial class PartialHost
        {
            [PartialImplementationMarker("PARTIAL-IMPLEMENTATION-MARKER")]
            public override partial void PairedPartial()
            {
                GraphTarget();
            }

            private static void GraphTarget() { }
        }
        """;

    private const string GraphSource = """
        using System;
        using System.Threading.Tasks;

        namespace Acceptance.Graph;

        public interface IGraphContract { void Bind(); }
        public class GraphBase { public virtual void OverrideMe() { } }
        public sealed class GraphDerived : GraphBase, IGraphContract
        {
            public override void OverrideMe() { }
            public void Bind() { }
        }

        public sealed class GraphHost
        {
            public void RootWithSecondary()
            {
                _ = "ROOT-FILTER-MARKER";
                SecondaryWithoutRootMarker();
            }

            public void SecondaryWithoutRootMarker() { }
            public async Task AsyncRoot() { await Task.Yield(); }
            public void SyncOwnerWithAsyncChild()
            {
                Func<Task> child = async () => await Task.Yield();
                _ = child;
            }

            public void CallerTarget() { }
            public void ACaller() => CallerTarget();
            public void ZCaller() => CallerTarget();
            public void TreeRoot() { }
            public void TreeLeft() => TreeRoot();
            public void TreeRight() => TreeRoot();
            public void TreeLeftGrandparent() => TreeLeft();
            public void TreeRightGrandparent() => TreeRight();

            public void AsyncTreeRoot() => AsyncMiddle();
            private void AsyncMiddle() => AsyncLeaf().GetAwaiter().GetResult();
            private async Task AsyncLeaf() => await Task.Yield();
        }
        """;

    private const string TopLevelSource = """
        using System.Threading.Tasks;

        await Task.Yield(); // TOP-LEVEL-MARKER

        void TopLevelLocal()
        {
            System.Action localLambda = () => System.Console.WriteLine("TOP-LEVEL-LAMBDA");
            localLambda();
        }

        TopLevelLocal();
        """;

    private static string NewlineCorpusSource =>
        "namespace Acceptance.Newlines;\n" +
        "public sealed class NewlineHost\n{\n" +
        "    public void NewlineMarkers()\n    {\n" +
        "        _ = @\"TAB-MARKER\tCR-MARKER\r" +
        "LF-MARKER\n" +
        "CRLF-MARKER\r\n" +
        "NEL-MARKER\u0085" +
        "LS-MARKER\u2028" +
        "PS-MARKER\u2029" +
        "END-MARKER" +
        "\";\n" +
        "    }\n}\n";

    private const string GeneratedSource = """
        namespace Acceptance.Generated;

        public sealed class GeneratedHost
        {
            public void GeneratedRoot() { }
        }
        """;
}

public sealed record CSharpSymbolPathCliResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record CSharpSymbolPathIsolatedIndexResult(
    string WorkspacePath,
    string DatabasePath,
    CSharpSymbolPathCliResult IndexResult);
