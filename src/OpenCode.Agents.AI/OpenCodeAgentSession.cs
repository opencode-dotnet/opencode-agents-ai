using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;

namespace OpenCode.Agents.AI;

/// <summary>
/// Represents an agent session that refers to an OpenCode conversation.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class OpenCodeAgentSession : AgentSession
{
    /// <summary>Gets or sets the session ID of the OpenCode conversation.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; internal set; }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeAgentSession"/> class.</summary>
    internal OpenCodeAgentSession()
    {
    }

    [JsonConstructor]
    internal OpenCodeAgentSession(string? sessionId, AgentSessionStateBag? stateBag) : base(stateBag ?? new())
    {
        SessionId = sessionId;
    }

    internal JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var options = jsonSerializerOptions ?? OpenCodeAgentJsonUtilities.DefaultOptions;
        return JsonSerializer.SerializeToElement(this, GetSessionTypeInfo(options));
    }

    internal static OpenCodeAgentSession Deserialize(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (serializedState.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The serialized session state must be a JSON object.", nameof(serializedState));
        }

        var options = jsonSerializerOptions ?? OpenCodeAgentJsonUtilities.DefaultOptions;
        return serializedState.Deserialize(GetSessionTypeInfo(options))
            ?? new OpenCodeAgentSession();
    }

    private static JsonTypeInfo<OpenCodeAgentSession> GetSessionTypeInfo(JsonSerializerOptions options)
    {
        // The typed metadata overload is available only on .NET 11 and later.
#if NET11_0_OR_GREATER
        return options.GetTypeInfo<OpenCodeAgentSession>();
#else
        return (JsonTypeInfo<OpenCodeAgentSession>)options.GetTypeInfo(typeof(OpenCodeAgentSession));
#endif
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay => $"SessionId = {SessionId}, StateBag Count = {StateBag.Count}";
}
