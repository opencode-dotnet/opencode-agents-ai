using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests.Support;

internal static class TurnEvents
{
    public static IEvent Connected() => new EventServerConnected { Id = "event_connected", Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal) };

    public static PermissionAsked PermissionAsked(string requestId, string sessionId = FakeOpenCode.SessionId) => new()
    {
        Id = "event_permission_asked",
        Created = 1,
        Data = new PermissionAskedData
        {
            Id = requestId,
            SessionId = sessionId,
            Action = "edit",
            Resources = ["z.cs", "a.cs"],
            Save = ["z/*", "a/*"],
            Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["reason"] = JsonSerializer.SerializeToElement("update") },
            Source = new PermissionSourceTool { Type = "tool", MessageId = "msg_permission", Id = "call_edit" },
            Message = "Update these files?",
        },
    };

    public static IEvent Delivered(string inboxId, string sessionId = FakeOpenCode.SessionId) => new SessionInboxDelivered
    {
        Id = "event_delivered",
        Created = 1,
        Durable = new SessionInboxDeliveredDurable { AggregateId = sessionId, Seq = 1, Version = 1 },
        Data = new SessionInboxDeliveredData { SessionId = sessionId, InboxId = inboxId },
    };

    public static IEvent Text(string delta, string sessionId = FakeOpenCode.SessionId, string assistantMessageId = "msg_assistant", long ordinal = 0) => new SessionTextDelta
    {
        Id = "event_text",
        Created = 1,
        Data = new SessionTextDeltaData { SessionId = sessionId, AssistantMessageId = assistantMessageId, Ordinal = ordinal, Delta = delta },
    };

    public static IEvent Reasoning(string delta, string assistantMessageId = "msg_reasoning", long ordinal = 0) => new SessionReasoningDelta
    {
        Id = "event_reasoning",
        Created = 1,
        Data = new SessionReasoningDeltaData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = assistantMessageId, Ordinal = ordinal, Delta = delta },
    };

    public static SessionTextStarted TextStarted(string assistantMessageId = "msg_assistant", long ordinal = 0) => new()
    {
        Id = "event_text_started",
        Created = 1,
        Durable = new SessionTextStartedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 1, Version = 1 },
        Data = new SessionTextStartedData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = assistantMessageId, Ordinal = ordinal },
    };

    public static SessionTextEnded TextEnded(string text, string assistantMessageId = "msg_assistant", long ordinal = 0) => new()
    {
        Id = "event_text_ended",
        Created = 1,
        Durable = new SessionTextEndedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 2, Version = 1 },
        Data = new SessionTextEndedData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = assistantMessageId, Ordinal = ordinal, Text = text },
    };

    public static SessionReasoningStarted ReasoningStarted(string assistantMessageId = "msg_reasoning", long ordinal = 0) => new()
    {
        Id = "event_reasoning_started",
        Created = 1,
        Durable = new SessionReasoningStartedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 1, Version = 1 },
        Data = new SessionReasoningStartedData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = assistantMessageId, Ordinal = ordinal },
    };

    public static SessionReasoningEnded ReasoningEnded(string text, string assistantMessageId = "msg_reasoning", long ordinal = 0) => new()
    {
        Id = "event_reasoning_ended",
        Created = 1,
        Durable = new SessionReasoningEndedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 2, Version = 1 },
        Data = new SessionReasoningEndedData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = assistantMessageId, Ordinal = ordinal, Text = text },
    };

    public static SessionMessageUser StoredUser(string id, string text = "hello") => new()
    {
        Id = id,
        Text = text,
        Time = new SessionMessageUserTime { Created = 1 },
    };

    public static SessionMessageAssistant StoredAssistant(string id, IReadOnlyList<ISessionMessageAssistantContent> content,
        TokenUsageInfo? tokens = null, SessionMessageAssistantFinish? finish = null) => new()
        {
            Id = id,
            Content = content,
            Tokens = tokens,
            Finish = finish,
            Agent = "build",
            Model = new ModelRef { Id = "model_test", ProviderId = "provider_test" },
            Time = new SessionMessageAssistantTime { Created = 1 },
        };

    public static SessionMessageIdle StoredIdle(string id, SessionMessageIdleOutcome outcome, SessionStructuredError? error = null) => new()
    {
        Id = id,
        Outcome = outcome,
        Error = error,
        Time = new SessionMessageIdleTime { Created = 1 },
    };

    public static SessionMessageAssistantTool StoredTool(string id, string name, ISessionMessageToolState state, double? ran = null) => new()
    {
        Id = id,
        Name = name,
        State = state,
        Time = new SessionMessageAssistantToolTime { Created = 1, Ran = ran },
    };

    public static IEvent ToolStarted(string callId, string name) => new SessionToolInputStarted
    {
        Id = "event_tool_started",
        Created = 1,
        Durable = new SessionToolInputStartedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 1, Version = 1 },
        Data = new SessionToolInputStartedData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = "msg_assistant", Id = callId, Name = name },
    };

    public static IEvent ToolCalled(string callId, IReadOnlyDictionary<string, JsonElement> input) => new SessionToolCalled
    {
        Id = "event_tool_called",
        Created = 1,
        Durable = new SessionToolCalledDurable { AggregateId = FakeOpenCode.SessionId, Seq = 2, Version = 1 },
        Data = new SessionToolCalledData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = "msg_assistant", Id = callId, Input = input, Executed = true },
    };

    public static IEvent ToolSucceeded(string callId, string text) => new SessionToolSuccess
    {
        Id = "event_tool_succeeded",
        Created = 1,
        Durable = new SessionToolSuccessDurable { AggregateId = FakeOpenCode.SessionId, Seq = 3, Version = 1 },
        Data = new SessionToolSuccessData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = "msg_assistant", Id = callId, Content = [new ToolTextContent { Text = text }], Executed = true },
    };

    public static IEvent ToolFailed(string callId, string message) => new SessionToolFailed
    {
        Id = "event_tool_failed",
        Created = 1,
        Durable = new SessionToolFailedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 3, Version = 1 },
        Data = new SessionToolFailedData
        {
            SessionId = FakeOpenCode.SessionId,
            AssistantMessageId = "msg_assistant",
            Id = callId,
            Error = new SessionStructuredError { Type = "tool_error", Message = message },
            Executed = true,
        },
    };

    public static IEvent StepEnded(double input, double output, double reasoning, double cacheRead, double cost) => new SessionStepEnded
    {
        Id = "event_step_ended",
        Created = 1,
        Durable = new SessionStepEndedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 4, Version = 1 },
        Data = new SessionStepEndedData
        {
            SessionId = FakeOpenCode.SessionId,
            AssistantMessageId = "msg_assistant",
            Finish = SessionStepEndedDataFinish.Stop,
            Cost = cost,
            Tokens = new TokenUsageInfo { Input = input, Output = output, Reasoning = reasoning, Cache = new TokenUsageInfoCache { Read = cacheRead, Write = 0 } },
        },
    };

    public static IEvent Succeeded(string sessionId = FakeOpenCode.SessionId) => new SessionExecutionSucceeded
    {
        Id = "event_succeeded",
        Created = 1,
        Durable = new SessionExecutionSucceededDurable { AggregateId = sessionId, Seq = 2, Version = 1 },
        Data = new SessionExecutionSucceededData { SessionId = sessionId },
    };

    public static SessionExecutionStarted ExecutionStarted(string sessionId) => new()
    {
        Id = "event_started",
        Created = 1,
        Durable = new SessionExecutionStartedDurable { AggregateId = sessionId, Seq = 1, Version = 1 },
        Data = new SessionExecutionStartedData { SessionId = sessionId },
    };

    public static IEvent Failed(string message, string sessionId = FakeOpenCode.SessionId) => new SessionExecutionFailed
    {
        Id = "event_failed",
        Created = 1,
        Durable = new SessionExecutionFailedDurable { AggregateId = sessionId, Seq = 2, Version = 1 },
        Data = new SessionExecutionFailedData
        {
            SessionId = sessionId,
            Error = new SessionStructuredError { Type = "model_error", Message = message },
        },
    };

    public static IEvent Interrupted(SessionExecutionInterruptedDataReason reason, string sessionId = FakeOpenCode.SessionId) => new SessionExecutionInterrupted
    {
        Id = "event_interrupted",
        Created = 1,
        Durable = new SessionExecutionInterruptedDurable { AggregateId = sessionId, Seq = 2, Version = 1 },
        Data = new SessionExecutionInterruptedData { SessionId = sessionId, Reason = reason },
    };

    public static PermissionRequest PendingPermission(string requestId, string sessionId = FakeOpenCode.SessionId)
    {
        var asked = PermissionAsked(requestId, sessionId).Data;
        return new PermissionRequest
        {
            Id = asked.Id,
            SessionId = asked.SessionId,
            Action = asked.Action,
            Resources = asked.Resources,
            Save = asked.Save,
            Metadata = asked.Metadata,
            Source = asked.Source,
            Message = asked.Message,
        };
    }

