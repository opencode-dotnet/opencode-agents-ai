using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests.Support;

internal static class TurnEvents
{
    public static IEvent Connected() => new EventServerConnected { Id = "event_connected", Data = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) };

    public static IEvent Delivered(string inboxId, string sessionId = FakeOpenCode.SessionId) => new SessionInboxDelivered
    {
        Id = "event_delivered",
        Created = 1,
        Durable = new SessionInboxDeliveredDurable { AggregateId = sessionId, Seq = 1, Version = 1 },
        Data = new SessionInboxDeliveredData { SessionId = sessionId, InboxId = inboxId },
    };

    public static IEvent Text(string delta, string sessionId = FakeOpenCode.SessionId) => new SessionTextDelta
    {
        Id = "event_text",
        Created = 1,
        Data = new SessionTextDeltaData { SessionId = sessionId, AssistantMessageId = "msg_assistant", Ordinal = 0, Delta = delta },
    };

    public static IEvent Reasoning(string delta) => new SessionReasoningDelta
    {
        Id = "event_reasoning",
        Created = 1,
        Data = new SessionReasoningDeltaData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = "msg_reasoning", Ordinal = 0, Delta = delta },
    };

    public static IEvent Succeeded(string sessionId = FakeOpenCode.SessionId) => new SessionExecutionSucceeded
    {
        Id = "event_succeeded",
        Created = 1,
        Durable = new SessionExecutionSucceededDurable { AggregateId = sessionId, Seq = 2, Version = 1 },
        Data = new SessionExecutionSucceededData { SessionId = sessionId },
    };

    public static IEvent Failed(string message) => new SessionExecutionFailed
    {
        Id = "event_failed",
        Created = 1,
        Durable = new SessionExecutionFailedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 2, Version = 1 },
        Data = new SessionExecutionFailedData
        {
            SessionId = FakeOpenCode.SessionId,
            Error = new SessionStructuredError { Type = "model_error", Message = message },
        },
    };

    public static IEvent Interrupted(SessionExecutionInterruptedDataReason reason) => new SessionExecutionInterrupted
    {
        Id = "event_interrupted",
        Created = 1,
        Durable = new SessionExecutionInterruptedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 2, Version = 1 },
        Data = new SessionExecutionInterruptedData { SessionId = FakeOpenCode.SessionId, Reason = reason },
    };
}
