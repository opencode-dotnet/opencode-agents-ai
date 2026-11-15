using System.Globalization;
using System.Runtime.ExceptionServices;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Permissions;

internal sealed class PermissionResponder(SessionsClient sessions, string sessionId,
    Func<PermissionRequest, CancellationToken, ValueTask<SessionPermissionReplyRequest>>? onPermissionRequest)
{
    public const string NoHandlerMessage = "No permission handler is configured, so the request was rejected. Continue without this action.";
    public const string HandlerFailedMessage = "The permission handler failed, so this request was rejected.";

    private readonly Dictionary<string, bool> _owned = new(StringComparer.Ordinal) { [sessionId] = true };
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);

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
        catch (OpenCodeException)
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
                reply = await onPermissionRequest(request, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The permission callback returned no reply.");
            }
            catch (Exception error)
            {
                try
                {
                    // Settlement must still be attempted after the caller cancels the callback.
                    await ReplyAsync(request, new SessionPermissionReplyRequest { Decision = PermissionReply.Reject, Message = HandlerFailedMessage }, CancellationToken.None).ConfigureAwait(false);
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
        var response = await sessions.GetSessionClient(request.SessionId)
            .ReplyToPermissionAsync(request.Id, reply, OpenCodeRequestOptions.NoThrow, cancellationToken).ConfigureAwait(false);
        // A not-found reply means the request is no longer pending.
        if (response.IsError && response.Error is not PermissionNotFoundError)
        {
            var status = response.Status.ToString(CultureInfo.InvariantCulture);
            throw new OpenCodeApiException($"The permission reply for request '{request.Id}' failed with HTTP {status} ({response.Error?.Tag ?? "untyped"}).",
                response.Status, response.Error, response.RawBody);
        }
    }
}
