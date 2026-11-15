using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenCode.Agents.AI.Permissions;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Agents.AI.Tests.Turns;

public sealed class TurnRefreshTests
{
    private readonly FakeOpenCode _fake = new();
    private readonly TurnEventMapper _mapper = new("agent_test", "msg_prompt");

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RefreshAsync_Should_Reconcile_Before_Reading_The_Stored_Turn(bool listFails)
    {
        using var cancellation = new CancellationTokenSource();
        var trace = new List<string>();
        var failure = new OpenCodeTransportException("location failed");
        var request = TurnEvents.PendingPermission("pending");
        var reply = new SessionPermissionReplyRequest { Decision = PermissionReply.Once };
        _fake.TraceRootLocation(trace);
        _fake.Permissions.ListRequestsAsync(Arg.Any<PermissionRequestListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            trace.Add("location-permission-list");
            return listFails ? Task.FromException<PermissionRequestListResponse>(failure) : Task.FromResult(new PermissionRequestListResponse
            {
                Status = 200,
                Location = new LocationPublicRef { Directory = "test-directory" },
                Requests = [request],
            });
        });
        _fake.TracePermissionReply("pending", trace);
        _fake.TraceStoredMessages(trace, () => Page([TurnEvents.StoredUser("msg_prompt")]));
        var permissions = new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, (_, _) => new(reply));
        var refresh = new TurnRefresh(_fake.Client, FakeOpenCode.SessionId, "msg_prompt", permissions, _mapper);

        if (listFails)
        {
            var error = await Assert.ThrowsAsync<OpenCodeTransportException>(async () => await refresh.RefreshAsync(delivered: false, cancellation.Token));
            await Assert.That(error).IsSameReferenceAs(failure);
        }
        else
        {
            await refresh.RefreshAsync(delivered: false, cancellation.Token);
            await permissions.RespondAsync(TurnEvents.PermissionAsked("pending"), cancellation.Token);
            await _fake.Session.Received(1).ReplyToPermissionAsync("pending", reply, OpenCodeRequestOptions.NoThrow, cancellation.Token);
        }
        string[] expected = listFails ? ["get-root-location", "location-permission-list"] : ["get-root-location", "location-permission-list", "owned-reply", "messages"];
        await Assert.That(trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await _fake.Session.Received(1).GetAsync(null, cancellation.Token);
        await _fake.Permissions.Received(1).ListRequestsAsync(Arg.Is<PermissionRequestListRequest>(query => query.Location != null && query.Location.Directory == "test-directory"), null, cancellation.Token);
        await Assert.That(_fake.Session.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(SessionClient.ListMessagesAsync))).IsEqualTo(listFails ? 0 : 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadOutcomeAsync_Should_Find_The_Prompt_Across_Ordered_Pages(bool failed)
    {
        using var cancellation = new CancellationTokenSource();
        var first = TurnEvents.StoredAssistant("a1", [new SessionMessageAssistantText { Text = "one" }]);
        var second = TurnEvents.StoredAssistant("a2", [new SessionMessageAssistantText { Text = "two" }]);
        var idle = TurnEvents.StoredIdle("idle1", failed ? SessionMessageIdleOutcome.Failed : SessionMessageIdleOutcome.Succeeded);
        var queries = new List<SessionMessageListRequest>();
        _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), null, cancellation.Token).Returns(call =>
        {
            var query = call.Arg<SessionMessageListRequest>();
            queries.Add(query);
            return query.Cursor switch
            {
                null => Page([TurnEvents.StoredIdle("idle2", SessionMessageIdleOutcome.Succeeded), TurnEvents.StoredAssistant("later", [new SessionMessageAssistantText { Text = "foreign" }]), TurnEvents.StoredUser("later-user")], "opaque-a"),
                "opaque-a" => Page([idle, second], "opaque-b"),
                _ => Page([first, TurnEvents.StoredUser("msg_prompt"), TurnEvents.StoredIdle("older", SessionMessageIdleOutcome.Failed)]),
            };
        });

        var result = await Refresh().ReadOutcomeAsync(cancellation.Token);

        await Assert.That(result.Delivered).IsTrue();
        await Assert.That(result.End).IsSameReferenceAs(idle);
        await Assert.That(Texts(result)).IsEquivalentTo(["one", "two"], CollectionOrdering.Matching);
        string?[] messageIds = ["a1", "a2"];
        await Assert.That(result.Updates.Select(update => update.MessageId)).IsEquivalentTo(messageIds, CollectionOrdering.Matching);
        await Assert.That(result.Failure?.Message).IsEqualTo(failed ? "Session error: the turn failed while the event stream was reconnecting" : null);
        await Assert.That(queries.Count).IsEqualTo(3);
        await Assert.That(queries.Select(query => query.Cursor)).IsEquivalentTo([null, "opaque-a", "opaque-b"], CollectionOrdering.Matching);
        ListOrder?[] orders = [ListOrder.Descending, null, null];
        await Assert.That(queries.Select(query => query.Order)).IsEquivalentTo(orders, CollectionOrdering.Matching);
        foreach (var query in queries)
        {
            await Assert.That(query.Limit).IsEqualTo(200);
            await Assert.That(query.Type).IsNull();
        }
        await Assert.That(_fake.Permissions.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RefreshAsync_Should_Not_Start_Recovery_While_The_Prompt_Is_Queued(bool found, bool delivered)
    {
        _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Page(found ? [TurnEvents.StoredUser("msg_prompt")] : []));

        var result = await Refresh().RefreshAsync(delivered, CancellationToken.None);
        var delta = _mapper.MapUpdates(TurnEvents.Text("mid"));
        var ended = _mapper.MapUpdates(TurnEvents.TextEnded("whole"));

        await Assert.That(result.Delivered).IsEqualTo(found);
        await Assert.That(result.End).IsNull();
        await Assert.That(result.Updates).IsEmpty();
        string[] expected = found ? [] : ["mid"];
        await Assert.That(delta.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
        if (found)
        {
            await Assert.That(ended.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text)).IsEquivalentTo(["whole"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ReadOutcomeAsync_Should_Return_Ended_Parts_Before_The_Original_Idle()
    {
        var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var step = (SessionStepEnded)TurnEvents.StepEnded(10.6, 4.4, 2.5, 3.5, 0);
        var assistant = TurnEvents.StoredAssistant("msg_assistant", [new SessionMessageAssistantText { Text = "done" },
            new SessionMessageAssistantReasoning { Text = "thought" },
            TurnEvents.StoredTool("c1", "read", new SessionMessageToolStateCompleted { Input = input, Content = [new ToolTextContent { Text = "ok" }] }),
            TurnEvents.StoredTool("c2", "shell", new SessionMessageToolStateError { Input = input, Error = new SessionStructuredError { Type = "tool_error", Message = "denied" } }),
            new SessionMessageAssistantText { Text = "" }], step.Data.Tokens, SessionMessageAssistantFinish.Stop);
        _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(Page([assistant, TurnEvents.StoredUser("msg_prompt"), TurnEvents.StoredIdle("old", SessionMessageIdleOutcome.Succeeded)]));

        var result = await Refresh().ReadOutcomeAsync(CancellationToken.None);

        await Assert.That(result.Delivered).IsTrue();
        await Assert.That(result.End).IsNull();
        await Assert.That(result.Updates.SelectMany(update => update.Contents).Select(content => content.GetType()))
            .IsEquivalentTo([typeof(TextContent), typeof(TextReasoningContent), typeof(FunctionCallContent), typeof(FunctionResultContent), typeof(FunctionCallContent), typeof(FunctionResultContent), typeof(UsageContent)], CollectionOrdering.Matching);
        await Assert.That(Texts(result)).IsEquivalentTo(["done"], CollectionOrdering.Matching);
        await Assert.That(_mapper.CloseOpenCalls()).IsEmpty();
    }

    [Test]
    [Arguments("query")]
    [Arguments("message")]
    [Arguments("idle")]
    [Arguments("outcome")]
    public async Task ReadOutcomeAsync_Should_Preserve_Raw_Unknowns_And_Surface_Query_Failures(string kind)
    {
        var failure = new OpenCodeTransportException("messages failed");
        var type = kind == "idle" ? "idle" : "future";
        ISessionMessageInfo unknown = kind == "outcome" ? TurnEvents.StoredIdle("end", new SessionMessageIdleOutcome("future"))
            : new UnknownSessionMessageInfo(type, JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = type }));
        _fake.Session.ListMessagesAsync(Arg.Any<SessionMessageListRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(kind == "query" ? Task.FromException<SessionMessageListResponse>(failure) : Task.FromResult(Page([unknown, TurnEvents.StoredUser("msg_prompt")])));
        if (kind == "query")
        {
            var error = await Assert.ThrowsAsync<OpenCodeTransportException>(async () => await Refresh().ReadOutcomeAsync(CancellationToken.None));
            await Assert.That(error).IsSameReferenceAs(failure);
        }
        else
        {
            var result = await Refresh().ReadOutcomeAsync(CancellationToken.None);
            await Assert.That(result.Failure).IsNull();
            if (kind is "idle" or "outcome") { await Assert.That(result.End).IsSameReferenceAs(unknown); }
            else
            {
                var raw = result.Updates.Single().Contents.Single();
                await Assert.That(raw.RawRepresentation).IsSameReferenceAs(unknown);
                await Assert.That(raw.GetType()).IsEqualTo(typeof(AIContent));
            }
        }
    }

    private TurnRefresh Refresh() => new(_fake.Client, FakeOpenCode.SessionId, "msg_prompt",
        new PermissionResponder(_fake.Sessions, FakeOpenCode.SessionId, onPermissionRequest: null), _mapper);

    private static IEnumerable<string> Texts(TurnRefreshResult result) => result.Updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text);

    private static SessionMessageListResponse Page(IReadOnlyList<ISessionMessageInfo> messages, string? next = null) => new()
    {
        Status = 200,
        Messages = messages,
        Cursor = new ListCursor { Next = next },
    };
}
