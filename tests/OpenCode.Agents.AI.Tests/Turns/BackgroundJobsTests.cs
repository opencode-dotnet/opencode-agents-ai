using System.Globalization;
using System.Threading.Channels;
using NSubstitute;
using OpenCode.Agents.AI.Internal.Threading;
using OpenCode.Agents.AI.Permissions;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Agents.AI.Tests.Turns;

public sealed class BackgroundJobsTests
{
    private readonly FakeOpenCode _fake = new();
    private readonly List<string> _trace = [];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HasPendingAsync_Should_Select_This_Prompts_Jobs_And_Remember_Their_Sessions(bool declined)
    {
        _fake.AddSession("old_child", FakeOpenCode.SessionId);
        _fake.AddSession("child_boundary", FakeOpenCode.SessionId);
        _fake.AddSession("nested_owner", FakeOpenCode.SessionId);
        _fake.AddSession("child_nested", "nested_owner");
        var queries = new List<(string? SessionId, bool? Descendants, object? Kind, object? Status)>();
#pragma warning disable OPENCODE001 // JobSubagent, JobStatus and ExperimentalClient.ListJobsAsync arrange the selected snapshots.
        var boundary = TurnEvents.SubagentJob("boundary", FakeOpenCode.SessionId, "child_boundary", 100, JobStatus.Cancelled);
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<ExperimentalJobListRequest>();
                queries.Add((query.SessionId, query.Descendants, query.Kind, query.Status));
                return new ExperimentalJobListResponse
                {
                    Status = 200,
                    Jobs = declined ? [boundary] : [
                        TurnEvents.SubagentJob("old", FakeOpenCode.SessionId, "old_child", 99, JobStatus.Running), boundary,
                        TurnEvents.SubagentJob("nested", "nested_owner", "child_nested", 101, JobStatus.Completed),
                        TurnEvents.ShellJob("shell", FakeOpenCode.SessionId, 101, JobStatus.Error),
                        TurnEvents.ImageJob("image", FakeOpenCode.SessionId, 101, JobStatus.Cancelled),
                    ],
                };
            });
