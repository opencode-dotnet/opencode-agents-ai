using System.Text.Json;
using NSubstitute;
using NSubstitute.Core;
using OpenCode.Agents.AI.Internal.Threading;
using OpenCode.Agents.AI.Permissions;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Agents.AI.Tests.Permissions;

public sealed class PermissionResponderTests
{
    private readonly FakeOpenCode _fake = new();

    [Test]
    public async Task RespondAsync_Should_Reject_With_The_No_Handler_Message_When_There_Is_No_Callback()
    {
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, onPermissionRequest: null);

        await responder.RespondAsync(Request(), CancellationToken.None);

        var calls = Replies(_fake.Session);
        await Assert.That(calls.Length).IsEqualTo(1);
        await Assert.That(calls[0].GetArguments()[0]).IsEqualTo("per_1");
        var reply = (SessionPermissionReplyRequest)calls[0].GetArguments()[1]!; // The SDK signature requires a reply body.
        await Assert.That(reply.Decision).IsEqualTo(PermissionReply.Reject);
        await Assert.That(reply.Message.IsSet).IsTrue();
        const string expected = "No permission handler is configured, so the request was rejected. Continue without this action.";
        await Assert.That(reply.Message.Value).IsEqualTo(expected);
        await Assert.That(PermissionResponder.NoHandlerMessage).IsEqualTo(expected);
    }

    [Test]
    [Arguments("absent")]
    [Arguments("null")]
    [Arguments("feedback")]
    public async Task RespondAsync_Should_Pass_The_Callbacks_Reply_Unchanged(string message)
    {
        var reply = Reply(message);
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) => new(reply));
        using var cancellation = new CancellationTokenSource();

        await responder.RespondAsync(Request(), cancellation.Token);

        var call = Replies(_fake.Session).Single();
        await Assert.That(call.GetArguments()[1]).IsSameReferenceAs(reply);
        await _fake.Session.Received(1).ReplyToPermissionAsync("per_1", reply, OpenCodeRequestOptions.NoThrow, cancellation.Token);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["per_1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RespondAsync_Should_Give_The_Callback_The_Requests_Fields()
    {
        var request = Request() with
        {
            Action = "shell",
            Resources = ["second", "first"],
            Save = ["later", "earlier"],
            Message = "Run command?",
            Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["audit"] = JsonSerializer.SerializeToElement(42) },
            Source = new PermissionSourceTool { Type = "tool", MessageId = "msg_shell", Id = "call_shell" },
        };
        var received = new List<PermissionRequest>();
        using var cancellation = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (input, token) =>
        {
            received.Add(input);
            tokens.Add(token);
            return new(Reply("absent"));
        });

        await responder.RespondAsync(request, cancellation.Token);

        var input = received.Single();
        await Assert.That(input).IsSameReferenceAs(request);
        await Assert.That((input.Id, input.SessionId, input.Action, input.Message)).IsEqualTo(("per_1", FakeOpenCode.SessionId, "shell", "Run command?"));
        await Assert.That(input.Resources).IsEquivalentTo(["second", "first"], CollectionOrdering.Matching);
        await Assert.That(input.Save).IsEquivalentTo(["later", "earlier"], CollectionOrdering.Matching);
        await Assert.That(input.Metadata?["audit"].GetInt32()).IsEqualTo(42);
        await Assert.That(input.Source).IsSameReferenceAs(request.Source);
        await Assert.That((input.Source?.Type, input.Source?.MessageId, input.Source?.Id)).IsEqualTo(("tool", "msg_shell", "call_shell"));
        await Assert.That(tokens).IsEquivalentTo([cancellation.Token], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RespondAsync_Should_Call_The_Callback_Once_Per_Request()
    {
        var ids = new List<string>();
        SessionPermissionReplyRequest[] answers = [Reply("absent"), Reply("feedback")];
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            ids.Add(request.Id);
            return new(answers[ids.Count - 1]);
        });
        var first = Request();

        await responder.RespondAsync(first, CancellationToken.None);
        await responder.RespondAsync(first, CancellationToken.None);
        await responder.RespondAsync(first with { Id = "per_2" }, CancellationToken.None);

        await Assert.That(ids).IsEquivalentTo(["per_1", "per_2"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["per_1", "per_2"], CollectionOrdering.Matching);
        var calls = Replies(_fake.Session);
        await Assert.That(calls[0].GetArguments()[1]).IsSameReferenceAs(answers[0]);
        await Assert.That(calls[1].GetArguments()[1]).IsSameReferenceAs(answers[1]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespondAsync_Should_Answer_A_Child_Sessions_Request_On_The_Child(bool grandchild)
    {
        _fake.AddSession("ses_middle", FakeOpenCode.SessionId);
        var child = _fake.AddSession("ses_child", grandchild ? "ses_middle" : FakeOpenCode.SessionId);
        var received = new List<string>();
        var reply = Reply("feedback");
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            received.Add(request.Id);
            return new(reply);
        });

        await responder.RespondAsync(Request(sessionId: "ses_child"), CancellationToken.None);

        await Assert.That(received).IsEquivalentTo(["per_1"], CollectionOrdering.Matching);
        await child.Received(1).ReplyToPermissionAsync("per_1", reply, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await Assert.That(ReplyIds(child)).IsEquivalentTo(["per_1"], CollectionOrdering.Matching);
        await Assert.That(Replies(_fake.Session)).IsEmpty();
    }

    [Test]
    public async Task RespondAsync_Should_Not_Answer_Another_Sessions_Request()
    {
        var foreign = _fake.AddSession("ses_other", parentId: null);
        var received = new List<string>();
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            received.Add(request.Id);
            return new(Reply("absent"));
        });

        await responder.RespondAsync(Request("per_foreign", "ses_other"), CancellationToken.None);
        await responder.RespondAsync(Request(), CancellationToken.None);

        await Assert.That(received).IsEquivalentTo(["per_1"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["per_1"], CollectionOrdering.Matching);
        await Assert.That(Replies(foreign)).IsEmpty();
    }

    [Test]
    [Arguments("owned", true)]
    [Arguments("unrelated", false)]
    [Arguments("parentless", false)]
    [Arguments("api", false)]
    [Arguments("transport", false)]
    [Arguments("cancelled", true)]
    public async Task OwnsAsync_Should_Ask_The_Server_Once_Per_Session(string scenario, bool owned)
    {
        using var cancellation = new CancellationTokenSource();
        var middle = _fake.AddSession("ses_middle", scenario == "owned" ? FakeOpenCode.SessionId : null);
        var child = _fake.AddSession("ses_child", scenario == "parentless" ? null : "ses_middle");
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, onPermissionRequest: null);
        if (scenario is "api" or "transport" or "cancelled")
        {
            Exception failure = scenario switch
            {
                "api" => new OpenCodeApiException("lookup denied", 403, error: null, rawBody: null),
                "transport" => new OpenCodeTransportException("lookup failed"),
                _ => new OperationCanceledException(cancellation.Token),
            };
            child.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<SessionResponse>(failure));
            if (scenario == "cancelled")
            {
                await cancellation.CancelOnWorkerAsync();
                var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await responder.OwnsAsync("ses_child", cancellation.Token));
                await Assert.That(error).IsSameReferenceAs(failure);
                var recovered = _fake.AddSession("ses_child", FakeOpenCode.SessionId);
                await Assert.That(await responder.OwnsAsync("ses_child", CancellationToken.None)).IsTrue();
                await recovered.Received(1).GetAsync(null, CancellationToken.None);
            }
        }

        if (scenario != "cancelled")
        {
            await Assert.That(await responder.OwnsAsync("ses_child", cancellation.Token)).IsEqualTo(owned);
            await Assert.That(await responder.OwnsAsync("ses_child", cancellation.Token)).IsEqualTo(owned);
            if (scenario is "owned" or "unrelated")
            {
                await Assert.That(await responder.OwnsAsync("ses_middle", cancellation.Token)).IsEqualTo(owned);
                await middle.Received(1).GetAsync(null, cancellation.Token);
            }
            else
            {
                await middle.DidNotReceive().GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
            }
        }

        await child.Received(1).GetAsync(null, cancellation.Token);
        await Assert.That(await responder.OwnsAsync(FakeOpenCode.SessionId, CancellationToken.None)).IsTrue();
        await _fake.Session.DidNotReceive().GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespondAsync_Should_Reject_Then_Rethrow_When_The_Callback_Throws(bool childSession)
    {
        var request = Request(sessionId: childSession ? "ses_child" : FakeOpenCode.SessionId);
        var client = childSession ? _fake.AddSession("ses_child", FakeOpenCode.SessionId) : _fake.Session;
        var failure = new InvalidOperationException("callback failed");
        var entered = new TaskCompletionSource<SessionPermissionReplyRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<SessionPermissionReplyResponse>>();
        client.ReplyToPermissionAsync(Arg.Any<string>(), Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var release = new TaskCompletionSource<SessionPermissionReplyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                releases.Add(release);
                entered.TrySetResult(call.Arg<SessionPermissionReplyRequest>());
                return release.Task;
            });
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) => throw failure);

        var outcome = CaptureFailureAsync(() => responder.RespondAsync(request, CancellationToken.None).AsTask());
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            await Assert.That(outcome.IsCompleted).IsFalse();
            var rejection = await entered.Task;
            await Assert.That(rejection.Decision).IsEqualTo(PermissionReply.Reject);
            await Assert.That(rejection.Message.Value).IsEqualTo(PermissionResponder.HandlerFailedMessage);
            await Assert.That(PermissionResponder.HandlerFailedMessage).IsEqualTo("The permission handler failed, so this request was rejected.");
        }
        finally
        {
            foreach (var release in releases)
            {
                release.TrySetResult(new SessionPermissionReplyResponse { Status = 204 });
            }

            await outcome;
        }

        await Assert.That(await outcome).IsSameReferenceAs(failure);
        await Assert.That(ReplyIds(client)).IsEquivalentTo(["per_1"], CollectionOrdering.Matching);
        await Assert.That(Replies(client).Single().GetArguments()[3]).IsEqualTo(CancellationToken.None);
        if (childSession)
        {
            await Assert.That(Replies(_fake.Session)).IsEmpty();
        }
    }

    [Test]
    [Arguments("transport", false, false)]
    [Arguments("transport", true, false)]
    [Arguments("not-found", false, false)]
    [Arguments("not-found", true, false)]
    [Arguments("untyped-404", false, false)]
    [Arguments("api", false, false)]
    [Arguments("api", true, false)]
    [Arguments("session-not-found", false, false)]
    [Arguments("unknown-500", false, false)]
    [Arguments("untyped-500", false, false)]
    [Arguments("not-found", false, true)]
    [Arguments("not-found", true, true)]
    [Arguments("transport", false, true)]
    [Arguments("untyped-404", false, true)]
    public async Task RespondAsync_Should_Ignore_Not_Found_And_Surface_Other_Reply_Failures_Without_Retrying(string kind, bool childSession, bool priorReject)
    {
        var target = childSession ? _fake.AddSession("ses_child", FakeOpenCode.SessionId) : _fake.Session;
        var requestId = priorReject ? "per_2" : "per_1";
        var failure = new OpenCodeTransportException("reply failed");
        var response = ReplyError(kind, requestId);
        if (kind == "transport")
        {
            FailReply(target, requestId, failure);
        }
        else
        {
            target.ReplyToPermissionAsync(requestId, Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(response);
        }

        var callbacks = new List<string>();
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            callbacks.Add(request.Id);
            return new(new SessionPermissionReplyRequest { Decision = priorReject && request.Id == "per_1" ? PermissionReply.Reject : PermissionReply.Once });
        });
        if (priorReject)
        {
            await responder.RespondAsync(Request(), CancellationToken.None);
        }

        var request = Request(requestId, childSession ? "ses_child" : FakeOpenCode.SessionId);
        var error = await CaptureFailureAsync(() => responder.RespondAsync(request, CancellationToken.None).AsTask());
        await responder.RespondAsync(request, CancellationToken.None);

        if (kind == "not-found")
        {
            await Assert.That(error).IsNull();
            if (priorReject)
            {
                await responder.RespondAsync(Request("per_3"), CancellationToken.None);
            }
        }
        else if (kind == "transport")
        {
            await Assert.That(error).IsSameReferenceAs(failure);
        }
        else
        {
            var api = (await Assert.That(error).IsTypeOf<OpenCodeApiException>())!; // The type assertion proves an API exception exists.
            await Assert.That(api.Status).IsEqualTo(response.Status);
            await Assert.That(api.Error).IsSameReferenceAs(response.Error);
            await Assert.That(api.RawBody).IsEqualTo(response.RawBody);
            var expectedMessage = (priorReject, kind) switch
            {
                (true, _) => "The permission reply for request 'per_2' failed with HTTP 404 (untyped).",
                (_, "api") => "The permission reply for request 'per_1' failed with HTTP 401 (UnauthorizedError).",
                (_, "session-not-found") => "The permission reply for request 'per_1' failed with HTTP 404 (SessionNotFoundError).",
                (_, "unknown-500") => "The permission reply for request 'per_1' failed with HTTP 500 (FuturePermissionError).",
                (_, "untyped-500") => "The permission reply for request 'per_1' failed with HTTP 500 (untyped).",
                _ => "The permission reply for request 'per_1' failed with HTTP 404 (untyped).",
            };
            await Assert.That(api.Message).IsEqualTo(expectedMessage);
        }

        string[] expected = (priorReject, kind) switch
        {
            (false, _) => ["per_1"],
            (_, "not-found") => ["per_1", "per_2", "per_3"],
            _ => ["per_1", "per_2"],
        };
        await Assert.That(callbacks).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await target.Received(1).ReplyToPermissionAsync(requestId, Arg.Is<SessionPermissionReplyRequest>(reply => reply.Decision == PermissionReply.Once), OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        var targetReplies = childSession ? [requestId] : expected;
        await Assert.That(ReplyIds(target)).IsEquivalentTo(targetReplies, CollectionOrdering.Matching);
        if (childSession)
        {
            string[] rootReplies = priorReject ? ["per_1", "per_3"] : [];
            await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(rootReplies, CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespondAsync_Should_Reject_Then_Propagate_Cancellation_When_The_Callback_Is_Cancelled(bool childSession)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        var target = childSession ? _fake.AddSession("ses_child", FakeOpenCode.SessionId) : _fake.Session;
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, async (_, token) =>
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tokens.Add(token);
            using var registration = token.Register(() => cancelled.TrySetResult(true));
            entered.TrySetResult(true);
            await cancelled.Task;
            throw failure;
        });

        var outcome = CaptureFailureAsync(() => responder.RespondAsync(Request(sessionId: childSession ? "ses_child" : FakeOpenCode.SessionId), cancellation.Token).AsTask());
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            await cancellation.CancelOnWorkerAsync();
            await Assert.That(await outcome).IsSameReferenceAs(failure);
        }
        finally
        {
            await cancellation.CancelOnWorkerAsync();
            await outcome;
        }

        await Assert.That(tokens).IsEquivalentTo([cancellation.Token], CollectionOrdering.Matching);
        await target.Received(1).ReplyToPermissionAsync("per_1", Arg.Any<SessionPermissionReplyRequest>(), OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        var call = Replies(target).Single();
        var reply = (SessionPermissionReplyRequest)call.GetArguments()[1]!; // The SDK signature requires a body.
        await Assert.That(reply.Decision).IsEqualTo(PermissionReply.Reject);
        await Assert.That(reply.Message.Value).IsEqualTo(PermissionResponder.HandlerFailedMessage);
        await Assert.That(((CancellationToken)call.GetArguments()[3]!).IsCancellationRequested).IsFalse(); // The SDK signature requires the token value.
        if (childSession)
        {
            await Assert.That(Replies(_fake.Session)).IsEmpty();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespondAsync_Should_Preserve_Both_Failures_When_Callback_Rejection_Fails(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        Exception callbackFailure = cancelled ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("callback failed");
        if (cancelled)
        {
            await cancellation.CancelOnWorkerAsync();
        }

        var replyFailure = new OpenCodeTransportException("rejection failed");
        FailReply(_fake.Session, "per_1", replyFailure);
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) => throw callbackFailure);

        var error = await Assert.ThrowsAsync<AggregateException>(async () => await responder.RespondAsync(Request(), cancellation.Token));

        await Assert.That(error!.InnerExceptions).IsEquivalentTo([callbackFailure, replyFailure], CollectionOrdering.Matching); // ThrowsAsync proves the aggregate exists.
        await Assert.That(error.InnerExceptions[0]).IsSameReferenceAs(callbackFailure);
        await Assert.That(error.InnerExceptions[1]).IsSameReferenceAs(replyFailure);
        await _fake.Session.Received(1).ReplyToPermissionAsync("per_1", Arg.Is<SessionPermissionReplyRequest>(reply => reply.Decision == PermissionReply.Reject && reply.Message.Value == PermissionResponder.HandlerFailedMessage), OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await Assert.That(Replies(_fake.Session).Length).IsEqualTo(1);
    }

    [Test]
    public async Task RespondAsync_Should_Propagate_Non_Sdk_Lookup_Failures_Without_Caching()
    {
        var child = _fake.AddSession("ses_child", FakeOpenCode.SessionId);
        var failure = new ObjectDisposedException("session client");
        child.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SessionResponse>(failure));
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, onPermissionRequest: null);
        var request = Request(sessionId: "ses_child");

        var failures = new List<Exception?>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            failures.Add(await CaptureFailureAsync(() => responder.RespondAsync(request, CancellationToken.None).AsTask()));
        }

        await Assert.That(failures[0]).IsSameReferenceAs(failure);
        await Assert.That(failures[1]).IsSameReferenceAs(failure);
        await child.Received(2).GetAsync(null, CancellationToken.None);
        await Assert.That(Replies(child)).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespondAsync_Should_Reject_When_The_Callback_Returns_No_Reply(bool childSession)
    {
        var target = childSession ? _fake.AddSession("ses_child", FakeOpenCode.SessionId) : _fake.Session;
        var request = Request(sessionId: childSession ? "ses_child" : FakeOpenCode.SessionId);
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId,
            (_, _) => new ValueTask<SessionPermissionReplyRequest>((SessionPermissionReplyRequest)null!)); // Deliberately violates the callback's non-null contract.
        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await responder.RespondAsync(request, cancellation.Token));

        await Assert.That(error?.Message).IsEqualTo("The permission callback returned no reply.");
        await target.Received(1).ReplyToPermissionAsync("per_1", Arg.Any<SessionPermissionReplyRequest>(), OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        var call = Replies(target).Single();
        var rejection = (SessionPermissionReplyRequest)call.GetArguments()[1]!; // The SDK signature requires a reply body.
        await Assert.That(rejection.Decision).IsEqualTo(PermissionReply.Reject);
        await Assert.That(rejection.Message.Value).IsEqualTo(PermissionResponder.HandlerFailedMessage);
        if (childSession)
        {
            await Assert.That(Replies(_fake.Session)).IsEmpty();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RespondAsync_Should_Rethrow_The_Callback_Failure_When_Its_Rejection_Is_Not_Found(bool childSession)
    {
        var target = childSession ? _fake.AddSession("ses_child", FakeOpenCode.SessionId) : _fake.Session;
        var request = Request(sessionId: childSession ? "ses_child" : FakeOpenCode.SessionId);
        target.ReplyToPermissionAsync("per_1", Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ReplyError("not-found", "per_1"));
        var failure = new InvalidOperationException("callback failed");
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) => throw failure);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await responder.RespondAsync(request, CancellationToken.None));

        await Assert.That(error).IsSameReferenceAs(failure);
        await target.Received(1).ReplyToPermissionAsync("per_1", Arg.Is<SessionPermissionReplyRequest>(reply => reply.Decision == PermissionReply.Reject && reply.Message.Value == PermissionResponder.HandlerFailedMessage), OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await Assert.That(ReplyIds(target)).IsEquivalentTo(["per_1"], CollectionOrdering.Matching);
        if (childSession)
        {
            await Assert.That(Replies(_fake.Session)).IsEmpty();
        }
    }

    [Test]
    public async Task ReconcileAsync_Should_Answer_The_Roots_Pending_Requests_In_Order()
    {
        var child = _fake.AddSession("ses_child", FakeOpenCode.SessionId);
        var foreign = _fake.AddSession("ses_foreign", parentId: null);
        var first = Request("per_z") with
        {
            Save = ["save_z", "save_a"],
            Message = "Pending feedback",
            Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["audit"] = JsonSerializer.SerializeToElement(42) },
        };
        var second = Request("per_a");
        _fake.PendingPermissions(FakeOpenCode.SessionId, [first, second]);
        _fake.PendingPermissions("ses_child", [Request("per_child", "ses_child")]);
        _fake.PendingPermissions("ses_foreign", [Request("per_foreign", "ses_foreign")]);
        var received = new List<PermissionRequest>();
        var reply = Reply("feedback");
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            received.Add(request);
            return new(reply);
        });

        await responder.ReconcileAsync(CancellationToken.None);

        await Assert.That(received.Select(request => request.Id)).IsEquivalentTo(["per_z", "per_a"], CollectionOrdering.Matching);
        await Assert.That(received[0]).IsSameReferenceAs(first);
        await Assert.That(received[1]).IsSameReferenceAs(second);
        await Assert.That((received[0].SessionId, received[0].Action, received[0].Message)).IsEqualTo((FakeOpenCode.SessionId, "edit", "Pending feedback"));
        await Assert.That(received[0].Resources).IsEquivalentTo(["file.cs"], CollectionOrdering.Matching);
        await Assert.That(received[0].Save).IsEquivalentTo(["save_z", "save_a"], CollectionOrdering.Matching);
        await Assert.That(received[0].Metadata?["audit"].GetInt32()).IsEqualTo(42);
        await Assert.That(received[0].Source).IsSameReferenceAs(first.Source);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["per_z", "per_a"], CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("per_z", reply, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await _fake.Session.Received(1).ReplyToPermissionAsync("per_a", reply, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await child.DidNotReceive().ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        await foreign.DidNotReceive().ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReconcileAsync_Should_Answer_An_Id_Only_Once_Across_The_Event_Stream(bool snapshotFirst)
    {
        _fake.PendingPermissions(FakeOpenCode.SessionId, [Request("per_shared"), Request("per_new")]);
        _fake.Session.ReplyToPermissionAsync("per_new", Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ReplyError("not-found", "per_new"));
        var ids = new List<string>();
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            ids.Add(request.Id);
            return new(new SessionPermissionReplyRequest { Decision = PermissionReply.Reject });
        });
        if (!snapshotFirst)
        {
            await responder.RespondAsync(TurnEvents.PermissionAsked("per_shared"), CancellationToken.None);
        }

        await responder.ReconcileAsync(CancellationToken.None);
        await responder.RespondAsync(TurnEvents.PermissionAsked("per_shared"), CancellationToken.None);

        await Assert.That(ids).IsEquivalentTo(["per_shared", "per_new"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["per_shared", "per_new"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReconcileAsync_Should_Surface_The_List_Failure_Without_Retrying()
    {
        var failure = new OpenCodeTransportException("snapshot failed");
        _fake.Session.ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SessionPermissionListResponse>(failure));
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, onPermissionRequest: null);

        var error = await Assert.ThrowsAsync<OpenCodeTransportException>(async () => await responder.ReconcileAsync(CancellationToken.None));

        await Assert.That(error).IsSameReferenceAs(failure);
        await _fake.Session.Received(1).ListRequestsAsync(null, CancellationToken.None);
        await Assert.That(Replies(_fake.Session)).IsEmpty();
    }

    [Test]
    public async Task ReconcileAsync_Should_Apply_The_Selected_Not_Found_Policy()
    {
        _fake.PendingPermissions(FakeOpenCode.SessionId, [Request("per_missing"), Request("per_callback")]);
        foreach (var id in new[] { "per_missing", "per_callback" })
        {
            _fake.Session.ReplyToPermissionAsync(id, Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(ReplyError("not-found", id));
        }

        var failure = new InvalidOperationException("handler failed");
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
            request.Id == "per_callback" ? throw failure : new(Reply("absent")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await responder.ReconcileAsync(CancellationToken.None));

        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["per_missing", "per_callback"], CollectionOrdering.Matching);
        var cleanup = Replies(_fake.Session)[1];
        var reply = (SessionPermissionReplyRequest)cleanup.GetArguments()[1]!; // The SDK requires a reply body.
        await Assert.That(reply.Decision).IsEqualTo(PermissionReply.Reject);
        await Assert.That(reply.Message.Value).IsEqualTo(PermissionResponder.HandlerFailedMessage);
        await Assert.That(cleanup.GetArguments()[3]).IsEqualTo(CancellationToken.None);
    }

    [Test]
    [Arguments("absent", "reject", "success", true)]
    [Arguments("null", "reject", "success", true)]
    [Arguments("empty", "reject", "success", true)]
    [Arguments("space", "reject", "success", false)]
    [Arguments("feedback", "reject", "success", false)]
    [Arguments("absent", "once", "success", false)]
    [Arguments("absent", "always", "success", false)]
    [Arguments("absent", "reject", "not-found", false)]
    [Arguments("absent", "reject", "transport", false)]
    [Arguments("absent", "reject", "cleanup", false)]
    public async Task RespondAsync_Should_Record_Only_Accepted_Message_Less_Rejects(string message, string decision, string outcome, bool recorded)
    {
        var child = _fake.AddSession("ses_child", FakeOpenCode.SessionId);
        var reply = Reply(message) with
        {
            Decision = decision switch { "reject" => PermissionReply.Reject, "always" => PermissionReply.Always, _ => PermissionReply.Once },
            Message = message switch { "empty" => "", "space" => " ", _ => Reply(message).Message },
        };
        var failure = new OpenCodeTransportException("reply failed");
        if (outcome == "transport")
        {
            FailReply(child, "per_child", failure);
        }
        else if (outcome == "not-found")
        {
            child.ReplyToPermissionAsync("per_child", Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(ReplyError("not-found", "per_child"));
        }

        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId,
            (_, _) => outcome == "cleanup" ? throw failure : new(reply));
        await Assert.That(responder.HasRejectedWithoutMessage("ses_child")).IsFalse();
        var error = await CaptureFailureAsync(() => responder.RespondAsync(Request("per_child", "ses_child"), CancellationToken.None).AsTask());

        await Assert.That(responder.HasRejectedWithoutMessage("ses_child")).IsEqualTo(recorded);
        await Assert.That(responder.HasRejectedWithoutMessage(FakeOpenCode.SessionId)).IsFalse();
        await Assert.That(responder.HasRejectedWithoutMessage("ses_other")).IsFalse();
        await Assert.That(ReplyIds(child)).IsEquivalentTo(["per_child"], CollectionOrdering.Matching);
        if (outcome == "cleanup")
        {
            await Assert.That(((SessionPermissionReplyRequest)Replies(child)[0].GetArguments()[1]!).Message.Value).IsEqualTo(PermissionResponder.HandlerFailedMessage); // The SDK requires the reply body.
        }
        else
        {
            await Assert.That(Replies(child)[0].GetArguments()[1]).IsSameReferenceAs(reply);
        }

        if (outcome is "transport" or "cleanup")
        {
            await Assert.That(error).IsSameReferenceAs(failure);
        }
        else
        {
            await Assert.That(error).IsNull();
        }
    }

    [Test]
    public async Task ReconcileLocationAsync_Should_Answer_Only_Owned_Requests_In_Returned_Order()
    {
        using var cancellation = new CancellationTokenSource();
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var grandchild = _fake.AddSession("grandchild", "child");
        var foreign = _fake.AddSession("foreign", parentId: null);
        _fake.AddSession("negative", parentId: null);
        var root = FakeOpenCode.SessionInfo(FakeOpenCode.SessionId, parentId: null, directory: "root-location");
        _fake.Session.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SessionResponse { Status = 200, Session = root });
        PermissionRequest[] requests = [Request("child_b-request", "child"), Request("foreign-request", "foreign"),
            Request("root_z-request"), Request("grandchild-request", "grandchild"), Request("root_a-request"), Request("negative-request", "negative")];
        LocationPermissions(requests);
        var received = new List<PermissionRequest>();
        var reply = Reply("feedback");
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, token) =>
        {
            received.Add(request);
            return new(reply);
        });
        await Assert.That(await responder.OwnsAsync("negative", cancellation.Token)).IsFalse();
        var negative = _fake.AddSession("negative", FakeOpenCode.SessionId);

        await responder.ReconcileLocationAsync(_fake.Permissions, cancellation.Token);

        await Assert.That(received.Select(request => request.Id)).IsEquivalentTo(
            ["child_b-request", "root_z-request", "grandchild-request", "root_a-request"], CollectionOrdering.Matching);
        for (var index = 0; index < received.Count; index++)
        {
            var expected = requests[new[] { 0, 2, 3, 4 }[index]];
            await Assert.That(received[index]).IsSameReferenceAs(expected);
            await _fake.Sessions.GetSessionClient(expected.SessionId).Received(1)
                .ReplyToPermissionAsync(expected.Id, reply, OpenCodeRequestOptions.NoThrow, cancellation.Token);
        }
        await Assert.That(ReplyIds(child)).IsEquivalentTo(["child_b-request"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(grandchild)).IsEquivalentTo(["grandchild-request"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["root_z-request", "root_a-request"], CollectionOrdering.Matching);
        await Assert.That(Replies(foreign)).IsEmpty();
        await Assert.That(Replies(negative)).IsEmpty();
        await negative.DidNotReceive().GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        await _fake.Session.Received(1).GetAsync(null, cancellation.Token);
        await _fake.Permissions.Received(1).ListRequestsAsync(Arg.Is<PermissionRequestListRequest>(query => query.Location != null && query.Location.Directory == "root-location"), null, cancellation.Token);
        await child.DidNotReceive().ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        await Assert.That(_fake.Sessions.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(SessionsClient.ListSessionsAsync))).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReconcileLocationAsync_Should_Attempt_An_Id_Once_Across_Snapshot_And_Live_Events(bool snapshotFirst)
    {
        var child = _fake.AddSession("child", FakeOpenCode.SessionId);
        var moved = _fake.AddSession("moved", FakeOpenCode.SessionId, directory: "other-location");
        LocationPermissions([Request("shared"), Request("fresh", "child")]);
        child.ReplyToPermissionAsync("fresh", Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ReplyError("not-found", "fresh"));
        var ids = new List<string>();
        var reply = Reply("feedback");
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            ids.Add(request.Id);
            return new(reply);
        });
        if (!snapshotFirst) { await responder.RespondAsync(Request("shared"), CancellationToken.None); }

        await responder.ReconcileLocationAsync(_fake.Permissions, CancellationToken.None);
        await responder.RespondAsync(TurnEvents.PermissionAsked("shared"), CancellationToken.None);
        await responder.RespondAsync(Request("fresh", "child"), CancellationToken.None);

        await Assert.That(ids).IsEquivalentTo(["shared", "fresh"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["shared"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(child)).IsEquivalentTo(["fresh"], CollectionOrdering.Matching);
        await _fake.Session.Received(1).ReplyToPermissionAsync("shared", reply, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await child.Received(1).ReplyToPermissionAsync("fresh", reply, OpenCodeRequestOptions.NoThrow, CancellationToken.None);
        await moved.DidNotReceive().GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        await moved.DidNotReceive().ListRequestsAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        await _fake.Permissions.Received(1).ListRequestsAsync(Arg.Is<PermissionRequestListRequest>(query => query.Location != null && query.Location.Directory == "test-directory"), null, CancellationToken.None);
    }

    [Test]
    [Arguments("root")]
    [Arguments("list")]
    [Arguments("callback")]
    [Arguments("reply")]
    [Arguments("rejection")]
    [Arguments("joined-reply")]
    [Arguments("serial-callback")]
    public async Task ReconcileLocationAsync_Should_Surface_Failures_And_Join_Replies(string stage)
    {
        if (stage is "joined-reply" or "serial-callback")
        {
            await AssertLocationJoinAsync(stage);
            return;
        }
        using var cancellation = new CancellationTokenSource();
        var failure = new OpenCodeTransportException("location pass failed");
        var rejection = new OpenCodeTransportException("rejection failed");
        LocationPermissions([Request("first"), Request("second")]);
        if (stage == "root")
        {
            _fake.Session.GetAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<SessionResponse>(failure));
        }
        if (stage == "list")
        {
            _fake.Permissions.ListRequestsAsync(Arg.Any<PermissionRequestListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<PermissionRequestListResponse>(failure));
        }
        if (stage is "reply" or "rejection") { FailReply(_fake.Session, "first", stage == "reply" ? failure : rejection); }
        var callbacks = new List<string>();
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (request, _) =>
        {
            callbacks.Add(request.Id);
            return stage is "callback" or "rejection" ? throw failure : new(Reply("feedback"));
        });

        var error = await CaptureFailureAsync(() => responder.ReconcileLocationAsync(_fake.Permissions, cancellation.Token).AsTask());

        if (stage == "rejection")
        {
            var aggregate = (await Assert.That(error).IsTypeOf<AggregateException>())!; // The assertion establishes the aggregate.
            await Assert.That(aggregate.InnerExceptions).IsEquivalentTo(new Exception[] { failure, rejection }, CollectionOrdering.Matching);
            await Assert.That(aggregate.InnerExceptions[0]).IsSameReferenceAs(failure);
            await Assert.That(aggregate.InnerExceptions[1]).IsSameReferenceAs(rejection);
        }
        else { await Assert.That(error).IsSameReferenceAs(failure); }
        string[] expected = stage is "root" or "list" ? [] : ["first"];
        await Assert.That(callbacks).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await _fake.Session.Received(1).GetAsync(null, cancellation.Token);
        await Assert.That(_fake.Permissions.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(PermissionsClient.ListRequestsAsync))).IsEqualTo(stage == "root" ? 0 : 1);
        if (expected.Length != 0)
        {
            await Assert.That(Replies(_fake.Session).Single().GetArguments()[3]).IsEqualTo(stage == "reply" ? cancellation.Token : CancellationToken.None);
        }
    }

    private void LocationPermissions(IReadOnlyList<PermissionRequest> requests) =>
        _fake.Permissions.ListRequestsAsync(Arg.Any<PermissionRequestListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PermissionRequestListResponse { Status = 200, Requests = requests, Location = new LocationPublicRef { Directory = "test-directory" } });

    private async Task AssertLocationJoinAsync(string stage)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = new List<TaskCompletionSource<bool>>();
        var callbacks = new List<string>();
        var reply = Reply("feedback");
        LocationPermissions([Request("first"), Request("second")]);
        if (stage == "joined-reply")
        {
            _fake.Session.ReplyToPermissionAsync("first", reply, OpenCodeRequestOptions.NoThrow, CancellationToken.None).Returns(async _ =>
            {
                await HoldFirstLocationOperationAsync(entered, releases);
                return new SessionPermissionReplyResponse { Status = 204 };
            });
        }
        var responder = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, async (request, _) =>
        {
            callbacks.Add(request.Id);
            if (stage == "serial-callback" && request.Id == "first") { await HoldFirstLocationOperationAsync(entered, releases); }
            return reply;
        });
        var outcome = CaptureFailureAsync(() => responder.ReconcileLocationAsync(_fake.Permissions, CancellationToken.None).AsTask());
        try
        {
            await Assert.That(await Task.WhenAny(entered.Task, outcome)).IsSameReferenceAs(entered.Task);
            await Assert.That(callbacks).IsEquivalentTo(["first"], CollectionOrdering.Matching);
            await Assert.That(outcome.IsCompleted).IsFalse();
        }
        finally
        {
            foreach (var release in releases) { release.TrySetResult(true); }
            await outcome;
        }
        await Assert.That(await outcome).IsNull();
        await Assert.That(callbacks).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);
        await Assert.That(ReplyIds(_fake.Session)).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);
    }

    private static async Task HoldFirstLocationOperationAsync(TaskCompletionSource<bool> entered, List<TaskCompletionSource<bool>> releases)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        releases.Add(release);
        entered.TrySetResult(true);
        await release.Task;
    }

    private static PermissionRequest Request(string id = "per_1", string sessionId = FakeOpenCode.SessionId) => new()
    {
        Id = id,
        SessionId = sessionId,
        Action = "edit",
        Resources = ["file.cs"],
        Source = new PermissionSourceTool { Type = "tool", MessageId = "msg_edit", Id = "call_edit" },
    };

    private static SessionPermissionReplyRequest Reply(string message) => new()
    {
        Decision = PermissionReply.Once,
        Message = message switch { "absent" => default, "null" => Optional<string?>.Null, _ => "caller feedback" },
    };

    private static ICall[] Replies(SessionClient client) => [.. client.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(SessionClient.ReplyToPermissionAsync))];

    private static IEnumerable<string> ReplyIds(SessionClient client) => Replies(client).Select(call => call.GetArguments()[0]).Cast<string>();

    private static void FailReply(SessionClient client, string requestId, Exception failure) =>
        client.ReplyToPermissionAsync(requestId, Arg.Any<SessionPermissionReplyRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SessionPermissionReplyResponse>(failure));

    private static SessionPermissionReplyResponse ReplyError(string kind, string requestId)
    {
        const string unknownTag = "FuturePermissionError";
        IOpenCodeError? error = kind switch
        {
            "not-found" => new PermissionNotFoundError { RequestId = requestId, Message = "already settled" },
            "api" => new UnauthorizedError { Message = "reply denied" },
            "session-not-found" => new SessionNotFoundError { SessionId = "ses_missing", Message = "session missing" },
            "unknown-500" => new UnknownOpenCodeError(unknownTag, JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["_tag"] = unknownTag,
                ["message"] = "server failed",
            })),
            _ => null,
        };
        return new SessionPermissionReplyResponse
        {
            IsError = true,
            Status = kind switch { "api" => 401, "unknown-500" or "untyped-500" => 500, _ => 404 },
            Error = error,
            RawBody = error is null ? "reply denied" : JsonSerializer.Serialize(error, error.GetType()),
        };
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
