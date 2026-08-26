using System.Globalization;
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
    // Internal Task 5 seam proving prepared-analysis lifetime on cache and failure paths.
    internal static Action<PreparedAnalysis>? PreparedAnalysisObserver { get; set; }

    private static readonly string[] IndexOptions =
    [
        "db", "mode", "solution", "configuration", "framework", "target-framework", "runtime",
        "profile-name", "define", "undefine", "define-file", "reference", "unity-editor", "exclude",
        "generated-source", "rebuild", "verbose", "diagnostics", "help",
    ];

    private static readonly HelpOption DatabaseHelpOption = new(
        "--db <path>", "SQLite index path (default: .csindex/index.sqlite)");
    private static readonly HelpOption ProfileHelpOption = new(
        "--profile <name>", "Analysis profile (default: most recently indexed profile)");
    private static readonly HelpOption ShortNamesHelpOption = new(
        "--short-names", "Shorten namespaces in displayed symbol names");
    private static readonly HelpOption HelpHelpOption = new("--help", "Show this help text");
    private static readonly HelpOption TableJsonOutputHelpOption = new(
        "--output-format table|json", "Output format (default: table)");
    private static readonly HelpOption AsyncOutputHelpOption = new(
        "--output-format tree|line|json", "Output format (default: tree)");
    private static readonly HelpOption CallerOutputHelpOption = new(
        "--output-format tree|mermaid|json", "Output format (default: tree)");
    private static readonly HelpOption FunctionKindHelpOption = new(
        "--kind all|method|lambda", "Limit function targets by kind (default: all)");
    private static readonly HelpOption AsyncStatusHelpOption = new(
        "--async-status all|async|sync", "Limit function targets by direct async status (default: all)");
    private static readonly HelpOption SourceLayoutHelpOption = new(
        "--source-layout single-line|multi-line", "Source table layout (default: single-line)");
    private static readonly HelpOption OutputFileHelpOption = new(
        "-o <path> | --output-file <path>", "Write the result payload to a file");
    private static readonly HelpOption IncludeHelpOption = new(
        "--include <text>", "Require normalized source text (repeatable)");
    private static readonly HelpOption ExcludeHelpOption = new(
        "--exclude <text>", "Reject normalized source text (repeatable)");
    private static readonly HelpOption FileHelpOption = new(
        "--file <pattern>", "Stored source path filter");

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancellationHandler;
        try
        {
            return await RunAsync(
                args,
                cancellation.Token,
                static (outputPath, databasePath) => OutputDestination.Create(outputPath, databasePath));
        }
        finally
        {
            Console.CancelKeyPress -= cancellationHandler;
        }
    }

    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(outputDestinationFactory);
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteHelp();
            return ExitCodes.Success;
        }

        try
        {
            return args[0] switch
            {
                "index" => await RunIndexAsync(args[1..], cancellationToken),
                "symbol" when args.Length > 1 && args[1] == "list" =>
                    await RunSymbolListAsync(args[2..], cancellationToken, outputDestinationFactory),
                "symbol" when args.Length > 1 && args[1] == "find" =>
                    await RunSymbolAsync(args[2..], cancellationToken, outputDestinationFactory),
                "async" when args.Length > 1 && args[1] == "tree" =>
                    await RunAsyncTreeAsync(args[2..], cancellationToken, outputDestinationFactory),
                "callers" when args.Length > 1 && args[1] == "tree" =>
                    await RunCallerTreeAsync(args[2..], cancellationToken, outputDestinationFactory),
                "source" when args.Length > 1 && args[1] == "show" =>
                    await RunSourceShowAsync(args[2..], cancellationToken, outputDestinationFactory),
                "source" when args.Length > 1 && args[1] == "search" =>
                    await RunSourceSearchAsync(args[2..], cancellationToken, outputDestinationFactory),
                "definition" => await RunDefinitionAsync(args[1..], cancellationToken, outputDestinationFactory),
                "references" => await RunReferencesAsync(args[1..], cancellationToken, outputDestinationFactory),
                "callers" => await RunCallersAsync(args[1..], cancellationToken, outputDestinationFactory),
                "callees" => await RunCalleesAsync(args[1..], cancellationToken, outputDestinationFactory),
                "overrides" => await RunOverridesAsync(args[1..], cancellationToken, outputDestinationFactory),
                "conditions" => await RunConditionsAsync(args[1..], cancellationToken, outputDestinationFactory),
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
        catch (OutputException exception)
        {
            Console.Error.WriteLine($"Output error: {exception.Message}");
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
        var paths = IndexPathResolver.CreateForIndex(databasePath, input.RootPath);
        using var prepared = await coordinator.PrepareAsync(input, options, paths, cancellationToken);
        PreparedAnalysisObserver?.Invoke(prepared);
        var requestHash = RequestHasher.Build(input, options);
        var inputFingerprint = await coordinator.BuildInputFingerprintAsync(
            input,
            options,
            paths,
            cancellationToken);
        var index = new SqliteIndex(paths.DatabasePath);

        WriteProgress("Input mode", input.Mode.ToString());
        WriteProgress("Input path", input.OriginalPath);
        WriteProgress("Database path", paths.DatabasePath);
        if (!options.Rebuild && await index.IsCacheValidAsync(
                ".",
                inputFingerprint,
                requestHash,
                cancellationToken))
        {
            WriteProgress("Cache", "reused");
            return ExitCodes.Success;
        }

        var result = await coordinator.AnalyzeAsync(
            prepared,
            options,
            inputFingerprint,
            requestHash,
            cancellationToken);
        await index.SaveAsync(result.Snapshot, cancellationToken);
        WriteIndexSummary(result, paths.DatabasePath, options);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSymbolAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "require-single", "short-names", "include-overrides", "namespace", "type",
            "method", "file", "kind", "async-status", "include", "exclude", "show-source", "source-layout", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex symbol find [<pattern>] [options]",
                ["Provide <pattern> or at least one of --namespace, --type, or --method."],
                DatabaseHelpOption,
                ProfileHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--require-single", "Fail unless the search matches exactly one symbol"),
                ShortNamesHelpOption,
                new HelpOption("--namespace <pattern>", "Namespace component filter"),
                new HelpOption("--type <pattern>", "Type component filter"),
                new HelpOption("--method <pattern>", "Method component filter"),
                FileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                IncludeHelpOption,
                ExcludeHelpOption,
                new HelpOption("--show-source", "Include normalized source in output"),
                SourceLayoutHelpOption,
                new HelpOption(
                    "--include-overrides",
                    "Include descendant overrides and interface implementations (exact method pattern only)"),
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var request = CreateSymbolSelectionRequest(parsed);
        var showSource = parsed.HasFlag("show-source");
        var formatterSettings = ParseOutputFormatterSettings(parsed, sourceLayoutAllowed: showSource);
        if (parsed.HasFlag("include-overrides"))
        {
            if (request.KindSpecified && request.FunctionFilter.Kind == IndexedSymbolKind.Lambda)
            {
                throw new CliUsageException("--kind lambda cannot be combined with --include-overrides.");
            }

            if (!IsExactOverrideSearch(request))
            {
                throw new CliUsageException(
                    "--include-overrides cannot be combined with component, lambda, or declaration filter options.");
            }
        }

        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        QueryContext result;
        if (parsed.HasFlag("include-overrides"))
        {
            result = await service.FindSymbolsAsync(
                request,
                profileName: parsed.GetSingle("profile"),
                sourceOnly: false,
                includeOverrides: true,
                includeSourceText: showSource,
                cancellationToken: cancellationToken);
            result = result with { ShowSource = showSource };
        }
        else
        {
            result = await service.SearchSymbolsAsync(
                request,
                showSource,
                parsed.GetSingle("profile"),
                cancellationToken);
        }

        if (RequiresSingleFailure(parsed, result.MatchedSymbols.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSymbols(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunAsyncTreeAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args, "db", "profile", "output-format", "output-file", "kind", "async-status", "max-nodes", "short-names", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex async tree <symbol> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                AsyncOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--max-nodes <count>", "Maximum path nodes (default: 500)"),
                ShortNamesHelpOption,
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var output = ParseOutput(parsed.GetSingle("output-format") ?? "tree", "async tree", "tree", "line", "json");
        var query = RequireQuery(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var maxNodes = ParsePositiveInteger(parsed.GetSingle("max-nodes"), "Maximum node count", defaultValue: 500);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var result = await CreateQueryService(parsed).FindAsyncPathAsync(
            query,
            filter,
            maxNodes,
            parsed.GetSingle("profile"),
            cancellationToken);
        destination.WritePayload(
            writer => new GraphOutputFormatter(parsed.HasFlag("short-names"), writer)
                .WriteAsyncPath(result, output, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunCallerTreeAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args, "db", "profile", "output-format", "output-file", "kind", "async-status", "depth", "max-nodes", "short-names", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex callers tree <symbol> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                CallerOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--depth <count>", "Maximum caller depth; 0 is unlimited (default: 3)"),
                new HelpOption("--max-nodes <count>", "Maximum graph nodes (default: 500)"),
                ShortNamesHelpOption,
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var output = ParseOutput(parsed.GetSingle("output-format") ?? "tree", "callers tree", "tree", "mermaid", "json");
        var query = RequireQuery(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var depth = ParseNonNegativeInteger(parsed.GetSingle("depth"), "Depth", defaultValue: 3);
        var maxNodes = ParsePositiveInteger(parsed.GetSingle("max-nodes"), "Maximum node count", defaultValue: 500);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var result = await CreateQueryService(parsed).FindCallerTreeAsync(
            query,
            filter,
            depth,
            maxNodes,
            parsed.GetSingle("profile"),
            cancellationToken);
        destination.WritePayload(
            writer => new GraphOutputFormatter(parsed.HasFlag("short-names"), writer)
                .WriteCallerTree(result, output, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSourceShowAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args, "db", "profile", "output-format", "output-file", "kind", "async-status", "source-layout", "short-names", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex source show <symbol> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                SourceLayoutHelpOption,
                ShortNamesHelpOption,
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed, sourceLayoutAllowed: true);
        var query = RequireQuery(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var result = await CreateQueryService(parsed).ShowSourceAsync(
            query,
            filter,
            profileName: parsed.GetSingle("profile"),
            cancellationToken: cancellationToken);
        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSymbols(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSourceSearchAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "kind", "async-status", "source-layout", "include", "exclude", "short-names", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex source search (--include <text> | --exclude <text>)... [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                IncludeHelpOption,
                ExcludeHelpOption,
                SourceLayoutHelpOption,
                ShortNamesHelpOption,
                HelpHelpOption);
            return ExitCodes.Success;
        }

        if (parsed.Positionals.Count != 0)
        {
            throw new CliUsageException("source search does not accept positional arguments.");
        }

        var includes = parsed.GetMany("include");
        var excludes = parsed.GetMany("exclude");
        if (includes.Count == 0 && excludes.Count == 0)
        {
            throw new CliUsageException("source search requires at least one include or exclude condition.");
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed, sourceLayoutAllowed: true);
        var request = CreateSymbolSelectionRequest(parsed, requireNameCondition: false);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var result = await CreateQueryService(parsed).SearchSourceAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            cancellationToken: cancellationToken);
        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSymbols(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSymbolListAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "kind", "async-status", "async-involved", "short-names", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex symbol list [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--async-involved", "Include only symbols with async involvement"),
                ShortNamesHelpOption,
                HelpHelpOption);
            return ExitCodes.Success;
        }

        if (parsed.Positionals.Count != 0)
        {
            throw new CliUsageException("symbol list does not accept positional arguments.");
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        var result = await service.ListSymbolsAsync(
            filter.Kind,
            filter.AsyncStatus,
            parsed.HasFlag("async-involved"),
            parsed.GetSingle("profile"),
            cancellationToken);

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSymbolList(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunDefinitionAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "kind", "async-status", "at", "require-single", "short-names",
            "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex definition <query> | --at <path:line:column> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--at <path:line:column>", "Resolve the call target at a source position"),
                new HelpOption("--require-single", "Fail unless the search matches exactly one symbol"),
                ShortNamesHelpOption,
                new HelpOption(
                    "--include-overrides",
                    "Include descendant overrides and interface implementations (method queries only)"),
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var at = parsed.GetSingle("at");
        string? query = null;
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
        }
        else
        {
            query = RequireQuery(parsed);
        }

        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        DefinitionResult result;
        if (at is not null)
        {
            result = await service.FindDefinitionAtAsync(
                at,
                filter,
                profileName: parsed.GetSingle("profile"),
                cancellationToken: cancellationToken);
        }
        else
        {
            result = await service.FindDefinitionsAsync(
                query!,
                filter,
                profileName: parsed.GetSingle("profile"),
                includeOverrides: parsed.HasFlag("include-overrides"),
                cancellationToken: cancellationToken);
        }

        if (RequiresSingleFailure(parsed, result.Selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteDefinitions(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunReferencesAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "kind", "async-status", "exclude-generated", "only-generated",
            "require-single", "short-names", "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex references <query> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--exclude-generated", "Exclude generated documents"),
                new HelpOption("--only-generated", "Include only generated documents"),
                new HelpOption("--require-single", "Fail unless the search matches exactly one symbol"),
                ShortNamesHelpOption,
                new HelpOption(
                    "--include-overrides",
                    "Include descendant overrides and interface implementations (method queries only)"),
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var query = RequireQuery(parsed);
        var generatedFilter = ParseGeneratedFilter(parsed);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        var result = await service.FindReferencesAsync(
            query,
            generatedFilter,
            filter,
            profileName: parsed.GetSingle("profile"),
            includeOverrides: parsed.HasFlag("include-overrides"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.Selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteCalls(result, "reference(s)", cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunCallersAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "kind", "async-status", "exclude-generated", "only-generated",
            "require-single", "dispatch", "caller-scope", "short-names", "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex callers <query> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--exclude-generated", "Exclude generated documents"),
                new HelpOption("--only-generated", "Include only generated documents"),
                new HelpOption("--require-single", "Fail unless the search matches exactly one symbol"),
                new HelpOption("--dispatch static|virtual|all", "Dispatch mode (default: static)"),
                new HelpOption("--caller-scope direct|containing|both", "Caller scope (default: direct)"),
                ShortNamesHelpOption,
                new HelpOption(
                    "--include-overrides",
                    "Include descendant overrides and interface implementations (method queries only)"),
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var query = RequireQuery(parsed);
        var generatedFilter = ParseGeneratedFilter(parsed);
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
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        var result = await service.FindCallersAsync(
            query,
            generatedFilter,
            dispatch,
            callerScope,
            filter,
            profileName: parsed.GetSingle("profile"),
            includeOverrides: parsed.HasFlag("include-overrides"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.Selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteCalls(
                result,
                "caller call site(s)",
                cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunCalleesAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "kind", "async-status", "exclude-generated", "only-generated",
            "require-single", "short-names", "exclude-lambda-calls", "include-overrides", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex callees <query> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--exclude-generated", "Exclude generated documents"),
                new HelpOption("--only-generated", "Include only generated documents"),
                new HelpOption("--require-single", "Fail unless the search matches exactly one symbol"),
                ShortNamesHelpOption,
                new HelpOption("--exclude-lambda-calls", "Exclude calls made by nested lambdas"),
                new HelpOption(
                    "--include-overrides",
                    "Include descendant overrides and interface implementations (method queries only)"),
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var query = RequireQuery(parsed);
        var generatedFilter = ParseGeneratedFilter(parsed);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        var result = await service.FindCalleesAsync(
            query,
            generatedFilter,
            filter,
            includeLambdaCalls: !parsed.HasFlag("exclude-lambda-calls"),
            profileName: parsed.GetSingle("profile"),
            includeOverrides: parsed.HasFlag("include-overrides"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.Selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteCalls(result, "callee call(s)", cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunOverridesAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(
            args,
            "db", "profile", "output-format", "output-file", "kind", "async-status", "require-single", "short-names", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex overrides <query> [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                FunctionKindHelpOption,
                AsyncStatusHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                new HelpOption("--require-single", "Fail unless the search matches exactly one symbol"),
                ShortNamesHelpOption,
                HelpHelpOption);
            return ExitCodes.Success;
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var query = RequireQuery(parsed);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        var result = await service.FindOverridesAsync(
            query,
            filter,
            profileName: parsed.GetSingle("profile"),
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, result.Selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteRelations(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunConditionsAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        var parsed = ParseQueryArguments(args, "db", "profile", "output-format", "output-file", "help");
        if (parsed.HasFlag("help"))
        {
            WriteCommandHelp(
                "csindex conditions [options]",
                [],
                DatabaseHelpOption,
                ProfileHelpOption,
                TableJsonOutputHelpOption,
                OutputFileHelpOption,
                HelpHelpOption);
            return ExitCodes.Success;
        }

        if (parsed.Positionals.Count != 0)
        {
            throw new CliUsageException("conditions does not accept a positional query.");
        }

        var formatterSettings = ParseOutputFormatterSettings(parsed);
        using var destination = CreateOutputDestination(parsed, outputDestinationFactory);
        var service = CreateQueryService(parsed);
        var result = await service.GetConditionsAsync(parsed.GetSingle("profile"), cancellationToken);
        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteConditions(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static CliArguments ParseQueryArguments(string[] args, params string[] allowed)
    {
        var parsed = CliArguments.Parse(args);
        parsed.EnsureOnly(allowed);
        return parsed;
    }

    private static SymbolSelectionRequest CreateSymbolSelectionRequest(
        CliArguments parsed,
        bool requireNameCondition = true)
    {
        if (parsed.Positionals.Count > 1)
        {
            throw new CliUsageException("symbol find accepts at most one positional pattern.");
        }

        var pattern = parsed.Positionals.Count == 1 ? parsed.Positionals[0] : null;
        var namespacePattern = parsed.GetSingle("namespace");
        var typePattern = parsed.GetSingle("type");
        var methodPattern = parsed.GetSingle("method");
        var filter = ParseFunctionTargetFilter(parsed);
        if (requireNameCondition &&
            pattern is null &&
            namespacePattern is null &&
            typePattern is null &&
            methodPattern is null)
        {
            throw new CliUsageException(
                "symbol find requires a pattern or at least one --namespace, --type, or --method condition.");
        }

        var conditions = new List<TypedCondition>();
        AddCondition(ConditionCategory.Namespace, namespacePattern);
        AddCondition(ConditionCategory.Type, typePattern);
        AddCondition(ConditionCategory.Method, methodPattern);
        AddCondition(ConditionCategory.File, parsed.GetSingle("file"));
        foreach (var include in parsed.GetMany("include"))
        {
            conditions.Add(new TypedCondition(ConditionCategory.Include, ConditionSyntax.Glob, include));
        }

        foreach (var exclude in parsed.GetMany("exclude"))
        {
            conditions.Add(new TypedCondition(ConditionCategory.Exclude, ConditionSyntax.Glob, exclude));
        }

        return new SymbolSelectionRequest(
            pattern,
            conditions,
            new SymbolCaseOptions(),
            filter,
            KindSpecified: parsed.GetSingle("kind") is not null,
            AsyncStatusSpecified: parsed.GetSingle("async-status") is not null);

        void AddCondition(ConditionCategory category, string? value)
        {
            if (value is not null)
            {
                conditions.Add(new TypedCondition(category, ConditionSyntax.Glob, value));
            }
        }
    }

    private static FunctionTargetFilter ParseFunctionTargetFilter(CliArguments parsed)
    {
        var kind = parsed.GetSingle("kind") switch
        {
            null or "all" => (IndexedSymbolKind?)null,
            "method" => IndexedSymbolKind.Method,
            "lambda" => IndexedSymbolKind.Lambda,
            var value => throw new CliUsageException(
                $"Unknown symbol kind: {value}. Use all, method, or lambda."),
        };
        var asyncStatus = parsed.GetSingle("async-status") switch
        {
            null or "all" => AsyncStatusFilter.All,
            "async" => AsyncStatusFilter.Async,
            "sync" => AsyncStatusFilter.Sync,
            var value => throw new CliUsageException(
                $"Unknown async status: {value}. Use all, async, or sync."),
        };

        return new FunctionTargetFilter(kind, asyncStatus);
    }

    private static bool IsExactOverrideSearch(SymbolSelectionRequest request) =>
        request.Selector is not null &&
        !request.Selector.Contains('*') &&
        request.Conditions.Count == 0;

    private static string ParseOutput(string value, string command, params string[] allowed)
    {
        if (allowed.Contains(value, StringComparer.Ordinal))
        {
            return value;
        }

        throw new CliUsageException($"Unknown {command} output: {value}. Use {string.Join(", ", allowed)}.");
    }

    private static int ParseNonNegativeInteger(string? value, string description, int defaultValue)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new CliUsageException($"{description} must be an integer.");
        }

        if (parsed < 0)
        {
            throw new CliUsageException($"{description} cannot be negative.");
        }

        return parsed;
    }

    private static int ParsePositiveInteger(string? value, string description, int defaultValue)
    {
        var parsed = ParseNonNegativeInteger(value, description, defaultValue);
        if (parsed == 0)
        {
            throw new CliUsageException($"{description} must be positive.");
        }

        return parsed;
    }

    private static SemanticQueryService CreateQueryService(CliArguments parsed) =>
        new(new SqliteIndex(GetDatabasePath(parsed)).CreateQueryRepository());

    private static string GetDatabasePath(CliArguments parsed) =>
        parsed.GetSingle("db") ?? Path.Combine(Environment.CurrentDirectory, ".csindex", "index.sqlite");

    private static OutputDestination CreateOutputDestination(
        CliArguments parsed,
        Func<string?, string, OutputDestination> outputDestinationFactory) =>
        outputDestinationFactory(parsed.GetSingle("output-file"), GetDatabasePath(parsed));

    private static OutputFormatterSettings ParseOutputFormatterSettings(
        CliArguments parsed,
        bool sourceLayoutAllowed = false)
    {
        var outputFormat = parsed.GetSingle("output-format") ?? "table";
        if (outputFormat is not ("table" or "json"))
        {
            throw new CliUsageException(
                $"Output format '{outputFormat}' is reserved but not implemented. Use table or json.");
        }

        var sourceLayoutValue = parsed.GetSingle("source-layout");
        var sourceLayout = ParseSourceLayout(sourceLayoutValue);
        if (sourceLayoutValue is not null && !sourceLayoutAllowed)
        {
            throw new CliUsageException("--source-layout requires --show-source for symbol find.");
        }

        if (sourceLayoutValue is not null && outputFormat == "json")
        {
            throw new CliUsageException("--source-layout cannot be combined with --output-format json.");
        }

        return new OutputFormatterSettings(outputFormat, parsed.HasFlag("short-names"), sourceLayout);
    }

    private static OutputFormatter CreateFormatter(OutputFormatterSettings settings, TextWriter writer) => new(
        settings.Format,
        settings.ShortNames,
        settings.SourceLayout,
        writer,
        Console.Error);

    private static SourceLayout ParseSourceLayout(string? value) => value switch
    {
        null or "single-line" => SourceLayout.SingleLine,
        "multi-line" => SourceLayout.MultiLine,
        _ => throw new CliUsageException(
            $"Unknown source layout: {value}. Use single-line or multi-line."),
    };

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
              csindex symbol find [<pattern>] [options]
              csindex symbol list [options]
              csindex async tree <symbol> [options]
              csindex callers tree <symbol> [options]
              csindex source show <symbol> [options]
              csindex source search (--include <text> | --exclude <text>)... [options]
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
              --output-format table|json  Output format
              -o <path> | --output-file <path>
                                          Write the result payload to a file
              --kind all|method|lambda    Limit function targets by kind (default: all)
              --async-status all|async|sync
                                          Limit function targets by direct async status (default: all)
              --exclude-generated         Exclude generated documents
              --only-generated            Include only generated documents
              --require-single            Fail unless the query matches one symbol
              --short-names               Shorten namespaces in displayed symbol names
              --include-overrides         Include descendant overrides and interface implementations (method queries only)

            Symbol list options:
              --async-involved             Include only symbols with async involvement

            Symbol find options:
              --namespace <pattern>        Namespace component filter
              --type <pattern>             Type component filter
              --method <pattern>           Method component filter
              --file <pattern>             Stored source path filter
              --include <text>             Require normalized source text (repeatable)
              --exclude <text>             Reject normalized source text (repeatable)
              --show-source                Include normalized source in output
              --source-layout single-line|multi-line
                                          Source table layout (default: single-line)

            Async tree options:
              --output-format tree|line|json
                                          Output format (default: tree)
              --max-nodes <count>          Maximum path nodes (default: 500)

            Callers tree options:
              --depth <count>              Maximum caller depth; 0 is unlimited (default: 3)
              --max-nodes <count>          Maximum graph nodes (default: 500)
              --output-format tree|mermaid|json
                                          Output format (default: tree)

            Source commands:
              source show supports --output-format table|json and --source-layout single-line|multi-line
              source search requires --include <text> or --exclude <text> and supports --source-layout single-line|multi-line

            Callees options:
              --exclude-lambda-calls       Exclude calls made by nested lambdas

            Run 'csindex index --help' for indexing options.
            """);
    }

    private static void WriteCommandHelp(
        string usage,
        IReadOnlyList<string> notes,
        params HelpOption[] options)
    {
        Console.WriteLine($"Usage: {usage}");
        if (notes.Count > 0)
        {
            Console.WriteLine();
            foreach (var note in notes)
            {
                Console.WriteLine($"  {note}");
            }
        }

        Console.WriteLine();
        foreach (var option in options)
        {
            var padding = new string(' ', Math.Max(1, 28 - option.Syntax.Length));
            Console.WriteLine($"  {option.Syntax}{padding}{option.Description}");
        }
    }

    private readonly record struct OutputFormatterSettings(
        string Format,
        bool ShortNames,
        SourceLayout SourceLayout);

    private sealed record HelpOption(string Syntax, string Description);

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