#pragma warning restore OPENCODE001 // JobSubagent, JobStatus and ExperimentalClient.ListJobsAsync arrangement ends here.
        var responder = Responder(new SessionPermissionReplyRequest { Decision = PermissionReply.Reject });
        if (declined)
        {
            await responder.RespondAsync(TurnEvents.PendingPermission("per_child", "child_boundary"), CancellationToken.None);
        }

        TraceInboxes(FakeOpenCode.SessionId, "old_child", "child_boundary", "nested_owner", "child_nested");
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder);
        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsEqualTo(!declined);
        await Assert.That(queries.Count).IsEqualTo(1);
        var query = queries[0];
        await Assert.That(query.SessionId).IsEqualTo(FakeOpenCode.SessionId);
        await Assert.That(query.Descendants).IsTrue();
        await Assert.That(query.Kind).IsNull();
        await Assert.That(query.Status).IsNull();
        _fake.Jobs([]);

        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsFalse();

        string[] expected = declined
            ? ["inbox:ses_test", "inbox:child_boundary", "inbox:ses_test", "inbox:child_boundary"]
            : ["inbox:ses_test", "inbox:child_boundary", "inbox:nested_owner", "inbox:child_nested"];
        await Assert.That(_trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task HasPendingAsync_Should_Check_Job_Then_Inbox_Then_Active()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var foreign = _fake.AddSession("foreign", parentId: null);
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, Responder());
        var cycle = 0;
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync and JobStatus.Running arrange the stage transitions.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _trace.Add("jobs");
                cycle++;
                return new ExperimentalJobListResponse
                {
                    Status = 200,
                    Jobs = cycle == 1 ? [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)] : [],
                };
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync and JobStatus.Running arrangement ends here.
        TraceInboxes(FakeOpenCode.SessionId, "child");
        child.ListInboxAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _trace.Add("inbox:child");
            return new SessionInboxListResponse { Status = 200, Inbox = cycle == 2 ? [TurnEvents.QueuedWake("wake", "child")] : [] };
        });
        TraceActive(() => cycle == 3 ? Active("foreign", "child") : Active("foreign"));

        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsFalse();

        await Assert.That(_trace).IsEquivalentTo([
            "jobs", "jobs", "inbox:ses_test", "inbox:child",
            "jobs", "inbox:ses_test", "inbox:child", "active",
            "jobs", "inbox:ses_test", "inbox:child", "active",
        ], CollectionOrdering.Matching);
        await child.Received(1).GetAsync(null, CancellationToken.None);
        await foreign.Received(1).GetAsync(null, CancellationToken.None);
        await _fake.Session.DidNotReceive().ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("jobs")]
    [Arguments("inbox")]
    [Arguments("active")]
    public async Task HasPendingAsync_Should_Surface_Query_Failures(string stage)
    {
        var failure = new OpenCodeTransportException(stage + " failed");
        TraceQueries();
        if (stage == "jobs")
        {
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync returns the controlled query failure.
            _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _trace.Add("jobs"); return Task.FromException<ExperimentalJobListResponse>(failure); });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync arrangement ends here.
        }
        else if (stage == "inbox")
        {
            _fake.Session.ListInboxAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _trace.Add("inbox:ses_test"); return Task.FromException<SessionInboxListResponse>(failure); });
        }
        else
        {
            _fake.Sessions.GetActiveAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _trace.Add("active"); return Task.FromException<SessionActiveResponse>(failure); });
        }

        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, Responder());
        var error = await Assert.ThrowsAsync<OpenCodeTransportException>(async () => await jobs.HasPendingAsync(CancellationToken.None));

        await Assert.That(error).IsSameReferenceAs(failure);
        string[] expected = stage switch { "jobs" => ["jobs"], "inbox" => ["jobs", "inbox:ses_test"], _ => ["jobs", "inbox:ses_test", "active"] };
        await Assert.That(_trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task HasPendingAsync_Should_Refuse_A_Job_Without_Selection_Fields()
    {
        _fake.Jobs([TurnEvents.UnknownJob("future")]);
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, Responder());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await jobs.HasPendingAsync(CancellationToken.None));

        await Assert.That(error?.Message).IsEqualTo("Background job kind 'future' does not expose the session and start time needed to determine completion.");
    }

    [Test]
    [Arguments("running")]
    [Arguments("cancelled")]
    [Arguments("nested")]
    [Arguments("shell")]
    [Arguments("image")]
    [Arguments("other")]
    [Arguments("restart")]
    [Arguments("root-start")]
    public async Task WaitAsync_Should_Exclude_A_Child_After_This_Runs_Accepted_Message_Less_Reject(string scenario)
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        _fake.AddSession("grandchild", "child");
        _fake.AddSession("other", FakeOpenCode.SessionId);
        var decline = new SessionPermissionReplyRequest { Decision = PermissionReply.Reject };
        var once = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) => new(request.Id == "decline" ? decline : once));
        await responder.RespondAsync(TurnEvents.PendingPermission("decline", "child"), CancellationToken.None);
        await Assert.That(responder.HasRejectedWithoutMessage("child")).IsTrue();
        TraceQueries();
#pragma warning disable OPENCODE001 // JobSubagent, JobShell, JobImage and JobStatus arrange the retained rows.
        ListJobs(_ => scenario switch
        {
            "nested" => [TurnEvents.SubagentJob("nested", "child", "grandchild", 100, JobStatus.Running)],
            "shell" => [TurnEvents.ShellJob("shell", "child", 100, JobStatus.Running)],
            "image" => [TurnEvents.ImageJob("image", "child", 100, JobStatus.Running)],
            "other" => [TurnEvents.SubagentJob("other", FakeOpenCode.SessionId, "other", 100, JobStatus.Running)],
            _ => [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, scenario == "cancelled" ? JobStatus.Cancelled : JobStatus.Running)],
        });
