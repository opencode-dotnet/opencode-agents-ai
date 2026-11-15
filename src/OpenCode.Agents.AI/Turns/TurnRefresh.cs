using OpenCode.Agents.AI.Permissions;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class TurnRefresh(OpenCodeClient client, string sessionId, string promptId,
    PermissionResponder permissions, TurnEventMapper mapper)
{
    public async ValueTask<TurnRefreshResult> RefreshAsync(bool delivered, CancellationToken cancellationToken)
    {
        await permissions.ReconcileLocationAsync(client.Permissions, cancellationToken).ConfigureAwait(false);
        var messages = await ReadMessagesAsync(cancellationToken).ConfigureAwait(false);
        if (delivered || messages is not null) { mapper.BeginRecovery(); }
        return Reconcile(messages);
    }

    public async ValueTask<TurnRefreshResult> ReadOutcomeAsync(CancellationToken cancellationToken) =>
        Reconcile(await ReadMessagesAsync(cancellationToken).ConfigureAwait(false));

    private async ValueTask<List<ISessionMessageInfo>?> ReadMessagesAsync(CancellationToken cancellationToken)
    {
        var session = client.Sessions.GetSessionClient(sessionId);
        var messages = new List<ISessionMessageInfo>();
        string? cursor = null;
        do
        {
            var response = await session.ListMessagesAsync(new SessionMessageListRequest
            {
                Limit = 200,
                Order = cursor is null ? ListOrder.Descending : null,
                Cursor = cursor,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                if (message is SessionMessageUser user && user.Id == promptId)
                {
                    messages.Reverse();
                    return messages;
                }
                messages.Add(message);
            }
            cursor = response.Cursor.Next;
        } while (cursor is not null);
        return null;
    }

    private TurnRefreshResult Reconcile(List<ISessionMessageInfo>? messages)
    {
        if (messages is null) { return new(false, null, [], null); }
        var endIndex = messages.FindIndex(message => message.Type == "idle");
        var end = endIndex < 0 ? null : messages[endIndex];
        var selected = endIndex < 0 ? messages : messages.GetRange(0, endIndex);
        return new(true, end, mapper.Reconcile(selected), end is null ? null : TurnOutcome.Failure(end));
    }
}
