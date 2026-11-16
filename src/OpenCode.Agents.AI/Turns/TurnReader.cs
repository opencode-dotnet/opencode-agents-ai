using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenCode.Agents.AI.Permissions;
using OpenCode.Agents.AI.Telemetry;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class TurnReader(TurnEventMapper mapper, PermissionResponder permissions, TurnRefresh refresh,
    RunCancellation cancellation, RunTags tags, string promptId, string agentId)
{
    private bool _delivered;
    private bool _recovering;
    private bool _ended;
    private ExceptionDispatchInfo? _failure;

    public async IAsyncEnumerable<AgentResponseUpdate> ReadAsync(IAsyncEnumerator<IEvent> events, string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!_ended)
        {
            IReadOnlyList<AgentResponseUpdate> updates;
            try
            {
                if (!await events.MoveNextAsync().ConfigureAwait(false)) { break; }
                updates = await ReadEventAsync(events.Current, sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException cancelled || cancelled.CancellationToken != cancellationToken)
            {
                _failure = ExceptionDispatchInfo.Capture(error);
                break;
            }
            foreach (var update in updates)
            {
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    switch (usage.RawRepresentation)
                    {
                        case SessionStepEnded ended: tags.Cost(ended.Data.Cost); break;
                        case SessionMessageAssistant { Cost: { } cost }: tags.Cost(cost); break;
                    }
                }
                yield return update;
            }
        }
        foreach (var closingResult in mapper.CloseOpenCalls()) { yield return closingResult; }
        _failure?.Throw();
        if (!_ended) { throw new InvalidOperationException("The event stream ended before the turn ended."); }
    }

    private async ValueTask<IReadOnlyList<AgentResponseUpdate>> ReadEventAsync(IEvent current, string sessionId, CancellationToken cancellationToken)
    {
        if (current is PermissionAsked asked)
        {
            await permissions.RespondAsync(asked, cancellationToken).ConfigureAwait(false);
        }
        else if (current is SessionExecutionStarted started) { permissions.OnExecutionStarted(started.Data.SessionId); }

        if (current.Type == "server.connected")
        {
            var recovered = await refresh.RefreshAsync(_delivered, cancellationToken).ConfigureAwait(false);
            _recovering |= _delivered || recovered.Delivered;
            var updates = new List<AgentResponseUpdate>();
            ObserveDelivery(recovered);
            if (_delivered) { updates.AddRange(mapper.MapUpdates(current)); }
            AddRecovered(updates, recovered);
            return updates;
        }
        var eventSessionId = GetEventSessionId(current);
        if (eventSessionId is not null && eventSessionId != sessionId) { return []; }
        if (current is SessionInboxDelivered delivery && delivery.Data.InboxId == promptId)
        {
            _delivered = true;
            cancellation.MarkDelivered();
        }
        if (!_delivered) { return []; }
        if (current is SessionStepStarted stepStarted) { tags.Step(stepStarted.Data.Model); }

        var terminal = eventSessionId == sessionId && TurnOutcome.EndsTurn(current);
        var liveFailure = terminal ? TurnOutcome.Failure(current) : null;
        var result = new List<AgentResponseUpdate>();
        if (_recovering && terminal && !(current.Type == "session.execution.interrupted" && liveFailure is not null))
        {
            var recovered = await refresh.ReadOutcomeAsync(cancellationToken).ConfigureAwait(false);
            ObserveDelivery(recovered);
            AddRecovered(result, recovered);
        }
        if (!_ended)
        {
            result.AddRange(mapper.MapUpdates(current));
            _ended = terminal;
            if (liveFailure is not null) { _failure = ExceptionDispatchInfo.Capture(liveFailure); }
        }
        return result;
    }

    private void ObserveDelivery(TurnRefreshResult recovered)
    {
        if (_delivered && !recovered.Delivered)
        {
            throw new InvalidOperationException("Session error: the admitted prompt is missing from stored messages.");
        }
        _delivered |= recovered.Delivered;
        if (_delivered) { cancellation.MarkDelivered(); }
    }

    private void AddRecovered(List<AgentResponseUpdate> updates, TurnRefreshResult recovered)
    {
        updates.AddRange(recovered.Updates);
        if (recovered.End is { } end)
        {
            updates.Add(StoredEnd(end));
            _ended = true;
            if (recovered.Failure is { } failure) { _failure = ExceptionDispatchInfo.Capture(failure); }
        }
    }

    private AgentResponseUpdate StoredEnd(ISessionMessageInfo end) => new(ChatRole.Assistant, [new AIContent { RawRepresentation = end }])
    {
        AgentId = agentId,
        ResponseId = promptId,
        MessageId = end.Id,
    };

    private static string? GetEventSessionId(IEvent current) => current switch
    {
        ISessionScopedEvent scoped => scoped.SessionId,
        FormCreated created => created.Data.Form.SessionId,
        FormReplied replied => replied.Data.SessionId,
        FormCancelled cancelled => cancelled.Data.SessionId,
        UnknownEvent unknown when unknown.Payload.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("sessionID", out var id)
            && id.ValueKind == JsonValueKind.String => id.GetString(),
        _ => null,
    };
}
