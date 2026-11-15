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
        await Assert.That(await TextAfterAsync(TurnEvents.Connected())).IsEqualTo("a");
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
        await AssertOriginalOutputAndClosedBusAsync(updates);
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
