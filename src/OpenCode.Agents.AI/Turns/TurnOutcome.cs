using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal static class TurnOutcome
{
    public static bool EndsTurn(IEvent @event) =>
        @event.Type is "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted";

    public static Exception? Failure(IEvent turnEnd) => turnEnd switch
    {
        SessionExecutionFailed failed => new InvalidOperationException($"Session error: {failed.Data.Error.Message}"),
        SessionExecutionInterrupted interrupted when interrupted.Data.Reason == SessionExecutionInterruptedDataReason.Shutdown =>
            new InvalidOperationException("Session error: the OpenCode server is shutting down"),
        _ => null,
    };
}
