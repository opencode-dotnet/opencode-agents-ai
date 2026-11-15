using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class RunCancellation(OpenCodeClient client, string sessionId, string promptId, double admittedAt)
{
    private bool _delivered;

    public void MarkDelivered() => _delivered = true;

    public async Task CancelAsync()
    {
        // The caller already cancelled; cleanup must reach the server independently of that token.
        var root = client.Sessions.GetSessionClient(sessionId);
        await AttemptAsync(() => root.CancelInboxAsync(promptId, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        if (!_delivered) { return; }

#pragma warning disable OPENCODE001 // OpenCodeClient.Experimental, ExperimentalClient, ExperimentalJobListRequest and ExperimentalJobListResponse expose the cancellation snapshot.
        var snapshot = await AttemptAsync(() => client.Experimental.ListJobsAsync(new ExperimentalJobListRequest
        {
            SessionId = sessionId,
            Descendants = true,
        }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
#pragma warning restore OPENCODE001 // OpenCodeClient.Experimental, ExperimentalClient, ExperimentalJobListRequest and ExperimentalJobListResponse snapshot ends here.
        var children = new List<string>();
        var shells = new List<string>();
#pragma warning disable OPENCODE001 // ExperimentalJobListResponse, IJobInfo, JobTime, JobSubagent and JobShell select actionable admission-time descendants.
        if (snapshot is not null)
        {
            foreach (var job in snapshot.Jobs)
            {
                switch (job)
                {
                    case JobSubagent child when child.Time.Started >= admittedAt:
                        children.Add(child.ChildSessionId);
                        break;
                    case JobShell shell when shell.Time.Started >= admittedAt:
                        shells.Add(shell.ShellId);
                        break;
                }
            }
        }
#pragma warning restore OPENCODE001 // ExperimentalJobListResponse, IJobInfo, JobTime, JobSubagent and JobShell selection ends here.
        var actions = new List<Task> { AttemptAsync(() => root.InterruptAsync(cancellationToken: CancellationToken.None)) };
        foreach (var childId in children)
        {
            actions.Add(AttemptAsync(() => client.Sessions.GetSessionClient(childId).InterruptAsync(cancellationToken: CancellationToken.None)));
        }
        if (shells.Count != 0) { actions.Add(RemoveShellsAsync(shells)); }
        await Task.WhenAll(actions).ConfigureAwait(false);
    }

    private async Task RemoveShellsAsync(List<string> shellIds)
    {
        var root = await AttemptAsync(() => client.Sessions.GetSessionClient(sessionId).GetAsync(cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        if (root is not null)
        {
            var removals = shellIds.Select(shellId => AttemptAsync(() => client.Shells.GetShellClient(shellId).RemoveAsync(new ShellRemoveRequest
            {
                Location = new LocationSelector { Directory = root.Session.Location.Directory },
            }, cancellationToken: CancellationToken.None)));
            await Task.WhenAll(removals).ConfigureAwait(false);
        }
    }

    private static async Task<T?> AttemptAsync<T>(Func<Task<T>> action) where T : class
    {
        T? response = null;
        try
        {
            response = await action().ConfigureAwait(false);
        }
        // slopwatch-ignore: SW003 Best-effort cancellation cleanup: the failure is not observed because the caller already holds the cancellation.
        catch (OpenCodeException)
        {
            // With None, the SDK maps its own inactivity timeout to OpenCodeTransportException, not caller cancellation.
        }
        return response;
    }
}
