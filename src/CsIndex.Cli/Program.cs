using System.Globalization;
using System.Collections.Immutable;
using CsIndex.Core.Analysis;
using CsIndex.Core.Caching;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Query.Symbols;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal sealed record ProgramDependencies(
    Func<string?, string, OutputDestination> OutputDestinationFactory,
    Func<string, string?, SemanticQueryService> QueryServiceFactory,
    Func<AnalysisCoordinator> AnalysisCoordinatorFactory,
    Func<string, SqliteIndex> SqliteIndexFactory);

internal static class Program
{
    // Internal Task 5 seam proving prepared-analysis lifetime on cache and failure paths.
    internal static Action<PreparedAnalysis>? PreparedAnalysisObserver { get; set; }

    private static readonly ProgramDependencies DefaultDependencies = new(
        static (outputPath, databasePath) => OutputDestination.Create(outputPath, databasePath),
        static (databasePath, baseDirectory) =>
            new SemanticQueryService(new SqliteIndex(databasePath).CreateQueryRepository(), baseDirectory),
        static () => AnalysisCoordinator.CreateDefault(),
        static databasePath => new SqliteIndex(databasePath));

    private static readonly string[] NamespaceConditions =
    [
        "namespace", "namespace-literal", "namespace-regex", "namespace-case",
    ];

    private static readonly string[] TypeConditions =
    [
        "type", "type-literal", "type-regex", "type-case",
    ];

    private static readonly string[] MethodConditions =
    [
        "method", "method-literal", "method-regex", "method-case",
    ];

    private static readonly string[] FileConditions =
    [
        "file", "file-literal", "file-regex", "file-case",
    ];

    private static readonly string[] SourceConditions =
    [
        "include", "include-literal", "include-regex",
        "exclude", "exclude-literal", "exclude-regex", "source-case",
    ];

    private static readonly string[] QueryPathOptions = ["base-dir", "path-style"];

    private static readonly string[] SymbolPresentationOptions = ["symbol-path-style", "short-names"];

    private static readonly string[] RootConditions =
    [
        .. NamespaceConditions, .. TypeConditions, .. MethodConditions, .. FileConditions,
    ];

    private static readonly string[] AllConditions = [.. RootConditions, .. SourceConditions];

    private static readonly string[] ConditionOptions =
    [
        "namespace", "namespace-literal", "namespace-regex",
        "type", "type-literal", "type-regex",
        "method", "method-literal", "method-regex",
        "file", "file-literal", "file-regex",
        "include", "include-literal", "include-regex",
        "exclude", "exclude-literal", "exclude-regex",
    ];

    private static readonly string[] QuerySelectionOptions = [.. AllConditions, "kind", "async-status"];

    private static readonly string[] QuerySingletonOptions =
    [
        "db", "profile", "output-format", "output-file",
        "namespace-case", "type-case", "method-case", "file-case", "source-case",
        "kind", "async-status", "symbol-path-style", "base-dir", "path-style",
        "source-layout", "at", "dispatch", "caller-scope", "depth", "max-nodes",
    ];

    private static readonly string[] QueryPresentationPathOptions =
    [
        "db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose",
        .. SymbolPresentationOptions, .. QueryPathOptions,
    ];

    private static readonly string[] QueryOptions =
    [
        .. QueryPresentationPathOptions, .. QuerySelectionOptions,
    ];

    private static readonly string[] GlobalOptions =
    ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose"];

    private static readonly string[] DefinitionAtOptions =
    [
        .. QueryPresentationPathOptions, "at",
    ];

    private static readonly string[] IndexOptions =
    [
        "db", "mode", "solution", "configuration", "framework", "target-framework", "runtime",
        "profile-name", "define", "undefine", "define-file", "reference", "unity-editor", "exclude",
        "generated-source", "rebuild", "verbose", "diagnostics", "help", "help-verbose",
    ];

