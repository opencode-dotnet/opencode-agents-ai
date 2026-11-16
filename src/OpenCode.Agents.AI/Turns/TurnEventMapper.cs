using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class TurnEventMapper(string agentId, string promptId)
{
    private readonly Dictionary<(string MessageId, string CallId), string> _toolNames = [];
    private readonly Dictionary<(string MessageId, string CallId), string> _openCalls = [];
    private readonly Dictionary<(bool Reasoning, string MessageId, long Ordinal), string> _emitted = [];
    private readonly HashSet<(bool Reasoning, string MessageId, long Ordinal)> _completed = [];
    private readonly HashSet<(bool Reasoning, string MessageId, long Ordinal)> _streamable = [];
    private readonly HashSet<(string MessageId, string CallId)> _calls = [];
    private readonly HashSet<(string MessageId, string CallId)> _results = [];
    private readonly Dictionary<(string MessageId, string CallId), IReadOnlyDictionary<string, JsonElement>> _inputs = [];
    private readonly HashSet<string> _usage = new(StringComparer.Ordinal);
    private bool _recovering;

    public void BeginRecovery()
    {
        _recovering = true;
        _streamable.Clear();
    }

    public IReadOnlyList<AgentResponseUpdate> MapUpdates(IEvent current)
    {
        switch (current)
        {
            case SessionTextStarted start:
                Authorize((false, start.Data.AssistantMessageId, start.Data.Ordinal));
                break;
            case SessionReasoningStarted start:
                Authorize((true, start.Data.AssistantMessageId, start.Data.Ordinal));
                break;
            case SessionTextDelta delta:
                return [Delta((false, delta.Data.AssistantMessageId, delta.Data.Ordinal), delta.Data.Delta, current)];
            case SessionReasoningDelta delta:
                return [Delta((true, delta.Data.AssistantMessageId, delta.Data.Ordinal), delta.Data.Delta, current)];
            case SessionTextEnded end when _recovering:
                return [Full((false, end.Data.AssistantMessageId, end.Data.Ordinal), end.Data.Text, current)];
            case SessionReasoningEnded end when _recovering:
                return [Full((true, end.Data.AssistantMessageId, end.Data.Ordinal), end.Data.Text, current)];
            case SessionToolCalled called:
                _inputs[(called.Data.AssistantMessageId, called.Data.Id)] = called.Data.Input;
                if (_recovering && _calls.Contains((called.Data.AssistantMessageId, called.Data.Id)))
                {
                    return [Raw(current, called.Data.AssistantMessageId)];
                }
                _calls.Add((called.Data.AssistantMessageId, called.Data.Id));
                break;
            case SessionToolSuccess success:
                return LiveResult((success.Data.AssistantMessageId, success.Data.Id), ToolText(success.Data.Content), current);
            case SessionToolFailed failed:
                return LiveResult((failed.Data.AssistantMessageId, failed.Data.Id), failed.Data.Error.Message, current);
            case SessionStepEnded ended when !_usage.Add(ended.Data.AssistantMessageId) && _recovering:
                return [Raw(current, ended.Data.AssistantMessageId)];
        }
        return [Map(current)!]; // Map always constructs an update, including raw-only events.
    }

    public IReadOnlyList<AgentResponseUpdate> Reconcile(IReadOnlyList<ISessionMessageInfo> messages)
    {
        var updates = new List<AgentResponseUpdate>();
        foreach (var message in messages)
        {
            if (message is not SessionMessageAssistant assistant)
            {
                updates.Add(Raw(message, message.Id));
                continue;
            }
            long textOrdinal = 0;
            long reasoningOrdinal = 0;
            foreach (var part in assistant.Content)
            {
                switch (part)
                {
                    case SessionMessageAssistantText text:
                        if (text.Text.Length != 0) { AddSemantic(updates, Full((false, assistant.Id, textOrdinal), text.Text, text)); }
                        textOrdinal++;
                        break;
                    case SessionMessageAssistantReasoning reasoning:
                        if (reasoning.Text.Length != 0) { AddSemantic(updates, Full((true, assistant.Id, reasoningOrdinal), reasoning.Text, reasoning)); }
                        reasoningOrdinal++;
                        break;
                    case SessionMessageAssistantTool tool:
                        updates.AddRange(StoredTool(assistant.Id, tool));
                        break;
                    default:
                        updates.Add(Raw(part, assistant.Id));
                        break;
                }
            }
            if (assistant.Finish is not null && assistant.Error is null && assistant.Tokens is { } tokens && _usage.Add(assistant.Id))
            {
                updates.Add(WithRaw(Usage(tokens), assistant, assistant.Id));
            }
        }
        return updates;
    }

    private static void AddSemantic(List<AgentResponseUpdate> updates, AgentResponseUpdate update)
    {
        if (update.Contents.Single().GetType() != typeof(AIContent)) { updates.Add(update); }
    }

    private void Authorize((bool Reasoning, string MessageId, long Ordinal) key)
    {
        if (!_completed.Contains(key)) { _streamable.Add(key); }
    }

    private AgentResponseUpdate Delta((bool Reasoning, string MessageId, long Ordinal) key, string delta, IEvent current)
    {
        if (_recovering && !_streamable.Contains(key)) { return Raw(current, key.MessageId); }
        _emitted.TryGetValue(key, out var previous);
        _emitted[key] = previous + delta;
        return WithRaw(key.Reasoning ? new TextReasoningContent(delta) : new TextContent(delta), current, key.MessageId);
    }

    private AgentResponseUpdate Full((bool Reasoning, string MessageId, long Ordinal) key, string full, object raw)
    {
        _completed.Add(key);
        _streamable.Remove(key);
        var previous = _emitted.TryGetValue(key, out var emitted) ? emitted : "";
        if (full.Length == previous.Length || !full.StartsWith(previous, StringComparison.Ordinal)) { return Raw(raw, key.MessageId); }
        _emitted[key] = full;
        var suffix = full[previous.Length..];
        return WithRaw(key.Reasoning ? new TextReasoningContent(suffix) : new TextContent(suffix), raw, key.MessageId);
    }

    private List<AgentResponseUpdate> StoredTool(string messageId, SessionMessageAssistantTool tool)
    {
        var key = (messageId, tool.Id);
        _toolNames[key] = tool.Name;
        var updates = new List<AgentResponseUpdate>();
        var input = tool.State switch
        {
            SessionMessageToolStateRunning running => running.Input,
            SessionMessageToolStateCompleted completed => completed.Input,
            // A tool that failed while its input streamed never ran, so no call was ever made.
            SessionMessageToolStateError error when tool.Time.Ran is not null => error.Input,
            _ => null,
        };
        if (input is not null)
        {
            _inputs[key] = input;
            if (_calls.Add(key)) { updates.Add(WithRaw(Call(key, input), tool, messageId)); }
        }
        var result = tool.State switch
        {
            SessionMessageToolStateCompleted completed => ToolText(completed.Content),
            SessionMessageToolStateError error => error.Error.Message,
            _ => null,
        };
        if (result is not null && _results.Add(key)) { updates.Add(WithRaw(MapResult(messageId, tool.Id, result), tool, messageId)); }
        if (input is null && result is null && tool.State is not SessionMessageToolStateStreaming) { updates.Add(Raw(tool.State, messageId)); }
        return updates;
    }

    private List<AgentResponseUpdate> LiveResult((string MessageId, string CallId) key, string result, IEvent current)
    {
        if (_recovering && _results.Contains(key)) { return [Raw(current, key.MessageId)]; }
        var updates = new List<AgentResponseUpdate>();
        if (_recovering && !_calls.Contains(key) && _inputs.TryGetValue(key, out var input))
        {
            _calls.Add(key);
            updates.Add(WithRaw(Call(key, input), current, key.MessageId));
        }
        _results.Add(key);
        updates.Add(WithRaw(MapResult(key.MessageId, key.CallId, result), current, key.MessageId));
        return updates;
    }

    private AgentResponseUpdate Raw(object raw, string? messageId) => WithRaw(new AIContent(), raw, messageId);

    private AgentResponseUpdate WithRaw(AIContent content, object raw, string? messageId)
    {
        content.RawRepresentation = raw;
        return CreateUpdate(content, content is FunctionResultContent ? ChatRole.Tool : ChatRole.Assistant, messageId);
    }

    private static string ToolText(IReadOnlyList<IToolContent> content) => string.Join('\n', content.OfType<ToolTextContent>().Select(item => item.Text));

    private static UsageContent Usage(TokenUsageInfo tokens) => new(new UsageDetails
    {
        InputTokenCount = (long)Math.Round(tokens.Input, MidpointRounding.AwayFromZero),
        OutputTokenCount = (long)Math.Round(tokens.Output, MidpointRounding.AwayFromZero),
        ReasoningTokenCount = (long)Math.Round(tokens.Reasoning, MidpointRounding.AwayFromZero),
        CachedInputTokenCount = (long)Math.Round(tokens.Cache.Read, MidpointRounding.AwayFromZero),
    });

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
                ToolText(succeeded.Data.Content)),
            SessionToolFailed failed => MapResult(failed.Data.AssistantMessageId, failed.Data.Id, failed.Data.Error.Message),
            SessionStepEnded ended => Usage(ended.Data.Tokens),
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
        => Call((data.AssistantMessageId, data.Id), data.Input);

    private FunctionCallContent Call((string MessageId, string CallId) key, IReadOnlyDictionary<string, JsonElement> input)
    {
        var name = _toolNames.TryGetValue(key, out var startedName) ? startedName : "tool";
        _openCalls[key] = name;
        return new FunctionCallContent(key.CallId, name,
            input.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.Ordinal));
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