#pragma warning restore OPENCODE001 // JobSubagent, JobShell, JobImage and JobStatus arrangement ends here.
        TraceInboxes("child");
        if (scenario == "restart")
        {
            _fake.Emit(TurnEvents.ExecutionStarted("child"));
        }
        else if (scenario == "root-start")
        {
            _fake.Emit(TurnEvents.ExecutionStarted(FakeOpenCode.SessionId));
        }

        // Finite input is the test-level cancellation guard: retained rows fail at the EOF oracle, never a clock.
        _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
        _fake.EndBus();
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder);
        var error = await WaitOutcomeAsync(jobs);

        if (scenario is "running" or "cancelled" or "root-start")
        {
            await Assert.That(error).IsNull();
            await Assert.That(_trace).IsEquivalentTo(["jobs:1", "inbox:ses_test", "inbox:child", "active"], CollectionOrdering.Matching);
        }
        else
        {
            await Assert.That(error?.Message).IsEqualTo(BackgroundJobs.PendingWorkEofMessage);
        }

        await Assert.That(responder.HasRejectedWithoutMessage("child")).IsEqualTo(scenario != "restart");
        await child.Received(1).ReplyToPermissionAsync("decline", decline, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", once, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WaitAsync_Should_Recheck_When_A_Start_Clears_A_Decline_During_The_Check()
    {
        _fake.AddSession("child", FakeOpenCode.SessionId);
        var checking = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new List<TaskCompletionSource<SessionActiveResponse>>();
        var decline = new SessionPermissionReplyRequest { Decision = PermissionReply.Reject };
        var once = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            if (request.Id == "checkpoint") { _fake.EndBus(); }
            return new(request.Id == "decline" ? decline : once);
        });
        await responder.RespondAsync(TurnEvents.PendingPermission("decline", "child"), CancellationToken.None);
        TraceInboxes(FakeOpenCode.SessionId, "child");
#pragma warning disable OPENCODE001 // JobStatus.Running supplies the retained JobSubagent snapshot.
        ListJobs(_ => [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)]);
#pragma warning restore OPENCODE001 // JobStatus.Running and JobSubagent arrangement ends here.
        _fake.Sessions.GetActiveAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _trace.Add("active");
            var result = new TaskCompletionSource<SessionActiveResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            snapshots.Add(result);
            checking.TrySetResult(true);
            return result.Task;
        });
        await using var pump = EventPump.Start(_fake.Client.Events, CancellationToken.None);
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder);
        var outcome = WaitOutcomeAsync(jobs, pump.Reader, CancellationToken.None);
        try
        {
            await Assert.That(await Task.WhenAny(checking.Task, outcome)).IsSameReferenceAs(checking.Task);
            await Assert.That(responder.HasRejectedWithoutMessage("child")).IsTrue();
            _fake.Emit(TurnEvents.ExecutionStarted("child"));
            await Assert.That(await pump.Reader.WaitToReadAsync()).IsTrue();
            _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
            foreach (var snapshot in snapshots) { snapshot.TrySetResult(new SessionActiveResponse { Status = 200, Active = Active() }); }
            var error = await outcome;
            await Assert.That(_trace).IsEquivalentTo(["jobs:1", "inbox:ses_test", "inbox:child", "active", "jobs:2"], CollectionOrdering.Matching);
            await Assert.That(error?.Message).IsEqualTo(BackgroundJobs.PendingWorkEofMessage);
            await Assert.That(responder.HasRejectedWithoutMessage("child")).IsFalse();
        }
        finally
        {
            foreach (var snapshot in snapshots) { snapshot.TrySetResult(new SessionActiveResponse { Status = 200, Active = Active() }); }
            _fake.EndBus();
            await outcome;
        }

        await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", once, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WaitAsync_Should_Exclude_A_Live_Decline_Without_A_Later_Status_Event()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable OPENCODE001 // JobStatus.Running and TaskCompletionSource<ExperimentalSessionWaitResponse> arrange the live decline.
        ListJobs(_ => [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)]);
        var releases = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
        HoldIdleWait(entered, releases, trace: false);
