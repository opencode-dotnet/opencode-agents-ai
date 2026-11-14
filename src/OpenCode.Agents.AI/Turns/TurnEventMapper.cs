using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class TurnEventMapper(string agentId, string promptId)
{
    private readonly Dictionary<(string MessageId, string CallId), string> _toolNames = [];
    private readonly Dictionary<(string MessageId, string CallId), string> _openCalls = [];

    public AgentResponseUpdate? Map(IEvent @event)
    {
        if (@event is SessionToolInputStarted started)
        {
            _toolNames[(started.Data.AssistantMessageId, started.Data.Id)] = started.Data.Name;
        }

        var content = @event switch
        {
            SessionTextDelta text => new TextContent(text.Data.Delta),
            SessionReasoningDelta reasoning => new TextReasoningContent(reasoning.Data.Delta),
            SessionToolCalled called => MapCall(called.Data),
            SessionToolSuccess succeeded => MapResult(succeeded.Data.AssistantMessageId, succeeded.Data.Id,
                string.Join('\n', succeeded.Data.Content.OfType<ToolTextContent>().Select(item => item.Text))),
            SessionToolFailed failed => MapResult(failed.Data.AssistantMessageId, failed.Data.Id, failed.Data.Error.Message),
            SessionStepEnded ended => new UsageContent(new UsageDetails
            {
                InputTokenCount = (long)Math.Round(ended.Data.Tokens.Input, MidpointRounding.AwayFromZero),
                OutputTokenCount = (long)Math.Round(ended.Data.Tokens.Output, MidpointRounding.AwayFromZero),
                ReasoningTokenCount = (long)Math.Round(ended.Data.Tokens.Reasoning, MidpointRounding.AwayFromZero),
                CachedInputTokenCount = (long)Math.Round(ended.Data.Tokens.Cache.Read, MidpointRounding.AwayFromZero),
            }),
            _ => new AIContent(),
        };
        content.RawRepresentation = @event;

        var messageId = @event switch
        {
            SessionTextDelta text => text.Data.AssistantMessageId,
            SessionReasoningDelta reasoning => reasoning.Data.AssistantMessageId,
            SessionToolInputStarted tool => tool.Data.AssistantMessageId,
            SessionToolCalled tool => tool.Data.AssistantMessageId,
            SessionToolSuccess tool => tool.Data.AssistantMessageId,
            SessionToolFailed tool => tool.Data.AssistantMessageId,
            SessionStepEnded ended => ended.Data.AssistantMessageId,
            _ => null,
        };
        return CreateUpdate(content, content is FunctionResultContent ? ChatRole.Tool : ChatRole.Assistant, messageId);
    }

    public IReadOnlyList<AgentResponseUpdate> CloseOpenCalls()
    {
        var updates = _openCalls.Select(call => CreateUpdate(
            new FunctionResultContent(call.Key.CallId, $"Tool execution interrupted: {call.Value}"),
            ChatRole.Tool, call.Key.MessageId)).ToArray();
        _openCalls.Clear();
        return updates;
    }

    private FunctionCallContent MapCall(SessionToolCalledData data)
    {
        var key = (data.AssistantMessageId, data.Id);
        var name = _toolNames.TryGetValue(key, out var startedName) ? startedName : "tool";
        _openCalls[key] = name;
        return new FunctionCallContent(data.Id, name,
            data.Input.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal));
    }

    private FunctionResultContent MapResult(string messageId, string callId, string result)
    {
        _openCalls.Remove((messageId, callId));
        return new FunctionResultContent(callId, result);
    }

    private AgentResponseUpdate CreateUpdate(AIContent content, ChatRole role, string? messageId) =>
        new(role, [content])
        {
            AgentId = agentId,
            ResponseId = promptId,
            MessageId = messageId,
        };
}
