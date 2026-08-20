using Microsoft.CodeAnalysis;

namespace CsIndex.Core.Input;

public sealed class LoadedWorkspace(
    Workspace workspace,
    IReadOnlyList<Project> projects,
    IReadOnlyList<string> metadataReferences,
    IReadOnlyList<string> warnings,
    int documentsExcluded) : IDisposable
{
    private int _disposed;

    public Workspace Workspace { get; } = workspace;
    public IReadOnlyList<Project> Projects { get; } = projects;
    public IReadOnlyList<string> MetadataReferences { get; } = metadataReferences;
    public IReadOnlyList<string> Warnings { get; } = warnings;
    public int DocumentsExcluded { get; } = documentsExcluded;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Workspace.Dispose();
        }
    }
}
