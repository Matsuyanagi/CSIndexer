namespace CsIndex.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int AnalysisFailure = 3;
    public const int DatabaseFailure = 4;
    public const int RequireSingleFailure = 5;
}
