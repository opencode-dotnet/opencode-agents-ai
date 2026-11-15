using System.Reflection;
using OpenCode.Sdk;
using TUnit.Assertions.Enums;

namespace OpenCode.Agents.AI.Tests.Turns;

public sealed class SdkExperimentalSurfaceTests
{
    [Test]
    public async Task Sdk_Should_Keep_The_Providers_Canonical_Experimental_Usage_Marked()
    {
        var assembly = typeof(OpenCodeClient).Assembly;
        using var stream = typeof(SdkExperimentalSurfaceTests).Assembly.GetManifestResourceStream("Fixtures.AgentIntegration.md")
            ?? throw new InvalidOperationException("The canonical agent integration resource was not embedded.");
        using var reader = new StreamReader(stream);
        var document = await reader.ReadToEndAsync();
        var section = document.IndexOf("### Experimental SDK declarations used by the provider", StringComparison.Ordinal);
        await Assert.That(section).IsGreaterThanOrEqualTo(0);
        var start = document.IndexOf("```text", section, StringComparison.Ordinal) + "```text".Length;
        var end = document.IndexOf("```", start, StringComparison.Ordinal);
        var names = document[start..end].Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        await Assert.That(names).IsNotEmpty();
        await Assert.That(names).Contains("OpenCode.Sdk.OpenCodeClient.Experimental");
        await Assert.That(names).Contains("OpenCode.Sdk.ExperimentalClient");
        await Assert.That(names).Contains("OpenCode.Sdk.Models.JobShell");

        var declarations = names.Select(name => Resolve(assembly, name)).ToArray();
        await Assert.That(declarations.Select(declaration => declaration is Type type ? type.FullName : declaration.DeclaringType?.FullName + "." + declaration.Name))
            .IsEquivalentTo(names.Cast<string?>(), CollectionOrdering.Matching);
        foreach (var declaration in declarations)
        {
            var name = declaration is Type type ? type.FullName : declaration.DeclaringType?.FullName + "." + declaration.Name;
            var message = $"SDK experimental contract changed: {name} in {assembly.FullName} no longer carries OPENCODE001. Review the pinned API and remove or narrow the provider opt-in and its inventory row only after approval.";
            var markers = declaration.CustomAttributes.Where(attribute => attribute.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.ExperimentalAttribute").ToArray();
            await Assert.That(markers).IsNotEmpty().Because(message);
            foreach (var marker in markers)
            {
                await Assert.That(marker.ConstructorArguments.Select(argument => argument.Value)).IsEquivalentTo(new object?[] { "OPENCODE001" }, CollectionOrdering.Matching)
                    .Because($"{message} Expected OPENCODE001; actual diagnostic ids: {string.Join(", ", marker.ConstructorArguments.Select(argument => argument.Value))}.");
            }
        }
    }

    private static MemberInfo Resolve(Assembly assembly, string name)
    {
        if (assembly.GetType(name, throwOnError: false) is { } type)
        {
            return type;
        }

        var separator = name.LastIndexOf('.', StringComparison.Ordinal);
        return assembly.GetType(name[..separator], throwOnError: false)?.GetProperty(name[(separator + 1)..])
            ?? throw new InvalidOperationException($"Cannot resolve canonical SDK declaration '{name}' in {assembly.FullName}.");
    }
}
