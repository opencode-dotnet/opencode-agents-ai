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

#pragma warning disable OPENCODE001 // The test supplies the experimental job-list records used by this boundary.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HasPendingAsync_Should_Select_This_Prompts_Jobs_And_Remember_Their_Sessions(bool declined)
    {
        _fake.AddSession("old_child", FakeOpenCode.SessionId);
        _fake.AddSession("child_boundary", FakeOpenCode.SessionId);
        _fake.AddSession("nested_owner", FakeOpenCode.SessionId);
        _fake.AddSession("child_nested", "nested_owner");
        _fake.Jobs([
            TurnEvents.SubagentJob("old", FakeOpenCode.SessionId, "old_child", 99, JobStatus.Running),
            TurnEvents.SubagentJob("boundary", FakeOpenCode.SessionId, "child_boundary", 100, JobStatus.Cancelled),
            TurnEvents.SubagentJob("nested", "nested_owner", "child_nested", 101, JobStatus.Completed),
            TurnEvents.ShellJob("shell", FakeOpenCode.SessionId, 101, JobStatus.Error),
            TurnEvents.ImageJob("image", FakeOpenCode.SessionId, 101, JobStatus.Cancelled),
        ]);
        var responder = Responder(new SessionPermissionReplyRequest { Decision = PermissionReply.Reject });
        if (declined)
        {
            await responder.RespondAsync(TurnEvents.PendingPermission("per_child", "child_boundary"), CancellationToken.None);
        }

        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder);
        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsTrue();
        var query = (ExperimentalJobListRequest)_fake.Experimental.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ExperimentalClient.ListJobsAsync)).GetArguments()[0]!; // The list call requires the query.
        await Assert.That(query.SessionId).IsEqualTo(FakeOpenCode.SessionId);
        await Assert.That(query.Descendants).IsTrue();
        await Assert.That(query.Kind).IsNull();
        await Assert.That(query.Status).IsNull();
        _fake.Jobs([]);
        TraceInboxes(FakeOpenCode.SessionId, "old_child", "child_boundary", "nested_owner", "child_nested");

        await Assert.That(await jobs.HasPendingAsync(CancellationToken.None)).IsFalse();

        await Assert.That(_trace).IsEquivalentTo(["inbox:ses_test", "inbox:child_boundary", "inbox:nested_owner", "inbox:child_nested"], CollectionOrdering.Matching);
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // The controlled endpoint transitions exercise experimental selection.
    [Test]
    public async Task HasPendingAsync_Should_Check_Job_Then_Inbox_Then_Active()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        _fake.AddSession("foreign", parentId: null);
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, Responder());
        var cycle = 0;
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
        await _fake.Session.DidNotReceive().ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // The query failure is injected at the published experimental seam.
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
            _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _trace.Add("jobs"); return Task.FromException<ExperimentalJobListResponse>(failure); });
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
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // The SDK unknown carrier is a valid unsupported representation.
    [Test]
    public async Task HasPendingAsync_Should_Refuse_A_Job_Without_Selection_Fields()
    {
        _fake.Jobs([TurnEvents.UnknownJob("future")]);
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, Responder());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await jobs.HasPendingAsync(CancellationToken.None));

        await Assert.That(error?.Message).IsEqualTo("Background job kind 'future' does not expose the session and start time needed to determine completion.");
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // The accepted-decline exception is tested against real SDK job records.
    [Test]
    [Arguments("running")]
    [Arguments("cancelled")]
    [Arguments("nested")]
    [Arguments("shell")]
    [Arguments("other")]
    [Arguments("restart")]
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
        ListJobs(_ => scenario switch
        {
            "nested" => [TurnEvents.SubagentJob("nested", "child", "grandchild", 100, JobStatus.Running)],
            "shell" => [TurnEvents.ShellJob("shell", "child", 100, JobStatus.Running)],
            "other" => [TurnEvents.SubagentJob("other", FakeOpenCode.SessionId, "other", 100, JobStatus.Running)],
            _ => [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, scenario == "cancelled" ? JobStatus.Cancelled : JobStatus.Running)],
        });
        TraceInboxes("child");
        if (scenario == "restart")
        {
            _fake.Emit(ExecutionStarted("child"));
        }

        // Finite input is the test-level cancellation guard: retained rows fail at the EOF oracle, never a clock.
        _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
        _fake.EndBus();
        var jobs = new BackgroundJobs(_fake.Client, FakeOpenCode.SessionId, 100, responder);
        var error = await WaitOutcomeAsync(jobs);

        if (scenario is "running" or "cancelled")
        {
            await Assert.That(error).IsNull();
            await Assert.That(_trace).IsEquivalentTo(["jobs:1", "inbox:ses_test", "inbox:child", "active"], CollectionOrdering.Matching);
        }
        else
        {
            await Assert.That(error?.Message).IsEqualTo("The event stream ended before background work settled.");
        }

        await Assert.That(responder.HasRejectedWithoutMessage("child")).IsEqualTo(scenario != "restart");
        await child.Received(1).ReplyToPermissionAsync("decline", decline, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", once, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // Live permissions and terminals are arranged around the experimental idle wait.
    [Test]
    public async Task WaitAsync_Should_Exclude_A_Live_Decline_Without_A_Later_Status_Event()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        _fake.Jobs([TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)]);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
        HoldIdleWait(entered, releases, trace: false);
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
        await _fake.Experimental.Received(2).ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), null, Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // No status or foreign decline may replace this run's accepted message-less reply.
    [Test]
    [Arguments("running")]
    [Arguments("cancelled")]
    [Arguments("external")]
    public async Task WaitAsync_Should_Keep_Waiting_After_A_Reject_With_A_Message(string scenario)
    {
        _fake.AddSession("child", FakeOpenCode.SessionId);
        ListJobs(checks =>
            {
                if (checks == 1)
                {
                    _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
                }

                var status = scenario == "running" ? JobStatus.Running : JobStatus.Cancelled;
                return checks == 1 ? [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, status)] : [];
            });
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
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // Keep the root HTTP idle wait pending while the responder handles a descendant.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitAsync_Should_Answer_A_Late_Descendant_Ask_While_The_Root_Idle_Wait_Is_Pending(bool noCallback)
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var foreign = _fake.AddSession("foreign", parentId: null);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
        HoldIdleWait(entered, releases, trace: false);
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
        if (noCallback)
        {
            await Assert.That(actual.Decision).IsEqualTo(PermissionReply.Reject);
            await Assert.That(actual.Message.Value).IsEqualTo("No permission handler is configured, so the request was rejected. Continue without this action.");
        }
        else
        {
            await Assert.That(actual).IsSameReferenceAs(reply);
        }

        await foreign.DidNotReceive().ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // A finite event stream distinguishes a lost query-time signal without a timing oracle.
    [Test]
    public async Task WaitAsync_Should_Recheck_When_A_Job_Ends_During_The_Query()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var info = await child.GetAsync();
        var querying = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new List<TaskCompletionSource<ExperimentalJobListResponse>>();
        var lookups = new List<TaskCompletionSource<SessionResponse>>();
        ListJobs(checks =>
            {
                var result = new TaskCompletionSource<ExperimentalJobListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                queries.Add(result);
                if (checks > 1) { result.TrySetResult(new ExperimentalJobListResponse { Status = 200, Jobs = [] }); }
                querying.TrySetResult(true);
                return result.Task;
            });
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
            queries[0].TrySetResult(new ExperimentalJobListResponse { Status = 200, Jobs = [TurnEvents.ShellJob("job", FakeOpenCode.SessionId, 100, JobStatus.Running)] });
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
            foreach (var query in queries) { query.TrySetResult(new ExperimentalJobListResponse { Status = 200, Jobs = [] }); }
            _fake.EndBus();
            await outcome;
        }

        await Assert.That(_trace).IsEquivalentTo(["jobs:1", "jobs:2"], CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("marker", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // Each consumed generation must authorize just the next experimental wait/check cycle.
    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task WaitAsync_Should_Not_Repeat_A_Check_Without_A_New_Terminal(bool duringQuery, bool secondTerminal)
    {
        _fake.AddSession("child", FakeOpenCode.SessionId);
        var t2 = false;
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

                if (checks == (duringQuery ? 2 : 1))
                {
                    _fake.Emit(TurnEvents.PermissionAsked("marker"));
                }

                var quiet = checks == (duringQuery ? 3 : 2);
                return quiet ? [] : [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)];
            });
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

        if (secondTerminal)
        {
            await Assert.That(error).IsNull();
        }
        else
        {
            await Assert.That(error?.Message).IsEqualTo("The event stream ended before background work settled.");
        }

        string[] expected = duringQuery && secondTerminal ? ["jobs:1", "jobs:2", "jobs:3"] : ["jobs:1", "jobs:2"];
        await Assert.That(_trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("marker", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // EOF is defensive, not a production SDK disconnect signal.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitAsync_Should_Handle_A_Defensive_End_Of_Stream(bool pending)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
        HoldIdleWait(entered, releases, trace: true);
        TraceQueries();
        if (pending)
        {
            _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _trace.Add("jobs"); return new ExperimentalJobListResponse { Status = 200, Jobs = [TurnEvents.ShellJob("shell", FakeOpenCode.SessionId, 100, JobStatus.Running)] }; });
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
                await Assert.That(error?.Message).IsEqualTo("The event stream ended before background work settled.");
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
        await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // A remembered terminal remains useful even when SDK ancestry lookup was unavailable.
    [Test]
    public async Task WaitAsync_Should_Wake_For_A_Remembered_Session_When_The_Ownership_Lookup_Failed()
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        _fake.AddSession("foreign", parentId: null);
        child.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SessionResponse>(new OpenCodeTransportException("ancestry unavailable")));
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = Responder(reply);
        await Assert.That(await responder.OwnsAsync("child", CancellationToken.None)).IsFalse();
        ListJobs(checks =>
            {
                if (checks == 1)
                {
                    _fake.Emit(TurnEvents.Succeeded("foreign"));
                    _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
                }

                return checks == 1 ? [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)] : [];
            });
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
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // The waiter owns the outstanding experimental HTTP wait on every exit.
    [Test]
    [Arguments("caller")]
    [Arguments("callback")]
    [Arguments("wait")]
    [Arguments("channel")]
    public async Task WaitAsync_Should_Cancel_And_Join_Its_Started_Tasks(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = new List<TaskCompletionSource<ExperimentalSessionWaitResponse>>();
        var joined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken waitToken = default;
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
        var failure = new OpenCodeTransportException("controlled failure");
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, async (request, _) =>
        {
            if (request.Id == "failure")
            {
                throw failure;
            }

            if (scenario == "caller") { await cancellation.CancelOnWorkerAsync(); }
            else if (scenario == "callback") { _fake.Emit(TurnEvents.PermissionAsked("failure")); }
            else if (scenario == "wait") { foreach (var idle in waits) { idle.TrySetException(failure); } }
            else { _fake.FailBus(failure); }
            return reply;
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

        await _fake.Session.Received(1).ReplyToPermissionAsync("marker", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // An independent wait failure during cleanup must not replace the callback failure.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitAsync_Should_Preserve_An_Independent_Cleanup_Failure(bool internalCancellation)
    {
        var primary = new InvalidOperationException("callback failed");
        var cleanup = new OpenCodeTransportException("wait cleanup failed");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // Shared mechanics for the test-owned experimental idle-wait gates.
    private void HoldIdleWait(TaskCompletionSource<bool> entered, List<TaskCompletionSource<ExperimentalSessionWaitResponse>> releases, bool trace)
    {
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
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // Complete only this test's outstanding SDK wait responses.
    private static void ReleaseIdleWait(IEnumerable<TaskCompletionSource<ExperimentalSessionWaitResponse>> releases)
    {
        foreach (var release in releases) { release.TrySetResult(new ExperimentalSessionWaitResponse { Status = 204 }); }
    }
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // Share endpoint setup, not the product's selection algorithm.
    private void ListJobs(Func<int, IReadOnlyList<IJobInfo>> snapshot)
        => ListJobs(checks => Task.FromResult(new ExperimentalJobListResponse { Status = 200, Jobs = snapshot(checks) }));
#pragma warning restore OPENCODE001

#pragma warning disable OPENCODE001 // The asynchronous query arrangement holds an actual SDK response, not a mirrored selector.
    private void ListJobs(Func<int, Task<ExperimentalJobListResponse>> snapshot)
    {
        var checks = 0;
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _trace.Add("jobs:" + (++checks).ToString(CultureInfo.InvariantCulture));
                return snapshot(checks);
            });
    }
#pragma warning restore OPENCODE001

    private static SessionExecutionStarted ExecutionStarted(string sessionId) => new()
    {
        Id = "event_started",
        Created = 1,
        Durable = new SessionExecutionStartedDurable { AggregateId = sessionId, Seq = 1, Version = 1 },
        Data = new SessionExecutionStartedData { SessionId = sessionId },
    };

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

#pragma warning disable OPENCODE001 // Trace the actual experimental call rather than a mirror selector.
    private void TraceQueries()
    {
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _trace.Add("jobs"); return new ExperimentalJobListResponse { Status = 200, Jobs = [] }; });
        TraceInboxes(FakeOpenCode.SessionId);
        TraceActive(() => Active());
    }
#pragma warning restore OPENCODE001

    private void TraceActive(Func<Dictionary<string, SessionActive>> snapshot) =>
        _fake.Sessions.GetActiveAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _trace.Add("active"); return new SessionActiveResponse { Status = 200, Active = snapshot() }; });

    private static Dictionary<string, SessionActive> Active(params string[] ids) =>
        ids.ToDictionary(id => id, _ => new SessionActive { Type = "running" }, StringComparer.Ordinal);
}
