using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal static class Program
{
    private static readonly string[] IndexOptions =
    [
        "db", "mode", "solution", "configuration", "framework", "target-framework", "runtime",
        "profile-name", "define", "undefine", "define-file", "reference", "unity-editor", "exclude",
        "generated-source", "rebuild", "verbose", "diagnostics", "help",
    ];

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteHelp();
            return ExitCodes.Success;
        }

        try
        {
            return args[0] switch
            {
                "index" => await RunIndexAsync(args[1..], cancellation.Token),
                "symbol" when args.Length > 1 && args[1] == "list" =>
                    await RunSymbolListAsync(args[2..], cancellation.Token),
                "symbol" when args.Length > 1 && args[1] == "find" =>
                    await RunSymbolAsync(args[2..], cancellation.Token),
                "definition" => await RunDefinitionAsync(args[1..], cancellation.Token),
                "references" => await RunReferencesAsync(args[1..], cancellation.Token),
                "callers" => await RunCallersAsync(args[1..], cancellation.Token),
                "callees" => await RunCalleesAsync(args[1..], cancellation.Token),
                "overrides" => await RunOverridesAsync(args[1..], cancellation.Token),
                "conditions" => await RunConditionsAsync(args[1..], cancellation.Token),
                _ => throw new CliUsageException($"Unknown command: {string.Join(' ', args)}"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation was cancelled.");
            return ExitCodes.AnalysisFailure;
        }
        catch (CliUsageException exception)
        {
            Console.Error.WriteLine($"Argument error: {exception.Message}");
            Console.Error.WriteLine("Run 'csindex --help' for usage.");
            return ExitCodes.InvalidArguments;
        }
        catch (SymbolQueryParseException exception)
        {
            Console.Error.WriteLine($"Query error: {exception.Message}");
            return ExitCodes.InvalidArguments;
        }
        catch (InputResolutionException exception)
        {
            Console.Error.WriteLine($"Input error: {exception.Message}");
            return ExitCodes.AnalysisFailure;
        }
        catch (IndexDatabaseException exception)
        {
            Console.Error.WriteLine($"Database error: {exception.Message}");
            return ExitCodes.DatabaseFailure;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Fatal error: {exception.Message}");
            return ExitCodes.AnalysisFailure;
        }
    }

    private static async Task<int> RunIndexAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = CliArguments.Parse(args);
        parsed.EnsureOnly(IndexOptions);
        if (parsed.HasFlag("help"))
        {
            WriteIndexHelp();
            return ExitCodes.Success;
        }

        if (parsed.Positionals.Count != 1)
        {
            throw new CliUsageException("index requires exactly one input path.");
        }

        var framework = CoalesceAliases(parsed, "framework", "target-framework");
        if (parsed.GetSingle("unity-editor") is not null)
        {
            throw new CliUsageException("--unity-editor is reserved for Phase 3 and is not implemented in this build.");
        }

        var generatedSource = ParseGeneratedSource(parsed.GetSingle("generated-source"));
        if (generatedSource != GeneratedSourceMode.Physical)
        {
            throw new CliUsageException(
                "--generated-source all/none is reserved for Phase 4; this build supports physical only.");
        }

        var options = new IndexOptions
        {
            InputPath = parsed.Positionals[0],
            DatabasePath = parsed.GetSingle("db"),
            ForcedMode = ParseInputMode(parsed.GetSingle("mode")),
            SolutionPath = parsed.GetSingle("solution"),
            Configuration = parsed.GetSingle("configuration"),
            TargetFramework = framework,
            RuntimeIdentifier = parsed.GetSingle("runtime") ?? "win-x64",
            ProfileName = parsed.GetSingle("profile-name"),
            Defines = parsed.GetMany("define"),
            Undefines = parsed.GetMany("undefine"),
            DefineFiles = parsed.GetMany("define-file"),
            References = parsed.GetMany("reference"),
            UnityEditorPath = parsed.GetSingle("unity-editor"),
            Excludes = parsed.GetMany("exclude"),
            GeneratedSourceMode = generatedSource,
            Rebuild = parsed.HasFlag("rebuild"),
            Verbose = parsed.HasFlag("verbose"),
            Diagnostics = parsed.HasFlag("diagnostics"),
        };

        MSBuildBootstrapper.EnsureRegistered();
        var coordinator = AnalysisCoordinator.CreateDefault();
        var input = coordinator.ResolveInput(options);
        var databasePath = options.DatabasePath is null
            ? Path.Combine(input.RootPath, ".csindex", "index.sqlite")
            : Path.GetFullPath(options.DatabasePath);
        var index = new SqliteIndex(databasePath);
        var requestHash = RequestHasher.Build(input, options);
        var inputFingerprint = await coordinator.BuildInputFingerprintAsync(input, options, cancellationToken);

        WriteProgress("Input mode", input.Mode.ToString());
        WriteProgress("Input path", input.OriginalPath);
        WriteProgress("Database path", databasePath);
        if (!options.Rebuild && await index.IsCacheValidAsync(
                input.RootPath,
                inputFingerprint,
                requestHash,
                cancellationToken))
        {
            WriteProgress("Cache", "reused");
            return ExitCodes.Success;
        }

        var result = await coordinator.AnalyzeAsync(
            input,
            options,
            inputFingerprint,
            requestHash,
            cancellationToken);
        await index.SaveAsync(result.Snapshot, cancellationToken);
        WriteIndexSummary(result, databasePath, options);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSymbolAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(
            args, "db", "profile", "output", "require-single", "short-names", "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            Console.WriteLine("""
                Usage: csindex symbol find <query> [--db <path>] [--output table|json] [--short-names]

                  --include-overrides         Include descendant overrides and interface implementations
                """);
            return ExitCodes.Success;
        }

        var query = RequireQuery(parsed);
        var service = CreateQueryService(parsed);
        var result = await service.FindSymbolsAsync(
            query,
            parsed.GetSingle("profile"),
            includeOverrides: parsed.HasFlag("include-overrides"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.MatchedSymbols.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        CreateFormatter(parsed).WriteSymbols(result);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSymbolListAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(args, "db", "profile", "output", "kind", "async-involved", "short-names", "help");
        if (parsed.HasFlag("help"))
        {
            Console.WriteLine("Usage: csindex symbol list [--kind method|lambda] [--async-involved] [--db <path>] [--output table|json] [--short-names]");
            return ExitCodes.Success;
        }

        if (parsed.Positionals.Count != 0)
        {
            throw new CliUsageException("symbol list does not accept positional arguments.");
        }

        var kind = parsed.GetSingle("kind") switch
        {
            null => (IndexedSymbolKind?)null,
            "method" => IndexedSymbolKind.Method,
            "lambda" => IndexedSymbolKind.Lambda,
            var value => throw new CliUsageException($"Unknown symbol kind: {value}. Use method or lambda."),
        };
        var service = CreateQueryService(parsed);
        var result = await service.ListSymbolsAsync(
            kind,
            parsed.HasFlag("async-involved"),
            parsed.GetSingle("profile"),
            cancellationToken);

        CreateFormatter(parsed).WriteSymbolList(result);
        return ExitCodes.Success;
    }

    private static async Task<int> RunDefinitionAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(
            args, "db", "profile", "output", "at", "require-single", "short-names", "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            Console.WriteLine("""
                Usage: csindex definition <query> | --at <path:line:column> [--short-names]

                  --include-overrides         Include descendant overrides and interface implementations
                """);
            return ExitCodes.Success;
        }

        var service = CreateQueryService(parsed);
        DefinitionResult result;
        var at = parsed.GetSingle("at");
        if (at is not null)
        {
            if (parsed.HasFlag("include-overrides"))
            {
                throw new CliUsageException("--include-overrides requires a method query.");
            }

            if (parsed.Positionals.Count > 0)
            {
                throw new CliUsageException("definition accepts either a query or --at, not both.");
            }

            result = await service.FindDefinitionAtAsync(at, parsed.GetSingle("profile"), cancellationToken);
        }
        else
        {
            result = await service.FindDefinitionsAsync(
                RequireQuery(parsed),
                parsed.GetSingle("profile"),
                includeOverrides: parsed.HasFlag("include-overrides"),
                cancellationToken);
        }

        if (RequiresSingleFailure(parsed, result.Definitions.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        CreateFormatter(parsed).WriteDefinitions(result);
        return ExitCodes.Success;
    }

    private static async Task<int> RunReferencesAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output", "exclude-generated", "only-generated", "require-single", "short-names",
            "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            Console.WriteLine("""
                Usage: csindex references <query> [options]

                  --include-overrides         Include descendant overrides and interface implementations
                """);
            return ExitCodes.Success;
        }

        var service = CreateQueryService(parsed);
        var result = await service.FindReferencesAsync(
            RequireQuery(parsed),
            ParseGeneratedFilter(parsed),
            parsed.GetSingle("profile"),
            includeOverrides: parsed.HasFlag("include-overrides"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.Context.MatchedSymbols.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        CreateFormatter(parsed).WriteCalls(result, "reference(s)");
        return ExitCodes.Success;
    }

    private static async Task<int> RunCallersAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output", "exclude-generated", "only-generated", "require-single", "dispatch",
            "caller-scope", "short-names", "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            Console.WriteLine("""
                Usage: csindex callers <query> [options]

                  --include-overrides         Include descendant overrides and interface implementations
                """);
            return ExitCodes.Success;
        }

        var dispatch = (parsed.GetSingle("dispatch") ?? "static") switch
        {
            "static" => DispatchSearchMode.Static,
            "virtual" => DispatchSearchMode.Virtual,
            "all" => DispatchSearchMode.All,
            var value => throw new CliUsageException($"Unknown dispatch mode: {value}"),
        };
        var callerScope = (parsed.GetSingle("caller-scope") ?? "direct") switch
        {
            "direct" => CallerScope.Direct,
            "containing" => CallerScope.Containing,
            "both" => CallerScope.Both,
            var value => throw new CliUsageException($"Unknown caller scope: {value}"),
        };
        var service = CreateQueryService(parsed);
        var result = await service.FindCallersAsync(
            RequireQuery(parsed),
            ParseGeneratedFilter(parsed),
            dispatch,
            callerScope,
            parsed.GetSingle("profile"),
            includeOverrides: parsed.HasFlag("include-overrides"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.Context.MatchedSymbols.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        CreateFormatter(parsed).WriteCalls(result, "caller call site(s)");
        return ExitCodes.Success;
    }

    private static async Task<int> RunCalleesAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output", "exclude-generated", "only-generated", "require-single", "short-names",
            "exclude-lambda-calls", "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            Console.WriteLine("""
                Usage: csindex callees <query> [options]

                  --include-overrides         Include descendant overrides and interface implementations
                """);
            return ExitCodes.Success;
        }

        var service = CreateQueryService(parsed);
        var result = await service.FindCalleesAsync(
            RequireQuery(parsed),
            ParseGeneratedFilter(parsed),
            includeLambdaCalls: !parsed.HasFlag("exclude-lambda-calls"),
            profileName: parsed.GetSingle("profile"),
            includeOverrides: parsed.HasFlag("include-overrides"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.Context.MatchedSymbols.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        CreateFormatter(parsed).WriteCalls(result, "callee call(s)");
        return ExitCodes.Success;
    }

    private static async Task<int> RunOverridesAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(args, "db", "profile", "output", "require-single", "short-names", "help");
        var service = CreateQueryService(parsed);
        var result = await service.FindOverridesAsync(
            RequireQuery(parsed),
            parsed.GetSingle("profile"),
            cancellationToken);
        if (RequiresSingleFailure(parsed, result.Context.MatchedSymbols.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        CreateFormatter(parsed).WriteRelations(result);
        return ExitCodes.Success;
    }

    private static async Task<int> RunConditionsAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseQueryArguments(args, "db", "profile", "output", "help");
        if (parsed.Positionals.Count != 0)
        {
            throw new CliUsageException("conditions does not accept a positional query.");
        }

        var service = CreateQueryService(parsed);
        var result = await service.GetConditionsAsync(parsed.GetSingle("profile"), cancellationToken);
        CreateFormatter(parsed).WriteConditions(result);
        return ExitCodes.Success;
    }

    private static CliArguments ParseQueryArguments(string[] args, params string[] allowed)
    {
        var parsed = CliArguments.Parse(args);
        parsed.EnsureOnly(allowed);
        return parsed;
    }

    private static SemanticQueryService CreateQueryService(CliArguments parsed)
    {
        var databasePath = parsed.GetSingle("db") ?? Path.Combine(Environment.CurrentDirectory, ".csindex", "index.sqlite");
        return new SemanticQueryService(new SqliteIndex(databasePath).CreateQueryRepository());
    }

    private static OutputFormatter CreateFormatter(CliArguments parsed) =>
        new(parsed.GetSingle("output") ?? "table", parsed.HasFlag("short-names"));

    private static string RequireQuery(CliArguments parsed)
    {
        if (parsed.Positionals.Count != 1)
        {
            throw new CliUsageException("This command requires exactly one symbol query.");
        }

        return parsed.Positionals[0];
    }

    private static bool RequiresSingleFailure(CliArguments parsed, int count)
    {
        if (!parsed.HasFlag("require-single") || count == 1)
        {
            return false;
        }

        Console.Error.WriteLine($"--require-single expected one symbol but matched {count}.");
        return true;
    }

    private static GeneratedFilter ParseGeneratedFilter(CliArguments parsed)
    {
        if (parsed.HasFlag("exclude-generated") && parsed.HasFlag("only-generated"))
        {
            throw new CliUsageException("--exclude-generated and --only-generated are mutually exclusive.");
        }

        return parsed.HasFlag("exclude-generated")
            ? GeneratedFilter.Exclude
            : parsed.HasFlag("only-generated")
                ? GeneratedFilter.Only
                : GeneratedFilter.Include;
    }

    private static GeneratedSourceMode ParseGeneratedSource(string? value) => value switch
    {
        null or "physical" => GeneratedSourceMode.Physical,
        "all" => GeneratedSourceMode.All,
        "none" => GeneratedSourceMode.None,
        _ => throw new CliUsageException($"Unknown generated-source mode: {value}"),
    };

    private static InputMode? ParseInputMode(string? value) => value switch
    {
        null or "auto" => null,
        "solution" => InputMode.Solution,
        "project" => InputMode.Project,
        "directory" => InputMode.Directory,
        _ => throw new CliUsageException($"Unknown input mode: {value}"),
    };

    private static string? CoalesceAliases(CliArguments parsed, string first, string second)
    {
        var firstValue = parsed.GetSingle(first);
        var secondValue = parsed.GetSingle(second);
        if (firstValue is not null && secondValue is not null)
        {
            throw new CliUsageException($"--{first} and --{second} are aliases; specify only one.");
        }

        return firstValue ?? secondValue;
    }

    private static void WriteIndexSummary(AnalysisResult result, string databasePath, IndexOptions options)
    {
        var snapshot = result.Snapshot;
        foreach (var warning in snapshot.Warnings)
        {
            Console.Error.WriteLine($"Warning: {warning}");
        }

        if (snapshot.ConditionalSymbols.Count > 0)
        {
            Console.Error.WriteLine("Warning CSIDX1001: Conditional compilation directives were found; only active branches were indexed.");
        }

        if (options.Diagnostics)
        {
            foreach (var diagnostic in snapshot.Diagnostics)
            {
                Console.Error.WriteLine($"Diagnostic: {diagnostic}");
            }
        }

        WriteProgress("Projects detected", snapshot.Projects.Count.ToString());
        WriteProgress("Documents detected", snapshot.Documents.Count.ToString());
        WriteProgress("Documents excluded", snapshot.DocumentsExcluded.ToString());
        WriteProgress("Generated documents", snapshot.Documents.Count(document => document.IsGenerated).ToString());
        WriteProgress("Analysis profile", snapshot.Profile.Name);
        WriteProgress("Active preprocessor symbols", string.Join(", ", snapshot.Profile.PreprocessorSymbols));
        WriteProgress("Metadata references", snapshot.MetadataReferences.Count.ToString());
        WriteProgress("Unresolved references", snapshot.Calls.Count(call => call.ResolutionStatus != ResolutionStatus.Resolved).ToString());
        WriteProgress(
            "Compilation diagnostics summary",
            string.Join(", ", snapshot.CompilationSummaries.Select(summary =>
                $"{summary.ProjectName}: {summary.Errors} error(s), {summary.Warnings} warning(s)")));
        WriteProgress("Symbols indexed", snapshot.Symbols.Count.ToString());
        WriteProgress("Calls indexed", snapshot.Calls.Count.ToString());
        WriteProgress("Relations indexed", snapshot.Relations.Count.ToString());
        WriteProgress("Elapsed time", result.Elapsed.ToString("c"));
        WriteProgress("Memory", $"{GC.GetTotalMemory(false) / (1024 * 1024)} MiB");
        WriteProgress("Database path", databasePath);
        WriteProgress("Cache", "rebuilt");
        if (options.Verbose)
        {
            foreach (var project in snapshot.Projects)
            {
                WriteProgress("Project", project.ProjectPath ?? project.Name);
            }

            foreach (var document in snapshot.Documents)
            {
                WriteProgress("Document", document.NormalizedPath);
            }

            foreach (var reference in snapshot.MetadataReferences)
            {
                WriteProgress("Metadata reference", reference);
            }
        }
    }

    private static void WriteProgress(string name, string value) => Console.Error.WriteLine($"{name}: {value}");

    private static void WriteHelp()
    {
        Console.WriteLine("""
            csindex - C# semantic analysis and search CLI

            Usage:
              csindex index <input> [options]
              csindex symbol find <query> [options]
              csindex symbol list [options]
              csindex definition <query> [options]
              csindex definition --at <path:line:column> [options]
              csindex references <query> [options]
              csindex callers <query> [options]
              csindex callees <query> [options]
              csindex overrides <query> [options]
              csindex conditions [options]

            Common query options:
              --db <path>                 SQLite index path (default: .csindex/index.sqlite)
              --profile <name>            Analysis profile
              --output table|json         Output format
              --exclude-generated         Exclude generated documents
              --only-generated            Include only generated documents
              --require-single            Fail unless the query matches one symbol
              --short-names               Shorten namespaces in displayed symbol names
              --include-overrides         Include descendant overrides and interface implementations

            Symbol list options:
              --kind method|lambda         Limit listed function symbols by kind
              --async-involved             Include only symbols with async involvement

            Callees options:
              --exclude-lambda-calls       Exclude calls made by nested lambdas

            Run 'csindex index --help' for indexing options.
            """);
    }

    private static void WriteIndexHelp()
    {
        Console.WriteLine("""
            Usage: csindex index <input> [options]

              --db <path>
              --mode auto|solution|project|directory
              --solution <path>
              --configuration <name>
              --framework <tfm> | --target-framework <tfm>
              --runtime <rid>
              --profile-name <name>
              --define <symbol>            Repeatable
              --undefine <symbol>          Repeatable
              --define-file <path>         Repeatable
              --reference <dll>            Repeatable
              --unity-editor <directory>   Reserved for Phase 3
              --exclude <glob>             Repeatable; obj is always excluded
              --generated-source physical|all|none
              --rebuild
              --verbose
              --diagnostics
            """);
    }
}