#pragma warning restore OPENCODE001 // JobStatus.Running and TaskCompletionSource<ExperimentalSessionWaitResponse> arrangement ends here.
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Reject };
        child.ReplyToPermissionAsync("decline", reply, Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _fake.Emit(TurnEvents.Succeeded("child"));
            _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
            return new SessionPermissionReplyResponse { Status = 204 };
        });
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            if (request.Id == "checkpoint")
            {
                ReleaseIdleWait(releases);
                return new(new SessionPermissionReplyRequest { Decision = PermissionReply.Once });
            }

            return new(reply);
        });
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder);
        var outcome = WaitOutcomeAsync(jobs);
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            _fake.Emit(TurnEvents.PermissionAsked("decline", "child"));
            await Assert.That(await outcome).IsNull();
        }
        finally
        {
            ReleaseIdleWait(releases);
            _fake.EndBus();
            await outcome;
        }

        await Assert.That(responder.HasRejectedWithoutMessage("child")).IsTrue();
        await child.Received(1).ReplyToPermissionAsync("decline", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
        await Assert.That(_trace).IsEquivalentTo(["jobs:1", "jobs:2"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("running")]
    [Arguments("cancelled")]
    [Arguments("external")]
    public async Task WaitAsync_Should_Keep_Waiting_After_A_Reject_With_A_Message(string scenario)
    {
        _fake.AddSession("child", FakeOpenCode.SessionId);
#pragma warning disable OPENCODE001 // JobStatus arranges retained JobSubagent statuses before notification.
        ListJobs(checks =>
            {
                if (checks == 1)
                {
                    _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
                }

                var status = scenario == "running" ? JobStatus.Running : JobStatus.Cancelled;
                return checks == 1 ? [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, status)] : [];
            });
#pragma warning restore OPENCODE001 // JobStatus and JobSubagent arrangement ends here.
        TraceInboxes(FakeOpenCode.SessionId, "child");
        var rejection = new SessionPermissionReplyRequest { Decision = PermissionReply.Reject, Message = "Continue without this action" };
        var checkpointReply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, async (request, _) =>
        {
            if (request.Id == "checkpoint")
            {
                await Assert.That(_trace).IsEquivalentTo(["jobs:1"], CollectionOrdering.Matching);
                if (scenario == "external")
                {
                    _fake.Emit(TurnEvents.Interrupted(SessionExecutionInterruptedDataReason.Shutdown, "child"));
                }
                else
                {
                    _fake.Emit(TurnEvents.Succeeded("child"));
                }
            }

            return request.Id == "checkpoint" ? checkpointReply : rejection;
        });
        if (scenario != "external")
        {
            await responder.RespondAsync(TurnEvents.PendingPermission("decline", "child"), CancellationToken.None);
        }

        var error = await WaitOutcomeAsync(new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder));

        await Assert.That(error).IsNull();
        await Assert.That(responder.HasRejectedWithoutMessage("child")).IsFalse();
        await Assert.That(_trace).IsEquivalentTo(["jobs:1", "jobs:2", "inbox:ses_test", "inbox:child"], CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", checkpointReply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
        if (scenario != "external")
        {
            await _fake.Sessions.GetSessionClient("child").Received(1).ReplyToPermissionAsync("decline", rejection, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitAsync_Should_Answer_A_Late_Descendant_Ask_While_The_Root_Idle_Wait_Is_Pending(bool noCallback)
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var foreign = _fake.AddSession("foreign", parentId: null);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable OPENCODE001 // TaskCompletionSource<ExperimentalSessionWaitResponse> arranges the held root idle wait.
        var releases = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
        HoldIdleWait(entered, releases, trace: false);
#pragma warning restore OPENCODE001 // TaskCompletionSource<ExperimentalSessionWaitResponse> arrangement ends here.
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        child.ReplyToPermissionAsync("late", Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _fake.Emit(TurnEvents.Succeeded("child"));
                ReleaseIdleWait(releases);
                return new SessionPermissionReplyResponse { Status = 204 };
            });
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, noCallback ? Responder() : Responder(reply));
        var outcome = WaitOutcomeAsync(jobs);
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            _fake.Emit(TurnEvents.PermissionAsked("foreign", "foreign"));
            _fake.Emit(TurnEvents.PermissionAsked("late", "child"));
            await Assert.That(await outcome).IsNull();
        }
        finally
        {
            ReleaseIdleWait(releases);
            _fake.EndBus();
            await outcome;
        }

        var actual = (SessionPermissionReplyRequest)child.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(SessionClient.ReplyToPermissionAsync)).GetArguments()[1]!; // The SDK requires a body.
        var expected = noCallback
            ? new SessionPermissionReplyRequest { Decision = PermissionReply.Reject, Message = PermissionResponder.NoHandlerMessage }
            : reply;
        await Assert.That(actual).IsEqualTo(expected);
        if (!noCallback)
        {
            await Assert.That(actual).IsSameReferenceAs(reply);
        }

        await foreign.DidNotReceive().ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WaitAsync_Should_Recheck_When_A_Job_Ends_During_The_Query()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var info = await child.GetAsync();
        var querying = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable OPENCODE001 // ExperimentalJobListResponse and JobStatus.Running arrange the gated query results.
        var queries = new List<TaskCompletionSource<ExperimentalJobListResponse>>();
        var pending = new ExperimentalJobListResponse { Status = 200, Jobs = [TurnEvents.ShellJob("job", FakeOpenCode.SessionId, 100, JobStatus.Running)] };
        var quiet = new ExperimentalJobListResponse { Status = 200, Jobs = [] };
