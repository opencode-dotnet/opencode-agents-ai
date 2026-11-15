using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using OpenCode.Agents.AI.Internal.Threading;
using OpenCode.Agents.AI.Permissions;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class BackgroundJobs(OpenCodeClient client, string sessionId, double admittedAt, PermissionResponder permissions)
{
    private readonly HashSet<string> _remembered = new(StringComparer.Ordinal) { sessionId };
    private readonly List<string> _sessions = [sessionId];
    private long _terminalGeneration;
    private bool _ended;

    public async Task WaitAsync(ChannelReader<IEvent> events, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failures = new List<Exception>();
        Task? idle = null;
        Task<bool>? ready = null;
        try
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var generation = _terminalGeneration;
#pragma warning disable OPENCODE001 // This SDK wait observes root idleness, not descendant completion.
                idle = client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: lifetime.Token);
#pragma warning restore OPENCODE001
                while (!idle.IsCompleted && !_ended)
                {
                    await DrainAsync(events, lifetime.Token).ConfigureAwait(false);
                    if (!_ended && !idle.IsCompleted)
                    {
                        ready ??= WaitForEventsAsync(events, lifetime.Token);
                        await Task.WhenAny(idle, ready).ConfigureAwait(false);
                        if (ready.IsCompleted)
                        {
                            _ended |= !await ready.ConfigureAwait(false);
                            ready = null;
                        }
                    }
                }

                await idle.ConfigureAwait(false);
                idle = null;
                await DrainAsync(events, lifetime.Token).ConfigureAwait(false);
                var pending = await HasPendingAsync(lifetime.Token).ConfigureAwait(false);
                await DrainAsync(events, lifetime.Token).ConfigureAwait(false);
                if (ready is { IsCompleted: true })
                {
                    _ended |= !await ready.ConfigureAwait(false);
                    ready = null;
                    await DrainAsync(events, lifetime.Token).ConfigureAwait(false);
                }

                if (_terminalGeneration == generation && !pending) { break; }
                while (_terminalGeneration == generation)
                {
                    ThrowIfEnded();
                    ready ??= WaitForEventsAsync(events, lifetime.Token);
                    _ended |= !await ready.ConfigureAwait(false);
                    ready = null;
                    await DrainAsync(events, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failures.Add(new OperationCanceledException(cancellationToken));
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        finally
        {
            await lifetime.CancelOnWorkerAsync().ConfigureAwait(false);
            try
            {
                if (idle is not null) { await idle.ConfigureAwait(false); }
            }
            catch (Exception error) { RecordCleanupFailure(error, failures, lifetime.Token); }
            try
            {
                if (ready is not null) { await ready.ConfigureAwait(false); }
            }
            catch (Exception error) { RecordCleanupFailure(error, failures, lifetime.Token); }
        }

        ThrowFailures(failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
    }

    private static async Task<bool> WaitForEventsAsync(ChannelReader<IEvent> events, CancellationToken cancellationToken) =>
        await events.WaitToReadAsync(cancellationToken).ConfigureAwait(false);

    private void ThrowIfEnded()
    {
        if (_ended)
        {
            throw new InvalidOperationException("The event stream ended before background work settled.");
        }
    }

    private async Task DrainAsync(ChannelReader<IEvent> events, CancellationToken cancellationToken)
    {
        while (events.TryRead(out var current))
        {
            if (current is PermissionAsked asked)
            {
                await permissions.RespondAsync(asked, cancellationToken).ConfigureAwait(false);
            }
            else if (current is SessionExecutionStarted started)
            {
                permissions.OnExecutionStarted(started.Data.SessionId);
            }

            if (current is ISessionScopedEvent scoped && TurnOutcome.EndsTurn(current)
                && (_remembered.Contains(scoped.SessionId) || await permissions.OwnsAsync(scoped.SessionId, cancellationToken).ConfigureAwait(false)))
            {
                _terminalGeneration++;
            }
        }

        if (!_ended && events.Completion.IsCompleted)
        {
            // The channel's readiness operation preserves its completion fault without a foreign-task join.
            _ended |= !await events.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RecordCleanupFailure(Exception error, List<Exception> failures, CancellationToken cancellationToken)
    {
        if (error is OperationCanceledException cancelled && cancelled.CancellationToken == cancellationToken && cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!failures.Contains(error))
        {
            failures.Add(error);
        }
    }

    public async ValueTask<bool> HasPendingAsync(CancellationToken cancellationToken)
    {
        var pending = false;
#pragma warning disable OPENCODE001 // The pinned SDK exposes background selection only through its experimental API.
        var response = await client.Experimental.ListJobsAsync(new ExperimentalJobListRequest
        {
            SessionId = sessionId,
            Descendants = true,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var job in response.Jobs)
        {
            if (job.Time is not { } time || job.SessionId is not { } owner)
            {
                throw new InvalidOperationException($"Background job kind '{job.Kind}' does not expose the session and start time needed to determine completion.");
            }

            if (time.Started < admittedAt)
            {
                continue;
            }

            Remember(owner);
            if (job is JobSubagent subagent)
            {
                Remember(subagent.ChildSessionId);
                pending |= !permissions.HasRejectedWithoutMessage(subagent.ChildSessionId);
            }
            else
            {
                pending = true;
            }
        }
#pragma warning restore OPENCODE001
        if (pending)
        {
            return true;
        }

        foreach (var id in _sessions)
        {
            var inbox = await client.Sessions.GetSessionClient(id).ListInboxAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (inbox.Inbox.Count != 0)
            {
                return true;
            }
        }

        var active = await client.Sessions.GetActiveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var id in active.Active.Keys)
        {
            if (await permissions.OwnsAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private void Remember(string id)
    {
        if (_remembered.Add(id))
        {
            _sessions.Add(id);
        }
    }
}
