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
                profile = result.Context.Profile.Name,
                matched = SelectWithCancellation(
                    result.Context.MatchedSymbols,
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
                    cancellationToken),
                definitions = SelectWithCancellation(
                    result.Definitions,
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
                    cancellationToken),
            });
            return;
        }

        _writer.WriteLine($"{result.Definitions.Count} definition(s):");
        foreach (var definition in result.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                profile = result.Context.Profile.Name,
                matched = SelectWithCancellation(
                    result.Context.MatchedSymbols,
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
                    cancellationToken),
                calls = SelectWithCancellation(
                    result.Calls,
                    call => new
                    {
                        call.Id,
                        caller = FormatName(call.CallerDisplayName),
                        callee = FormatName(call.CalleeDefinitionDisplayName ?? call.CalleeDisplayName),
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
                        source = FormatName(relation.SourceDisplayName),
                        target = FormatName(relation.TargetDisplayName),
                        kind = relation.Kind.ToString(),
                    },
                    cancellationToken),
            });
            return;
        }

        _writer.WriteLine($"{result.Context.MatchedSymbols.Count} matched symbol(s); {result.Calls.Count} {heading}:");
        foreach (var call in result.Calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = SafeResolve(call.DocumentPath, call.SourceStart);
            var target = FormatName(call.CalleeDefinitionDisplayName ?? call.CalleeDisplayName ?? call.UnresolvedName ?? "<unresolved>");
            _writer.WriteLine(
                $"  {point.Path}:{point.Line}:{point.Column}  {FormatName(call.CallerDisplayName)} -> {target} " +
                $"[{call.ReferenceKind}, {call.ResolutionStatus}] [{call.AsyncUsageKind}]");
        }

        if (result.EffectiveCallers.Count > 0)
        {
            _writer.WriteLine("Callers:");
            foreach (var caller in result.EffectiveCallers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _writer.WriteLine($"  {FormatName(caller.DisplayName)}");
            }
        }

        if (result.PossibleRuntimeTargets.Count > 0)
        {
            _writer.WriteLine("Possible runtime targets:");
            foreach (var relation in result.PossibleRuntimeTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _writer.WriteLine($"  {FormatName(relation.SourceDisplayName)} [{relation.Kind}]");
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
                profile = result.Context.Profile.Name,
                matched = SelectWithCancellation(
                    result.Context.MatchedSymbols,
                    symbol => ToSymbolObject(symbol, _shortNames, includeSource: false),
                    cancellationToken),
                relations = SelectWithCancellation(
                    result.Relations,
                    relation => new
                    {
                        source = FormatName(relation.SourceDisplayName),
                        target = FormatName(relation.TargetDisplayName),
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
            _writer.WriteLine($"  {FormatName(relation.SourceDisplayName)} -> {FormatName(relation.TargetDisplayName)}");
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
            ["displayName"] = SymbolSignatureFormatter.FormatDisplayName(symbol.DisplayName, shortNames),
            ["signature"] = SymbolSignatureFormatter.Format(symbol, shortNames),
            ["fullyQualifiedName"] = symbol.FullyQualifiedName,
            ["namespaceName"] = symbol.NamespaceName,
            ["typeSimpleName"] = symbol.TypeSimpleName,
            ["parameters"] = symbol.Parameters.Select(parameter => parameter.TypeKey).ToArray(),
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
            ["returnType"] = symbol.ReturnTypeKey,
            ["methodKind"] = symbol.MethodKind,
            ["sourceAvailable"] = symbol.DocumentPath is not null,
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

    private string? FormatName(string? name) => _shortNames && name is not null
        ? SymbolNameShortener.Shorten(name)
        : name;

    private void WriteJsonPayload(object value) => WriteJson(value, _writer);

    internal static void WriteJson(object value) => WriteJson(value, Console.Out);

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