#pragma warning restore OPENCODE001 // ExperimentalJobListResponse and JobStatus.Running arrangement ends here.
        var lookups = new List<TaskCompletionSource<SessionResponse>>();
#pragma warning disable OPENCODE001 // TaskCompletionSource<ExperimentalJobListResponse> holds each actual query response.
        ListJobs(checks =>
            {
                var result = new TaskCompletionSource<ExperimentalJobListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                queries.Add(result);
                if (checks > 1) { result.TrySetResult(quiet); }
                querying.TrySetResult(true);
                return result.Task;
            });
#pragma warning restore OPENCODE001 // TaskCompletionSource<ExperimentalJobListResponse> arrangement ends here.
        child.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var result = new TaskCompletionSource<SessionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lookups.Add(result);
            resolving.TrySetResult(true);
            return result.Task;
        });
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        await using var pump = EventPump.Start(_fake.Client.Events, CancellationToken.None);
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, Responder(reply));
        var outcome = WaitOutcomeAsync(jobs, pump.Reader, CancellationToken.None);
        try
        {
            await Assert.That(await Task.WhenAny(querying.Task, outcome)).IsSameReferenceAs(querying.Task);
            _fake.Emit(TurnEvents.Succeeded("child"));
            queries[0].TrySetResult(pending);
            await Assert.That(await Task.WhenAny(resolving.Task, outcome)).IsSameReferenceAs(resolving.Task);
            _fake.Emit(TurnEvents.PermissionAsked("marker"));
            // The terminal has been read; readiness now proves the following marker is buffered.
            await Assert.That(await pump.Reader.WaitToReadAsync()).IsTrue();
            _fake.EndBus();
            foreach (var lookup in lookups) { lookup.TrySetResult(info); }
            await Assert.That(await outcome).IsNull();
        }
        finally
        {
            foreach (var lookup in lookups) { lookup.TrySetResult(info); }
            foreach (var query in queries) { query.TrySetResult(quiet); }
            _fake.EndBus();
            await outcome;
        }

        await Assert.That(_trace).IsEquivalentTo(["jobs:1", "jobs:2"], CollectionOrdering.Matching);
        await AssertMarkerReplyAsync(reply);
    }

    [Test]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    public async Task WaitAsync_Should_Not_Repeat_A_Check_Without_A_New_Terminal(bool duringQuery, bool secondTerminal, bool foreignOnly)
    {
        _fake.AddSession("child", FakeOpenCode.SessionId);
        _fake.AddSession("foreign", parentId: null);
        var t2 = false;
#pragma warning disable OPENCODE001 // JobStatus.Running arranges a pending JobSubagent until a qualifying terminal.
        ListJobs(checks =>
            {
                if (checks == 3 && !t2)
                {
                    throw new InvalidOperationException("A stale terminal authorized a third check.");
                }

                if (checks == 1 && duringQuery)
                {
                    _fake.Emit(TurnEvents.Succeeded("child"));
                }
                else if (checks == 1 && foreignOnly)
                {
                    _fake.Emit(TurnEvents.Succeeded("foreign"));
                }

                if (checks == (duringQuery ? 2 : 1))
                {
                    _fake.Emit(TurnEvents.PermissionAsked("marker"));
                }

                var quiet = checks == (duringQuery ? 3 : 2);
                return quiet ? [] : [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)];
            });
