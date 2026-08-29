using System.Text.Json;
using CsIndex.Core.Input;
using CsIndex.Core.Model;
using CsIndex.Core.Symbols;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal sealed class OutputFormatter
{
    private static readonly SymbolPathFormatOptions FullyQualifiedNameOptions =
        new(SymbolPathStyle.CSharp, ShortNames: false);

    private readonly TextWriter _diagnosticsWriter;
    private readonly string _format;
    private readonly IndexPathResolver _pathResolver;
    private readonly PathDisplayStyle _pathStyle;
    private readonly SourceLayout _sourceLayout;
    private readonly SymbolPathFormatOptions _symbolPathOptions;
    private readonly TextWriter _writer;

    public OutputFormatter(
        string format,
        SymbolPathFormatOptions symbolPathOptions,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle)
        : this(
            format,
            symbolPathOptions,
            pathResolver,
            pathStyle,
            SourceLayout.SingleLine,
            Console.Out,
            Console.Error)
    {
    }

    public OutputFormatter(
        string format,
        SymbolPathFormatOptions symbolPathOptions,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle,
        SourceLayout sourceLayout,
        TextWriter writer,
        TextWriter diagnosticsWriter)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(diagnosticsWriter);
        ArgumentNullException.ThrowIfNull(pathResolver);

        _format = format switch
        {
            "table" or "json" => format,
            _ => throw new CliUsageException(
                $"Output format '{format}' is reserved but not implemented. Use table or json."),
        };
        _symbolPathOptions = symbolPathOptions;
        _pathResolver = pathResolver;
        _pathStyle = pathStyle;
        _sourceLayout = sourceLayout;
        _writer = writer;
        _diagnosticsWriter = diagnosticsWriter;
    }

    public void WriteSymbols(QueryContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_format == "json")
        {
            WriteJsonPayload(new
            {
                profile = context.Profile.Name,
                matched = SelectWithCancellation(
                    context.MatchedSymbols,
                    symbol => ToSymbolObject(
                        symbol,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        context.ShowSource),
                    cancellationToken),
            });
            return;
        }

        if (_sourceLayout == SourceLayout.MultiLine)
        {
            WriteSymbolsMultiLine(context, cancellationToken);
            return;
        }

        foreach (var symbol in context.MatchedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = FormatTableSignature(symbol);
            var location = FormatDefinitionLocationField(symbol);
            if (context.ShowSource)
            {
                var source = TableTextSanitizer.Sanitize(symbol.NormalizedSource);
                _writer.WriteLine($"{signature}\t{location}\t{source}");
            }
            else
            {
                _writer.WriteLine($"{signature}\t{location}");
            }
        }

        _diagnosticsWriter.WriteLine($"Query matched {context.MatchedSymbols.Count} symbol(s):");
    }

    public void WriteSourceSearch(
        SourceSearchResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (_format == "json")
        {
            WriteJsonPayload(new
            {
                profile = result.Profile.Name,
                matched = SelectWithCancellation(
                    result.Matches,
                    row => ToDeclarationObject(
                        row,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        includeSource: true),
                    cancellationToken),
            });
            return;
        }

        if (_sourceLayout == SourceLayout.MultiLine)
        {
            WriteSourceSearchMultiLine(result, cancellationToken);
            return;
        }

        foreach (var row in result.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = ApplyDeclaration(row.Symbol, row.Declaration);
            _writer.WriteLine(
                $"{FormatTableSignature(symbol)}\t{FormatDeclarationRole(row.Declaration.Role)}\t" +
                $"{FormatDefinitionLocationField(symbol)}\t{TableTextSanitizer.Sanitize(symbol.NormalizedSource)}");
        }

        _diagnosticsWriter.WriteLine($"Query matched {result.Matches.Count} symbol(s):");
    }

    public void WriteDefinitions(DefinitionResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_format == "json")
        {
            WriteJsonPayload(new
            {
                profile = result.Selection.Profile.Name,
                matched = SelectWithCancellation(
                    result.Selection.Roots.Select(root => root.Symbol),
                    symbol => ToSymbolObject(
                        symbol,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        includeSource: false),
                    cancellationToken),
                definitions = SelectWithCancellation(
                    result.Definitions,
                    row => ToDeclarationObject(
                        row,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        includeSource: false),
                    cancellationToken),
            });
            return;
        }

        _writer.WriteLine($"{result.Definitions.Count} definition(s):");
        foreach (var row in result.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = ApplyDeclaration(row.Symbol, row.Declaration);
            _writer.WriteLine(
                $"  {SymbolSignatureFormatter.Format(definition, _symbolPathOptions)}\t" +
                $"{FormatDeclarationRole(row.Declaration.Role)}\t{FormatDefinitionLocationField(definition)}");
            if (definition.DocumentPath is null)
            {
                _writer.WriteLine($"    assembly: {definition.AssemblyName ?? "unknown"}; no source definition");
            }
        }
    }

    public void WriteCalls(CallResult result, string heading, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_format == "json")
        {
            WriteJsonPayload(new
            {
                profile = result.Selection.Profile.Name,
                matched = SelectWithCancellation(
                    result.Selection.Roots.Select(root => root.Symbol),
                    symbol => ToSymbolObject(
                        symbol,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        includeSource: false),
                    cancellationToken),
                calls = SelectWithCancellation(
                    result.Calls,
                    call => new
                    {
                        call.Id,
                        caller = FormatCallEndpoint(result, call.CallerSymbolId),
                        callee = FormatCallTarget(result, call),
                        referenceKind = call.ReferenceKind.ToString(),
                        dispatchKind = call.DispatchKind.ToString(),
                        resolutionStatus = call.ResolutionStatus.ToString(),
                        resolutionReason = call.ResolutionReason.ToString(),
                        asyncUsageKind = call.AsyncUsageKind.ToString(),
                        location = ToLocationObject(
                            call.DocumentPath,
                            call.SourceStart,
                            _pathResolver,
                            _pathStyle),
                        call.IsGenerated,
                        unresolvedName = call.CalleeSymbolId is null && call.CalleeDefinitionId is null
                            ? call.UnresolvedName
                            : null,
                        call.ReceiverTypeKey,
                    },
                    cancellationToken),
                callers = SelectWithCancellation(
                    result.EffectiveCallers,
                    symbol => ToSymbolObject(
                        symbol,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        includeSource: false),
                    cancellationToken),
                possibleRuntimeTargets = SelectWithCancellation(
                    result.PossibleRuntimeTargets,
                    relation => new
                    {
                        source = FormatRelationEndpoint(result, relation.SourceSymbolId),
                        target = FormatRelationEndpoint(result, relation.TargetSymbolId),
                        kind = relation.Kind.ToString(),
                    },
                    cancellationToken),
            });
            return;
        }

        _writer.WriteLine($"{result.Selection.Roots.Count} matched symbol(s); {result.Calls.Count} {heading}:");
        foreach (var call in result.Calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = ResolveLocation(call.DocumentPath, call.SourceStart, _pathResolver, _pathStyle);
            var target = FormatCallTarget(result, call);
            _writer.WriteLine(
                $"  {point.Path}:{point.Line}:{point.Column}  {FormatCallEndpoint(result, call.CallerSymbolId)} -> {target} " +
                $"[{call.ReferenceKind}, {call.ResolutionStatus}] [{call.AsyncUsageKind}]");
        }

        if (result.EffectiveCallers.Count > 0)
        {
            _writer.WriteLine("Callers:");
            foreach (var caller in result.EffectiveCallers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _writer.WriteLine($"  {FormatSymbolName(caller)}");
            }
        }

        if (result.PossibleRuntimeTargets.Count > 0)
        {
            _writer.WriteLine("Possible runtime targets:");
            foreach (var relation in result.PossibleRuntimeTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _writer.WriteLine($"  {FormatRelationEndpoint(result, relation.SourceSymbolId)} [{relation.Kind}]");
            }
        }
    }

    public void WriteRelations(RelationResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_format == "json")
        {
            WriteJsonPayload(new
            {
                profile = result.Selection.Profile.Name,
                matched = SelectWithCancellation(
                    result.Selection.Roots.Select(root => root.Symbol),
                    symbol => ToSymbolObject(
                        symbol,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        includeSource: false),
                    cancellationToken),
                relations = SelectWithCancellation(
                    result.Relations,
                    relation => new
                    {
                        source = FormatRelationEndpoint(result, relation.SourceSymbolId),
                        target = FormatRelationEndpoint(result, relation.TargetSymbolId),
                        kind = relation.Kind.ToString(),
                    },
                    cancellationToken),
            });
            return;
        }

        _writer.WriteLine($"{result.Relations.Count} override(s):");
        foreach (var relation in result.Relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.WriteLine(
                $"  {FormatRelationEndpoint(result, relation.SourceSymbolId)} -> " +
                $"{FormatRelationEndpoint(result, relation.TargetSymbolId)}");
        }
    }

    public void WriteConditions(ConditionsResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_format == "json")
        {
            WriteJsonPayload(new
            {
                profile = result.Profile.Name,
                activeSymbols = result.Profile.PreprocessorSymbols,
                conditionalSymbols = SelectWithCancellation(
                    result.Symbols,
                    symbol => new
                    {
                        symbol.SymbolName,
                        symbol.FileCount,
                        symbol.OccurrenceCount,
                        symbol.IsDefined,
                    },
                    cancellationToken),
            });
            return;
        }

        _writer.WriteLine($"Profile: {result.Profile.Name}");
        _writer.WriteLine($"Active symbols: {string.Join(", ", result.Profile.PreprocessorSymbols)}");
        _writer.WriteLine("Conditional symbols found:");
        foreach (var symbol in result.Symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.WriteLine(
                $"  {symbol.SymbolName,-30} {symbol.FileCount,6} file(s)  " +
                (symbol.IsDefined ? "defined" : "undefined"));
        }
    }

    internal static Dictionary<string, object?> ToSymbolObject(
        StoredSymbol symbol,
        SymbolPathFormatOptions symbolPathOptions,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle,
        bool includeSource)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(pathResolver);

        var value = new Dictionary<string, object?>
        {
            ["id"] = symbol.Id,
            ["stableKey"] = symbol.StableKey,
            ["kind"] = symbol.Kind.ToString().ToLowerInvariant(),
            ["displayName"] = SymbolSignatureFormatter.FormatDisplayName(symbol, symbolPathOptions),
            ["signature"] = SymbolSignatureFormatter.Format(symbol, symbolPathOptions),
            ["fullyQualifiedName"] = SymbolSignatureFormatter.FormatDisplayName(symbol, FullyQualifiedNameOptions),
            ["namespaceName"] = symbol.NamespaceName,
            ["typeSimpleName"] = symbol.TypeSimpleName,
            ["parameters"] = symbol.Parameters
                .Select(parameter => string.IsNullOrEmpty(parameter.TypeDisplay)
                    ? parameter.TypeKey
                    : parameter.TypeDisplay)
                .ToArray(),
            ["location"] = symbol.DocumentPath is null || symbol.SourceStart is null
                ? null
                : ToLocationObject(
                    symbol.DocumentPath,
                    symbol.SourceStart.Value,
                    pathResolver,
                    pathStyle),
            ["isGenerated"] = symbol.IsGenerated,
            ["assemblyName"] = symbol.AssemblyName,
            ["accessibility"] = SymbolSignatureFormatter.FormatAccessibility(symbol.Accessibility),
            ["isStatic"] = symbol.IsStatic,
            ["isAsync"] = symbol.AsyncRole != AsyncRole.None,
            ["asyncRole"] = symbol.AsyncRole.ToString(),
            ["isAsyncInvolved"] = symbol.AsyncInvolvementDepth is not null,
            ["asyncInvolvementDepth"] = symbol.AsyncInvolvementDepth,
            ["returnType"] = symbol.ReturnTypeDisplay ?? symbol.ReturnTypeKey,
            ["methodKind"] = symbol.MethodKind,
            ["sourceAvailable"] = symbol.PreferredDeclarationId is not null,
        };
        if (includeSource)
        {
            value["normalizedSource"] = symbol.NormalizedSource;
        }

        return value;
    }

    private static IReadOnlyDictionary<string, object?> ToDeclarationObject(
        DeclarationResultRow row,
        SymbolPathFormatOptions symbolPathOptions,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle,
        bool includeSource)
    {
        var value = new Dictionary<string, object?>(ToSymbolObject(
            ApplyDeclaration(row.Symbol, row.Declaration),
            symbolPathOptions,
            pathResolver,
            pathStyle,
            includeSource))
        {
            ["declarationRole"] = FormatDeclarationRole(row.Declaration.Role),
        };
        return value;
    }

    private static object ToLocationObject(
        string path,
        int offset,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle)
    {
        var point = ResolveLocation(path, offset, pathResolver, pathStyle);
        return new { path = point.Path, line = point.Line, column = point.Column, offset = point.Offset };
    }

    private string FormatDefinitionLocation(StoredSymbol symbol)
    {
        if (symbol.DocumentPath is null || symbol.SourceStart is null)
        {
            return string.Empty;
        }

        var point = ResolveLocation(symbol.DocumentPath, symbol.SourceStart.Value, _pathResolver, _pathStyle);
        return $"  {point.Path}:{point.Line}:{point.Column}";
    }

    private static StoredSymbol ApplyDeclaration(StoredSymbol symbol, StoredDeclaration declaration) =>
        symbol with
        {
            PreferredDeclaration = declaration,
            PreferredDocumentPath = declaration.DocumentPath,
            PreferredSourceStart = declaration.SourceStart,
            PreferredIsGenerated = declaration.IsGenerated,
            DocumentPath = declaration.DocumentPath,
            SourceStart = declaration.SourceStart,
            IsGenerated = declaration.IsGenerated,
        };

    private static string FormatDeclarationRole(DeclarationRole role) => role switch
    {
        DeclarationRole.Ordinary => "ordinary",
        DeclarationRole.PartialDefinition => "partial-definition",
        DeclarationRole.PartialImplementation => "partial-implementation",
        _ => throw new InvalidOperationException($"Unknown declaration role: {role}."),
    };

    private string FormatDefinitionLocationField(StoredSymbol symbol)
    {
        if (symbol.DocumentPath is null || symbol.SourceStart is null)
        {
            return string.Empty;
        }

        var point = ResolveLocation(symbol.DocumentPath, symbol.SourceStart.Value, _pathResolver, _pathStyle);
        return TableTextSanitizer.Sanitize($"{point.Path}:{point.Line}:{point.Column}");
    }

    private static string FormatAsyncAnalysis(StoredSymbol symbol)
    {
        if (symbol.AsyncRole == AsyncRole.None && symbol.AsyncInvolvementDepth is null)
        {
            return string.Empty;
        }

        return $" [async: {symbol.AsyncRole}; depth: {symbol.AsyncInvolvementDepth?.ToString() ?? "null"}]";
    }

    private static SourcePoint ResolveLocation(
        string storedPath,
        int offset,
        IndexPathResolver pathResolver,
        PathDisplayStyle pathStyle)
    {
        var displayPath = pathResolver.ToDisplayPath(storedPath, pathStyle);
        var absolutePath = pathResolver.ToAbsolutePath(storedPath);
        try
        {
            var resolved = SourcePositionResolver.ResolveOffset(absolutePath, offset);
            return new SourcePoint(displayPath, resolved.Line, resolved.Column, offset);
        }
        catch (IOException)
        {
            throw new InputResolutionException(
                $"Source file '{displayPath}' could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InputResolutionException(
                $"Source file '{displayPath}' could not be read.");
        }
    }

    public void WriteSymbolList(QueryContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_format == "json")
        {
            WriteJsonPayload(new
            {
                profile = context.Profile.Name,
                symbols = SelectWithCancellation(
                    context.MatchedSymbols,
                    symbol => ToSymbolObject(
                        symbol,
                        _symbolPathOptions,
                        _pathResolver,
                        _pathStyle,
                        context.ShowSource),
                    cancellationToken),
            });
            return;
        }

        _diagnosticsWriter.WriteLine($"{context.MatchedSymbols.Count} symbol(s):");
        foreach (var symbol in context.MatchedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.WriteLine(FormatTableSignature(symbol));
        }
    }

    private string FormatTableSignature(StoredSymbol symbol) => TableTextSanitizer.Sanitize(
        $"{SymbolSignatureFormatter.Format(symbol, _symbolPathOptions)}{FormatAsyncAnalysis(symbol)}");

    private void WriteSymbolsMultiLine(QueryContext context, CancellationToken cancellationToken)
    {
        _writer.WriteLine($"Query matched {context.MatchedSymbols.Count} symbol(s):");
        foreach (var symbol in context.MatchedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.WriteLine(TableTextSanitizer.Sanitize(
                $"  {SymbolSignatureFormatter.Format(symbol, _symbolPathOptions)}{FormatDefinitionLocation(symbol)}{FormatAsyncAnalysis(symbol)}"));
            if (context.ShowSource)
            {
                _writer.WriteLine($"    source: {TableTextSanitizer.Sanitize(symbol.NormalizedSource)}");
            }
        }
    }

    private void WriteSourceSearchMultiLine(
        SourceSearchResult result,
        CancellationToken cancellationToken)
    {
        _writer.WriteLine($"Query matched {result.Matches.Count} symbol(s):");
        foreach (var row in result.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = ApplyDeclaration(row.Symbol, row.Declaration);
            _writer.WriteLine(TableTextSanitizer.Sanitize(
                $"  {SymbolSignatureFormatter.Format(symbol, _symbolPathOptions)}" +
                $" [declaration-role: {FormatDeclarationRole(row.Declaration.Role)}]" +
                $"{FormatDefinitionLocation(symbol)}{FormatAsyncAnalysis(symbol)}"));
            _writer.WriteLine($"    source: {TableTextSanitizer.Sanitize(symbol.NormalizedSource)}");
        }
    }

    private static IReadOnlyList<TResult> SelectWithCancellation<TSource, TResult>(
        IEnumerable<TSource> source,
        Func<TSource, TResult> selector,
        CancellationToken cancellationToken)
    {
        var results = new List<TResult>();
        foreach (var value in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(selector(value));
        }

        return results;
    }

    private string FormatSymbolName(StoredSymbol symbol) =>
        SymbolSignatureFormatter.FormatDisplayName(symbol, _symbolPathOptions);

    private string FormatCallEndpoint(CallResult result, long symbolId) =>
        FormatHydratedEndpoint(result.SymbolsById, symbolId, "call");

    private string FormatCallTarget(CallResult result, StoredCall call)
    {
        var callee = call.CalleeSymbolId is long calleeId
            ? FormatCallEndpoint(result, calleeId)
            : null;
        var definition = call.CalleeDefinitionId is long definitionId
            ? FormatCallEndpoint(result, definitionId)
            : null;
        if (definition is not null)
        {
            return definition;
        }

        if (callee is not null)
        {
            return callee;
        }

        if (!string.IsNullOrWhiteSpace(call.UnresolvedName))
        {
            return call.UnresolvedName!;
        }

        throw new InvalidOperationException(
            $"Call ID {call.Id} has no resolved callee endpoint or unresolved name.");
    }

    private string FormatRelationEndpoint(RelationResult result, long symbolId) =>
        FormatHydratedEndpoint(result.SymbolsById, symbolId, "relation");

    private string FormatRelationEndpoint(CallResult result, long symbolId) =>
        FormatHydratedEndpoint(result.SymbolsById, symbolId, "relation");

    private string FormatHydratedEndpoint(
        IReadOnlyDictionary<long, StoredSymbol> symbolsById,
        long symbolId,
        string endpointKind)
    {
        if (!symbolsById.TryGetValue(symbolId, out var symbol))
        {
            throw new InvalidOperationException(
                $"{endpointKind} endpoint symbol ID {symbolId} is missing from the hydration batch.");
        }

        return FormatSymbolName(symbol);
    }

    private void WriteJsonPayload(object value) => WriteJson(value, _writer);

    internal static void WriteJson(object value, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }
}
