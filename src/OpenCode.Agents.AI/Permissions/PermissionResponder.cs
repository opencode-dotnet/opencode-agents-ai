using System.Runtime.ExceptionServices;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Permissions;

internal sealed class PermissionResponder(SessionsClient sessions, string sessionId,
    Func<PermissionRequest, CancellationToken, ValueTask<SessionPermissionReplyRequest>>? onPermissionRequest)
{
    public const string NoHandlerMessage = "No permission handler is configured, so the request was rejected. Continue without this action.";

    private readonly Dictionary<string, bool> _owned = new(StringComparer.Ordinal) { [sessionId] = true };
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejectedSessions = new(StringComparer.Ordinal);

    public async ValueTask<bool> OwnsAsync(string requestSessionId, CancellationToken cancellationToken)
    {
        if (_owned.TryGetValue(requestSessionId, out var owned))
        {
            return owned;
        }

        try
        {
            var response = await sessions.GetSessionClient(requestSessionId).GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            owned = response.Session.ParentId is { } parentId && await OwnsAsync(parentId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            owned = false;
        }

        _owned[requestSessionId] = owned;
        return owned;
    }

    public async ValueTask RespondAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        if (!await OwnsAsync(request.SessionId, cancellationToken).ConfigureAwait(false) || !_answered.Add(request.Id))
        {
            return;
        }

        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Reject, Message = NoHandlerMessage };
        if (onPermissionRequest is not null)
        {
            try
            {
                reply = await onPermissionRequest(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                try
                {
                    // Settlement must still be attempted after the caller cancels the callback.
                    await ReplyAsync(request, new SessionPermissionReplyRequest { Decision = PermissionReply.Reject }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rejectionError)
                {
                    throw new AggregateException(error, rejectionError);
                }

                ExceptionDispatchInfo.Capture(error).Throw();
                throw;
            }
        }

        await ReplyAsync(request, reply, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReplyAsync(PermissionRequest request, SessionPermissionReplyRequest reply, CancellationToken cancellationToken)
    {
        try
        {
            await sessions.GetSessionClient(request.SessionId).ReplyToPermissionAsync(request.Id, reply, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeApiException error) when (error.Error is PermissionNotFoundError && _rejectedSessions.Contains(request.SessionId))
        {
            // A successful Reject already settles every pending ask of that session.
            return;
        }

        if (reply.Decision == PermissionReply.Reject)
        {
            _rejectedSessions.Add(request.SessionId);
        }
    }
}