#pragma warning restore OPENCODE001 // JobStatus.Running and JobSubagent arrangement ends here.
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, async (_, _) =>
        {
            string[] checkpoint = duringQuery ? ["jobs:1", "jobs:2"] : ["jobs:1"];
            await Assert.That(_trace).IsEquivalentTo(checkpoint, CollectionOrdering.Matching);
            if (secondTerminal)
            {
                t2 = true;
                _fake.Emit(TurnEvents.Succeeded("child"));
            }

            _fake.EndBus();
            return reply;
        });

        var error = await WaitOutcomeAsync(new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder));

        string[] expected = (foreignOnly, duringQuery, secondTerminal) switch
        {
            (true, _, _) => ["jobs:1"],
            (_, true, true) => ["jobs:1", "jobs:2", "jobs:3"],
            _ => ["jobs:1", "jobs:2"],
        };
        await Assert.That(_trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
        if (secondTerminal)
        {
            await Assert.That(error).IsNull();
        }
        else
        {
            await Assert.That(error?.Message).IsEqualTo(BackgroundJobs.PendingWorkEofMessage);
        }

        await AssertMarkerReplyAsync(reply);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitAsync_Should_Handle_A_Defensive_End_Of_Stream(bool pending)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable OPENCODE001 // TaskCompletionSource<ExperimentalSessionWaitResponse> arranges the defensive EOF wait.
        var releases = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
        HoldIdleWait(entered, releases, trace: true);
#pragma warning restore OPENCODE001 // TaskCompletionSource<ExperimentalSessionWaitResponse> arrangement ends here.
        TraceQueries();
        if (pending)
        {
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync and JobStatus.Running arrange a retained shell row.
            _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _trace.Add("jobs"); return new ExperimentalJobListResponse { Status = 200, Jobs = [TurnEvents.ShellJob("shell", FakeOpenCode.SessionId, 100, JobStatus.Running)] }; });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync and JobStatus.Running arrangement ends here.
        }

        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) =>
        {
            _fake.EndBus();
            ReleaseIdleWait(releases);
            return new(reply);
        });
        var outcome = WaitOutcomeAsync(new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder));
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
            var error = await outcome;
            if (pending)
            {
                await Assert.That(error?.Message).IsEqualTo(BackgroundJobs.PendingWorkEofMessage);
            }
            else
            {
                await Assert.That(error).IsNull();
            }
        }
        finally
        {
            ReleaseIdleWait(releases);
            _fake.EndBus();
            await outcome;
        }

        string[] expected = pending ? ["wait", "jobs"] : ["wait", "jobs", "inbox:ses_test", "active"];
        await Assert.That(_trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(BackgroundJobs.PendingWorkEofMessage).IsEqualTo("The event stream ended before background work settled.");
        await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WaitAsync_Should_Wake_For_A_Remembered_Session_When_The_Ownership_Lookup_Failed()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        _fake.AddSession("foreign", parentId: null);
        child.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SessionResponse>(new OpenCodeTransportException("ancestry unavailable")));
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
#pragma warning disable OPENCODE001 // JobStatus.Running arranges the remembered JobSubagent child.
        ListJobs(checks =>
            {
                if (checks == 1)
                {
                    _fake.Emit(TurnEvents.Succeeded("foreign"));
                    _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
                }

                return checks == 1 ? [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)] : [];
            });
