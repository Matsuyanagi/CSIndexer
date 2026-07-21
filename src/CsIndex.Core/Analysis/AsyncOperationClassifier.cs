using CsIndex.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsIndex.Core.Analysis;

public static class AsyncOperationClassifier
{
    public static AsyncUsageKind ClassifyInvocation(IInvocationOperation invocation)
    {
        var result = AsyncUsageKind.None;
        for (IOperation? current = invocation.Parent; current is not null; current = current.Parent)
        {
            var candidate = current switch
            {
                IAwaitOperation => AsyncUsageKind.Awaited,
                IReturnOperation => AsyncUsageKind.Forwarded,
                ISimpleAssignmentOperation { Target: IDiscardOperation } => AsyncUsageKind.Discarded,
                IVariableInitializerOperation or ISimpleAssignmentOperation => AsyncUsageKind.Stored,
                IArgumentOperation => AsyncUsageKind.Passed,
                IExpressionStatementOperation => AsyncUsageKind.Unobserved,
                _ => AsyncUsageKind.None,
            };
            result = HigherPriority(result, candidate);
        }

        return result;
    }

    private static AsyncUsageKind HigherPriority(AsyncUsageKind left, AsyncUsageKind right) =>
        Priority(left) <= Priority(right) ? left : right;

    private static int Priority(AsyncUsageKind kind) => kind switch
    {
        AsyncUsageKind.Awaited => 0,
        AsyncUsageKind.Forwarded => 1,
        AsyncUsageKind.Discarded => 2,
        AsyncUsageKind.Stored => 3,
        AsyncUsageKind.Passed => 4,
        AsyncUsageKind.Unobserved => 5,
        AsyncUsageKind.None => 6,
        _ => int.MaxValue,
    };
}
