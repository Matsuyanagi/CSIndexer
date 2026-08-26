using System.Text.Json;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal sealed class OutputFormatter
{
    private readonly TextWriter _diagnosticsWriter;
    private readonly string _format;
    private readonly bool _shortNames;
    private readonly SourceLayout _sourceLayout;
    private readonly TextWriter _writer;

    public OutputFormatter(string format, bool shortNames = false)
        : this(format, shortNames, SourceLayout.SingleLine, Console.Out, Console.Error)
    {
    }

    public OutputFormatter(
        string format,
        bool shortNames,
        SourceLayout sourceLayout,
        TextWriter writer,
        TextWriter diagnosticsWriter)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(diagnosticsWriter);

        _format = format switch
        {
            "table" or "json" => format,
            _ => throw new CliUsageException(
                $"Output format '{format}' is reserved but not implemented. Use table or json."),
        };
        _shortNames = shortNames;
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
                    symbol => ToSymbolObject(symbol, _shortNames, context.ShowSource),
                    cancellationToken),
            });
            return;
        }

        if (_sourceLayout == SourceLayout.MultiLine)
        {
            WriteSymbolsMultiLine(context, cancellationToken);
            return;
        }

        _diagnosticsWriter.WriteLine($"Query matched {context.MatchedSymbols.Count} symbol(s):");
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
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
                    cancellationToken),
                definitions = SelectWithCancellation(
                    result.Definitions,
                    row => ToSymbolObject(row.Symbol, _shortNames, includeSource: false),
                    cancellationToken),
            });
            return;
        }

        _writer.WriteLine($"{result.Definitions.Count} definition(s):");
        foreach (var row in result.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = ApplyDeclaration(row.Symbol, row.Declaration);
            _writer.WriteLine($"  {SymbolSignatureFormatter.Format(definition, _shortNames)}{FormatDefinitionLocation(definition)}");
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
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
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
                        location = ToLocationObject(call.DocumentPath, call.SourceStart),
                        call.IsGenerated,
                        call.UnresolvedName,
                        call.ReceiverTypeKey,
                    },
                    cancellationToken),
                callers = SelectWithCancellation(
                    result.EffectiveCallers,
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
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
            var point = SafeResolve(call.DocumentPath, call.SourceStart);
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
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
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

    internal static IReadOnlyDictionary<string, object?> ToSymbolObject(
        StoredSymbol symbol,
        bool shortNames,
        bool includeSource)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var value = new Dictionary<string, object?>
        {
            ["id"] = symbol.Id,
            ["stableKey"] = symbol.StableKey,
            ["kind"] = symbol.Kind.ToString().ToLowerInvariant(),
            ["displayName"] = SymbolSignatureFormatter.FormatDisplayName(symbol, shortNames),
            ["signature"] = SymbolSignatureFormatter.Format(symbol, shortNames),
            ["fullyQualifiedName"] = SymbolSignatureFormatter.FormatDisplayName(symbol, shortNames: false),
            ["namespaceName"] = symbol.NamespaceName,
            ["typeSimpleName"] = symbol.TypeSimpleName,
            ["parameters"] = symbol.Parameters
                .Select(parameter => string.IsNullOrEmpty(parameter.TypeDisplay)
                    ? parameter.TypeKey
                    : parameter.TypeDisplay)
                .ToArray(),
            ["location"] = symbol.DocumentPath is null || symbol.SourceStart is null
                ? null
                : ToLocationObject(symbol.DocumentPath, symbol.SourceStart.Value),
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

    private static object ToLocationObject(string path, int offset)
    {
        var point = SafeResolve(path, offset);
        return new { path = point.Path, line = point.Line, column = point.Column, offset = point.Offset };
    }

    private static string FormatDefinitionLocation(StoredSymbol symbol)
    {
        if (symbol.DocumentPath is null || symbol.SourceStart is null)
        {
            return string.Empty;
        }

        var point = SafeResolve(symbol.DocumentPath, symbol.SourceStart.Value);
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

    private static string FormatDefinitionLocationField(StoredSymbol symbol)
    {
        if (symbol.DocumentPath is null || symbol.SourceStart is null)
        {
            return string.Empty;
        }

        var point = SafeResolve(symbol.DocumentPath, symbol.SourceStart.Value);
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

    private static SourcePoint SafeResolve(string path, int offset)
    {
        try
        {
            return SourcePositionResolver.ResolveOffset(path, offset);
        }
        catch (IOException)
        {
            return new SourcePoint(path, 0, 0, offset);
        }
        catch (ArgumentException)
        {
            return new SourcePoint(path, 0, 0, offset);
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
                    symbol => ToSymbolObject(symbol, _shortNames, context.ShowSource),
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
        $"{SymbolSignatureFormatter.Format(symbol, _shortNames)}{FormatAsyncAnalysis(symbol)}");

    private void WriteSymbolsMultiLine(QueryContext context, CancellationToken cancellationToken)
    {
        _writer.WriteLine($"Query matched {context.MatchedSymbols.Count} symbol(s):");
        foreach (var symbol in context.MatchedSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.WriteLine(TableTextSanitizer.Sanitize(
                $"  {SymbolSignatureFormatter.Format(symbol, _shortNames)}{FormatDefinitionLocation(symbol)}{FormatAsyncAnalysis(symbol)}"));
            if (context.ShowSource)
            {
                _writer.WriteLine($"    source: {TableTextSanitizer.Sanitize(symbol.NormalizedSource)}");
            }
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
        SymbolSignatureFormatter.FormatDisplayName(symbol, _shortNames);

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