#pragma warning disable OPENCODE001 // JobSubagent, JobShell, JobImage, UnknownJobInfo and JobStatus require the SDK's experimental model opt-in.
    public static JobSubagent SubagentJob(string id, string ownerSessionId, string childSessionId, double started, JobStatus status) => new()
    {
        Id = id,
        SessionId = ownerSessionId,
        ChildSessionId = childSessionId,
        Time = new JobTime { Started = started },
        Status = status,
        Agent = "build",
        Description = "Background implementation",
    };

    public static JobShell ShellJob(string id, string sessionId, double started, JobStatus status) => new()
    {
        Id = id,
        SessionId = sessionId,
        Time = new JobTime { Started = started },
        Status = status,
        ShellId = "shell_test",
        Command = "build",
    };

    public static JobImage ImageJob(string id, string sessionId, double started, JobStatus status) => new()
    {
        Id = id,
        SessionId = sessionId,
        Time = new JobTime { Started = started },
        Status = status,
        Path = "image.png",
        Model = "image-model",
    };

    public static UnknownJobInfo UnknownJob(string kind) => new(kind,
        JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = kind }));
#pragma warning restore OPENCODE001 // JobSubagent, JobShell, JobImage, UnknownJobInfo and JobStatus builder declarations end here.

    public static SessionInboxSynthetic QueuedWake(string id, string sessionId) => new()
    {
        Id = id,
        SessionId = sessionId,
        Time = new SessionInboxSyntheticTime { Created = 101 },
        Payload = new SessionInboxSyntheticPayload { Text = "Background work finished" },
        Delivery = SessionInboxDelivery.Queue,
    };
}
