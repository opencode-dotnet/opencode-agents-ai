using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenCode.Agents.AI.Internal.Threading;
using OpenCode.Agents.AI.Permissions;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests;

public sealed class OpenCodeAgentRunTests
{
    private readonly FakeOpenCode _fake = new();

    [Test]
    public async Task RunAsync_Should_Interrupt_The_Turn_When_Cancelled() => await AssertTurnCancellationAsync(null);

    [Test]
    public async Task RunAsync_Should_Still_Throw_Cancellation_When_The_Interrupt_Fails() =>
        await AssertTurnCancellationAsync(new OpenCodeTransportException("interrupt timed out"));

    private async Task AssertTurnCancellationAsync(OpenCodeException? interruptFailure)
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace(interruptFailure);
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("a"), TurnEvents.PermissionAsked("cancel")]);
        var callerError = new OperationCanceledException(cancellation.Token);
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (_, _) =>
            {
                await cancellation.CancelOnWorkerAsync();
                throw callerError;
            },
        });

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await agent.RunAsync("hello", cancellationToken: cancellation.Token));

        await Assert.That(error).IsSameReferenceAs(callerError);
        await AssertRunCancellationAsync(error, cancellation.Token);
        await Assert.That(trace).IsEquivalentTo(["reject", "cancel-inbox:" + PromptRequest().Id.Value, "jobs", "interrupt"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Not_Interrupt_When_Cancelled_Before_The_Prompt_Is_Sent(bool preCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.Client.Events.SubscribeAsync(Arg.Any<EventSubscribeRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => HeldConnectionAsync(entered, call.Arg<CancellationToken>()));
        await using var agent = new OpenCodeAgent(_fake.Client);
        var session = await agent.CreateSessionAsync(FakeOpenCode.SessionId);
        if (preCancelled) { await cancellation.CancelOnWorkerAsync(); }
        var result = CaptureFailureAsync(async () => await agent.RunAsync("hello", session, cancellationToken: cancellation.Token));
        try
        {
            if (!preCancelled) { await Assert.That(await Task.WhenAny(entered.Task, result)).IsSameReferenceAs(entered.Task); }
            await cancellation.CancelOnWorkerAsync();
            var error = await Assert.That(await result).IsTypeOf<OperationCanceledException>();
            await Assert.That(error?.CancellationToken).IsEqualTo(cancellation.Token);
            await _fake.Session.DidNotReceive().PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
            await Assert.That(ArrangeReceivedCancellationCalls()).IsEmpty();
        }
        finally { await cancellation.CancelOnWorkerAsync(); await result; }
    }

    [Test]
    public async Task RunAsync_Should_Not_Interrupt_Without_Acknowledged_Admission()
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace();
        var callerError = new OperationCanceledException(cancellation.Token);
        _fake.Session.PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ => await CancelPromptAsync(cancellation, callerError));
        await using var agent = new OpenCodeAgent(_fake.Client);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await agent.RunAsync("hello", cancellationToken: cancellation.Token));

        await Assert.That(error).IsSameReferenceAs(callerError);
        await Assert.That(trace).IsEmpty();
        await Assert.That(PromptRequest().Text).IsEqualTo("hello");
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Only_Cancel_Its_Queued_Prompt_Before_Delivery(bool reconciliation)
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace();
        _fake.OnPrompt(prompt => reconciliation
            ? [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("unseen")]
            : [TurnEvents.Delivered("foreign-prompt"), TurnEvents.PermissionAsked("cancel", "child")]);
        await using var agent = new OpenCodeAgent(_fake.Client);
        var error = await CancelAfterEntryAsync(entered =>
        {
            if (reconciliation)
            {
                _fake.Session.ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(call => HoldUntilCancelledAsync<SessionPermissionListResponse>(entered, call.Arg<CancellationToken>()));
            }
            else
            {
                _fake.AddSession("child", FakeOpenCode.SessionId).GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(call => HoldUntilCancelledAsync<SessionResponse>(entered, call.Arg<CancellationToken>()));
            }
            return CaptureFailureAsync(async () => await agent.RunAsync("hello", cancellationToken: cancellation.Token));
        }, cancellation);

        var cancelled = await Assert.That(error).IsTypeOf<OperationCanceledException>();
        await Assert.That(cancelled?.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(trace).IsEquivalentTo(["cancel-inbox:" + PromptRequest().Id.Value], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await _fake.Session.Received(1).CancelInboxAsync(PromptRequest().Id.Value!, null, CancellationToken.None);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments("snapshot", false)]
    [Arguments("original", false)]
    [Arguments("background", false)]
    [Arguments("background", true)]
    public async Task RunAsync_Should_Interrupt_When_A_Permission_Callback_Is_Cancelled(string phase, bool rejectionFails)
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace();
        var rejectEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<bool>>();
        var callerError = new OperationCanceledException(cancellation.Token);
        var rejectionError = new OpenCodeTransportException("reject failed");
        _fake.OnPrompt(prompt => phase == "original"
            ? [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.PermissionAsked("cancel")]
            : [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("original"), TurnEvents.Succeeded()]);
        if (phase == "snapshot") { _fake.PendingPermissions(FakeOpenCode.SessionId, [TurnEvents.PendingPermission("cancel")]); }
        if (phase == "background") { ArrangeBackgroundAsk(); }
        _fake.Session.ReplyToPermissionAsync("cancel", Arg.Any<SessionPermissionReplyRequest>(), OpenCodeRequestOptions.NoThrow, CancellationToken.None)
            .Returns(async _ =>
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                releases.Add(release);
                trace.Add("reject-enter");
                rejectEntered.TrySetResult(true);
                await release.Task;
                trace.Add("reject-exit");
                if (rejectionFails) { throw rejectionError; }
                return new SessionPermissionReplyResponse { Status = 204 };
            });
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (_, _) => { trace.Add("callback"); await cancellation.CancelOnWorkerAsync(); throw callerError; },
        });
        var result = CaptureFailureAsync(async () => await agent.RunAsync("hello", cancellationToken: cancellation.Token));
        try
        {
            await Assert.That(await Task.WhenAny(rejectEntered.Task, result)).IsSameReferenceAs(rejectEntered.Task);
            await Assert.That(trace).IsEquivalentTo(["callback", "reject-enter"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(result.IsCompleted).IsFalse();
            foreach (var release in releases) { release.TrySetResult(true); }
            var error = await result;
            if (rejectionFails)
            {
                var aggregate = await Assert.That(error).IsTypeOf<AggregateException>();
                await Assert.That(aggregate?.InnerExceptions).IsEquivalentTo(new Exception[] { callerError, rejectionError }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            }
            else
            {
                var cancelled = await Assert.That(error).IsTypeOf<OperationCanceledException>();
                await Assert.That(cancelled?.CancellationToken).IsEqualTo(cancellation.Token);
            }
            var expected = new List<string> { "callback", "reject-enter", "reject-exit", "cancel-inbox:" + PromptRequest().Id.Value };
            if (phase != "snapshot") { expected.AddRange(["jobs", "interrupt"]); }
            await Assert.That(trace).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
        }
        finally { foreach (var release in releases) { release.TrySetResult(true); } await cancellation.CancelOnWorkerAsync(); await result; }
    }

    [Test]
    public async Task RunStreamingAsync_Should_Interrupt_When_Disposed_After_Caller_Cancellation() => await AssertHeldUpdateDisposalAsync(cancel: true);

    [Test]
    public async Task RunStreamingAsync_Should_Not_Interrupt_On_Uncancelled_Early_Disposal() => await AssertHeldUpdateDisposalAsync(cancel: false);

    private async Task AssertHeldUpdateDisposalAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace();
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("a")]);
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();
        await using (var enumerator = agent.RunStreamingAsync("hello", cancellationToken: cancellation.Token).GetAsyncEnumerator())
        {
            while (await enumerator.MoveNextAsync())
            {
                updates.Add(enumerator.Current);
                if (enumerator.Current.Contents.OfType<TextContent>().Any()) { break; }
            }
            if (cancel) { await cancellation.CancelOnWorkerAsync(); }
        }

        await Assert.That(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text))
            .IsEquivalentTo(["a"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        Type?[] expectedTypes = [typeof(SessionInboxDelivered), typeof(SessionTextDelta)];
        await Assert.That(updates.SelectMany(update => update.Contents).Select(content => content.RawRepresentation?.GetType()))
            .IsEquivalentTo(expectedTypes, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        string[] expected = cancel ? ["cancel-inbox:" + PromptRequest().Id.Value, "jobs", "interrupt"] : [];
        await Assert.That(trace).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments("success")]
    [Arguments("failure")]
    [Arguments("foreign cancellation")]
    public async Task RunAsync_Should_Not_Interrupt_Without_Caller_Cancellation(string outcome)
    {
        var trace = ArrangeCancellationTrace();
        using var foreign = new CancellationTokenSource();
        var error = new OperationCanceledException(foreign.Token);
        _fake.OnPrompt(prompt => outcome switch
        {
            "failure" => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Failed("failed")],
            "foreign cancellation" => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.PermissionAsked("foreign")],
            _ => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("done"), TurnEvents.Succeeded()],
        });
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions { OnPermissionRequest = (_, _) => throw error });
        var result = await CaptureFailureAsync(async () => await agent.RunAsync("hello"));

        if (outcome == "success") { await Assert.That(result).IsNull(); }
        else if (outcome == "foreign cancellation") { await Assert.That(result).IsSameReferenceAs(error); }
        else { await Assert.That(result?.Message).IsEqualTo("Session error: failed"); }
        string[] expected = outcome switch { "success" => ["jobs"], "foreign cancellation" => ["reject"], _ => [] };
        await Assert.That(trace).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments("idle")]
    [Arguments("jobs")]
    [Arguments("inbox")]
    [Arguments("active")]
    [Arguments("ancestry")]
    [Arguments("channel")]
    public async Task RunAsync_Should_Interrupt_From_Each_Background_Cancellation_Point(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace();
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("original"), TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();
        var error = await CancelAfterEntryAsync(checkpoint =>
        {
            ArrangeBackgroundCancellationPoint(phase, trace, checkpoint);
            return CaptureRunFailureAsync(agent, updates, cancellation.Token);
        }, cancellation);
        await AssertRunCancellationAsync(error, cancellation.Token);

        await _fake.Session.Received(1).InterruptAsync(null, null, CancellationToken.None);
        await Assert.That(trace.Where(action => action != "reject")).IsEquivalentTo(["phase:" + phase, "cancel-inbox:" + PromptRequest().Id.Value, "jobs", "interrupt"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertOriginalOutputAndClosedBusAsync(updates);
        if (phase == "channel")
        {
            await _fake.Shells.GetShellClient("shell_test").Received(1).RemoveAsync(Arg.Is<ShellRemoveRequest>(request => request.Location != null && request.Location.Directory == "test-directory"), null, CancellationToken.None);
        }
    }

    [Test]
    public async Task RunAsync_Should_Preserve_Independent_Local_Cleanup_Failures_When_Cancellation_Actions_Fail()
    {
        using var cancellation = new CancellationTokenSource();
        var idleFailure = new OpenCodeTransportException("independent idle failure");
        var callerError = new OperationCanceledException(cancellation.Token);
        var trace = ArrangeCancellationTrace(new OpenCodeTransportException("interrupt failed"));
        var joined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("original"), TurnEvents.Succeeded()]);
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ExperimentalSessionWaitResponse arrange an independently failing local join.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var release = new TaskCompletionSource<ExperimentalSessionWaitResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = call.Arg<CancellationToken>().Register(() => release.TrySetException(idleFailure));
                _fake.Emit(TurnEvents.PermissionAsked("cancel"));
                try { return await release.Task; }
                finally { joined.TrySetResult(true); }
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ExperimentalSessionWaitResponse arrangement ends here.
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (_, _) => { await cancellation.CancelOnWorkerAsync(); throw callerError; },
        });
        var updates = new List<AgentResponseUpdate>();
        var error = await CaptureRunFailureAsync(agent, updates, cancellation.Token);

        var aggregate = (await Assert.That(error).IsTypeOf<AggregateException>())!; // The assertion establishes the aggregate.
        await Assert.That(aggregate.InnerExceptions.Count).IsEqualTo(2);
        var cancelled = (await Assert.That(aggregate.InnerExceptions[0]).IsTypeOf<OperationCanceledException>())!; // The assertion establishes cancellation.
        await Assert.That(cancelled.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(cancelled.InnerException).IsSameReferenceAs(callerError);
        await Assert.That(aggregate.InnerExceptions[1]).IsSameReferenceAs(idleFailure);
        await Assert.That(joined.Task.Status).IsEqualTo(TaskStatus.RanToCompletion);
        await _fake.Session.Received(1).InterruptAsync(null, null, CancellationToken.None);
        await Assert.That(trace).IsEquivalentTo(["reject", "cancel-inbox:" + PromptRequest().Id.Value, "jobs", "interrupt"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertOriginalOutputAndClosedBusAsync(updates);
    }

    private void ArrangeBackgroundCancellationPoint(string phase, List<string> trace, TaskCompletionSource<bool> entered)
    {
#pragma warning disable OPENCODE001 // ExperimentalClient, ExperimentalJobListRequest and ExperimentalJobListResponse arrange the cancellable job query separately from the sweep.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Is<CancellationToken>(token => token.CanBeCanceled))
            .Returns(call => phase == "jobs" ? HoldPhaseAsync<ExperimentalJobListResponse>(phase, trace, entered, call.Arg<CancellationToken>())
                : Task.FromResult(new ExperimentalJobListResponse { Status = 200, Jobs = [] }));
#pragma warning restore OPENCODE001 // ExperimentalClient, ExperimentalJobListRequest and ExperimentalJobListResponse arrangement ends here.
        if (phase == "idle")
        {
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ExperimentalSessionWaitResponse arrange the held idle wait.
            _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => HoldPhaseAsync<ExperimentalSessionWaitResponse>(phase, trace, entered, call.Arg<CancellationToken>()));
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ExperimentalSessionWaitResponse arrangement ends here.
        }
        if (phase == "channel")
        {
            _fake.Session.ReplyToPermissionAsync("checkpoint", Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { trace.Add("phase:channel"); entered.TrySetResult(true); return new SessionPermissionReplyResponse { Status = 204 }; });
#pragma warning disable OPENCODE001 // ExperimentalClient, ExperimentalJobListRequest, ExperimentalJobListResponse, JobShell and JobStatus arrange pending shell work.
            _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (call.Arg<CancellationToken>().CanBeCanceled) { _fake.Emit(TurnEvents.PermissionAsked("checkpoint")); }
                    else { trace.Add("jobs"); }
                    return new ExperimentalJobListResponse { Status = 200, Jobs = [TurnEvents.ShellJob("shell", FakeOpenCode.SessionId, 100, JobStatus.Running)] };
                });
#pragma warning restore OPENCODE001 // ExperimentalClient, ExperimentalJobListRequest, ExperimentalJobListResponse, JobShell and JobStatus arrangement ends here.
        }
        if (phase == "inbox")
        {
            _fake.Session.ListInboxAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => HoldPhaseAsync<SessionInboxListResponse>(phase, trace, entered, call.Arg<CancellationToken>()));
        }
        if (phase == "active")
        {
            _fake.Sessions.GetActiveAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => HoldPhaseAsync<SessionActiveResponse>(phase, trace, entered, call.Arg<CancellationToken>()));
        }
        if (phase == "ancestry")
        {
            _fake.Active(new Dictionary<string, SessionActive>(StringComparer.Ordinal) { ["child"] = new SessionActive { Type = "running" } });
            _fake.AddSession("child", FakeOpenCode.SessionId).GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => HoldPhaseAsync<SessionResponse>(phase, trace, entered, call.Arg<CancellationToken>()));
        }
    }

    private static Task<T> HoldPhaseAsync<T>(string phase, List<string> trace, TaskCompletionSource<bool> entered, CancellationToken token)
    {
        trace.Add("phase:" + phase);
        return HoldUntilCancelledAsync<T>(entered, token);
    }

    private async Task AssertRunCancellationAsync(Exception? error, CancellationToken token)
    {
        var cancelled = await Assert.That(error).IsTypeOf<OperationCanceledException>();
        await Assert.That(cancelled?.CancellationToken).IsEqualTo(token);
        await _fake.Session.Received(1).InterruptAsync(null, null, CancellationToken.None);
    }

    private static async Task<Exception?> CancelAfterEntryAsync(Func<TaskCompletionSource<bool>, Task<Exception?>> run, CancellationTokenSource cancellation)
    {
        var checkpoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = run(checkpoint);
        try
        {
            await Assert.That(await Task.WhenAny(checkpoint.Task, outcome)).IsSameReferenceAs(checkpoint.Task);
            await cancellation.CancelOnWorkerAsync();
            return await outcome;
        }
        finally { await cancellation.CancelOnWorkerAsync(); await outcome; }
    }

    private void ArrangeBackgroundAsk()
    {
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ExperimentalSessionWaitResponse arrange the callback's background phase.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var release = new TaskCompletionSource<ExperimentalSessionWaitResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                var token = call.Arg<CancellationToken>();
                using var registration = token.Register(() => release.TrySetCanceled(token));
                _fake.Emit(TurnEvents.PermissionAsked("cancel"));
                return await release.Task;
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ExperimentalSessionWaitResponse arrangement ends here.
    }

    private string[] ArrangeReceivedCancellationCalls()
    {
        var sessionCalls = _fake.Session.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name is nameof(SessionClient.InterruptAsync) or nameof(SessionClient.CancelInboxAsync))
            .Select(call => call.GetMethodInfo().Name);
