using CsIndex.Core.Model;

namespace CsIndex.Core.Input;

public sealed record ResolvedInput(
    InputMode Mode,
    string OriginalPath,
    string RootPath,
    IReadOnlyList<string> EntryPaths);

public sealed class InputResolutionException(string message) : Exception(message);
