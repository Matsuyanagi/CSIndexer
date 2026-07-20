using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsIndex.Core.Analysis;

public static class ConditionalDirectiveScanner
{
    public static IReadOnlyDictionary<string, int> Scan(CompilationUnitSyntax root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            var condition = trivia.GetStructure() switch
            {
                IfDirectiveTriviaSyntax directive => directive.Condition,
                ElifDirectiveTriviaSyntax directive => directive.Condition,
                _ => null,
            };
            if (condition is null)
            {
                continue;
            }

            foreach (var identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                counts.TryGetValue(identifier.Identifier.ValueText, out var count);
                counts[identifier.Identifier.ValueText] = count + 1;
            }
        }

        return counts;
    }
}
