namespace CsIndex.Query.Symbols;

public enum ConditionSyntax
{
    Glob = 0,
    Literal = 1,
    Regex = 2,
}

public enum CaseMode
{
    Strict = 0,
    Ignore = 1,
}

public enum ConditionCategory
{
    Namespace = 1,
    Type = 2,
    Method = 3,
    File = 4,
    Include = 5,
    Exclude = 6,
}

public sealed record TypedCondition(
    ConditionCategory Category,
    ConditionSyntax Syntax,
    string Value);

public sealed record SymbolCaseOptions(
    CaseMode Namespace = CaseMode.Strict,
    CaseMode Type = CaseMode.Strict,
    CaseMode Method = CaseMode.Strict,
    CaseMode File = CaseMode.Strict,
    CaseMode Source = CaseMode.Strict);
