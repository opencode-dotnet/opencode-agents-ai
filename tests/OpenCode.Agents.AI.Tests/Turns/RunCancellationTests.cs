using System.Collections.Concurrent;
using NSubstitute;
using OpenCode.Agents.AI.Permissions;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Agents.AI.Tests.Turns;

public sealed class RunCancellationTests
{
    private static readonly string[] SelectedMutations = ["interrupt:ses_test", "get:ses_test", "remove:shell_action@root-directory", "interrupt:child", "remove:second_shell@root-directory"];
    private readonly FakeOpenCode _fake = new();
    private readonly ConcurrentQueue<string> _trace = new();
    private readonly ConcurrentQueue<string> _completed = new();

    [Test]
    public async Task CancelAsync_Should_Stop_The_Selected_Work_In_Returned_Order()
    {
        var releases = new List<TaskCompletionSource<bool>>();
        ArrangeSweep(held: "mutations", releases: releases);
        var result = DeliveredCancellation().CancelAsync();
        try
        {
            await AssertSelectedTraceAsync();
            await Assert.That(_completed).IsEmpty().Because("every mutation must enter before any mutation completes");
            await Assert.That(result.IsCompleted).IsFalse();
        }
        finally { foreach (var release in releases) { release.TrySetResult(true); } await result; }

        await Assert.That(_completed).IsEquivalentTo(["interrupt:ses_test", "remove:shell_action@root-directory", "interrupt:child", "remove:second_shell@root-directory"]);
        await AssertCallsAsync();
    }

    [Test]
    public async Task CancelAsync_Should_Only_Cancel_An_Undelivered_Prompt()
    {
        ArrangeSweep();

        await new RunCancellation(_fake.Client, FakeOpenCode.SessionId, "prompt", 100).CancelAsync();

        await Assert.That(_trace).IsEquivalentTo(["cancel-inbox:prompt"], CollectionOrdering.Matching);
        await _fake.Session.Received(1).CancelInboxAsync("prompt", null, CancellationToken.None);
    }

    [Test]
    [Arguments("running", false)]
    [Arguments("completed", false)]
    [Arguments("error", false)]
    [Arguments("cancelled", false)]
    [Arguments("running", true)]
    public async Task CancelAsync_Should_Not_Use_Completion_Exemptions_To_Skip_Selected_Actions(string status, bool declined)
    {
        ArrangeSweep();
#pragma warning disable OPENCODE001 // ExperimentalClient, ExperimentalJobListRequest, ExperimentalJobListResponse, JobStatus, JobSubagent and JobShell construct selection regardless of completion status.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _trace.Enqueue("jobs");
                return new ExperimentalJobListResponse
                {
                    Status = 200,
                    Jobs = [TurnEvents.SubagentJob("child-job", "owner", "child", 100, new JobStatus(status)),
                    TurnEvents.ShellJob("shell-job", "owner", 100, new JobStatus(status)) with { ShellId = "shell_action" }]
                };
            });
