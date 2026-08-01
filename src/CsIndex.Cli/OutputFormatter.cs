using System.Text.Json;
using CsIndex.Core.Model;
using CsIndex.Query;
using CsIndex.Storage;

namespace CsIndex.Cli;

internal sealed class OutputFormatter(string format, bool shortNames = false)
{
    private readonly bool _shortNames = shortNames;

    private readonly string _format = format switch
    {
        "table" or "json" => format,
        _ => throw new CliUsageException(
            $"Output format '{format}' is reserved but not implemented. Use table or json."),
    };

    public void WriteSymbols(QueryContext context)
    {
        if (_format == "json")
        {
            WriteJson(new
            {
                profile = context.Profile.Name,
                matched = context.MatchedSymbols.Select(ToSymbolObject),
            });
            return;
        }

        Console.WriteLine($"Query matched {context.MatchedSymbols.Count} symbol(s):");
        foreach (var symbol in context.MatchedSymbols)
        {
            Console.WriteLine(
                $"  {FormatName(symbol.DisplayName)}{FormatDefinitionLocation(symbol)}{FormatAsyncAnalysis(symbol)}");
        }
    }

    public void WriteDefinitions(DefinitionResult result)
    {
        if (_format == "json")
        {
            WriteJson(new
            {
                profile = result.Context.Profile.Name,
                matched = result.Context.MatchedSymbols.Select(ToSymbolObject),
                definitions = result.Definitions.Select(ToSymbolObject),
            });
            return;
        }

        Console.WriteLine($"{result.Definitions.Count} definition(s):");
        foreach (var definition in result.Definitions)
        {
            Console.WriteLine($"  {FormatName(definition.DisplayName)}{FormatDefinitionLocation(definition)}");
            if (definition.DocumentPath is null)
            {
                Console.WriteLine($"    assembly: {definition.AssemblyName ?? "unknown"}; no source definition");
            }
        }
    }

    public void WriteCalls(CallResult result, string heading)
    {
        if (_format == "json")
        {
            WriteJson(new
            {
                profile = result.Context.Profile.Name,
                matched = result.Context.MatchedSymbols.Select(ToSymbolObject),
                calls = result.Calls.Select(call => new
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
                }),
                callers = result.EffectiveCallers.Select(ToSymbolObject),
                possibleRuntimeTargets = result.PossibleRuntimeTargets.Select(relation => new
                {
                    source = FormatName(relation.SourceDisplayName),
                    target = FormatName(relation.TargetDisplayName),
                    kind = relation.Kind.ToString(),
                }),
            });
            return;
        }

        Console.WriteLine($"{result.Context.MatchedSymbols.Count} matched symbol(s); {result.Calls.Count} {heading}:");
        foreach (var call in result.Calls)
        {
            var point = SafeResolve(call.DocumentPath, call.SourceStart);
            var target = FormatName(call.CalleeDefinitionDisplayName ?? call.CalleeDisplayName ?? call.UnresolvedName ?? "<unresolved>");
            Console.WriteLine(
                $"  {point.Path}:{point.Line}:{point.Column}  {FormatName(call.CallerDisplayName)} -> {target} " +
                $"[{call.ReferenceKind}, {call.ResolutionStatus}] [{call.AsyncUsageKind}]");
        }

        if (result.EffectiveCallers.Count > 0)
        {
            Console.WriteLine("Callers:");
            foreach (var caller in result.EffectiveCallers)
            {
                Console.WriteLine($"  {FormatName(caller.DisplayName)}");
            }
        }

        if (result.PossibleRuntimeTargets.Count > 0)
        {
            Console.WriteLine("Possible runtime targets:");
            foreach (var relation in result.PossibleRuntimeTargets)
            {
                Console.WriteLine($"  {FormatName(relation.SourceDisplayName)} [{relation.Kind}]");
            }
        }
    }

    public void WriteRelations(RelationResult result)
    {
        if (_format == "json")
        {
            WriteJson(new
            {
                profile = result.Context.Profile.Name,
                matched = result.Context.MatchedSymbols.Select(ToSymbolObject),
                relations = result.Relations.Select(relation => new
                {
                    source = FormatName(relation.SourceDisplayName),
                    target = FormatName(relation.TargetDisplayName),
                    kind = relation.Kind.ToString(),
                }),
            });
            return;
        }

        Console.WriteLine($"{result.Relations.Count} override(s):");
        foreach (var relation in result.Relations)
        {
            Console.WriteLine($"  {FormatName(relation.SourceDisplayName)} -> {FormatName(relation.TargetDisplayName)}");
        }
    }

    public void WriteConditions(ConditionsResult result)
    {
        if (_format == "json")
        {
            WriteJson(new
            {
                profile = result.Profile.Name,
                activeSymbols = result.Profile.PreprocessorSymbols,
                conditionalSymbols = result.Symbols.Select(symbol => new
                {
                    symbol.SymbolName,
                    symbol.FileCount,
                    symbol.OccurrenceCount,
                    symbol.IsDefined,
                }),
            });
            return;
        }

        Console.WriteLine($"Profile: {result.Profile.Name}");
        Console.WriteLine($"Active symbols: {string.Join(", ", result.Profile.PreprocessorSymbols)}");
        Console.WriteLine("Conditional symbols found:");
        foreach (var symbol in result.Symbols)
        {
            Console.WriteLine(
                $"  {symbol.SymbolName,-30} {symbol.FileCount,6} file(s)  " +
                (symbol.IsDefined ? "defined" : "undefined"));
        }
    }

    private object ToSymbolObject(StoredSymbol symbol) => new
    {
        symbol.Id,
        symbol.StableKey,
        kind = symbol.Kind.ToString(),
        displayName = FormatName(symbol.DisplayName),
        symbol.FullyQualifiedName,
        symbol.NamespaceName,
        symbol.TypeSimpleName,
        parameters = symbol.Parameters.Select(parameter => parameter.TypeKey),
        location = symbol.DocumentPath is null || symbol.SourceStart is null
            ? null
            : ToLocationObject(symbol.DocumentPath, symbol.SourceStart.Value),
        symbol.IsGenerated,
        symbol.AssemblyName,
        asyncRole = symbol.AsyncRole.ToString(),
        isAsyncInvolved = symbol.AsyncInvolvementDepth is not null,
        asyncInvolvementDepth = symbol.AsyncInvolvementDepth,
    };

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

    public void WriteSymbolList(QueryContext context)
    {
        if (_format == "json")
        {
            WriteJson(new
            {
                profile = context.Profile.Name,
                symbols = context.MatchedSymbols.Select(ToSymbolObject),
            });
            return;
        }

        Console.WriteLine($"{context.MatchedSymbols.Count} symbol(s):");
        foreach (var symbol in context.MatchedSymbols)
        {
            Console.WriteLine(
                $"  {FormatName(symbol.DisplayName)}{FormatDefinitionLocation(symbol)}{FormatAsyncAnalysis(symbol)}");
        }
    }

    private string? FormatName(string? name) => _shortNames && name is not null
        ? SymbolNameShortener.Shorten(name)
        : name;

    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    }));
}
