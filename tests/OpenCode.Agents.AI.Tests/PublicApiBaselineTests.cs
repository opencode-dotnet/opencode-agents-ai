#if NET
using System.Reflection;
using PublicApiGenerator;

namespace OpenCode.Agents.AI.Tests;

/// <summary>
/// The Agents.AI package's public surface against its reviewed baseline: a change to it is
/// reviewed as a diff of this file. It runs on .NET only: the
/// net472 assembly renders without the trimming annotations, one of the reviewed differences
/// <see cref="ShippedAssetPublicApiTests"/> holds every shipped asset to.
/// </summary>
public sealed class PublicApiBaselineTests
{
    [Test]
    public async Task PublicApi_Should_Match_The_Reviewed_Baseline()
    {
        // The first public type replaces this assembly-name load.
        var api = System.Reflection.Assembly.Load(new AssemblyName("OpenCode.Agents.AI")).GeneratePublicApi(new ApiGeneratorOptions
        {
            IncludeAssemblyAttributes = false,
        });

        var settings = new VerifySettings();
        settings.UseFileName("PublicApi");

        // Keep the reviewed baseline a normal LF-terminated text artifact on every OS.
        await Verify(string.Concat(api, "\n"), settings);
    }
}
#endif
