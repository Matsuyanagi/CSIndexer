namespace CsIndex.Core.Model;

public sealed record IndexOptions
{
    public required string InputPath { get; init; }
    public string? DatabasePath { get; init; }
    public InputMode? ForcedMode { get; init; }
    public string? SolutionPath { get; init; }
    public string? Configuration { get; init; }
    public string? TargetFramework { get; init; }
    public string RuntimeIdentifier { get; init; } = "win-x64";
    public string? ProfileName { get; init; }
    public IReadOnlyList<string> Defines { get; init; } = [];
    public IReadOnlyList<string> Undefines { get; init; } = [];
    public IReadOnlyList<string> DefineFiles { get; init; } = [];
    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public string? UnityEditorPath { get; init; }
    public GeneratedSourceMode GeneratedSourceMode { get; init; } = GeneratedSourceMode.Physical;
    public bool Rebuild { get; init; }
    public bool Verbose { get; init; }
    public bool Diagnostics { get; init; }
}