#pragma warning disable OPENCODE001 // ExperimentalClient verifies no pre-admission cancellation snapshot or wait.
        return [.. sessionCalls.Concat(_fake.Experimental.ReceivedCalls().Select(call => call.GetMethodInfo().Name))];
#pragma warning restore OPENCODE001 // ExperimentalClient verification ends here.
    }

    private static async Task<T> HoldUntilCancelledAsync<T>(TaskCompletionSource<bool> entered, CancellationToken token)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => result.TrySetCanceled(token));
        entered.TrySetResult(true);
        return await result.Task;
    }

    private static async IAsyncEnumerable<IEvent> HeldConnectionAsync(TaskCompletionSource<bool> entered, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        yield return await HoldUntilCancelledAsync<IEvent>(entered, token);
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> run)
    {
        try { await run(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<SessionPromptResponse> CancelPromptAsync(CancellationTokenSource cancellation, OperationCanceledException error)
    {
        await cancellation.CancelOnWorkerAsync();
        throw error;
    }

    private List<string> ArrangeCancellationTrace(OpenCodeException? interruptFailure = null)
    {
        var trace = new List<string>();
        _fake.Session.CancelInboxAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => { trace.Add("cancel-inbox:" + call.Arg<string>()); return new SessionInboxCancelResponse { Status = 204 }; });
        _fake.Session.InterruptAsync(Arg.Any<SessionInterruptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                trace.Add("interrupt");
                return interruptFailure is null
                    ? Task.FromResult(new SessionInterruptResponse { Status = 200, Interrupt = new SessionInterruptOutcome { Interrupted = true } })
                    : Task.FromException<SessionInterruptResponse>(interruptFailure);
            });
        _fake.Session.ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("reject"); return new SessionPermissionReplyResponse { Status = 204 }; });
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync and ExperimentalJobListResponse arrange the cancellation snapshot.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("jobs"); return new ExperimentalJobListResponse { Status = 200, Jobs = [] }; });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync and ExperimentalJobListResponse arrangement ends here.
        return trace;
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task RunAsync_Should_Answer_A_Permission_Request_And_Finish_The_Turn(bool beforeDelivery, bool child)
    {
        var target = child ? _fake.AddSession("ses_child", FakeOpenCode.SessionId) : _fake.Session;
        var ask = TurnEvents.PermissionAsked("per_1", child ? "ses_child" : FakeOpenCode.SessionId);
        var received = new List<PermissionRequest>();
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        _fake.OnPrompt(prompt =>
        [
            beforeDelivery ? ask : TurnEvents.Delivered(prompt.Id.Value!),
            beforeDelivery ? TurnEvents.Delivered(prompt.Id.Value!) : ask,
            TurnEvents.Text("child text", "ses_child"), TurnEvents.Text("done"), TurnEvents.Succeeded(),
        ]);
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = (request, _) => { received.Add(request); return new(reply); },
        });

        var response = await agent.RunAsync("hello");

        await Assert.That(received.Count).IsEqualTo(1);
        var actual = received.Single();
        await Assert.That(actual.Id).IsEqualTo("per_1");
        await Assert.That(actual.SessionId).IsEqualTo(ask.Data.SessionId);
        await Assert.That(actual.Action).IsEqualTo("edit");
        await Assert.That(actual.Resources).IsSameReferenceAs(ask.Data.Resources);
        await Assert.That(actual.Resources).IsEquivalentTo(["z.cs", "a.cs"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(actual.Save).IsSameReferenceAs(ask.Data.Save);
        await Assert.That(actual.Save).IsEquivalentTo(["z/*", "a/*"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(actual.Metadata).IsSameReferenceAs(ask.Data.Metadata);
        await Assert.That(actual.Metadata?["reason"].GetString()).IsEqualTo("update");
        await Assert.That(actual.Source).IsSameReferenceAs(ask.Data.Source);
        await Assert.That(actual.Source?.Type).IsEqualTo("tool");
        await Assert.That(actual.Source?.MessageId).IsEqualTo("msg_permission");
        await Assert.That(actual.Source?.Id).IsEqualTo("call_edit");
        await Assert.That(actual.Message).IsEqualTo("Update these files?");
        await target.Received(1).ReplyToPermissionAsync("per_1", Arg.Is<SessionPermissionReplyRequest>(value => ReferenceEquals(value, reply)), OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
        await Assert.That(response.Text).IsEqualTo("done");
        if (child)
        {
            await _fake.Session.DidNotReceive().ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
            await Assert.That(response.Messages.SelectMany(message => message.Contents).Select(content => content.RawRepresentation)).DoesNotContain(ask);
        }
    }

    [Test]
    public async Task RunAsync_Should_Reject_Then_Surface_The_Callback_Failure_And_Close_The_Bus()
    {
        var failure = new InvalidOperationException("permission callback failed");
        var ask = TurnEvents.PermissionAsked("per_failed");
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), ask, TurnEvents.Text("not success"), TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions { OnPermissionRequest = (_, _) => throw failure });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.RunAsync("hello"));

        await Assert.That(error).IsSameReferenceAs(failure);
        await _fake.Session.Received(1).ReplyToPermissionAsync("per_failed",
            Arg.Is<SessionPermissionReplyRequest>(reply => reply.Decision == PermissionReply.Reject && reply.Message.Value == PermissionResponder.HandlerFailedMessage), OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task RunAsync_Should_Return_The_Turns_Text()
    {
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), TurnEvents.Text("Hel"), TurnEvents.Text("lo"), TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var response = await agent.RunAsync("hello");

        await Assert.That(response.Text).IsEqualTo("Hello");
    }

    [Test]
    public async Task RunAsync_Should_Create_A_Session_With_The_Options_When_The_Session_Has_No_Id()
    {
        var model = new ModelRef { Id = "model_test", ProviderId = "provider_test" };
        var rule = new PermissionRule { Action = "edit", Resource = "*", Effect = PermissionEffect.Deny };
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            Model = model,
            Agent = "build",
            Directory = "work-directory",
            Title = "Test conversation",
            Permissions = [rule],
        });
        var session = (OpenCodeAgentSession)await agent.CreateSessionAsync();

        await agent.RunAsync("hello", session);

        var request = (SessionCreateRequest)_fake.Sessions.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(SessionsClient.CreateSessionAsync)).GetArguments()[0]!;
        await Assert.That(request.Model.IsSet).IsTrue();
        await Assert.That(request.Model.Value).IsEqualTo(model);
        await Assert.That(request.Agent.IsSet).IsTrue();
        await Assert.That(request.Agent.Value).IsEqualTo("build");
        await Assert.That(request.Location.IsSet).IsTrue();
        await Assert.That(request.Location.Value?.Directory).IsEqualTo("work-directory");
        await Assert.That(request.Title.IsSet).IsTrue();
        await Assert.That(request.Title.Value).IsEqualTo("Test conversation");
        await Assert.That(request.Permissions.IsSet).IsTrue();
        await Assert.That(request.Permissions.Value).IsEquivalentTo([rule], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(session.SessionId).IsEqualTo(FakeOpenCode.SessionId);
    }

    [Test]
    public async Task RunAsync_Should_Continue_The_Session_When_It_Has_An_Id()
    {
        await using var agent = new OpenCodeAgent(_fake.Client);
        var session = await agent.CreateSessionAsync(FakeOpenCode.SessionId);

        await agent.RunAsync("hello", session);

        await _fake.Sessions.DidNotReceive().CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        _fake.Sessions.Received().GetSessionClient(FakeOpenCode.SessionId);
        await Assert.That(((OpenCodeAgentSession)session).SessionId).IsEqualTo(FakeOpenCode.SessionId);
    }

    [Test]
    public async Task RunAsync_Should_Queue_The_Prompt()
    {
        await using var agent = new OpenCodeAgent(_fake.Client);

        await agent.RunAsync("hello");

        var request = PromptRequest();
        await Assert.That(request.Delivery.IsSet).IsTrue();
        await Assert.That(request.Delivery.Value).IsEqualTo(SessionInboxDelivery.Queue);
        await Assert.That(request.Id.IsSet).IsTrue();
        await Assert.That(request.Id.Value).StartsWith("msg_");
    }

    [Test]
    public async Task RunAsync_Should_Join_Message_Texts_With_Newlines()
    {
        await using var agent = new OpenCodeAgent(_fake.Client);
        ChatMessage[] messages = [new(ChatRole.User, "first"), new(ChatRole.User, "second")];

        await agent.RunAsync(messages);

        await Assert.That(PromptRequest().Text).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task RunAsync_Should_Ignore_Text_Before_The_Prompt_Is_Delivered()
    {
        _fake.OnPrompt(request =>
        [
            TurnEvents.Text("old"), TurnEvents.Succeeded(), TurnEvents.Delivered("msg_other"),
            TurnEvents.Text("not ours"), TurnEvents.Delivered(request.Id.Value!), TurnEvents.Text("new"), TurnEvents.Succeeded(),
        ]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var response = await agent.RunAsync("hello");

        await Assert.That(response.Text).IsEqualTo("new");
    }

    [Test]
    public async Task RunAsync_Should_Ignore_Events_Of_Other_Sessions()
    {
        const string type = "session.execution.succeeded";
        var otherEnd = new UnknownEvent(type, JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = type,
            ["data"] = new JsonObject { ["sessionID"] = "ses_other" },
        }));
        _fake.OnPrompt(request =>
        [
            TurnEvents.Delivered(request.Id.Value!), TurnEvents.Text("x", "ses_other"), TurnEvents.Succeeded("ses_other"),
            otherEnd, TurnEvents.Text("ours"), TurnEvents.Succeeded(),
        ]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var response = await agent.RunAsync("hello");

        await Assert.That(response.Text).IsEqualTo("ours");
    }

    [Test]
    public async Task RunAsync_Should_Not_End_On_A_Turn_End_Without_A_Session()
    {
        const string type = "session.execution.succeeded";
        var unscopedEnd = new UnknownEvent(type, JsonSerializer.SerializeToElement(new JsonObject { ["type"] = type }));
        await Assert.That(await TextAfterAsync(unscopedEnd)).IsEqualTo("a");
    }

    [Test]
    [Arguments("created")]
    [Arguments("replied")]
    [Arguments("cancelled")]
    public async Task RunAsync_Should_Ignore_Forms_Of_Other_Sessions(string type)
    {
        var form = OtherSessionForm(type);
        var text = TurnEvents.Text("ours");
        var end = TurnEvents.Succeeded();
        IEvent? delivered = null;
        _fake.OnPrompt(request =>
        {
            delivered = TurnEvents.Delivered(request.Id.Value!);
            return [delivered, form, text, end];
        });
        await using var agent = new OpenCodeAgent(_fake.Client);

        var updates = await UpdatesAsync(agent);

        await Assert.That(delivered).IsNotNull();
        await Assert.That(updates.SelectMany(update => update.Contents).Select(content => content.RawRepresentation))
            .IsEquivalentTo(new object?[] { delivered, text, end }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunStreamingAsync_Should_Map_Reasoning_To_Reasoning_Content()
    {
        var reasoning = TurnEvents.Reasoning("thinking");
        var text = TurnEvents.Text("answer");
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), reasoning, text, TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client, id: "agent_test");

        var updates = await UpdatesAsync(agent);

        var update = updates.Single(item => item.Contents.SingleOrDefault() is TextReasoningContent);
        var content = (TextReasoningContent)update.Contents.Single();
        await Assert.That(content.Text).IsEqualTo("thinking");
        await Assert.That(content.RawRepresentation).IsSameReferenceAs(reasoning);
        await Assert.That(update.Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(update.AgentId).IsEqualTo("agent_test");
        await Assert.That(update.MessageId).IsEqualTo("msg_reasoning");
        await Assert.That(update.ResponseId).IsEqualTo(PromptRequest().Id.Value);
        var textUpdate = updates.Single(item => item.Contents.SingleOrDefault() is TextContent);
        await Assert.That(((TextContent)textUpdate.Contents.Single()).Text).IsEqualTo("answer");
        await Assert.That(textUpdate.Contents.Single().RawRepresentation).IsSameReferenceAs(text);
        await Assert.That(textUpdate.Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(textUpdate.AgentId).IsEqualTo("agent_test");
        await Assert.That(textUpdate.MessageId).IsEqualTo("msg_assistant");
        await Assert.That(textUpdate.ResponseId).IsEqualTo(PromptRequest().Id.Value);
    }

    [Test]
    public async Task RunStreamingAsync_Should_Carry_Unknown_Events_As_Raw_Representation()
    {
        const string type = "future.event";
        var unknown = new UnknownEvent(type, JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = type }));
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), unknown, TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var updates = await UpdatesAsync(agent);

        var contents = updates.SelectMany(update => update.Contents).Where(content => ReferenceEquals(content.RawRepresentation, unknown)).ToList();
        await Assert.That(contents.Count).IsEqualTo(1);
        await Assert.That(contents[0].GetType()).IsEqualTo(typeof(AIContent));
        await Assert.That(contents[0].RawRepresentation).IsSameReferenceAs(unknown);
    }

    [Test]
    public async Task RunAsync_Should_Not_End_On_Server_Connected()
    {
        _fake.OnPrompt(prompt =>
        {
            var user = TurnEvents.StoredUser(prompt.Id.Value!);
            var assistant = TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = "" }]);
            _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(new SessionMessageListResponse { Status = 200, Cursor = new ListCursor(), Messages = [assistant, user] },
                    new SessionMessageListResponse
                    {
                        Status = 200,
                        Cursor = new ListCursor(),
                        Messages = [TurnEvents.StoredIdle("end", SessionMessageIdleOutcome.Succeeded),
                        assistant with { Content = [new SessionMessageAssistantText { Text = "a" }] }, user]
                    });
            return FiniteEvents([TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Connected(), TurnEvents.TextEnded("a"), TurnEvents.Succeeded()]);
        });
        await using var agent = new OpenCodeAgent(_fake.Client);

        var updates = await UpdatesAsync(agent);

        await Assert.That(TextChunks(updates)).IsEquivalentTo(["a"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertRefreshCallsAsync(messageQueries: 2);
    }

    [Test]
    public async Task RunAsync_Should_Throw_With_OpenCodes_Error_Text_When_The_Turn_Fails()
    {
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), TurnEvents.Failed("model overloaded")]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.RunAsync("hello"));

        await Assert.That(error?.Message).IsEqualTo("Session error: model overloaded");
    }

    [Test]
    public async Task RunAsync_Should_End_Normally_When_The_Turn_Is_Interrupted_By_The_User()
    {
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), TurnEvents.Text("partial"), TurnEvents.Interrupted(SessionExecutionInterruptedDataReason.User)]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var response = await agent.RunAsync("hello");

        await Assert.That(response.Text).IsEqualTo("partial");
    }

    [Test]
    public async Task RunAsync_Should_Throw_When_The_Turn_Is_Interrupted_By_Shutdown()
    {
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), TurnEvents.Interrupted(SessionExecutionInterruptedDataReason.Shutdown)]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.RunAsync("hello"));

        await Assert.That(error?.Message).IsEqualTo("Session error: the OpenCode server is shutting down");
    }

    [Test]
    public async Task RunAsync_Should_Throw_When_The_Bus_Does_Not_Start_With_Server_Connected()
    {
        _fake.Client.Events.SubscribeAsync(Arg.Any<EventSubscribeRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(NonConnectedBus());
        await using var agent = new OpenCodeAgent(_fake.Client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.RunAsync("hello"));

        await Assert.That(error?.Message).IsEqualTo("The event stream did not start with a server.connected frame.");
        await _fake.Session.DidNotReceive().PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_Should_Surface_The_Sdk_Exception_And_Close_The_Bus_When_The_Prompt_Fails()
    {
        var failure = new OpenCodeTransportException("prompt failed");
        _fake.Session.PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SessionPromptResponse>(failure));
        await using var agent = new OpenCodeAgent(_fake.Client);

        var error = await Assert.ThrowsAsync<OpenCodeTransportException>(async () => await agent.RunAsync("hello"));

        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task RunAsync_Should_Throw_When_The_Bus_Ends_Before_The_Turn()
    {
        _fake.OnPrompt(EndBeforeTurn);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.RunAsync("hello"));

        await Assert.That(error?.Message).IsEqualTo("The event stream ended before the turn ended.");
    }

    [Test]
    public async Task RunAsync_Should_Surface_The_Bus_Failure_After_The_Text_Before_It()
    {
        var failure = new OpenCodeTransportException("bus failed");
        _fake.OnPrompt(request => FailBeforeTurn(request, failure));
        await using var agent = new OpenCodeAgent(_fake.Client);
        var texts = new List<string>();

        var error = await Assert.ThrowsAsync<OpenCodeTransportException>(async () =>
        {
            await foreach (var update in agent.RunStreamingAsync("hello"))
            {
                texts.AddRange(update.Contents.OfType<TextContent>().Select(content => content.Text));
            }
        });

        await Assert.That(texts).IsEquivalentTo(["a"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(error).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task RunStreamingAsync_Should_Close_The_Bus_When_The_Caller_Stops_Early()
    {
        IEvent? firstEvent = null;
        _fake.OnPrompt(request =>
        {
            firstEvent = TurnEvents.Delivered(request.Id.Value!);
            _fake.Emit(firstEvent);
            _fake.Emit(TurnEvents.Text("a"));
            _fake.Emit(TurnEvents.Succeeded());
            _fake.EndBus();
            return [];
        });
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync("hello"))
        {
            updates.Add(update);
            if (ReferenceEquals(update.Contents.Single().RawRepresentation, firstEvent))
            {
                break;
            }
        }

        await Assert.That(updates.Count).IsEqualTo(1);
        await Assert.That(firstEvent).IsNotNull();
        await Assert.That(updates[0].Contents.Single().RawRepresentation).IsSameReferenceAs(firstEvent);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Send_No_Prompt_When_The_Messages_Hold_No_Text(bool emptyMessage)
    {
        await using var agent = new OpenCodeAgent(_fake.Client);
        ChatMessage[] messages = emptyMessage ? [new(ChatRole.User, string.Empty)] : [];

        var response = await agent.RunAsync(messages);

        await Assert.That(response.Messages).IsEmpty();
        await Assert.That(response.Text).IsEqualTo(string.Empty);
        await _fake.Session.DidNotReceive().PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunStreamingAsync_Should_Close_Open_Tool_Calls_Before_The_Run_Ends()
    {
        _fake.OnPrompt(request =>
        [
            TurnEvents.Delivered(request.Id.Value!), TurnEvents.ToolStarted("c1", "shell"),
            TurnEvents.ToolCalled("c1", new Dictionary<string, JsonElement>(StringComparer.Ordinal)),
            TurnEvents.Interrupted(SessionExecutionInterruptedDataReason.User),
        ]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var updates = await UpdatesAsync(agent);

        var last = updates[^1];
        var content = (await Assert.That(last.Contents.Single()).IsTypeOf<FunctionResultContent>())!; // The type assertion proves this is non-null.
        await Assert.That(content.CallId).IsEqualTo("c1");
        await Assert.That(content.Result).IsEqualTo("Tool execution interrupted: shell");
        await Assert.That(last.Role).IsEqualTo(ChatRole.Tool);
    }

    [Test]
    public async Task RunStreamingAsync_Should_Close_Open_Tool_Calls_Before_A_Bus_Failure()
    {
        var failure = new OpenCodeTransportException("bus failed with an open call");
        _fake.OnPrompt(request => OpenCallBeforeBusEnds(request, () => _fake.FailBus(failure)));
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();

        var error = await Assert.ThrowsAsync<OpenCodeTransportException>(async () => await UpdatesAsync(agent, updates));

        await Assert.That(error).IsSameReferenceAs(failure);
        var last = updates[^1];
        var result = await Assert.That(last.Contents.Single()).IsTypeOf<FunctionResultContent>();
        await Assert.That(result?.CallId).IsEqualTo("c1");
        await Assert.That(result?.Result).IsEqualTo("Tool execution interrupted: shell");
        await Assert.That(last.Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>().Select(content => content.CallId))
            .IsEquivalentTo(["c1"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunStreamingAsync_Should_Close_Open_Tool_Calls_When_The_Stream_Ends_Early()
    {
        _fake.OnPrompt(request => OpenCallBeforeBusEnds(request, _fake.EndBus));
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await UpdatesAsync(agent, updates));

        await Assert.That(error?.Message).IsEqualTo("The event stream ended before the turn ended.");
        (ChatRole? Role, Type Content)[] expected =
        [
            (ChatRole.Assistant, typeof(AIContent)), (ChatRole.Assistant, typeof(AIContent)),
            (ChatRole.Assistant, typeof(FunctionCallContent)), (ChatRole.Tool, typeof(FunctionResultContent)),
        ];
        await Assert.That(updates.SelectMany(update => update.Contents.Select(content => (update.Role, content.GetType()))))
            .IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var result = (FunctionResultContent)updates[^1].Contents.Single();
        await Assert.That(result.CallId).IsEqualTo("c1");
        await Assert.That(result.Result).IsEqualTo("Tool execution interrupted: shell");
    }

    [Test]
    public async Task RunAsync_Should_Reconcile_Permissions_After_Admission_Before_Delivery()
    {
        var trace = new List<string>();
        var pending = TurnEvents.PendingPermission("per_pending");
        _fake.Session.ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("list"); return new SessionPermissionListResponse { Status = 200, Requests = [pending] }; });
        _fake.OnPrompt(prompt =>
        {
            trace.Add("admission");
            return [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("done"), TurnEvents.Succeeded()];
        });
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        _fake.Session.ReplyToPermissionAsync("per_pending", reply, Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("reply"); return new SessionPermissionReplyResponse { Status = 204 }; });
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync arranges the completion-check trace.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("background check"); return new ExperimentalJobListResponse { Status = 200, Jobs = [] }; });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync arrangement ends here.
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = (request, _) => { trace.Add("callback:" + request.Id); return new(reply); },
        });

        await foreach (var update in agent.RunStreamingAsync("hello"))
        {
            trace.Add(update.Contents.Single().RawRepresentation switch
            {
                SessionInboxDelivered => "mapped delivery",
                SessionTextDelta => "text",
                SessionExecutionSucceeded => "original end",
                _ => "unexpected",
            });
        }

        await Assert.That(trace).IsEquivalentTo(["admission", "list", "callback:per_pending", "reply", "mapped delivery", "text", "original end", "background check"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("per_pending", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task RunAsync_Should_Wait_For_This_Prompts_Background_Work_And_Answer_Its_Late_Ask(bool noCallback, bool declinedBeforeEnd)
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new SessionPermissionReplyRequest { Decision = declinedBeforeEnd ? PermissionReply.Reject : PermissionReply.Once };
        var originalText = TurnEvents.Text("original");
        var originalEnd = TurnEvents.Succeeded();
        IEvent? delivery = null;
        _fake.OnPrompt(prompt =>
        {
            delivery = TurnEvents.Delivered(prompt.Id.Value!);
            return declinedBeforeEnd
                ? [delivery, TurnEvents.PermissionAsked("per_child", "child"), TurnEvents.Succeeded("child"), originalText, originalEnd]
                : [delivery, originalText, originalEnd];
        });
#pragma warning disable OPENCODE001 // JobStatus.Running and ExperimentalClient.WaitForSessionAsync arrange the background entry.
        _fake.Jobs([TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Running)]);
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                entered.TrySetResult(true);
                if (!declinedBeforeEnd) { _fake.Emit(TurnEvents.PermissionAsked("per_child", "child")); }
                return new ExperimentalSessionWaitResponse { Status = 204 };
            });
#pragma warning restore OPENCODE001 // JobStatus.Running and ExperimentalClient.WaitForSessionAsync arrangement ends here.
        child.ReplyToPermissionAsync("per_child", Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!declinedBeforeEnd)
                {
                    _fake.Jobs([]);
                    _fake.Emit(TurnEvents.Text("child output", "child"));
                    _fake.Emit(TurnEvents.Text("wake output"));
                    _fake.Emit(TurnEvents.Succeeded("child"));
                }
                return new SessionPermissionReplyResponse { Status = 204 };
            });
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions { OnPermissionRequest = noCallback ? null : (_, _) => new(reply) });
        var updates = new List<AgentResponseUpdate>();
        var outcome = CaptureRunFailureAsync(agent, updates);
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            if (declinedBeforeEnd) { _fake.EndBus(); }
            await Assert.That(await outcome).IsNull();
        }
        finally
        {
            _fake.EndBus();
            await outcome;
        }

        await Assert.That(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text)).IsEquivalentTo(["original"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(updates.SelectMany(update => update.Contents).Select(content => content.RawRepresentation)).IsEquivalentTo(new object?[] { delivery, originalText, originalEnd }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var actual = (SessionPermissionReplyRequest)child.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(SessionClient.ReplyToPermissionAsync)).GetArguments()[1]!; // The SDK requires a reply body.
        if (noCallback)
        {
            await Assert.That(actual.Decision).IsEqualTo(PermissionReply.Reject);
            await Assert.That(actual.Message.Value).IsEqualTo(PermissionResponder.NoHandlerMessage);
        }
        else { await Assert.That(actual).IsSameReferenceAs(reply); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Use_The_Admission_Time_Not_The_Turn_End_Time(bool restarted)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<bool>>();
        _fake.AddSession("boundary", FakeOpenCode.SessionId);
        _fake.AddSession("newer", FakeOpenCode.SessionId);
#pragma warning disable OPENCODE001 // JobStatus.Running arranges JobSubagent rows around the admission boundary.
        _fake.Jobs([
            TurnEvents.SubagentJob("old", FakeOpenCode.SessionId, "old_child", 99, JobStatus.Running),
            TurnEvents.SubagentJob("boundary", FakeOpenCode.SessionId, "boundary", 100, JobStatus.Running),
            TurnEvents.SubagentJob("newer", FakeOpenCode.SessionId, "newer", 101, JobStatus.Running),
        ]);
#pragma warning restore OPENCODE001 // JobStatus.Running and JobSubagent arrangement ends here.
        var trace = new List<string>();
        foreach (var id in new[] { FakeOpenCode.SessionId, "boundary", "newer" })
        {
            _fake.Sessions.GetSessionClient(id).ListInboxAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { trace.Add(id); return new SessionInboxListResponse { Status = 200, Inbox = [] }; });
        }
        _fake.OnPrompt(prompt =>
        [
            TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.PermissionAsked("boundary", "boundary"), TurnEvents.PermissionAsked("newer", "newer"),
            TurnEvents.PermissionAsked("turn_gate"),
            restarted ? TurnEvents.ExecutionStarted("boundary") : TurnEvents.Succeeded("boundary"),
            TurnEvents.Text("original"), TurnEvents.Succeeded(),
        ], admittedAt: 100);
        if (restarted)
        {
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync arranges the restarted child's retained row.
            _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => { _fake.Emit(TurnEvents.PermissionAsked("checkpoint")); return new ExperimentalJobListResponse { Status = 200, Jobs = [TurnEvents.SubagentJob("boundary", FakeOpenCode.SessionId, "boundary", 100, JobStatus.Running)] }; });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync arrangement ends here.
        }
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (request, _) =>
            {
                if (request.Id == "turn_gate")
                {
                    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    releases.Add(release);
                    entered.TrySetResult(true);
                    await release.Task;
                    if (!restarted) { _fake.EndBus(); }
                    return new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
                }
                if (request.Id == "checkpoint") { _fake.EndBus(); }
                return new SessionPermissionReplyRequest { Decision = request.Id == "checkpoint" ? PermissionReply.Once : PermissionReply.Reject };
            },
        });
        var updates = new List<AgentResponseUpdate>();
        var result = CaptureRunFailureAsync(agent, updates);
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, result)).IsSameReferenceAs(entered.Task);
            await Assert.That(trace).IsEmpty();
            foreach (var release in releases) { release.TrySetResult(true); }
            if (restarted)
            {
                await Assert.That((await result)?.Message).IsEqualTo(BackgroundJobs.PendingWorkEofMessage);
                await _fake.Session.Received(1).ReplyToPermissionAsync("checkpoint", Arg.Is<SessionPermissionReplyRequest>(reply => reply.Decision == PermissionReply.Once), OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
            }
            else
            {
                await Assert.That(await result).IsNull();
                await Assert.That(trace).IsEquivalentTo([FakeOpenCode.SessionId, "boundary", "newer"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            }
        }
        finally
        {
            foreach (var release in releases) { release.TrySetResult(true); }
            _fake.EndBus();
            await result;
        }
    }

    [Test]
    [Arguments("success")]
    [Arguments("user")]
    [Arguments("failed")]
    [Arguments("shutdown")]
    public async Task RunAsync_Should_Preserve_The_Original_Turns_Outcome_While_Settling(string outcome)
    {
        _fake.AddSession("child", FakeOpenCode.SessionId);
        var end = outcome switch
        {
            "failed" => TurnEvents.Failed("original failure"),
            "shutdown" => TurnEvents.Interrupted(SessionExecutionInterruptedDataReason.Shutdown),
            "user" => TurnEvents.Interrupted(SessionExecutionInterruptedDataReason.User),
            _ => TurnEvents.Succeeded(),
        };
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("original"), end]);
        var checks = 0;
