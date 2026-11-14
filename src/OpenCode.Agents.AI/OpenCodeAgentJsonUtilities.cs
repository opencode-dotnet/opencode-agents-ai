using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace OpenCode.Agents.AI;

/// <summary>Provides the source-generated serialization configuration for OpenCode agent sessions.</summary>
internal static partial class OpenCodeAgentJsonUtilities
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        JsonSerializerOptions options = new(JsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.TypeInfoResolverChain.Clear();
        // Both generated contexts supply resolvers; neither relies on reflection fallback.
        options.TypeInfoResolverChain.Add(AgentAbstractionsJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(JsonContext.Default.Options.TypeInfoResolver!);
        options.MakeReadOnly();
        return options;
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(OpenCodeAgentSession))]
    [JsonSerializable(typeof(Dictionary<string, object?>))]
    [ExcludeFromCodeCoverage]
    private sealed partial class JsonContext : JsonSerializerContext;
}
