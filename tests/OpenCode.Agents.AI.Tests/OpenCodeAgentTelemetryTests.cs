using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenCode.Agents.AI.Internal.Threading;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests;

public sealed class OpenCodeAgentTelemetryTests
{
    private readonly FakeOpenCode _fake = new();

    [Test]
    [Arguments("a")]
    [Arguments("b")]
    [Arguments("c")]
    [Arguments("d")]
    [Arguments("e")]
    [Arguments("f")]
    [Arguments("g")]
    public async Task RunAsync_Should_Count_Each_Step_Cost_Once_Across_A_Reconnect(string row)
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();
        var tokens = new TokenUsageInfo { Input = 12, Output = 7, Reasoning = 3, Cache = new TokenUsageInfoCache { Read = 2, Write = 0 } };
        var failure = new SessionStructuredError { Type = "model_error", Message = "step failed" };
        _fake.OnPrompt(prompt =>
        {
            var assistant = TurnEvents.StoredAssistant("recovered", row == "d" ? [] : [new SessionMessageAssistantText { Text = "recovered" }],
                tokens, row == "d" ? SessionMessageAssistantFinish.Error : SessionMessageAssistantFinish.Stop)
                with { Cost = row switch { "a" => .01, "c" => null, "d" => .05, _ => .02 }, Error = row == "d" ? failure : null };
            var user = TurnEvents.StoredUser(prompt.Id.Value!);
            var final = new SessionMessageListResponse
            {
                Status = 200, Cursor = new ListCursor(),
                Messages = [TurnEvents.StoredIdle("end", SessionMessageIdleOutcome.Succeeded), assistant, user],
            };
            var initial = row switch
            {
                "f" => final with { Messages = [assistant with { Finish = null, Tokens = null, Cost = null, Content = [] }, user] },
                "g" => final with { Messages = [assistant, user] },
                _ => final,
            };
            _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
                .Returns(initial, final);
            IEvent[] prefix = [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.StepStarted("model_a", "provider_a")];
            return FiniteEvents(row switch
            {
                "a" => [.. prefix, TurnEvents.StepEnded(12, 7, 3, 2, .01, "recovered"), TurnEvents.Connected(), TurnEvents.Succeeded()],
                "e" => [.. prefix, new SessionStepFailed
                {
                    Id = "failed-step", Created = 1,
                    Durable = new SessionStepFailedDurable { AggregateId = FakeOpenCode.SessionId, Seq = 4, Version = 1 },
                    Data = new SessionStepFailedData { SessionId = FakeOpenCode.SessionId, AssistantMessageId = "failed", Error = failure, Tokens = tokens, Cost = .05 },
                }, TurnEvents.Failed("step failed")],
                "g" => [.. prefix, TurnEvents.Connected(), TurnEvents.StepEnded(12, 7, 3, 2, .02, "recovered"), TurnEvents.Succeeded()],
                _ => [.. prefix, TurnEvents.Connected(), TurnEvents.Succeeded()],
            });
        });
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();

        var error = await CaptureFailureAsync(async () =>
        {
            await foreach (var update in agent.RunStreamingAsync("hello")) { updates.Add(update); }
        });

