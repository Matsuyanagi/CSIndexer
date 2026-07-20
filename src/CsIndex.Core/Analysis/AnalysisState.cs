using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsIndex.Core.Analysis;

internal sealed class ProjectAnalysisState
{
    public required Project Project { get; init; }
    public required Compilation Compilation { get; init; }
    public required ProjectData Data { get; init; }
    public required bool HasMissingReferenceDiagnostics { get; init; }
    public Dictionary<DocumentId, DocumentAnalysisState> Documents { get; } = [];
}

internal sealed class DocumentAnalysisState
{
    public required Document Document { get; init; }
    public required DocumentData Data { get; init; }
    public Dictionary<int, string> MethodOwners { get; } = [];
    public Dictionary<int, string> LocalFunctionOwners { get; } = [];
    public Dictionary<int, string> LambdaOwners { get; } = [];
    public Dictionary<int, string> InitializerOwners { get; } = [];
    public string? TopLevelOwner { get; set; }

    public string? FindOwner(SyntaxNode node, bool includeSelf = true)
    {
        for (var current = includeSelf ? node : node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AnonymousFunctionExpressionSyntax lambda
                    when LambdaOwners.TryGetValue(lambda.SpanStart, out var lambdaOwner):
                    return lambdaOwner;
                case LocalFunctionStatementSyntax localFunction
                    when LocalFunctionOwners.TryGetValue(localFunction.SpanStart, out var localOwner):
                    return localOwner;
                case BaseMethodDeclarationSyntax method
                    when MethodOwners.TryGetValue(method.SpanStart, out var methodOwner):
                    return methodOwner;
                case EqualsValueClauseSyntax initializer
                    when InitializerOwners.TryGetValue(initializer.SpanStart, out var initializerOwner):
                    return initializerOwner;
                case GlobalStatementSyntax:
                    return TopLevelOwner;
            }
        }

        return null;
    }
}
