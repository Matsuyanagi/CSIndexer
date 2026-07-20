using Microsoft.Build.Locator;

namespace CsIndex.Core.Input;

public static class MSBuildBootstrapper
{
    private static readonly object Sync = new();

    public static void EnsureRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        lock (Sync)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }
}