#pragma warning restore OPENCODE001 // ExperimentalClient, ExperimentalJobListRequest, ExperimentalJobListResponse, JobStatus, JobSubagent and JobShell construction ends here.
        if (declined)
        {
            var permissions = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) => new(new SessionPermissionReplyRequest { Decision = PermissionReply.Reject }));
            await permissions.RespondAsync(TurnEvents.PermissionAsked("declined", "child"), CancellationToken.None);
            await Assert.That(permissions.HasRejectedWithoutMessage("child")).IsTrue();
        }

        await DeliveredCancellation().CancelAsync();

        await Assert.That(_trace.Take(2)).IsEquivalentTo(["cancel-inbox:prompt", "jobs"], CollectionOrdering.Matching);
        await Assert.That(_trace.Skip(2)).IsEquivalentTo(["interrupt:ses_test", "interrupt:child", "get:ses_test", "remove:shell_action@root-directory"]);
    }

    [Test]
    [Arguments("cancel-inbox:prompt")]
    [Arguments("jobs")]
    [Arguments("interrupt:ses_test")]
    [Arguments("get:ses_test")]
    [Arguments("remove:shell_action@root-directory")]
    [Arguments("interrupt:child")]
    public async Task CancelAsync_Should_Continue_Independent_Actions_After_A_Failure(string failure)
    {
        ArrangeSweep(failure: failure);
        await DeliveredCancellation().CancelAsync();

        await Assert.That(_trace.Take(2)).IsEquivalentTo(["cancel-inbox:prompt", "jobs"], CollectionOrdering.Matching);
        var mutations = failure switch
        {
            "jobs" => ["interrupt:ses_test"],
            "get:ses_test" => ["interrupt:ses_test", "get:ses_test", "interrupt:child"],
            _ => SelectedMutations,
        };
        await Assert.That(_trace.Skip(2)).IsEquivalentTo(mutations);
    }

    [Test]
    public async Task CancelAsync_Should_Finish_Without_Following_New_Wakes()
    {
        ArrangeSweep();
        _fake.Sessions.GetSessionClient("child").InterruptAsync(Arg.Any<SessionInterruptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _trace.Enqueue("interrupt:child");
                _fake.Emit(TurnEvents.ExecutionStarted("child"));
#pragma warning disable OPENCODE001 // JobStatus and JobSubagent arrange work appearing after the cancellation snapshot.
                _fake.Jobs([TurnEvents.SubagentJob("late", FakeOpenCode.SessionId, "late-child", 102, JobStatus.Running)]);
#pragma warning restore OPENCODE001 // JobStatus and JobSubagent construction ends here.
                return InterruptResponse();
            });
        await DeliveredCancellation().CancelAsync();

        await AssertSelectedTraceAsync();
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync and WaitForSessionAsync verify the finite sweep boundary.
        await _fake.Experimental.Received(1).ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), null, CancellationToken.None);
        await _fake.Experimental.DidNotReceive().WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync and WaitForSessionAsync verification ends here.
    }

    [Test]
    [Arguments("interrupt:ses_test", false, true)]
    [Arguments("interrupt:child", false, true)]
    [Arguments("remove:shell_action@root-directory", false, true)]
    [Arguments("interrupt:ses_test", true, true)]
    [Arguments("interrupt:child", true, true)]
    [Arguments("remove:shell_action@root-directory", true, true)]
    [Arguments("cancel-inbox:prompt", false, true)]
    [Arguments("jobs", false, true)]
    [Arguments("cancel-inbox:prompt", false, false)]
    public async Task CancelAsync_Should_Await_Every_Started_Action(string held, bool fails, bool delivered)
    {
        var releases = new List<TaskCompletionSource<bool>>();
        ArrangeSweep(failure: fails ? held : null, held: held, releases: releases);
        var cancellation = new RunCancellation(_fake.Client, FakeOpenCode.SessionId, "prompt", 100);
        if (delivered) { cancellation.MarkDelivered(); }
        var result = cancellation.CancelAsync();
        try
        {
            if (held == "cancel-inbox:prompt")
            {
                await Assert.That(_trace).IsEquivalentTo(["cancel-inbox:prompt"], CollectionOrdering.Matching);
            }
            else if (held == "jobs")
            {
                await Assert.That(_trace).IsEquivalentTo(["cancel-inbox:prompt", "jobs"], CollectionOrdering.Matching);
            }
            else
            {
                await AssertSelectedTraceAsync();
                await Assert.That(_completed).DoesNotContain(held);
            }
            await Assert.That(result.IsCompleted).IsFalse();
        }
        finally { foreach (var release in releases) { release.TrySetResult(true); } await result; }
        if (held is "cancel-inbox:prompt" or "jobs")
        {
            if (delivered) { await AssertSelectedTraceAsync(); }
            else { await Assert.That(_trace).IsEquivalentTo(["cancel-inbox:prompt"], CollectionOrdering.Matching); }
        }
        else { await Assert.That(_completed).Contains(held); }
    }

    [Test]
    public async Task CancelAsync_Should_Propagate_A_Non_Sdk_Failure()
    {
        ArrangeSweep();
        var failure = new InvalidOperationException("not an SDK failure");
        _fake.Session.InterruptAsync(Arg.Any<SessionInterruptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SessionInterruptResponse>(failure));
        var cancellation = DeliveredCancellation();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(cancellation.CancelAsync);

        await Assert.That(error).IsSameReferenceAs(failure);
        await _fake.Sessions.GetSessionClient("child").Received(1).InterruptAsync(null, null, CancellationToken.None);
        await _fake.Shells.GetShellClient("shell_action").Received(1).RemoveAsync(Arg.Any<ShellRemoveRequest>(), null, CancellationToken.None);
    }

    private void ArrangeSweep(string? failure = null, string? held = null, List<TaskCompletionSource<bool>>? releases = null)
    {
        _fake.AddSession("owner", FakeOpenCode.SessionId, "owner-directory");
        _fake.AddSession("old-child", FakeOpenCode.SessionId);
        _fake.AddSession("child-job", FakeOpenCode.SessionId);
        _fake.AddShell("registry-id");
        _fake.AddShell("second-job");
        _fake.Session.CancelInboxAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => RecordAsync("cancel-inbox:" + call.Arg<string>(), new SessionInboxCancelResponse { Status = 204 }, failure, held, releases));
        ArrangeInterrupt(_fake.Session, "ses_test", failure, held, releases);
        ArrangeInterrupt(_fake.AddSession("child", FakeOpenCode.SessionId), "child", failure, held, releases);
        _fake.Session.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => RecordAsync("get:ses_test", new SessionResponse { Status = 200, Session = FakeOpenCode.SessionInfo(FakeOpenCode.SessionId, null, "root-directory") }, failure));
        foreach (var id in new[] { "shell_action", "second_shell" })
        {
            _fake.AddShell(id).RemoveAsync(Arg.Any<ShellRemoveRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => RecordAsync("remove:" + id + "@" + call.Arg<ShellRemoveRequest>().Location?.Directory, new ShellRemoveResponse { Status = 204 }, failure, held, releases));
        }
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync, ExperimentalJobListResponse, JobStatus, JobSubagent, JobShell, JobImage and UnknownJobInfo arrange the selected snapshot.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => RecordAsync("jobs", new ExperimentalJobListResponse
            {
                Status = 200,
                Jobs = [TurnEvents.SubagentJob("old-job", "owner", "old-child", 99, JobStatus.Running),
                    TurnEvents.ShellJob("registry-id", "owner", 100, JobStatus.Completed) with { ShellId = "shell_action" },
                    TurnEvents.SubagentJob("child-job", "owner", "child", 101, JobStatus.Cancelled),
                    TurnEvents.ImageJob("image", "owner", 100, JobStatus.Running), TurnEvents.UnknownJob("future"),
                    TurnEvents.ShellJob("second-job", "owner", 102, JobStatus.Error) with { ShellId = "second_shell" }],
            }, failure, held, releases));
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync, ExperimentalJobListResponse, JobStatus, JobSubagent, JobShell, JobImage and UnknownJobInfo arrangement ends here.
    }

    private RunCancellation DeliveredCancellation()
    {
        var cancellation = new RunCancellation(_fake.Client, FakeOpenCode.SessionId, "prompt", 100);
        cancellation.MarkDelivered();
        return cancellation;
    }

    private async Task AssertSelectedTraceAsync()
    {
        _fake.Sessions.DidNotReceive().GetSessionClient("old-child");
        _fake.Sessions.DidNotReceive().GetSessionClient("child-job");
        _fake.Shells.DidNotReceive().GetShellClient("registry-id");
        await Assert.That(_trace.Take(2)).IsEquivalentTo(["cancel-inbox:prompt", "jobs"], CollectionOrdering.Matching);
        await Assert.That(_trace.Skip(2)).IsEquivalentTo(SelectedMutations);
    }

    private void ArrangeInterrupt(SessionClient client, string id, string? failure, string? held, List<TaskCompletionSource<bool>>? releases) =>
        client.InterruptAsync(Arg.Any<SessionInterruptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => RecordAsync("interrupt:" + id, InterruptResponse(), failure, held, releases));

    private async Task<T> RecordAsync<T>(string action, T response, string? failure, string? held = null, List<TaskCompletionSource<bool>>? releases = null)
    {
        _trace.Enqueue(action);
        var mutation = action.StartsWith("interrupt:", StringComparison.Ordinal) || action.StartsWith("remove:", StringComparison.Ordinal);
        if (held == action || (held == "mutations" && mutation))
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            (releases ?? throw new InvalidOperationException("A held action requires a release owner.")).Add(release);
            await release.Task;
        }
        if (mutation) { _completed.Enqueue(action); }
        if (action == failure) { throw new OpenCodeTransportException("cleanup timed out: " + action); }
        return response;
    }

    private async Task AssertCallsAsync()
    {
        await _fake.Session.Received(1).CancelInboxAsync("prompt", null, CancellationToken.None);
        await _fake.Session.Received(1).GetAsync(null, CancellationToken.None);
        await _fake.Session.Received(1).InterruptAsync(null, null, CancellationToken.None);
        await _fake.Sessions.GetSessionClient("child").Received(1).InterruptAsync(null, null, CancellationToken.None);
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync and ExperimentalJobListRequest verify snapshot scope and token.
        await _fake.Experimental.Received(1).ListJobsAsync(Arg.Is<ExperimentalJobListRequest>(request => request.SessionId == FakeOpenCode.SessionId && request.Descendants == true && request.Kind == null && request.Status == null), null, CancellationToken.None);
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync and ExperimentalJobListRequest verification ends here.
        foreach (var id in new[] { "shell_action", "second_shell" })
        {
            await _fake.Shells.GetShellClient(id).Received(1).RemoveAsync(Arg.Is<ShellRemoveRequest>(request => request.Location != null && request.Location.Directory == "root-directory"), null, CancellationToken.None);
        }
    }

    private static SessionInterruptResponse InterruptResponse() => new() { Status = 200, Interrupt = new SessionInterruptOutcome { Interrupted = false } };
}