        await Assert.That(error?.Message).IsEqualTo(row == "e" ? "Session error: step failed" : null);
        double? cost = row switch { "a" => .01, "c" or "d" or "e" => null, _ => .02 };
        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags("2.3.0", "model_a", "provider_a", cost), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var usage = updates.SelectMany(update => update.Contents).OfType<UsageContent>().ToArray();
        await Assert.That(usage.Select(content => content.Details.InputTokenCount))
            .IsEquivalentTo(row is "d" or "e" ? [] : new long?[] { 12 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(usage.Select(content => content.Details.OutputTokenCount))
            .IsEquivalentTo(row is "d" or "e" ? [] : new long?[] { 7 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(usage.Select(content => content.Details.ReasoningTokenCount))
            .IsEquivalentTo(row is "d" or "e" ? [] : new long?[] { 3 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(usage.Select(content => content.Details.CachedInputTokenCount))
            .IsEquivalentTo(row is "d" or "e" ? [] : new long?[] { 2 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text))
            .IsEquivalentTo(row is "d" or "e" ? [] : new[] { "recovered" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunStreamingAsync_Should_Keep_The_Captured_Activity_Between_Pulls()
    {
        using var capture = new TelemetryCapture();
        using var caller = capture.Source.StartActivity("caller");
        IEvent[] sent = [];
        _fake.OnPrompt(prompt => sent = OriginalSteps(prompt));
        await using var agent = new OpenCodeAgent(_fake.Client);
        await using var reader = agent.RunStreamingAsync("hello").GetAsyncEnumerator();
        var updates = new List<AgentResponseUpdate>();
        while (await reader.MoveNextAsync())
        {
            updates.Add(reader.Current);
            if (reader.Current.Contents.OfType<TextContent>().Any()) { break; }
        }
        using var child = capture.Source.StartActivity("consumer");

        while (await reader.MoveNextAsync()) { updates.Add(reader.Current); }

        await Assert.That(caller?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags("2.3.0", "model_b", "provider_b", .03), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(child?.TagObjects.ToArray()).IsEmpty();
        await Assert.That(updates.SelectMany(update => update.Contents).Select(content => content.RawRepresentation))
            .IsEquivalentTo(sent.Cast<object?>(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<UsageContent>().Select(content => content.Details.InputTokenCount))
            .IsEquivalentTo(new long?[] { 10, 20 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunStreamingAsync_Should_Capture_At_First_Pull_Not_Enumerable_Creation()
    {
        using var capture = new TelemetryCapture();
        using var creation = capture.Source.StartActivity("creation");
        _fake.OnPrompt(OriginalSteps);
        await using var agent = new OpenCodeAgent(_fake.Client);
        var deferred = agent.RunStreamingAsync("hello");
        using var enumeration = capture.Source.StartActivity("enumeration");
        var text = new List<string>();

        await foreach (var update in deferred) { text.AddRange(update.Contents.OfType<TextContent>().Select(content => content.Text)); }

        await Assert.That(creation?.TagObjects.ToArray()).IsEmpty();
        await Assert.That(enumeration?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags("2.3.0", "model_b", "provider_b", .03), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(text).IsEquivalentTo(["answer"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunAsync_Should_Not_Query_The_Server_Without_An_Activity()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            _fake.OnPrompt(OriginalSteps);
            await using var agent = new OpenCodeAgent(_fake.Client);

            var response = await agent.RunAsync("hello");

            await Assert.That(response.Text).IsEqualTo("answer");
            await _fake.Server.DidNotReceive().GetInfoAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        }
        finally { Activity.Current = previous; }
    }

    [Test]
    [Arguments("success")]
    [Arguments("api")]
    [Arguments("transport")]
    [Arguments("local")]
    public async Task RunAsync_Should_Fetch_The_Server_Version_Once_Per_Agent(string outcome)
    {
        _fake.OnPrompt(OriginalSteps);
        Exception? failure = outcome switch
        {
            "api" => new OpenCodeApiException("denied", 403, error: null, rawBody: null),
            "transport" => new OpenCodeTransportException("offline"),
            "local" => new InvalidOperationException("local"),
            _ => null,
        };
        _fake.Server.GetInfoAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => failure is null ? Task.FromResult(FakeOpenCode.ServerInfo()) : Task.FromException<ServerInfoResponse>(failure));
        using var capture = new TelemetryCapture();
        await using var agent = new OpenCodeAgent(_fake.Client);
        for (var run = 0; run < 2; run++)
        {
            using var activity = capture.Source.StartActivity();
            var response = await agent.RunAsync("hello");

            await _fake.Server.Received(1).GetInfoAsync(null, Arg.Any<CancellationToken>());
            await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(
                ExpectedTags(outcome == "success" ? "2.3.0" : null, "model_b", "provider_b", .03), TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(response.Text).IsEqualTo("answer");
            await Assert.That(response.Usage?.InputTokenCount).IsEqualTo(30);
            await Assert.That(response.Usage?.OutputTokenCount).IsEqualTo(7);
        }
    }

    [Test]
    public async Task RunAsync_Should_Not_Consume_The_Server_Lookup_Without_Text()
    {
        using var capture = new TelemetryCapture();
        await using var agent = new OpenCodeAgent(_fake.Client);
        foreach (ChatMessage[] messages in new ChatMessage[][] { [], [new(ChatRole.User, "")] })
        {
            using var empty = capture.Source.StartActivity();
            var response = await agent.RunAsync(messages);

            await Assert.That(response.Messages).IsEmpty();
            await Assert.That(empty?.TagObjects.ToArray()).IsEmpty();
            await _fake.Server.DidNotReceive().GetInfoAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
            await _fake.Sessions.DidNotReceive().CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
            await _fake.Session.DidNotReceive().PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        }
        using var nonempty = capture.Source.StartActivity();
        _fake.OnPrompt(OriginalSteps);
        var final = await agent.RunAsync("hello");
        await _fake.Server.Received(1).GetInfoAsync(null, Arg.Any<CancellationToken>());
        await Assert.That(final.Text).IsEqualTo("answer");
        await Assert.That(nonempty?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags("2.3.0", "model_b", "provider_b", .03), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunAsync_Should_Share_The_Only_InFlight_Server_Lookup()
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var release = new TaskCompletionSource<ServerInfoResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        _fake.Server.GetInfoAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => { tokens.Add(call.Arg<CancellationToken>()); return release.Task; });
        await using var agent = new OpenCodeAgent(_fake.Client);
        var one = CaptureFailureAsync(async () => await agent.RunAsync("first", cancellationToken: first.Token));
        var two = CaptureFailureAsync(async () => await agent.RunAsync("second", cancellationToken: second.Token));
        try
        {
            await _fake.Server.Received(1).GetInfoAsync(null, first.Token);
            await Assert.That(tokens).IsEquivalentTo([first.Token], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await first.CancelOnWorkerAsync();
            await second.CancelOnWorkerAsync();
            release.TrySetResult(FakeOpenCode.ServerInfo());
            var firstError = await Assert.That(await one).IsTypeOf<OperationCanceledException>();
            var secondError = await Assert.That(await two).IsTypeOf<OperationCanceledException>();
            await Assert.That(firstError?.CancellationToken).IsEqualTo(first.Token);
            await Assert.That(secondError?.CancellationToken).IsEqualTo(second.Token);
            await _fake.Session.DidNotReceive().PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await first.CancelOnWorkerAsync();
            await second.CancelOnWorkerAsync();
            release.TrySetResult(FakeOpenCode.ServerInfo());
            await Task.WhenAll(one, two);
        }
    }

    [Test]
    public async Task RunAsync_Should_Preserve_Caller_Cancellation_During_Server_Lookup()
    {
        using var capture = new TelemetryCapture();
        using var cancellation = new CancellationTokenSource();
        _fake.Server.GetInfoAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await cancellation.CancelOnWorkerAsync();
                throw new OperationCanceledException(call.Arg<CancellationToken>());
            });
        await using var agent = new OpenCodeAgent(_fake.Client);
        using (var cancelled = capture.Source.StartActivity())
        {
            var failure = await CaptureFailureAsync(async () => await agent.RunAsync("hello", cancellationToken: cancellation.Token));

            await _fake.Server.Received(1).GetInfoAsync(null, cancellation.Token);
            var error = await Assert.That(failure).IsTypeOf<OperationCanceledException>();
            await Assert.That(error?.CancellationToken).IsEqualTo(cancellation.Token);
            await _fake.Session.DidNotReceive().PromptAsync(Arg.Any<SessionPromptRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
            await Assert.That(cancelled?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags(null, null, null, null), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        _fake.OnPrompt(OriginalSteps);
        using var later = capture.Source.StartActivity();
        var response = await agent.RunAsync("hello");
        await Assert.That(response.Text).IsEqualTo("answer");
        await _fake.Server.Received(1).GetInfoAsync(null, cancellation.Token);
        await Assert.That(later?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags(null, "model_b", "provider_b", .03), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunAsync_Should_Only_Tag_Original_Turn_Steps()
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();
        _fake.AddSession("child", FakeOpenCode.SessionId);
        _fake.AddSession("foreign", parentId: null);
        _fake.OnPrompt(prompt =>
        [
            TurnEvents.StepStarted("old", "old"), TurnEvents.StepEnded(99, 99, 0, 0, 9, "old"),
            TurnEvents.Delivered(prompt.Id.Value!),
            TurnEvents.StepStarted("foreign", "foreign", sessionId: "foreign"), TurnEvents.StepEnded(99, 99, 0, 0, 9, sessionId: "foreign"),
            TurnEvents.StepStarted("child", "child", sessionId: "child"), TurnEvents.StepEnded(99, 99, 0, 0, 9, sessionId: "child"),
            .. OriginalSteps(prompt).Skip(1),
            TurnEvents.StepStarted("wake", "wake"), TurnEvents.StepEnded(99, 99, 0, 0, 9, "wake"), TurnEvents.Text("wake"),
            TurnEvents.StepStarted("child", "child", sessionId: "child"), TurnEvents.StepEnded(99, 99, 0, 0, 9, sessionId: "child"),
            TurnEvents.Succeeded("child"), TurnEvents.Succeeded(),
        ]);
        await using var agent = new OpenCodeAgent(_fake.Client);
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync("hello")) { updates.Add(update); }

        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags("2.3.0", "model_b", "provider_b", .03), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text))
            .IsEquivalentTo(["answer"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(updates.SelectMany(update => update.Contents).OfType<UsageContent>().Select(content => content.Details.InputTokenCount))
            .IsEquivalentTo(new long?[] { 10, 20 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunAsync_Should_Reset_Cost_For_The_Next_Run()
    {
        using var capture = new TelemetryCapture();
        await using var agent = new OpenCodeAgent(_fake.Client);
        var totals = new List<object?>();
        foreach (var cost in new[] { .01, .02 })
        {
            _fake.OnPrompt(prompt => [TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.StepEnded(1, 2, 0, 0, cost), TurnEvents.Succeeded()]);
            using var activity = capture.Source.StartActivity();
            await agent.RunAsync("hello");
            totals.Add(activity?.GetTagItem("opencode.cost"));
            await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags("2.3.0", null, null, cost), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        await Assert.That(totals).IsEquivalentTo(new object?[] { .01, .02 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await _fake.Server.Received(1).GetInfoAsync(null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_Should_Not_Share_Server_Version_Between_Agents()
    {
        _fake.Server.GetInfoAsync(Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(FakeOpenCode.ServerInfo("first"), FakeOpenCode.ServerInfo("second"));
        using var capture = new TelemetryCapture();
        foreach (var version in new[] { "first", "second" })
        {
            using var activity = capture.Source.StartActivity();
            await using var agent = new OpenCodeAgent(_fake.Client);
            await agent.RunAsync("hello");

            await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(ExpectedTags(version, null, null, null), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        await _fake.Server.Received(2).GetInfoAsync(null, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Tag_The_Current_Activity_With_The_Session_Id(bool attached)
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity("caller");
        activity?.SetTag("caller", "kept");
        activity?.SetTag("gen_ai.provider.name", "opencode");
        KeyValuePair<string, object?>[]? atAdmission = null;
        _fake.OnPrompt(prompt =>
        {
            atAdmission = activity?.TagObjects.ToArray();
            return OriginalSteps(prompt);
        });
        await using var agent = new OpenCodeAgent(_fake.Client);
        var session = attached ? await agent.CreateSessionAsync(FakeOpenCode.SessionId) : await agent.CreateSessionAsync();

        var response = await agent.RunAsync("hello", session);

        await Assert.That(atAdmission).IsEquivalentTo(new KeyValuePair<string, object?>[]
        {
            new("caller", "kept"), new("gen_ai.provider.name", "opencode"),
            new("gen_ai.conversation.id", "ses_test"), new("opencode.server.version", "2.3.0"),
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(new KeyValuePair<string, object?>[]
        {
            new("caller", "kept"), new("gen_ai.provider.name", "opencode"),
            new("gen_ai.conversation.id", "ses_test"), new("opencode.server.version", "2.3.0"),
            new("gen_ai.response.model", "model_b"), new("opencode.model.provider_id", "provider_b"), new("opencode.cost", .03),
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(response.Text).IsEqualTo("answer");
        await Assert.That(response.Usage?.InputTokenCount).IsEqualTo(30);
        await Assert.That(response.Usage?.OutputTokenCount).IsEqualTo(7);
        await Assert.That(activity?.OperationName).IsEqualTo("caller");
        await Assert.That(activity?.Status).IsEqualTo(ActivityStatusCode.Unset);
        await Assert.That(activity?.Duration).IsEqualTo(TimeSpan.Zero);
    }

    private static IEvent[] OriginalSteps(SessionPromptRequest prompt) =>
    [
        TurnEvents.Delivered(prompt.Id.Value!), TurnEvents.StepStarted("model_a", "provider_a", "a"),
        TurnEvents.Text("answer"), TurnEvents.StepEnded(10, 3, 0, 0, .01, "a"),
        TurnEvents.StepStarted("model_b", "provider_b", "b"), TurnEvents.StepEnded(20, 4, 0, 0, .02, "b"), TurnEvents.Succeeded(),
    ];

    private static KeyValuePair<string, object?>[] ExpectedTags(string? version, string? model, string? provider, double? cost)
    {
        var tags = new List<KeyValuePair<string, object?>> { new("gen_ai.conversation.id", "ses_test") };
        if (version is not null) { tags.Add(new("opencode.server.version", version)); }
        if (model is not null) { tags.Add(new("gen_ai.response.model", model)); tags.Add(new("opencode.model.provider_id", provider)); }
        if (cost is not null) { tags.Add(new("opencode.cost", cost.Value)); }
        return [.. tags];
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> run)
    {
        try { await run(); return null; }
        catch (Exception error) { return error; }
    }

    private IEnumerable<IEvent> FiniteEvents(IEnumerable<IEvent> events)
    {
        foreach (var current in events) { yield return current; }
        _fake.EndBus();
    }
}