#pragma warning disable OPENCODE001 // ExperimentalClient.ListJobsAsync and JobStatus.Error arrange the later job outcome.
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                checks++;
                if (checks == 1) { _fake.Emit(TurnEvents.PermissionAsked("late", "child")); }
                return new ExperimentalJobListResponse { Status = 200, Jobs = checks == 1 ? [TurnEvents.SubagentJob("job", FakeOpenCode.SessionId, "child", 100, JobStatus.Error)] : [] };
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.ListJobsAsync and JobStatus.Error arrangement ends here.
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = (_, _) =>
            {
                _fake.Emit(TurnEvents.Failed("later root failure"));
                return new(reply);
            },
        });

        if (outcome is "failed" or "shutdown")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.RunAsync("hello"));
            await Assert.That(error?.Message).IsEqualTo(outcome == "failed" ? "Session error: original failure" : "Session error: the OpenCode server is shutting down");
            await Assert.That(checks).IsEqualTo(0);
        }
        else
        {
            var response = await agent.RunAsync("hello");
            await Assert.That(response.Text).IsEqualTo("original");
            await Assert.That(checks).IsEqualTo(2);
            await _fake.Sessions.GetSessionClient("child").Received(1).ReplyToPermissionAsync("late", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task RunAsync_Should_Surface_Background_Failure_And_Close_The_Bus()
    {
        var failure = new OpenCodeTransportException("background wait failed");
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("original"), TurnEvents.Succeeded()]);
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync arranges the background failure.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ExperimentalSessionWaitResponse>(failure));
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync arrangement ends here.
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();

        var error = await CaptureRunFailureAsync(agent, updates);

        await Assert.That(error).IsSameReferenceAs(failure);
        await AssertOriginalOutputAndClosedBusAsync(updates);
    }

    [Test]
    public async Task RunAsync_Should_Cancel_A_Background_Wait_And_Close_The_Bus()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("original"), TurnEvents.Succeeded()]);
        var waitSessions = new List<string>();
        var waitOptions = new List<OpenCodeRequestOptions?>();
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ListJobsAsync arrange the cancellation entry.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                waitSessions.Add(call.Arg<string>());
                waitOptions.Add(call.ArgAt<OpenCodeRequestOptions?>(1));
                return new ExperimentalSessionWaitResponse { Status = 204 };
            });
        _fake.Experimental.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _fake.Emit(TurnEvents.PermissionAsked("marker"));
                return new ExperimentalJobListResponse { Status = 200, Jobs = [TurnEvents.ShellJob("shell", FakeOpenCode.SessionId, 100, JobStatus.Running)] };
            });
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync and ListJobsAsync arrangement ends here.
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = (_, _) => { entered.TrySetResult(true); return new(reply); },
        });
        var updates = new List<AgentResponseUpdate>();
        var result = CaptureRunFailureAsync(agent, updates, cancellation.Token);
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, result)).IsSameReferenceAs(entered.Task);
            await cancellation.CancelOnWorkerAsync();
            var error = (await Assert.That(await result).IsTypeOf<OperationCanceledException>())!; // The assertion proves cancellation exists.
            await Assert.That(error.CancellationToken).IsEqualTo(cancellation.Token);
        }
        finally
        {
            await cancellation.CancelOnWorkerAsync();
            await result;
        }

        await Assert.That(waitSessions).IsEquivalentTo([FakeOpenCode.SessionId], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(waitOptions).IsEquivalentTo(new OpenCodeRequestOptions?[] { null }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("marker", reply, OpenCodeRequestOptions.NoThrow, Arg.Any<CancellationToken>());
        await _fake.Session.Received(1).InterruptAsync(null, null, CancellationToken.None);
        await AssertOriginalOutputAndClosedBusAsync(updates);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task RunAsync_Should_Finish_From_The_Refresh_When_The_Turn_Ended_During_A_Reconnect(bool unknown, bool rawIdle)
    {
        ISessionMessageInfo idle = rawIdle
            ? new UnknownSessionMessageInfo("idle", JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "idle" }))
            : TurnEvents.StoredIdle("idle_original", SessionMessageIdleOutcome.Succeeded);
        var marker = unknown ? new UnknownEvent("server.connected", JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "server.connected" })) : TurnEvents.Connected();
        _fake.OnPrompt(prompt =>
        {
            StoredMessages([idle, TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = "ab" }]), TurnEvents.StoredUser(prompt.Id.Value!)]);
            return FiniteEvents([TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("a"), marker]);
        });
        var updates = new List<AgentResponseUpdate>();

        var error = await CaptureRunFailureAsync(updates);

        await Assert.That(error).IsNull();
        await Assert.That(TextChunks(updates)).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(string.Concat(TextChunks(updates))).IsEqualTo("ab");
        await Assert.That(updates[^1].Contents.Single().RawRepresentation).IsSameReferenceAs(idle);
        await _fake.Session.Received(1).PromptAsync(Arg.Any<SessionPromptRequest>(), null, Arg.Any<CancellationToken>());
        await AssertRefreshCallsAsync(messageQueries: 1);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments("recovered")]
    [Arguments("queued")]
    [Arguments("open")]
    public async Task RunAsync_Should_Recover_A_Missed_Delivery_Without_Reading_Another_Turn(string scenario)
    {
        _fake.OnPrompt(prompt =>
        {
            var user = TurnEvents.StoredUser(prompt.Id.Value!);
            var idle = TurnEvents.StoredIdle("idle_original", SessionMessageIdleOutcome.Succeeded);
            var ours = TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = scenario == "open" ? "" : "ours" }]);
            StoredMessages(scenario == "queued" ? [] : [TurnEvents.StoredIdle("later_idle", SessionMessageIdleOutcome.Failed),
                TurnEvents.StoredAssistant("later", [new SessionMessageAssistantText { Text = "foreign" }]), TurnEvents.StoredUser("later_user"), idle, ours, user]);
            if (scenario == "open") { StoredMessages([ours, user]); }
            return FiniteEvents(scenario switch
            {
                "queued" => [TurnEvents.Connected(), TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("fresh"), TurnEvents.Succeeded()],
                "open" => [TurnEvents.Connected(), TurnEvents.Text("mid"), TurnEvents.TextEnded("whole"), TurnEvents.Succeeded()],
                _ => [TurnEvents.Connected()],
            });
        });
        var updates = new List<AgentResponseUpdate>();

        var error = await CaptureRunFailureAsync(updates);

        await Assert.That(error).IsNull();
        string[] expected = [scenario switch { "queued" => "fresh", "open" => "whole", _ => "ours" }];
        await Assert.That(TextChunks(updates)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertRefreshCallsAsync(scenario == "open" ? 2 : 1);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Keep_Streaming_Parts_And_Recover_A_Partial_Turn_After_A_Reconnect(bool secondMarker)
    {
        var updates = new List<AgentResponseUpdate>();
        _fake.OnPrompt(prompt =>
        {
            StoredMessages([TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = "" }]), TurnEvents.StoredUser(prompt.Id.Value!)]);
            return [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("a"), TurnEvents.Connected(), TurnEvents.PermissionAsked("continue")];
        });
        var passes = 0;
        _fake.Permissions.ListRequestsAsync(Arg.Any<PermissionRequestListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            passes++;
            if (secondMarker && passes == 1) { _fake.Emit(TurnEvents.Connected()); }
            return new PermissionRequestListResponse { Status = 200, Requests = [], Location = new LocationPublicRef { Directory = "test-directory" } };
        });
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (request, _) =>
            {
                if (request.Id == "continue")
                {
                    _fake.Emit(TurnEvents.Text("c"));
                    _fake.Emit(TurnEvents.TextEnded("abc"));
                    _fake.Emit(TurnEvents.TextStarted(ordinal: 1));
                    _fake.Emit(TurnEvents.Text("d", ordinal: 1));
                    _fake.Emit(TurnEvents.PermissionAsked("checkpoint"));
                    return new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
                }
                await Assert.That(TextChunks(updates)).IsEquivalentTo(["a", "bc", "d"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
                StoredMessages([TurnEvents.StoredIdle("end", SessionMessageIdleOutcome.Succeeded), TurnEvents.StoredAssistant("msg_assistant",
                    [new SessionMessageAssistantText { Text = "abc" }, new SessionMessageAssistantText { Text = "d" }]), TurnEvents.StoredUser(PromptRequest().Id.Value!)]);
                _fake.Emit(TurnEvents.Succeeded());
                _fake.EndBus();
                return new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
            },
        });

        var error = await CaptureRunFailureAsync(agent, updates);

        await Assert.That(error).IsNull();
        await Assert.That(TextChunks(updates)).IsEquivalentTo(["a", "bc", "d"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(passes).IsEqualTo(secondMarker ? 2 : 1);
        await AssertRefreshCallsAsync(secondMarker ? 3 : 2, secondMarker ? 2 : 1);
    }

    [Test]
    [Arguments("failed", false)]
    [Arguments("interrupted", false)]
    [Arguments("failed", true)]
    [Arguments("interrupted", true)]
    public async Task RunAsync_Should_Surface_The_Recovered_Original_Failure(string outcome, bool liveTerminal)
    {
        var trace = ArrangeCancellationTrace();
        _fake.OnPrompt(prompt =>
        {
            var final = new SessionMessageListResponse
            {
                Status = 200,
                Cursor = new ListCursor(),
                Messages = [TurnEvents.StoredIdle("later", SessionMessageIdleOutcome.Succeeded), TurnEvents.StoredIdle("ours", new SessionMessageIdleOutcome(outcome),
                    outcome == "failed" ? new SessionStructuredError { Type = "model_error", Message = "original error" } : null),
                    TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = "ab" }]), TurnEvents.StoredUser(prompt.Id.Value!)],
            };
            StoredMessages(final.Messages);
            if (liveTerminal)
            {
                _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(new SessionMessageListResponse { Status = 200, Cursor = new ListCursor(), Messages = [TurnEvents.StoredUser(prompt.Id.Value!)] }, final);
            }
            IEvent[] events = [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("a"), TurnEvents.ToolStarted("c1", "shell"),
                TurnEvents.ToolCalled("c1", new Dictionary<string, JsonElement>(StringComparer.Ordinal)), TurnEvents.Connected()];
            return FiniteEvents(liveTerminal ? [.. events, TurnEvents.Succeeded()] : events);
        });
        var updates = new List<AgentResponseUpdate>();

        var error = await CaptureRunFailureAsync(updates);

        await Assert.That(error?.Message).IsEqualTo(outcome == "failed" ? "Session error: original error" : null);
        await Assert.That(TextChunks(updates)).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>().Select(content => content.Result))
            .IsEquivalentTo(new object?[] { "Tool execution interrupted: shell" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        string[] expected = outcome == "failed" ? [] : ["jobs"];
        await Assert.That(trace).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RunAsync_Should_Fail_When_An_Observed_Delivery_Is_Missing_From_The_Projection(bool finalRead, bool found)
    {
        var reads = 0;
        _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            reads++;
            var present = (finalRead && reads == 1) || found;
            return new SessionMessageListResponse { Status = 200, Cursor = new ListCursor(), Messages = present ? [TurnEvents.StoredUser(PromptRequest().Id.Value!)] : [] };
        });
        _fake.OnPrompt(prompt =>
        {
            IEvent[] prefix = [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.ToolStarted("c1", "shell"),
                TurnEvents.ToolCalled("c1", new Dictionary<string, JsonElement>(StringComparer.Ordinal)), TurnEvents.Connected()];
            return FiniteEvents(finalRead ? [.. prefix, TurnEvents.Succeeded()] : prefix);
        });
        var updates = new List<AgentResponseUpdate>();

        var error = await CaptureRunFailureAsync(updates);

        await Assert.That(error?.Message).IsEqualTo(found ? null : "Session error: the admitted prompt is missing from stored messages.");
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>().Select(content => content.Result))
            .IsEquivalentTo(new object?[] { "Tool execution interrupted: shell" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(reads).IsEqualTo(finalRead ? 2 : 1);
        await Assert.That(TextChunks(updates)).IsEmpty();
    }

    [Test]
    public async Task RunAsync_Should_Accept_The_Extra_Background_Cycle_For_A_Buffered_Original_Terminal()
    {
        var trace = new List<string>();
        _fake.OnPrompt(prompt =>
        {
            StoredMessages([TurnEvents.StoredIdle("end", SessionMessageIdleOutcome.Succeeded), TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = "ours" }]), TurnEvents.StoredUser(prompt.Id.Value!)]);
            return FiniteEvents([TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Connected(), TurnEvents.Succeeded()]);
        });
        _fake.Jobs([]);