#pragma warning restore OPENCODE001 // JobStatus.Running and JobSubagent arrangement ends here.
        var checkpoint = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, async (_, _) =>
        {
            await Assert.That(_trace).IsEquivalentTo(["jobs:1"], CollectionOrdering.Matching);
            _fake.Emit(TurnEvents.PermissionAsked("unowned", "child"));
            _fake.Emit(TurnEvents.Succeeded("child"));
            _fake.EndBus();
            return reply;
        });
        await Assert.That(await checkpoint.OwnsAsync("child", CancellationToken.None)).IsFalse();

        var error = await WaitOutcomeAsync(new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, checkpoint));

        await Assert.That(error).IsNull();
        await Assert.That(_trace).IsEquivalentTo(["jobs:1", "jobs:2"], CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
        await child.DidNotReceive().ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("caller")]
    [Arguments("callback")]
    [Arguments("wait")]
    [Arguments("channel")]
    public async Task WaitAsync_Should_Cancel_And_Join_Its_Started_Tasks(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var callerFailure = new OperationCanceledException("cancelled background query", cancellation.Token);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable OPENCODE001 // TaskCompletionSource<ExperimentalSessionWaitResponse> holds the SDK wait responses.
        var waits = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
#pragma warning restore OPENCODE001 // TaskCompletionSource<ExperimentalSessionWaitResponse> declaration ends here.
        var joined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken waitToken = default;
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync arranges the owned HTTP wait and its cancellation.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                waitToken = call.Arg<CancellationToken>();
                var idle = new TaskCompletionSource<ExperimentalSessionWaitResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Add(idle);
                using var registration = waitToken.Register(() => idle.TrySetCanceled(waitToken));
                entered.TrySetResult(true);
                try { return await idle.Task; }
                finally { joined.TrySetResult(true); }
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync arrangement ends here.
        if (scenario == "caller")
        {
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync supplies a known caller-cancellation exception.
            _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns<Task<ExperimentalJobListResponse>>(async _ => { await cancellation.CancelOnWorkerAsync(); throw callerFailure; });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync arrangement ends here.
        }
        var failure = new OpenCodeTransportException("controlled failure");
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            if (request.Id == "failure")
            {
                throw failure;
            }

            if (scenario == "caller") { ReleaseIdleWait(waits); }
            else if (scenario == "callback") { _fake.Emit(TurnEvents.PermissionAsked("failure")); }
            else if (scenario == "wait") { foreach (var idle in waits) { idle.TrySetException(failure); } }
            else { _fake.FailBus(failure); }
            return new(reply);
        });
        var outcome = WaitOutcomeAsync(new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder), cancellation.Token);
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            _fake.Emit(TurnEvents.PermissionAsked("marker"));
            var error = await outcome;
            if (scenario == "caller")
            {
                var cancelled = (await Assert.That(error).IsTypeOf<OperationCanceledException>())!; // The type assertion proves cancellation exists.
                await Assert.That(cancelled.CancellationToken).IsEqualTo(cancellation.Token);
                await Assert.That(cancelled.InnerException).IsSameReferenceAs(callerFailure);
                await Assert.That(cancelled.Message).IsEqualTo(callerFailure.Message);
            }
            else
            {
                await Assert.That(error).IsSameReferenceAs(failure);
            }

            await Assert.That(joined.Task.Status).IsEqualTo(TaskStatus.RanToCompletion);
            await Assert.That(waitToken.IsCancellationRequested).IsTrue();
        }
        finally
        {
            await cancellation.CancelOnWorkerAsync();
            await outcome;
        }

        await AssertMarkerReplyAsync(reply);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitAsync_Should_Preserve_An_Independent_Cleanup_Failure(bool internalCancellation)
    {
        var primary = new InvalidOperationException("callback failed");
        var cleanup = new OpenCodeTransportException("wait cleanup failed");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync arranges the independent cleanup outcome.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                var release = new TaskCompletionSource<ExperimentalSessionWaitResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() =>
                {
                    if (internalCancellation) { release.TrySetCanceled(token); }
                    else { release.TrySetException(cleanup); }
                });
                entered.TrySetResult(true);
                return await release.Task;
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync arrangement ends here.
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) => throw primary);
        var outcome = WaitOutcomeAsync(new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder));
        await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
        _fake.Emit(TurnEvents.PermissionAsked("failure"));
        var error = await outcome;

        if (internalCancellation)
        {
            await Assert.That(error).IsSameReferenceAs(primary);
        }
        else
        {
            var aggregate = (await Assert.That(error).IsTypeOf<AggregateException>())!; // The type assertion proves the aggregate exists.
            await Assert.That(aggregate.InnerExceptions).IsEquivalentTo(new Exception[] { primary, cleanup }, CollectionOrdering.Matching);
            await Assert.That(aggregate.InnerExceptions[0]).IsSameReferenceAs(primary);
            await Assert.That(aggregate.InnerExceptions[1]).IsSameReferenceAs(cleanup);
        }

        await _fake.Session.Received(1).ReplyToPermissionAsync("failure", Arg.Is<SessionPermissionReplyRequest>(reply => reply.Message.Value == PermissionResponder.HandlerFailedMessage), OpenCodeRequestOptions.NoThrow, CancellationToken.None);
    }

#pragma warning disable OPENCODE001 // ExperimentalSessionWaitResponse appears in the gate parameter declaration.
    private void HoldIdleWait(TaskCompletionSource<bool> entered, List<TaskCompletionSource<ExperimentalSessionWaitResponse>> releases, bool trace)
