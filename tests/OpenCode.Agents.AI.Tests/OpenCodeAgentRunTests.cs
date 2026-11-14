using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests;

public sealed class OpenCodeAgentRunTests
{
    private readonly FakeOpenCode _fake = new();

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
        _fake.Sessions.Received(1).GetSessionClient(FakeOpenCode.SessionId);
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
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), unscopedEnd, TurnEvents.Text("a"), TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var response = await agent.RunAsync("hello");

        await Assert.That(response.Text).IsEqualTo("a");
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
        _fake.OnPrompt(request => [TurnEvents.Delivered(request.Id.Value!), TurnEvents.Connected(), TurnEvents.Text("a"), TurnEvents.Succeeded()]);
        await using var agent = new OpenCodeAgent(_fake.Client);

        var response = await agent.RunAsync("hello");

        await Assert.That(response.Text).IsEqualTo("a");
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
        var call = _fake.Client.Events.ReceivedCalls().Single(item => item.GetMethodInfo().Name == nameof(EventsClient.SubscribeAsync));
        var token = (CancellationToken)call.GetArguments()[2]!;
        await Assert.That(token.IsCancellationRequested).IsTrue();
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
        var call = _fake.Client.Events.ReceivedCalls().Single(item => item.GetMethodInfo().Name == nameof(EventsClient.SubscribeAsync));
        var token = (CancellationToken)call.GetArguments()[2]!;
        await Assert.That(token.IsCancellationRequested).IsTrue();
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

    private SessionPromptRequest PromptRequest() =>
        (SessionPromptRequest)_fake.Session.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(SessionClient.PromptAsync)).GetArguments()[0]!;

    private static async Task<List<AgentResponseUpdate>> UpdatesAsync(OpenCodeAgent agent)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("hello"))
        {
            updates.Add(update);
        }

        return updates;
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