    private static readonly HelpOption DatabaseHelpOption = new(
        "--db <path>", "SQLite index path (default: .csindex/index.sqlite)");
    private static readonly HelpOption ProfileHelpOption = new(
        "--profile <name>", "Analysis profile (default: most recently indexed profile)");
    private static readonly HelpOption ShortNamesHelpOption = new(
        "--short-names", "Shorten namespaces in displayed symbol names");
    private static readonly HelpOption HelpHelpOption = new("--help", "Show this help text");
    private static readonly HelpOption HelpVerboseHelpOption = new(
        "--help-verbose", "Show the full symbol-path and query grammar reference");
    private static readonly HelpOption VerboseHelpOption = new(
        "--verbose", "With --help, show the full reference; index uses runtime progress");
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
            return await RunAsync(args, cancellation.Token, DefaultDependencies);
        }
        finally
        {
            Console.CancelKeyPress -= cancellationHandler;
        }
    }

    internal static Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<string?, string, OutputDestination> outputDestinationFactory)
    {
        ArgumentNullException.ThrowIfNull(outputDestinationFactory);
        return RunAsync(
            args,
            cancellationToken,
            DefaultDependencies with { OutputDestinationFactory = outputDestinationFactory });
    }

    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(dependencies.OutputDestinationFactory);
        ArgumentNullException.ThrowIfNull(dependencies.QueryServiceFactory);
        ArgumentNullException.ThrowIfNull(dependencies.AnalysisCoordinatorFactory);
        ArgumentNullException.ThrowIfNull(dependencies.SqliteIndexFactory);
        if (args.Length == 0 ||
            args is ["-h"] or ["help"])
        {
            WriteHelp();
            return ExitCodes.Success;
        }

        try
        {
            if (IsGlobalHelpRequest(args))
            {
                var global = CliArguments.Parse(args);
                global.EnsureOnly(GlobalOptions);
                if (global.Positionals.Count > 0)
                {
                    throw new CliUsageException("Global help does not accept positional arguments.");
                }

                WriteHelp(global.HasFlag("help-verbose") || global.HasFlag("verbose"));
                return ExitCodes.Success;
            }

            return args[0] switch
            {
                "index" => await RunIndexAsync(args[1..], cancellationToken, dependencies),
                "symbol" when args.Length > 1 && args[1] == "list" =>
                    await RunSymbolListAsync(args[2..], cancellationToken, dependencies),
                "symbol" when args.Length > 1 && args[1] == "find" =>
                    await RunSymbolAsync(args[2..], cancellationToken, dependencies),
                "async" when args.Length > 1 && args[1] == "tree" =>
                    await RunAsyncTreeAsync(args[2..], cancellationToken, dependencies),
                "callers" when args.Length > 1 && args[1] == "tree" =>
                    await RunCallerTreeAsync(args[2..], cancellationToken, dependencies),
                "source" when args.Length > 1 && args[1] == "show" =>
                    await RunSourceShowAsync(args[2..], cancellationToken, dependencies),
                "source" when args.Length > 1 && args[1] == "search" =>
                    await RunSourceSearchAsync(args[2..], cancellationToken, dependencies),
                "definition" => await RunDefinitionAsync(args[1..], cancellationToken, dependencies),
                "references" => await RunReferencesAsync(args[1..], cancellationToken, dependencies),
                "callers" => await RunCallersAsync(args[1..], cancellationToken, dependencies),
                "callees" => await RunCalleesAsync(args[1..], cancellationToken, dependencies),
                "overrides" => await RunOverridesAsync(args[1..], cancellationToken, dependencies),
                "conditions" => await RunConditionsAsync(args[1..], cancellationToken, dependencies),
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

    private static async Task<int> RunIndexAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        var parsed = CliArguments.Parse(args);
        parsed.EnsureOnly(IndexOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteIndexHelp(parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"));
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
        var coordinator = dependencies.AnalysisCoordinatorFactory();
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
        var index = dependencies.SqliteIndexFactory(paths.DatabasePath);

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
        ProgramDependencies dependencies)
    {
        string[] allowedOptions =
            [.. QueryOptions, "require-single", "include-overrides", "show-source", "source-layout"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex symbol find [<pattern>] [options]",
                ["Provide <pattern> or at least one typed condition, --kind, or --async-status."],
                allowedOptions,
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

        var selector = GetOptionalSelector(parsed, "symbol find");
        var request = CreateSelectionRequest(parsed, selector);
        EnsureSelectionMinimum(request, "symbol find");
        var showSource = parsed.HasFlag("show-source");
        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed, sourceLayoutAllowed: showSource);
        ValidateIncludeOverridesShape(parsed, request);

        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: false,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        if (parsed.HasFlag("include-overrides"))
        {
            selection = await service.ExpandOverrideRootsAsync(selection, cancellationToken);
        }

        var rows = await service.LoadLogicalRowsAsync(selection, showSource, cancellationToken);
        var result = ProjectLogicalRows(selection, rows, showSource);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            selection.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSymbols(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunAsyncTreeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions = [.. QueryOptions, "max-nodes"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex async tree <symbol> [options]",
                [],
                allowedOptions,
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
        var presentationSettings = ParsePresentationSettings(parsed);
        var query = RequireQuery(parsed);
        var request = CreateSelectionRequest(parsed, query);
        var maxNodes = ParsePositiveInteger(parsed.GetSingle("max-nodes"), "Maximum node count", defaultValue: 500);
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        var pathResolver = CreateIndexPathResolver(parsed, selection.Profile);
        EnsureExactlyOneRoot(
            selection,
            presentationSettings.SymbolPathOptions,
            pathResolver,
            presentationSettings.PathStyle,
            "Graph",
            query);
        var result = await service.FindAsyncPathAsync(selection, maxNodes, cancellationToken);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);
        destination.WritePayload(
            writer => new GraphOutputFormatter(
                presentationSettings.SymbolPathOptions,
                pathResolver,
                presentationSettings.PathStyle,
                writer)
                .WriteAsyncPath(result, output, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunCallerTreeAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions = [.. QueryOptions, "depth", "max-nodes"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex callers tree <symbol> [options]",
                [],
                allowedOptions,
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
        var presentationSettings = ParsePresentationSettings(parsed);
        var query = RequireQuery(parsed);
        var request = CreateSelectionRequest(parsed, query);
        var depth = ParseNonNegativeInteger(parsed.GetSingle("depth"), "Depth", defaultValue: 3);
        var maxNodes = ParsePositiveInteger(parsed.GetSingle("max-nodes"), "Maximum node count", defaultValue: 500);
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        var pathResolver = CreateIndexPathResolver(parsed, selection.Profile);
        EnsureExactlyOneRoot(
            selection,
            presentationSettings.SymbolPathOptions,
            pathResolver,
            presentationSettings.PathStyle,
            "Graph",
            query);
        var result = await service.FindCallerTreeAsync(selection, depth, maxNodes, cancellationToken);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);
        destination.WritePayload(
            writer => new GraphOutputFormatter(
                presentationSettings.SymbolPathOptions,
                pathResolver,
                presentationSettings.PathStyle,
                writer)
                .WriteCallerTree(result, output, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSourceShowAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions = [.. QueryOptions, "source-layout"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex source show <symbol> [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed, sourceLayoutAllowed: true);
        var query = RequireQuery(parsed);
        var request = CreateSelectionRequest(parsed, query);
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            selection.Profile);
        EnsureExactlyOneRoot(
            selection,
            formatterSettings.SymbolPathOptions,
            formatterSettings.PathResolver,
            formatterSettings.PathStyle,
            "Source show",
            query);
        var rows = await service.LoadLogicalRowsAsync(selection, includeSourceText: true, cancellationToken);
        var result = ProjectLogicalRows(selection, rows, showSource: true);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);
        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSymbols(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSourceSearchAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions = [.. QueryOptions, "source-layout"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex source search [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed, sourceLayoutAllowed: true);
        var request = CreateSelectionRequest(parsed, selector: null);
        EnsureSelectionMinimum(request, "source search");
        var service = CreateQueryService(parsed, dependencies);
        var sourceResult = await service.SelectSourceRowsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            cancellationToken: cancellationToken);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            sourceResult.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);
        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSourceSearch(sourceResult, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunSymbolListAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions = [.. QueryOptions, "async-involved"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex symbol list [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed);
        var request = CreateSelectionRequest(parsed, selector: null);
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        if (parsed.HasFlag("async-involved"))
        {
            selection = new RootSelection(
                selection.Profile,
                selection.Roots
                    .Where(root => root.Symbol.AsyncInvolvementDepth is not null)
                    .ToArray());
        }

        var rows = await service.LoadLogicalRowsAsync(selection, includeSourceText: false, cancellationToken);
        var result = ProjectLogicalRows(selection, rows);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            selection.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteSymbolList(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunDefinitionAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] queryOptions = [.. QueryOptions, "at", "require-single", "include-overrides"];
        var parsed = CliArguments.Parse(args);
        var atMode = parsed.GetMany("at").Count > 0;
        var acceptedOptions = atMode
            ? DefinitionAtOptions
            : queryOptions;
        ValidateQueryArguments(parsed, acceptedOptions);

        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            var atHelpOption = new HelpOption(
                "--at <path:line:column>",
                "Resolve the call target at a source position");
            HelpOption[] helpOptions = atMode
                ? [
                    DatabaseHelpOption,
                    ProfileHelpOption,
                    TableJsonOutputHelpOption,
                    OutputFileHelpOption,
                    atHelpOption,
                    ShortNamesHelpOption,
                    HelpHelpOption,
                ]
                : [
                    DatabaseHelpOption,
                    ProfileHelpOption,
                    FunctionKindHelpOption,
                    AsyncStatusHelpOption,
                    TableJsonOutputHelpOption,
                    OutputFileHelpOption,
                    atHelpOption,
                    new HelpOption("--require-single", "Fail unless the search matches exactly one symbol"),
                    ShortNamesHelpOption,
                    new HelpOption(
                        "--include-overrides",
                        "Include descendant overrides and interface implementations (method queries only)"),
                    HelpHelpOption,
                ];
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex definition <query> | --at <path:line:column> [options]",
                [],
                acceptedOptions,
                helpOptions);
            return ExitCodes.Success;
        }

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var at = parsed.GetSingle("at");
        string? query = null;
        SymbolSelectionRequest? request = null;
        if (at is not null)
        {
            if (parsed.Positionals.Count > 0)
            {
                throw new CliUsageException("definition accepts either a query or --at, not both.");
            }
        }
        else
        {
            query = GetOptionalSelector(parsed, "definition");
            request = CreateSelectionRequest(parsed, query);
            ValidateIncludeOverridesShape(parsed, request);
            query ??= RequireQuery(parsed);
        }

        var service = CreateQueryService(parsed, dependencies);
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
            var selection = await service.SelectRootsAsync(
                request!,
                profileName: parsed.GetSingle("profile"),
                sourceOnly: true,
                rootGeneratedFilter: GeneratedFilter.Include,
                cancellationToken: cancellationToken);
            if (RequiresSingleFailure(parsed, selection.Roots.Count))
            {
                return ExitCodes.RequireSingleFailure;
            }

            EnsureNonEmptyRoots(selection, "definition");
            if (parsed.HasFlag("include-overrides"))
            {
                selection = await service.ExpandOverrideRootsAsync(selection, cancellationToken);
            }

            result = await service.FindDefinitionsAsync(selection, cancellationToken);
        }

        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            result.Selection.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteDefinitions(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunReferencesAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions =
            [.. QueryOptions, "exclude-generated", "only-generated", "require-single", "include-overrides"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex references <query> [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed);
        var optionalQuery = GetOptionalSelector(parsed, "references");
        var request = CreateSelectionRequest(parsed, optionalQuery);
        ValidateIncludeOverridesShape(parsed, request);
        var query = optionalQuery ?? RequireQuery(parsed);
        var generatedFilter = ParseGeneratedFilter(parsed);
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: generatedFilter,
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        EnsureNonEmptyRoots(selection, "references");
        if (parsed.HasFlag("include-overrides"))
        {
            selection = await service.ExpandOverrideRootsAsync(selection, cancellationToken);
        }

        var result = await service.FindReferencesAsync(selection, generatedFilter, cancellationToken);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            result.Selection.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteCalls(result, "reference(s)", cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunCallersAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions =
        [
            .. QueryOptions, "exclude-generated", "only-generated", "require-single", "dispatch", "caller-scope",
            "include-overrides",
        ];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex callers <query> [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed);
        var optionalQuery = GetOptionalSelector(parsed, "callers");
        var request = CreateSelectionRequest(parsed, optionalQuery);
        ValidateIncludeOverridesShape(parsed, request);
        var query = optionalQuery ?? RequireQuery(parsed);
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
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: generatedFilter,
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        EnsureNonEmptyRoots(selection, "callers");
        if (parsed.HasFlag("include-overrides"))
        {
            selection = await service.ExpandOverrideRootsAsync(selection, cancellationToken);
        }

        var result = await service.FindCallersAsync(
            selection,
            generatedFilter,
            dispatch,
            callerScope,
            cancellationToken);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            result.Selection.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);

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
        ProgramDependencies dependencies)
    {
        string[] allowedOptions =
        [
            .. QueryOptions, "exclude-generated", "only-generated", "require-single", "exclude-lambda-calls",
            "include-overrides",
        ];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex callees <query> [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed);
        var optionalQuery = GetOptionalSelector(parsed, "callees");
        var request = CreateSelectionRequest(parsed, optionalQuery);
        ValidateIncludeOverridesShape(parsed, request);
        var query = optionalQuery ?? RequireQuery(parsed);
        var generatedFilter = ParseGeneratedFilter(parsed);
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: generatedFilter,
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        EnsureNonEmptyRoots(selection, "callees");
        if (parsed.HasFlag("include-overrides"))
        {
            selection = await service.ExpandOverrideRootsAsync(selection, cancellationToken);
        }

        var result = await service.FindCalleesAsync(
            selection,
            generatedFilter,
            includeLambdaCalls: !parsed.HasFlag("exclude-lambda-calls"),
            cancellationToken: cancellationToken);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            result.Selection.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteCalls(result, "callee call(s)", cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunOverridesAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions =
            [.. AllConditions, "kind", "async-status", .. QueryPresentationPathOptions, "require-single"];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex overrides <query> [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed);
        var filter = ParseFunctionTargetFilter(parsed);
        var query = RequireQuery(parsed);
        if (filter.Kind == IndexedSymbolKind.Lambda)
        {
            throw new SymbolQueryParseException("--kind lambda is not applicable to overrides.");
        }

        var request = CreateSelectionRequest(parsed, query);
        var service = CreateQueryService(parsed, dependencies);
        var selection = await service.SelectRootsAsync(
            request,
            profileName: parsed.GetSingle("profile"),
            sourceOnly: true,
            rootGeneratedFilter: GeneratedFilter.Include,
            cancellationToken: cancellationToken);
        if (RequiresSingleFailure(parsed, selection.Roots.Count))
        {
            return ExitCodes.RequireSingleFailure;
        }

        EnsureNonEmptyRoots(selection, "overrides");
        var result = await service.FindOverridesAsync(selection, cancellationToken);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            result.Selection.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);

        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteRelations(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> RunConditionsAsync(
        string[] args,
        CancellationToken cancellationToken,
        ProgramDependencies dependencies)
    {
        string[] allowedOptions =
            ["db", "profile", "output-format", "output-file", "help", "help-verbose", "verbose", .. QueryPathOptions];
        var parsed = ParseQueryArguments(args, allowedOptions);
        if (parsed.HasFlag("help") || parsed.HasFlag("help-verbose"))
        {
            WriteCommandHelp(
                parsed.HasFlag("help-verbose") || parsed.HasFlag("verbose"),
                "csindex conditions [options]",
                [],
                allowedOptions,
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

        var parsedFormatterSettings = ParseOutputFormatterSettings(parsed);
        var service = CreateQueryService(parsed, dependencies);
        var result = await service.GetConditionsAsync(parsed.GetSingle("profile"), cancellationToken);
        var formatterSettings = MaterializeOutputFormatterSettings(
            parsed,
            parsedFormatterSettings,
            result.Profile);
        using var destination = CreateOutputDestination(parsed, dependencies.OutputDestinationFactory);
        destination.WritePayload(
            writer => CreateFormatter(formatterSettings, writer).WriteConditions(result, cancellationToken),
            cancellationToken);
        return ExitCodes.Success;
    }

    private static CliArguments ParseQueryArguments(string[] args, params string[] allowed)
    {
        var parsed = CliArguments.Parse(args);
        return ValidateQueryArguments(parsed, allowed);
    }

    private static CliArguments ValidateQueryArguments(CliArguments parsed, params string[] allowed)
    {
        parsed.EnsureOnly(allowed);
        if (parsed.HasFlag("verbose") &&
            !parsed.HasFlag("help") &&
            !parsed.HasFlag("help-verbose"))
        {
            throw new CliUsageException("Unknown option(s): --verbose");
        }

        if (!parsed.HasFlag("help") && !parsed.HasFlag("help-verbose"))
        {
            ValidateQuerySingletons(parsed);
            ValidateStagingOptions(parsed);
        }
        return parsed;
    }

    private static void ValidateQuerySingletons(CliArguments parsed)
    {
        foreach (var option in QuerySingletonOptions)
        {
            _ = parsed.GetSingle(option);
        }
    }

    private static bool IsGlobalHelpRequest(string[] args)
    {
        if (args[0] is "--help" or "--help-verbose" or "-h" or "help")
        {
            return true;
        }

        return args[0].StartsWith("--", StringComparison.Ordinal) &&
            args.Any(argument => argument is "--help" or "--help-verbose");
    }

    private static void ValidateStagingOptions(CliArguments parsed)
    {
        _ = parsed.GetSingle("base-dir");
        _ = parsed.GetSingle("symbol-path-style") switch
        {
            null or "csharp" or "explicit" => true,
            var value => throw new CliUsageException(
                $"Unknown symbol path style: {value}. Use csharp or explicit."),
        };
        _ = parsed.GetSingle("path-style") switch
        {
            null or "absolute" or "relative" => true,
            var value => throw new CliUsageException(
                $"Unknown path style: {value}. Use absolute or relative."),
        };
    }

    private static SymbolSelectionRequest CreateSelectionRequest(
        CliArguments arguments,
        string? selector)
    {
        var conditions = new List<TypedCondition>();
        foreach (var occurrence in arguments.GetOccurrences(ConditionOptions))
        {
            conditions.Add(new TypedCondition(
                GetConditionCategory(occurrence.Name),
                GetConditionSyntax(occurrence.Name),
                occurrence.Value));
        }

        var kindSpecified = arguments.GetSingle("kind") is not null;
        var asyncStatusSpecified = arguments.GetSingle("async-status") is not null;
        var request = new SymbolSelectionRequest(
            selector,
            conditions,
            new SymbolCaseOptions(
                ParseCase(arguments, "namespace-case"),
                ParseCase(arguments, "type-case"),
                ParseCase(arguments, "method-case"),
                ParseCase(arguments, "file-case"),
                ParseCase(arguments, "source-case")),
            ParseFunctionTargetFilter(arguments),
            kindSpecified,
            asyncStatusSpecified);
        _ = TypedConditionCompiler.Compile(request);
        return request;
    }

    private static ConditionCategory GetConditionCategory(string option) => option switch
    {
        "namespace" or "namespace-literal" or "namespace-regex" => ConditionCategory.Namespace,
        "type" or "type-literal" or "type-regex" => ConditionCategory.Type,
        "method" or "method-literal" or "method-regex" => ConditionCategory.Method,
        "file" or "file-literal" or "file-regex" => ConditionCategory.File,
        "include" or "include-literal" or "include-regex" => ConditionCategory.Include,
        "exclude" or "exclude-literal" or "exclude-regex" => ConditionCategory.Exclude,
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown condition option."),
    };

    private static ConditionSyntax GetConditionSyntax(string option) => option switch
    {
        _ when option.EndsWith("-literal", StringComparison.Ordinal) => ConditionSyntax.Literal,
        _ when option.EndsWith("-regex", StringComparison.Ordinal) => ConditionSyntax.Regex,
        _ => ConditionSyntax.Glob,
    };

    private static CaseMode ParseCase(CliArguments arguments, string option) =>
        arguments.GetSingle(option) switch
        {
            null or "strict" => CaseMode.Strict,
            "ignore" => CaseMode.Ignore,
            var value => throw new CliUsageException(
                $"Unknown {option}: {value}. Use strict or ignore."),
        };

    private static string? GetOptionalSelector(CliArguments arguments, string command)
    {
        if (arguments.Positionals.Count > 1)
        {
            throw new CliUsageException($"{command} accepts at most one positional pattern.");
        }

        return arguments.Positionals.Count == 1 ? arguments.Positionals[0] : null;
    }

    private static void EnsureSelectionMinimum(
        SymbolSelectionRequest request,
        string command)
    {
        if (request.Selector is null &&
            request.Conditions.Count == 0 &&
            !request.KindSpecified &&
            !request.AsyncStatusSpecified)
        {
            throw new CliUsageException(
                $"{command} requires a selector or at least one explicit selection condition.");
        }
    }

    private static void ValidateIncludeOverridesShape(
        CliArguments parsed,
        SymbolSelectionRequest request)
    {
        if (!parsed.HasFlag("include-overrides"))
        {
            return;
        }

        if (request.KindSpecified && request.FunctionFilter.Kind == IndexedSymbolKind.Lambda)
        {
            throw new CliUsageException("--kind lambda cannot be combined with --include-overrides.");
        }

        if (!IsExactOverrideSearch(request))
        {
            throw new CliUsageException(
                "--include-overrides requires an exact wildcard-free method selector.");
        }
    }

    private static void EnsureNonEmptyRoots(RootSelection selection, string command)
    {
        if (selection.Roots.Count == 0)
        {
            throw new SymbolQueryParseException(
                $"No source-backed executable matches {command} query.");
        }
    }

    private static void EnsureExactlyOneRoot(
        RootSelection selection,
        SymbolPathFormatOptions symbolPathOptions,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle,
        string queryKind,
        string query)
    {
        if (selection.Roots.Count == 0)
        {
            throw new SymbolQueryParseException(
                $"No source-backed executable matches {queryKind.ToLowerInvariant()} query: {query}");
        }

        if (selection.Roots.Count != 1)
        {
            throw new SymbolQueryParseException(
                $"{queryKind} query is ambiguous for '{query}'. Candidates: " +
                string.Join(", ", selection.Roots.Select(root => FormatGraphCandidate(
                    root,
                    symbolPathOptions,
                    pathResolver,
                    pathStyle))));
        }
    }

    private static string FormatGraphCandidate(
        ResolvedLogicalRoot root,
        SymbolPathFormatOptions symbolPathOptions,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle)
    {
        var displayName = root.Symbol.Path is null
            ? root.Symbol.Name
            : new SymbolPathFormatter().Format(root.Symbol.Path, symbolPathOptions);
        var storedPath = root.Symbol.PreferredDocumentPath ?? root.Symbol.DocumentPath;
        var sourceStart = root.Symbol.PreferredSourceStart ?? root.Symbol.SourceStart;
        return storedPath is not null && sourceStart is not null
            ? $"{displayName} @ {pathResolver.ToDisplayPath(storedPath, pathStyle)}:{sourceStart.Value}"
            : $"{displayName} @ assembly:{root.Symbol.AssemblyName ?? "unknown"}";
    }

    private static QueryContext ProjectLogicalRows(
        RootSelection selection,
        IReadOnlyList<LogicalSymbolResultRow> rows,
        bool showSource = false) =>
        new(
            selection.Profile,
            rows.Select(row => row.Symbol).ToArray(),
            showSource);

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
        !request.Selector.Contains('*');

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

    private static SemanticQueryService CreateQueryService(
        CliArguments parsed,
        ProgramDependencies dependencies) =>
        dependencies.QueryServiceFactory(
            GetDatabasePath(parsed),
            parsed.GetSingle("base-dir"));

    private static string GetDatabasePath(CliArguments parsed) =>
        parsed.GetSingle("db") ?? Path.Combine(Environment.CurrentDirectory, ".csindex", "index.sqlite");

    private static OutputDestination CreateOutputDestination(
        CliArguments parsed,
        Func<string?, string, OutputDestination> outputDestinationFactory) =>
        outputDestinationFactory(parsed.GetSingle("output-file"), GetDatabasePath(parsed));

    private static ParsedOutputFormatterSettings ParseOutputFormatterSettings(
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

        var presentation = ParsePresentationSettings(parsed);
        return new ParsedOutputFormatterSettings(
            outputFormat,
            presentation.SymbolPathOptions,
            sourceLayout,
            presentation.PathStyle);
    }

    private static OutputFormatterSettings MaterializeOutputFormatterSettings(
        CliArguments parsed,
        ParsedOutputFormatterSettings parsedSettings,
        StoredProfile profile) =>
        new(
            parsedSettings.Format,
            parsedSettings.SymbolPathOptions,
            parsedSettings.SourceLayout,
            CreateIndexPathResolver(parsed, profile),
            parsedSettings.PathStyle);

    private static OutputFormatter CreateFormatter(OutputFormatterSettings settings, TextWriter writer) =>
        new(
            settings.Format,
            settings.SymbolPathOptions,
            settings.PathResolver,
            settings.PathStyle,
            settings.SourceLayout,
            writer,
            Console.Error);

    private static ParsedPresentationSettings ParsePresentationSettings(CliArguments parsed) =>
        new(ParseSymbolPathFormatOptions(parsed), ParsePathDisplayStyle(parsed));

    private static IndexPathResolver CreateIndexPathResolver(CliArguments parsed, StoredProfile profile) =>
        IndexPathResolver.CreateForQuery(
            GetDatabasePath(parsed),
            profile.IndexRootAnchor,
            parsed.GetSingle("base-dir"));

    private static SymbolPathFormatOptions ParseSymbolPathFormatOptions(CliArguments parsed)
    {
        var style = parsed.GetSingle("symbol-path-style") switch
        {
            null or "csharp" => SymbolPathStyle.CSharp,
            "explicit" => SymbolPathStyle.Explicit,
            var value => throw new CliUsageException(
                $"Unknown symbol path style: {value}. Use csharp or explicit."),
        };
        return new SymbolPathFormatOptions(style, parsed.HasFlag("short-names"));
    }

    private static PathDisplayStyle ParsePathDisplayStyle(CliArguments parsed) =>
        parsed.GetSingle("path-style") switch
        {
            null or "absolute" => PathDisplayStyle.Absolute,
            "relative" => PathDisplayStyle.Relative,
            var value => throw new CliUsageException(
                $"Unknown path style: {value}. Use absolute or relative."),
        };

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

    private sealed record HelpSection(string Heading, ImmutableArray<string> Entries);

    // This is deliberately one shared, immutable reference.  Every terminal
    // help scope appends these sections so the three verbose spellings cannot
    // drift apart between commands.
    private static readonly ImmutableArray<HelpSection> VerboseReference = ImmutableArray.Create(
        new HelpSection(
            "Canonical symbol path examples",
            ImmutableArray.Create(
                "Game.Core.Player.Inventory::Load(int).Validate()",
                "Game.Core::Player.Inventory::Load(int).Validate()",
                "global::Program::<top-level-statements>",
                "Player.Inventory::Load(int).Validate()",
                "Outer.Local",
                "Outer(int).Local",
                "Outer.Local(string)",
                "Method",
                "Method<T>",
                "Method()",
                "Method<T>(T)",
                "Namespace1.Namespace1_2::*::**.Method2.<lambda#1>",
                "**::Class1::Method2.<lambda#1>",
                "Game::Player::[constructor](int,string)",
                "Game::Player::[static-constructor]()",
                "Game::Player::[destructor]()",
                "Math::Number::[operator:+](Math.Number,Math.Number)",
                "Math::Number::[checked-operator:+](Math.Number,Math.Number)",
                "Game::Value::[conversion:implicit:int](Game.Value)",
                "Game::Value::[conversion:explicit:string](Game.Value)",
                "Game::Value::[checked-conversion:explicit:int](Game.Value)",
                "Game::Player::[get:Name]()",
                "Game::Player::[set:Name](string)",
                "Game::Player::[init:Name](string)",
                "Game::Player::[get:Item](int)",
                "Game::Player::[set:Item](int,string)",
                "Game::Player::[add:Changed](System.EventHandler)",
                "Game::Player::[remove:Changed](System.EventHandler)",
                "Game::Player::[explicit:System.IDisposable.Dispose]()",
                "Game::Player::[explicit:Game.Contracts.IMapper.Map]<T>(T)",
                "Game::Player::<initializer:Score>",
                "Game::Player::Run().<lambda#1>",
                "Game::Player::Run().<anonymous-method#2>",
                "Game.Player.Run()",
                "Game::Player::Run().Local()",
                "Game::Player::Run(Guid)",
                "Game::Player::Method<System.String>")),
        new HelpSection(
            "Path validation examples (invalid forms and reasons)",
            ImmutableArray.Create(
                "Game::Player::Run()::<lambda#1>    invalid: old child :: separator",
                "Game.Player.Run()    invalid: no top-level :: separator",
                "Game::Player::Run()::Local()    invalid: three top-level fields",
                "Game::Player::Run(Guid)    invalid: non-alias type must be fully qualified",
                "Game::Player::Method<System.String>    invalid: constructed generic notation")),
        new HelpSection(
            "Command option scopes",
            ImmutableArray.Create(
                "Typed condition forms: each scope that lists namespace/type/method/file/include/exclude accepts concise glob, -literal, and -regex forms.",
                "Typed case options are --namespace-case strict|ignore, --type-case strict|ignore, --method-case strict|ignore, --file-case strict|ignore, and --source-case strict|ignore.",
                "Common query scope: symbol-emitting queries accept --db, --profile, documented --output-format, --output-file, --symbol-path-style, --short-names, --base-dir, --path-style, --help, --help-verbose, and --verbose unless a row explicitly restricts them; conditions excludes symbol presentation/short names, and definition --at accepts presentation/path but no root filters.",
                "Global scope accepts --db, --profile, --output-format, --output-file, --help, --help-verbose, and --verbose.",
                "Index scope accepts --db, --mode, --solution, --configuration, --framework, --target-framework, --runtime, --profile-name, --define, --undefine, --define-file, --reference, --unity-editor, --exclude, --generated-source, --rebuild, --verbose, --diagnostics, --help, and --help-verbose only.",
                "Index scope has no query presentation or path options.",
                "Symbol find scope: optional selector; --namespace, --namespace-literal, --namespace-regex, --type, --type-literal, --type-regex, --method, --method-literal, --method-regex, --file, --file-literal, --file-regex, --include, --include-literal, --include-regex, --exclude, --exclude-literal, --exclude-regex.",
                "Symbol find scope also accepts --namespace-case, --type-case, --method-case, --file-case, --source-case, --kind, --async-status, --require-single, --include-overrides, --show-source, and --source-layout.",
                "Symbol list scope: no selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Symbol list scope also accepts --async-involved.",
                "Source search scope: no selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Source search scope also accepts --source-layout.",
                "Source show scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Source show scope also accepts --source-layout.",
                "Definition scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Definition scope also accepts --require-single and --include-overrides.",
                "References scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "References scope also accepts --exclude-generated, --only-generated, --require-single, and --include-overrides.",
                "Callers scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Callers scope also accepts --exclude-generated, --only-generated, --require-single, --include-overrides, --dispatch, and --caller-scope.",
                "Callees scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Callees scope also accepts --exclude-generated, --only-generated, --require-single, --include-overrides, and --exclude-lambda-calls.",
                "Overrides scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Overrides scope does not accept --include-overrides.",
                "Async tree scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Async tree scope also accepts --max-nodes and graph output options.",
                "Callers tree scope: required selector; all typed namespace/type/method/file/include/exclude conditions and their case options, --kind, and --async-status.",
                "Callers tree scope also accepts --depth, --max-nodes, and graph output options.",
                "Definition --at scope: no selector or root conditions; presentation/path options only.",
                "Conditions scope: no selector or root conditions; --base-dir and --path-style only among query path options.")),
        new HelpSection(
            "Matching and condition semantics",
            ImmutableArray.Create(
                "csharp paths are namespace/type suffix searches and may return extra namespaces",
                "explicit syntax fixes the boundary",
                "global is the exact global namespace",
                "@global is a literal identifier",
                "* matches one hierarchy segment",
                "** matches zero or more",
                "omitted parameters and generic lists broaden independently",
                "() means exactly non-generic zero parameters",
                "concise conditions are glob",
                "literal and regex are explicit",
                "case categories are independent and default to strict",
                "same-category conditions OR",
                "categories AND",
                "includes AND",
                "excludes OR")),
        new HelpSection(
            "Selection, cardinality, and traversal",
            ImmutableArray.Create(
                "root filters run before cardinality",
                "never filter traversal descendants",
                "kind all is Method/Lambda/Initializer/TopLevelStatements",
                "direct AsyncRole, not child leakage",
                "partial definition/implementation is one logical root with role-specific declaration rows",
                "no artificial path suffix")),
        new HelpSection(
            "Database paths and presentation",
            ImmutableArray.Create(
                "DB paths are root-relative",
                "base-dir changes query reconstruction only",
                "path style defaults absolute",
                "relative stored paths",
                "symbol path style is presentation-only",
                "short names remove owner namespace only")));

    private static void WriteVerboseReference()
    {
        foreach (var section in VerboseReference)
        {
            Console.WriteLine();
            Console.WriteLine(section.Heading + ":");
            foreach (var entry in section.Entries)
            {
                Console.WriteLine($"  {entry}");
            }
        }
    }

    private static void WriteHelp(bool verbose = false)
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
              csindex source search [options]
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
              --base-dir <path>           Base directory for stored relative paths
              --path-style absolute|relative
                                          Display path style (default: absolute)
              --symbol-path-style csharp|explicit
                                          Symbol path notation (presentation only)
              --kind all|method|lambda    Limit function targets by kind (default: all)
              --async-status all|async|sync
                                          Limit function targets by direct async status (default: all)
              --exclude-generated         Exclude generated documents
              --only-generated            Include only generated documents
              --require-single            Fail unless the query matches one symbol
              --short-names               Shorten namespaces in displayed symbol names
              --include-overrides         Include descendant overrides and interface implementations (method queries only)

            Selection conditions (repeatable):
              --namespace <glob> | --namespace-literal <text> | --namespace-regex <pattern>
              --type <glob> | --type-literal <text> | --type-regex <pattern>
              --method <glob> | --method-literal <text> | --method-regex <pattern>
              --file <glob> | --file-literal <text> | --file-regex <pattern>
              --include <glob> | --include-literal <text> | --include-regex <pattern>
              --exclude <glob> | --exclude-literal <text> | --exclude-regex <pattern>
              --namespace-case strict|ignore
              --type-case strict|ignore
              --method-case strict|ignore
              --file-case strict|ignore
              --source-case strict|ignore
                                          Concise conditions are glob; literal and regex are explicit.

            Symbol list options:
              --async-involved             Include only symbols with async involvement

            Symbol find options:
              [<pattern>]                  Positional selector, or any explicit typed/kind/async selection condition
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
              source search accepts any explicit selection condition (including --include, --exclude, typed conditions, --kind, or --async-status)

            Callees options:
              --exclude-lambda-calls       Exclude calls made by nested lambdas

            Help:
              --help                      Show concise help
              --help-verbose              Show the full grammar reference (same as --help --verbose)
              --verbose                   With --help, include the full reference; index uses runtime progress

            Run 'csindex index --help' for indexing options.
            """);

        WriteAcceptedOptions(GlobalOptions);

        if (verbose)
        {
            WriteVerboseReference();
        }
    }

    private static void WriteCommandHelp(
        bool verbose,
        string usage,
        IReadOnlyList<string> notes,
        IReadOnlyList<string> acceptedOptions,
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

        foreach (var option in new[] { HelpVerboseHelpOption, VerboseHelpOption })
        {
            var padding = new string(' ', Math.Max(1, 28 - option.Syntax.Length));
            Console.WriteLine($"  {option.Syntax}{padding}{option.Description}");
        }

        WriteAcceptedOptions(acceptedOptions);

        if (verbose)
        {
            WriteVerboseReference();
        }
    }

    private readonly record struct ParsedPresentationSettings(
        SymbolPathFormatOptions SymbolPathOptions,
        PathDisplayStyle PathStyle);

    private readonly record struct ParsedOutputFormatterSettings(
        string Format,
        SymbolPathFormatOptions SymbolPathOptions,
        SourceLayout SourceLayout,
        PathDisplayStyle PathStyle);

    private readonly record struct OutputFormatterSettings(
        string Format,
        SymbolPathFormatOptions SymbolPathOptions,
        SourceLayout SourceLayout,
        IndexPathResolver PathResolver,
        PathDisplayStyle PathStyle);

    private sealed record HelpOption(string Syntax, string Description);

    private static void WriteAcceptedOptions(IEnumerable<string> options)
    {
        Console.WriteLine();
        Console.WriteLine("Accepted options:");
        foreach (var option in options.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            Console.WriteLine($"  --{option}");
        }
    }

    private static void WriteIndexHelp(bool verbose)
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
              --verbose                    Show runtime indexing progress
              --diagnostics
              --help                      Show concise help
              --help-verbose              Show the full grammar reference (same as --help --verbose)
            """);

        WriteAcceptedOptions(IndexOptions);

        if (verbose)
        {
            WriteVerboseReference();
        }
    }
}