#pragma warning restore OPENCODE001 // ExperimentalSessionWaitResponse parameter declaration ends here.
    {
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync arranges each test-owned response gate.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var release = new TaskCompletionSource<ExperimentalSessionWaitResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                releases.Add(release);
                if (releases.Count > 1) { release.TrySetResult(new ExperimentalSessionWaitResponse { Status = 204 }); }
                if (trace) { _trace.Add("wait"); }
                entered.TrySetResult(true);
                return release.Task;
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync arrangement ends here.
    }

#pragma warning disable OPENCODE001 // ExperimentalSessionWaitResponse appears in the release parameter declaration.
    private static void ReleaseIdleWait(IEnumerable<TaskCompletionSource<ExperimentalSessionWaitResponse>> releases)
#pragma warning restore OPENCODE001 // ExperimentalSessionWaitResponse parameter declaration ends here.
    {
#pragma warning disable OPENCODE001 // ExperimentalSessionWaitResponse arranges successful releases for the test-owned gates.
        foreach (var release in releases) { release.TrySetResult(new ExperimentalSessionWaitResponse { Status = 204 }); }
#pragma warning restore OPENCODE001 // ExperimentalSessionWaitResponse arrangement ends here.
    }

#pragma warning disable OPENCODE001 // IJobInfo and ExperimentalJobListResponse declare the synchronous snapshot arrangement.
    private void ListJobs(Func<int, IReadOnlyList<IJobInfo>> snapshot)
        => ListJobs(checks => Task.FromResult(new ExperimentalJobListResponse { Status = 200, Jobs = snapshot(checks) }));
#pragma warning restore OPENCODE001 // IJobInfo and ExperimentalJobListResponse arrangement ends here.

#pragma warning disable OPENCODE001 // ExperimentalJobListResponse appears in the asynchronous snapshot parameter.
    private void ListJobs(Func<int, Task<ExperimentalJobListResponse>> snapshot)
#pragma warning restore OPENCODE001 // ExperimentalJobListResponse parameter declaration ends here.
    {
        var checks = 0;
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync arranges the traced asynchronous response.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _trace.Add("jobs:" + (++checks).ToString(CultureInfo.InvariantCulture));
                return snapshot(checks);
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync arrangement ends here.
    }

    private async Task<Exception?> WaitOutcomeAsync(BackgroundJobs jobs, CancellationToken cancellationToken = default)
    {
        await using var pump = EventPump.Start(_fake.Client.Events, cancellationToken);
        return await WaitOutcomeAsync(jobs, pump.Reader, cancellationToken);
    }

    private static async Task<Exception?> WaitOutcomeAsync(BackgroundJobs jobs, ChannelReader<IEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await jobs.WaitAsync(events, cancellationToken);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private async Task AssertMarkerReplyAsync(SessionPermissionReplyRequest reply) =>
        await _fake.Session.Received(1).ReplyToPermissionAsync("marker", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());

    private PermissionResponder Responder(SessionPermissionReplyRequest? reply = null) => new(_fake.Sessions, FakeOpenCode.SessionId,
        reply is null ? null : (_, _) => new(reply));

    private void TraceInboxes(params string[] sessionIds)
    {
        foreach (var id in sessionIds)
        {
            _fake.Sessions.GetSessionClient(id).ListInboxAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _trace.Add("inbox:" + id); return new SessionInboxListResponse { Status = 200, Inbox = [] }; });
        }
    }

    private void TraceQueries()
    {
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync arranges the quiet job-list response.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _trace.Add("jobs"); return new ExperimentalJobListResponse { Status = 200, Jobs = [] }; });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync arrangement ends here.
        TraceInboxes(FakeOpenCode.SessionId);
        TraceActive(() => Active());
    }

    private void TraceActive(Func<Dictionary<string, SessionActive>> snapshot) =>
        _fake.Sessions.GetActiveAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _trace.Add("active"); return new SessionActiveResponse { Status = 200, Active = snapshot() }; });

    private static Dictionary<string, SessionActive> Active(params string[] ids) =>
        ids.ToDictionary(id => id, _ => new SessionActive { Type = "running" }, StringComparer.Ordinal);
}