#pragma warning disable OPENCODE001 // ExperimentalClient.WaitForSessionAsync, ExperimentalSessionWaitResponse and ListJobsAsync arrange the quietness-cycle trace.
        _fake.Experimental.WaitForSessionAsync(Arg.Any<string>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("wait"); return new ExperimentalSessionWaitResponse { Status = 204 }; });
        _fake.Experimental.When(client => client.ListJobsAsync(Arg.Any<ExperimentalJobListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()))
            .Do(_ => trace.Add("jobs"));
#pragma warning restore OPENCODE001 // ExperimentalClient.WaitForSessionAsync, ExperimentalSessionWaitResponse and ListJobsAsync arrangement ends here.
        _fake.Session.ListInboxAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("inbox"); return new SessionInboxListResponse { Status = 200, Inbox = [] }; });
        _fake.Sessions.GetActiveAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => { trace.Add("active"); return new SessionActiveResponse { Status = 200, Active = new Dictionary<string, SessionActive>(StringComparer.Ordinal) }; });
        var updates = new List<AgentResponseUpdate>();

        var error = await CaptureRunFailureAsync(updates);

        await Assert.That(error).IsNull();
        await Assert.That(trace).IsEquivalentTo(["wait", "jobs", "inbox", "active", "wait", "jobs", "inbox", "active"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(TextChunks(updates)).IsEquivalentTo(["ours"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await AssertRefreshCallsAsync(messageQueries: 1);
    }

    [Test]
    [Arguments("prompt")]
    [Arguments("admission-list")]
    [Arguments("snapshot-callback")]
    [Arguments("original-callback")]
    [Arguments("consumer")]
    public async Task RunAsync_Should_Refresh_A_Reconnect_Buffered_In_Each_Sequential_Phase(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<bool>>();
        var trace = new List<string>();
        var callbacks = new List<string>();
        var active = 0;
        var peak = 0;
        _fake.OnPrompt(prompt =>
        {
            return phase switch
            {
                "consumer" => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Text("a")],
                "original-callback" => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.PermissionAsked("phase")],
                _ => [],
            };
        });
        HoldAdmissionPhase(phase, trace, entered, releases);
        if (phase == "snapshot-callback") { _fake.PendingPermissions(FakeOpenCode.SessionId, [TurnEvents.PendingPermission("phase")]); }
        TraceRefresh(trace, [TurnEvents.PendingPermission("owned")], phase == "consumer" ? "abc" : "recovered");
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (request, _) =>
            {
                callbacks.Add(request.Id);
                peak = Math.Max(peak, ++active);
                try
                {
                    if (request.Id == "phase") { await HoldPhaseEntryAsync(trace, entered, releases); }
                    return reply;
                }
                finally { active--; }
            },
        });
        var updates = new List<AgentResponseUpdate>();
        var outcome = CaptureFailureAsync(async () =>
        {
            await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: cancellation.Token))
            {
                updates.Add(update);
                if (phase == "consumer" && TextChunks([update]).Contains("a", StringComparer.Ordinal)) { await HoldPhaseEntryAsync(trace, entered, releases); }
                if (TextChunks([update]).Any(text => text is "recovered" or "bc")) { trace.Add("recovered-output"); }
            }
        });
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            _fake.Emit(TurnEvents.Connected());
            _fake.EndBus();
            foreach (var release in releases) { release.TrySetResult(true); }
            await outcome;
        }
        finally { foreach (var release in releases) { release.TrySetResult(true); } _fake.EndBus(); await outcome; }

        await AssertRefreshCallsAsync(messageQueries: 1);
        await Assert.That(await outcome).IsNull();
        await Assert.That(trace).IsEquivalentTo(["phase-enter", "phase-exit", "get-root-location", "location-list", "owned-reply", "messages", "recovered-output"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        string[] ids = phase is "snapshot-callback" or "original-callback" ? ["phase", "owned"] : ["owned"];
        await Assert.That(callbacks).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(peak).IsEqualTo(1);
        await _fake.Session.Received(1).ListRequestsAsync(null, cancellation.Token);
        foreach (var id in ids) { await _fake.Session.Received(1).ReplyToPermissionAsync(id, reply, OpenCodeRequestOptions.NoThrow, cancellation.Token); }
    }

    [Test]
    [Arguments("root", false)]
    [Arguments("list", true)]
    [Arguments("messages", true)]
    [Arguments("messages", false)]
    [Arguments("callback", true)]
    [Arguments("held-update", true)]
    public async Task RunAsync_Should_Preserve_Cancellation_During_Refresh(string phase, bool delivered)
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace();
        if (phase == "held-update")
        {
            await AssertRecoveredUpdateDisposalAsync(cancellation, trace);
            return;
        }
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fake.OnPrompt(prompt =>
        {
            return FiniteEvents(delivered ? [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.Connected()] : [TurnEvents.Connected()]);
        });
        if (phase == "root")
        {
            _fake.Session.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Is<CancellationToken>(token => token.CanBeCanceled))
                .Returns(call => HoldRefreshAsync<SessionResponse>(entered, joined, call.Arg<CancellationToken>()));
        }
        if (phase == "list")
        {
            _fake.Permissions.ListRequestsAsync(Arg.Any<PermissionRequestListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => HoldRefreshAsync<PermissionRequestListResponse>(entered, joined, call.Arg<CancellationToken>()));
        }
        if (phase == "messages")
        {
            _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(call => HoldRefreshAsync<SessionMessageListResponse>(entered, joined, call.Arg<CancellationToken>()));
        }
        if (phase == "callback")
        {
            _fake.Permissions.ListRequestsAsync(Arg.Any<PermissionRequestListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(new PermissionRequestListResponse { Status = 200, Requests = [TurnEvents.PendingPermission("cancel")], Location = new LocationPublicRef { Directory = "test-directory" } });
        }
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (_, token) => await HoldRefreshAsync<SessionPermissionReplyRequest>(entered, joined, token),
        });
        var result = CaptureRunFailureAsync(agent, [], cancellation.Token);
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, result)).IsSameReferenceAs(entered.Task);
            await cancellation.CancelOnWorkerAsync();
            var error = await Assert.That(await result).IsTypeOf<OperationCanceledException>();
            await Assert.That(error?.CancellationToken).IsEqualTo(cancellation.Token);
        }
        finally { await cancellation.CancelOnWorkerAsync(); await result; }

        await Assert.That(joined.Task.Status).IsEqualTo(TaskStatus.RanToCompletion);
        var expected = new List<string>();
        if (phase == "callback") { expected.Add("reject"); }
        expected.Add("cancel-inbox:" + PromptRequest().Id.Value);
        if (delivered) { expected.AddRange(["jobs", "interrupt"]); }
        await Assert.That(trace).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task RunAsync_Should_Not_Refresh_Or_Reply_From_The_Cancellation_Sweep()
    {
        using var cancellation = new CancellationTokenSource();
        var trace = ArrangeCancellationTrace();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<bool>>();
        _fake.Session.CancelInboxAsync(Arg.Any<string>(), null, CancellationToken.None).Returns(async call =>
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            releases.Add(release);
            trace.Add("cancel-inbox:" + call.Arg<string>());
            entered.TrySetResult(true);
            await release.Task;
            return new SessionInboxCancelResponse { Status = 204 };
        });
        _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.PermissionAsked("cancel")]);
        await using var agent = new OpenCodeAgent(_fake.Client, new OpenCodeAgentOptions
        {
            OnPermissionRequest = async (_, _) => { await cancellation.CancelOnWorkerAsync(); throw new OperationCanceledException(cancellation.Token); },
        });
        var result = CaptureFailureAsync(async () => await agent.RunAsync("hello", cancellationToken: cancellation.Token));
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, result)).IsSameReferenceAs(entered.Task);
            _fake.Emit(TurnEvents.Connected());
            _fake.PendingPermissions(FakeOpenCode.SessionId, [TurnEvents.PendingPermission("late")]);
            foreach (var release in releases) { release.TrySetResult(true); }
            var error = await Assert.That(await result).IsTypeOf<OperationCanceledException>();
            await Assert.That(error?.CancellationToken).IsEqualTo(cancellation.Token);
        }
        finally { foreach (var release in releases) { release.TrySetResult(true); } await result; }

        await Assert.That(trace).IsEquivalentTo(["reject", "cancel-inbox:" + PromptRequest().Id.Value, "jobs", "interrupt"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(_fake.Permissions.ReceivedCalls()).IsEmpty();
        await _fake.Session.DidNotReceive().ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        await _fake.Session.Received(1).ListRequestsAsync(null, cancellation.Token);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    private static async Task<T> HoldRefreshAsync<T>(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> joined, CancellationToken token)
    {
        try { return await HoldUntilCancelledAsync<T>(entered, token); }
        finally { joined.TrySetResult(true); }
    }

    private async Task AssertRecoveredUpdateDisposalAsync(CancellationTokenSource cancellation, List<string> trace)
    {
        _fake.OnPrompt(prompt =>
        {
            StoredMessages([TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = "recovered" }]), TurnEvents.StoredUser(prompt.Id.Value!)]);
            return FiniteEvents([TurnEvents.Connected()]);
        });
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();
        var error = await CaptureFailureAsync(async () =>
        {
            await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: cancellation.Token))
            {
                updates.Add(update);
                if (update.Contents.OfType<TextContent>().Any())
                {
                    await cancellation.CancelOnWorkerAsync();
                    break;
                }
            }
        });
        await Assert.That(TextChunks(updates)).IsEquivalentTo(["recovered"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(error).IsNull();
        await Assert.That(trace).IsEquivalentTo(["cancel-inbox:" + PromptRequest().Id.Value, "jobs", "interrupt"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    private static async Task HoldPhaseEntryAsync(List<string> trace, TaskCompletionSource<bool> entered, List<TaskCompletionSource<bool>> releases)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        releases.Add(release);
        trace.Add("phase-enter");
        entered.TrySetResult(true);
        await release.Task;
        trace.Add("phase-exit");
    }

    private void HoldAdmissionPhase(string phase, List<string> trace, TaskCompletionSource<bool> entered, List<TaskCompletionSource<bool>> releases)
    {
        if (phase == "prompt")
        {
            _fake.Session.PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var prompt = call.Arg<SessionPromptRequest>();
                await HoldPhaseEntryAsync(trace, entered, releases);
                return new SessionPromptResponse
                {
                    Status = 200,
                    Prompt = new SessionInboxUser
                    {
                        Id = prompt.Id.Value!,
                        SessionId = FakeOpenCode.SessionId,
                        Delivery = SessionInboxDelivery.Queue,
                        Time = new SessionInboxUserTime { Created = 1 },
                        Payload = new SessionInboxUserPayload { Text = prompt.Text },
                    },
                };
            });
        }
        if (phase == "admission-list")
        {
            _fake.Session.ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                await HoldPhaseEntryAsync(trace, entered, releases);
                return new SessionPermissionListResponse { Status = 200, Requests = [] };
            });
        }
    }

    private void TraceRefresh(List<string> trace, IReadOnlyList<PermissionRequest> pending, string text)
    {
        _fake.TraceRootLocation(trace);
        _fake.Permissions.ListRequestsAsync(Arg.Any<PermissionRequestListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            trace.Add("location-list");
            return new PermissionRequestListResponse { Status = 200, Requests = pending, Location = new LocationPublicRef { Directory = "test-directory" } };
        });
        _fake.TracePermissionReply("owned", trace);
        _fake.TraceStoredMessages(trace, () =>
        {
            return new SessionMessageListResponse
            {
                Status = 200,
                Cursor = new ListCursor(),
                Messages = [TurnEvents.StoredIdle("end", SessionMessageIdleOutcome.Succeeded),
                TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = text }]), TurnEvents.StoredUser(PromptRequest().Id.Value!)]
            };
        });
    }

    private void StoredMessages(IReadOnlyList<ISessionMessageInfo> messages) =>
        _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionMessageListResponse { Status = 200, Messages = messages, Cursor = new ListCursor() });

    private IEnumerable<IEvent> FiniteEvents(IEnumerable<IEvent> events)
    {
        foreach (var current in events) { yield return current; }
        _fake.EndBus();
    }

    private static IEnumerable<string> TextChunks(IEnumerable<AgentResponseUpdate> updates) => updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text);

    private async Task<Exception?> CaptureRunFailureAsync(List<AgentResponseUpdate> updates)
    {
        await using var agent = new OpenCodeAgent(_fake.Client);
        return await CaptureRunFailureAsync(agent, updates);
    }

    private async Task AssertRefreshCallsAsync(int messageQueries, int markers = 1)
    {
        await _fake.Session.Received(markers).GetAsync(null, Arg.Any<CancellationToken>());
        await _fake.Permissions.Received(markers).ListRequestsAsync(Arg.Is<PermissionRequestListRequest>(request => request.Location != null && request.Location.Directory == "test-directory"), null, Arg.Any<CancellationToken>());
        await _fake.Session.Received(messageQueries).ListMessagesAsync(Arg.Is<SessionMessageListRequest>(request => request.Limit == 200 && request.Order == ListOrder.Descending && request.Cursor == null && request.Type == null), null, Arg.Any<CancellationToken>());
    }

    private async Task AssertOriginalOutputAndClosedBusAsync(List<AgentResponseUpdate> updates)
    {
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text)).IsEquivalentTo(["original"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(SubscriptionToken().IsCancellationRequested).IsTrue();
    }

    private static async Task<Exception?> CaptureRunFailureAsync(OpenCodeAgent agent, List<AgentResponseUpdate> updates, CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var update in agent.RunStreamingAsync("hello", cancellationToken: cancellationToken)) { updates.Add(update); }
            return null;
        }
        catch (Exception error) { return error; }
    }

    /// <summary>Runs a turn whose delivery is followed by <paramref name="interposed"/>, then Text("a") and its own success, and returns the run's text.</summary>
    private async Task<string> TextAfterAsync(IEvent interposed)
    {
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), interposed, TurnEvents.Text("a"), TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client);
        var response = await agent.RunAsync("hello");
        return response.Text;
    }

    /// <summary>Returns the token the pump passed to the bus subscription.</summary>
    private CancellationToken SubscriptionToken()
    {
        var call = _fake.Client.Events.ReceivedCalls().Single(item => item.GetMethodInfo().Name == nameof(EventsClient.SubscribeAsync));
        return (CancellationToken)call.GetArguments()[2]!;
    }

    private SessionPromptRequest PromptRequest() =>
        (SessionPromptRequest)_fake.Session.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(SessionClient.PromptAsync)).GetArguments()[0]!;

    private static async Task<List<AgentResponseUpdate>> UpdatesAsync(OpenCodeAgent agent, List<AgentResponseUpdate>? updates = null)
    {
        updates ??= [];
        await foreach (var update in agent.RunStreamingAsync("hello"))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static IEnumerable<IEvent> OpenCallBeforeBusEnds(SessionPromptRequest request, Action endBus)
    {
        yield return TurnEvents.Delivered(request.Id.Value!);
        yield return TurnEvents.ToolStarted("c1", "shell");
        yield return TurnEvents.ToolCalled("c1", new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        endBus();
    }

    private static async IAsyncEnumerable<IEvent> NonConnectedBus()
    {
        await Task.CompletedTask;
        yield return TurnEvents.Text("not connected");
    }

    private IEnumerable<IEvent> EndBeforeTurn(SessionPromptRequest request)
    {
        yield return TurnEvents.Delivered(request.Id.Value!);
        yield return TurnEvents.Text("a");
        _fake.EndBus();
    }

    private IEnumerable<IEvent> FailBeforeTurn(SessionPromptRequest request, Exception failure)
    {
        yield return TurnEvents.Delivered(request.Id.Value!);
        yield return TurnEvents.Text("a");
        _fake.FailBus(failure);
    }

    private static IEvent OtherSessionForm(string type) => type switch
    {
        "created" => new FormCreated
        {
            Id = "form_created",
            Created = 1,
            Data = new FormCreatedData
            {
                Form = new FormInfo { Id = "form_test", SessionId = "ses_other", Title = "Other session", Fields = [] },
            },
        },
        "replied" => new FormReplied
        {
            Id = "form_replied",
            Created = 1,
            Data = new FormRepliedData
            {
                Id = "form_test",
                SessionId = "ses_other",
                Answer = new Dictionary<string, FormValue>(StringComparer.Ordinal),
            },
        },
        "cancelled" => new FormCancelled
        {
            Id = "form_cancelled",
            Created = 1,
            Data = new FormCancelledData { Id = "form_test", SessionId = "ses_other" },
        },
        _ => throw new ArgumentException("Unknown test form type.", nameof(type)),
    };
}
