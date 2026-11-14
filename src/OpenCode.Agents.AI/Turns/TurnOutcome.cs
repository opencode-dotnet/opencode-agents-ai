using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal static class TurnOutcome
{
    private const string ShutdownMessage = "Session error: the OpenCode server is shutting down";

    public static bool EndsTurn(IEvent @event) =>
        @event.Type is "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted";

    public static Exception? Failure(IEvent turnEnd) => turnEnd switch
    {
        SessionExecutionFailed failed => new InvalidOperationException($"Session error: {failed.Data.Error.Message}"),
        SessionExecutionInterrupted interrupted when interrupted.Data.Reason == SessionExecutionInterruptedDataReason.Shutdown =>
            new InvalidOperationException(ShutdownMessage),
        UnknownEvent { Type: "session.execution.failed" } unknown =>
            new InvalidOperationException("Session error: " + (ReadString(unknown.Payload, "data", "error", "message") ?? "the turn failed.")),
        UnknownEvent { Type: "session.execution.interrupted" } unknown when ReadString(unknown.Payload, "data", "reason") == "shutdown" =>
            new InvalidOperationException(ShutdownMessage),
        _ => null,
    };

    private static string? ReadString(JsonElement payload, params string[] path)
    {
        foreach (var name in path)
        {
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out payload))
            {
                return null;
            }
        }

        return payload.ValueKind == JsonValueKind.String ? payload.GetString() : null;
    }
}
