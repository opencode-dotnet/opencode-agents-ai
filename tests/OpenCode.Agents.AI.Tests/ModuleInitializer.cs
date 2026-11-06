using System.Runtime.CompilerServices;

namespace OpenCode.Agents.AI.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        UseProjectRelativeDirectory("Snapshots");
    }
}
