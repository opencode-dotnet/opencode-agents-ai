using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Agents.AI.Tests.Turns;

public sealed class TurnEventMapperTests
{
    private readonly TurnEventMapper _mapper = new("agent_test", "msg_prompt");

    [Test]
    public async Task Map_Should_Return_A_Function_Call_With_The_Started_Name_And_The_Called_Input()
    {
        var started = (SessionToolInputStarted)TurnEvents.ToolStarted("c1", "shell");
        var called = TurnEvents.ToolCalled("c1", new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["command"] = JsonSerializer.SerializeToElement("ls") });

        var startUpdate = _mapper.Map(started)!;
        _mapper.Map(started with { Data = started.Data with { AssistantMessageId = "msg_other", Name = "read" } });
        var update = _mapper.Map(called)!;

        await Assert.That(startUpdate.Contents.Single().GetType()).IsEqualTo(typeof(AIContent));
        await Assert.That(startUpdate.Contents.Single().RawRepresentation).IsSameReferenceAs(started);
        var content = (await Assert.That(update.Contents.Single()).IsTypeOf<FunctionCallContent>())!; // The type assertion proves this is non-null.
        await Assert.That(content.CallId).IsEqualTo("c1");
        await Assert.That(content.Name).IsEqualTo("shell");
        await Assert.That(content.Arguments!.Keys).IsEquivalentTo(["command"], CollectionOrdering.Matching);
        await Assert.That(((JsonElement)content.Arguments["command"]!).GetString()).IsEqualTo("ls");
        await Assert.That(content.RawRepresentation).IsSameReferenceAs(called);
        await Assert.That(update.Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(update.AgentId).IsEqualTo("agent_test");
        await Assert.That(update.ResponseId).IsEqualTo("msg_prompt");
        await Assert.That(update.MessageId).IsEqualTo("msg_assistant");
    }

    [Test]
    public async Task Map_Should_Name_The_Call_Tool_When_No_Start_Was_Seen()
    {
        var called = TurnEvents.ToolCalled("c1", new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        var update = _mapper.Map(called)!;

        var content = (await Assert.That(update.Contents.Single()).IsTypeOf<FunctionCallContent>())!; // The type assertion proves this is non-null.
        await Assert.That(content.CallId).IsEqualTo("c1");
        await Assert.That(content.Name).IsEqualTo("tool");
        await Assert.That(update.Role).IsEqualTo(ChatRole.Assistant);
    }

    [Test]
    public async Task Map_Should_Return_A_Function_Result_With_The_Tools_Text_When_It_Succeeds()
    {
        var succeeded = (SessionToolSuccess)TurnEvents.ToolSucceeded("c1", "ok");
        var single = await Assert.That(_mapper.Map(succeeded)!.Contents.Single()).IsTypeOf<FunctionResultContent>();
        await Assert.That(single?.Result).IsEqualTo("ok");
        succeeded = succeeded with
        {
            Data = succeeded.Data with
            {
                Content = [new ToolTextContent { Text = "ok" }, new ToolFileContent { Uri = "file:///result.txt", Mime = "text/plain" }, new ToolTextContent { Text = "\"done\"\n" }],
            },
        };

        var update = _mapper.Map(succeeded)!;

        var content = (await Assert.That(update.Contents.Single()).IsTypeOf<FunctionResultContent>())!; // The type assertion proves this is non-null.
        await Assert.That(content.CallId).IsEqualTo("c1");
        await Assert.That(content.Result).IsEqualTo("ok\n\"done\"\n");
        await Assert.That(content.RawRepresentation).IsSameReferenceAs(succeeded);
        await Assert.That(update.Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(update.MessageId).IsEqualTo("msg_assistant");
    }

    [Test]
    public async Task Map_Should_Return_A_Function_Result_With_The_Error_Message_When_It_Fails()
    {
        var failed = TurnEvents.ToolFailed("c1", "command failed");

        var update = _mapper.Map(failed)!;

        var content = (await Assert.That(update.Contents.Single()).IsTypeOf<FunctionResultContent>())!; // The type assertion proves this is non-null.
        await Assert.That(content.CallId).IsEqualTo("c1");
        await Assert.That(content.Result).IsEqualTo("command failed");
        await Assert.That(content.RawRepresentation).IsSameReferenceAs(failed);
        await Assert.That(update.Role).IsEqualTo(ChatRole.Tool);
    }

    [Test]
    public async Task Map_Should_Round_Step_Usage_Into_Usage_Content()
    {
        var ended = TurnEvents.StepEnded(10.6, 4.4, 2.5, 3.5, 0.01);

        var update = _mapper.Map(ended)!;

        var content = (await Assert.That(update.Contents.Single()).IsTypeOf<UsageContent>())!; // The type assertion proves this is non-null.
        await Assert.That(content.Details.InputTokenCount).IsEqualTo(11);
        await Assert.That(content.Details.OutputTokenCount).IsEqualTo(4);
        await Assert.That(content.Details.ReasoningTokenCount).IsEqualTo(3);
        await Assert.That(content.Details.CachedInputTokenCount).IsEqualTo(4);
        await Assert.That(content.Details.TotalTokenCount).IsNull();
        await Assert.That(content.RawRepresentation).IsSameReferenceAs(ended);
        await Assert.That(update.Role).IsEqualTo(ChatRole.Assistant);
    }

    [Test]
    public async Task Map_Should_Not_Count_The_Session_Total_As_Usage()
    {
        var total = SessionTotal();

        var update = _mapper.Map(total)!;

        await Assert.That(update.Contents.Count).IsEqualTo(1);
        await Assert.That(update.Contents[0].GetType()).IsEqualTo(typeof(AIContent));
        await Assert.That(update.Contents[0].RawRepresentation).IsSameReferenceAs(total);
    }

    [Test]
    public async Task Map_Should_Not_Add_The_Session_Total_To_A_Later_Step()
    {
        _mapper.Map(SessionTotal());

        var update = _mapper.Map(TurnEvents.StepEnded(6.5, 1.6, 0.6, 0.5, 0.01))!;

        var content = await Assert.That(update.Contents.Single()).IsTypeOf<UsageContent>();
        await Assert.That(content?.Details.InputTokenCount).IsEqualTo(7);
        await Assert.That(content?.Details.OutputTokenCount).IsEqualTo(2);
        await Assert.That(content?.Details.ReasoningTokenCount).IsEqualTo(1);
        await Assert.That(content?.Details.CachedInputTokenCount).IsEqualTo(1);
        await Assert.That(content?.Details.TotalTokenCount).IsNull();
    }

    [Test]
    public async Task CloseOpenCalls_Should_Return_An_Interrupted_Result_For_Each_Open_Call()
    {
        var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        _mapper.Map(TurnEvents.ToolStarted("c1", "read"));
        _mapper.Map(TurnEvents.ToolCalled("c1", input));
        _mapper.Map(TurnEvents.ToolSucceeded("c1", "ok"));
        _mapper.Map(TurnEvents.ToolStarted("c2", "shell"));
        _mapper.Map(TurnEvents.ToolCalled("c2", input));
        _mapper.Map(TurnEvents.ToolCalled("c3", input));
        _mapper.Map(TurnEvents.ToolFailed("c3", "failed"));

        var updates = _mapper.CloseOpenCalls();

        await Assert.That(updates.Count).IsEqualTo(1);
        var result = (await Assert.That(updates[0].Contents.Single()).IsTypeOf<FunctionResultContent>())!; // The type assertion proves this is non-null.
        await Assert.That(result.CallId).IsEqualTo("c2");
        await Assert.That(result.Result).IsEqualTo("Tool execution interrupted: shell");
        await Assert.That(updates[0].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(updates[0].AgentId).IsEqualTo("agent_test");
        await Assert.That(updates[0].ResponseId).IsEqualTo("msg_prompt");
        await Assert.That(updates[0].MessageId).IsEqualTo("msg_assistant");
        await Assert.That(_mapper.CloseOpenCalls()).IsEmpty();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Reconcile_Should_Emit_Only_The_Missing_Text_And_Reasoning_Suffixes(bool split, bool openSnapshot)
    {
        var secondId = split ? "second" : "msg_assistant";
        _mapper.MapUpdates(TurnEvents.Text("a"));
        _mapper.MapUpdates(TurnEvents.Reasoning("think", "msg_assistant"));
        _mapper.MapUpdates(TurnEvents.Text("x", assistantMessageId: secondId, ordinal: split ? 0 : 1));
        var text = new SessionMessageAssistantText { Text = "abc" };
        var reasoning = new SessionMessageAssistantReasoning { Text = "thinking" };
        var last = new SessionMessageAssistantText { Text = "z" };
        SessionMessageAssistant[] messages = split
            ? [TurnEvents.StoredAssistant("msg_assistant", [text, reasoning]), TurnEvents.StoredAssistant(secondId, [new SessionMessageAssistantText { Text = "x" }, last])]
            : [TurnEvents.StoredAssistant("msg_assistant", [text, reasoning, new SessionMessageAssistantText { Text = "x" }, last])];
        _mapper.BeginRecovery();

        if (openSnapshot)
        {
            await Assert.That(_mapper.Reconcile([AssistantPart("", reasoning: false)])).IsEmpty();
            _mapper.BeginRecovery();
        }

        var updates = _mapper.Reconcile(messages);

        await Assert.That(Semantics(updates)).IsEquivalentTo(["text:bc", "reasoning:ing", "text:z"], CollectionOrdering.Matching);
        await Assert.That(updates.Select(update => update.MessageId)).IsEquivalentTo(new string?[] { "msg_assistant", "msg_assistant", secondId }, CollectionOrdering.Matching);
        await Assert.That(updates.Select(update => update.Contents.Single().RawRepresentation)).IsEquivalentTo(new object?[] { text, reasoning, last }, CollectionOrdering.Matching);
        foreach (var update in updates)
        {
            await Assert.That(update.AgentId).IsEqualTo("agent_test");
            await Assert.That(update.ResponseId).IsEqualTo("msg_prompt");
            await Assert.That(update.Role).IsEqualTo(ChatRole.Assistant);
        }
        await Assert.That(_mapper.Reconcile(messages)).IsEmpty();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task MapUpdates_Should_Stream_Post_Gap_Starts_And_Recover_Gap_Spanning_Parts_At_Their_Ends(bool reasoning, bool secondGap)
    {
        var updates = new List<AgentResponseUpdate>();
        updates.AddRange(_mapper.MapUpdates(Delta("a", reasoning)));
        _mapper.BeginRecovery();
        var suppressed = Delta("c", reasoning);
        updates.AddRange(_mapper.MapUpdates(suppressed));
        var ended = End("abc", reasoning);
        updates.AddRange(_mapper.MapUpdates(ended));
        updates.AddRange(_mapper.MapUpdates(Start(reasoning, ordinal: 1)));
        updates.AddRange(_mapper.MapUpdates(Delta("d", reasoning, ordinal: 1)));
        if (secondGap)
        {
            _mapper.BeginRecovery();
            updates.AddRange(_mapper.MapUpdates(Delta("f", reasoning, ordinal: 1)));
        }
        updates.AddRange(_mapper.MapUpdates(End(secondGap ? "def" : "d", reasoning, ordinal: 1)));
        updates.AddRange(_mapper.MapUpdates(Delta("tail", reasoning, ordinal: 2)));
        updates.AddRange(_mapper.MapUpdates(End("whole", reasoning, ordinal: 2)));

        var kind = reasoning ? "reasoning:" : "text:";
        string[] expected = secondGap ? [kind + "a", kind + "bc", kind + "d", kind + "ef", kind + "whole"] : [kind + "a", kind + "bc", kind + "d", kind + "whole"];
        await Assert.That(Semantics(updates)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        var raw = updates.Single(update => ReferenceEquals(update.Contents.Single().RawRepresentation, suppressed));
        await Assert.That(raw.Contents.Single().GetType()).IsEqualTo(typeof(AIContent));
        await Assert.That(updates.Single(update => ReferenceEquals(update.Contents.Single().RawRepresentation, ended)).Contents.Single().RawRepresentation).IsSameReferenceAs(ended);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Reconcile_Should_Skip_Non_Prefix_Full_Values_Without_Throwing(bool reasoning, bool live)
    {
        _mapper.MapUpdates(Delta("a", reasoning));
        _mapper.BeginRecovery();
        foreach (var full in new[] { "xb", "a" })
        {
            var skipped = live ? _mapper.MapUpdates(End(full, reasoning)) : _mapper.Reconcile([AssistantPart(full, reasoning)]);
            await Assert.That(Semantics(skipped)).IsEmpty();
        }
        var updates = live ? _mapper.MapUpdates(End("ab", reasoning)) : _mapper.Reconcile([AssistantPart("ab", reasoning)]);

        await Assert.That(Semantics(updates)).IsEquivalentTo([reasoning ? "reasoning:b" : "text:b"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task MapUpdates_Should_Not_Replay_A_Snapshot_Ended_Part_From_Buffered_Starts_And_Deltas()
    {
        _mapper.BeginRecovery();
        var updates = new List<AgentResponseUpdate>(_mapper.Reconcile([AssistantPart("abc", reasoning: false)]));
        updates.AddRange(_mapper.MapUpdates(TurnEvents.TextStarted()));
        updates.AddRange(_mapper.MapUpdates(TurnEvents.Text("abc")));
        updates.AddRange(_mapper.MapUpdates(TurnEvents.TextEnded("abc")));
        updates.AddRange(_mapper.MapUpdates(TurnEvents.TextStarted(ordinal: 1)));
        updates.AddRange(_mapper.MapUpdates(TurnEvents.Text("d", ordinal: 1)));

        await Assert.That(Semantics(updates)).IsEquivalentTo(["text:abc", "text:d"], CollectionOrdering.Matching);
        await Assert.That(updates.Skip(1).Take(3).Select(update => update.Contents.Single().GetType()))
            .IsEquivalentTo([typeof(AIContent), typeof(AIContent), typeof(AIContent)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reconcile_Should_Recover_Calls_And_Results_Independently()
    {
        var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["path"] = JsonSerializer.SerializeToElement("file.cs") };
        _mapper.MapUpdates(TurnEvents.ToolStarted("c1", "read"));
        _mapper.MapUpdates(TurnEvents.ToolCalled("c1", input));
        _mapper.MapUpdates(TurnEvents.ToolSucceeded("c1", "ok1"));
        _mapper.MapUpdates(TurnEvents.ToolStarted("c2", "shell"));
        _mapper.MapUpdates(TurnEvents.ToolCalled("c2", input));
        var c3 = CompletedTool("c3", input, "ok3");
        var c4 = TurnEvents.StoredTool("c4", "shell", new SessionMessageToolStateError
        {
            Input = input,
            Error = new SessionStructuredError { Type = "tool_error", Message = "denied" },
        });
        _mapper.BeginRecovery();

        var updates = new List<AgentResponseUpdate>(_mapper.Reconcile([TurnEvents.StoredAssistant("msg_assistant",
            [CompletedTool("c1", input, "ok1"), CompletedTool("c2", input, "ok2"), c3, c4])]));
        updates.AddRange(_mapper.MapUpdates(TurnEvents.ToolCalled("c3", input)));
        updates.AddRange(_mapper.MapUpdates(TurnEvents.ToolSucceeded("c3", "ok3")));
        updates.AddRange(_mapper.CloseOpenCalls());

        await Assert.That(Semantics(updates)).IsEquivalentTo(["result:c2:ok2", "call:c3:read", "result:c3:ok3", "call:c4:shell", "result:c4:denied"], CollectionOrdering.Matching);
        await Assert.That(updates.Take(5).Select(update => update.Role)).IsEquivalentTo(new ChatRole?[] { ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, CollectionOrdering.Matching);
        await Assert.That(updates[1].Contents.Single().RawRepresentation).IsSameReferenceAs(c3);
        var call = (FunctionCallContent)updates[1].Contents.Single();
        await Assert.That(((JsonElement)call.Arguments!["path"]!).GetString()).IsEqualTo("file.cs"); // The stored input supplies this argument.
        await Assert.That(_mapper.CloseOpenCalls()).IsEmpty();
    }

    [Test]
    public async Task Reconcile_Should_Preserve_The_Live_Tool_Result_Text_Policy()
    {
        var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var success = TurnEvents.StoredTool("c1", "read", new SessionMessageToolStateCompleted
        {
            Input = input,
            Content = [new ToolTextContent { Text = "ok" }, new ToolFileContent { Uri = "file:///result.txt", Mime = "text/plain" }, new ToolTextContent { Text = "\"done\"\n" }],
        });
        var failed = TurnEvents.StoredTool("c2", "read", new SessionMessageToolStateError
        {
            Input = input,
            Error = new SessionStructuredError { Type = "tool_error", Message = "denied" },
            Content = [new ToolTextContent { Text = "not the error" }],
        });

        var updates = _mapper.Reconcile([TurnEvents.StoredAssistant("msg_assistant", [success, failed])]);

        await Assert.That(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>().Select(content => content.Result))
            .IsEquivalentTo(new object?[] { "ok\n\"done\"\n", "denied" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Reconcile_Should_Not_Fabricate_Settled_Output_For_An_Open_Stored_Tool(bool running, bool called)
    {
        var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        _mapper.MapUpdates(TurnEvents.ToolStarted("c2", "shell"));
        _mapper.MapUpdates(TurnEvents.ToolCalled("c2", input));
        ISessionMessageToolState state = running ? new SessionMessageToolStateRunning { Input = input, Metadata = input } : new SessionMessageToolStateStreaming { Input = "unparsed" };
        _mapper.BeginRecovery();
        var updates = _mapper.Reconcile([TurnEvents.StoredAssistant("msg_assistant", [TurnEvents.StoredTool("c3", "read", state)])]);

        string[] expected = running ? ["call:c3:read"] : [];
        await Assert.That(Semantics(updates)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        if (called)
        {
            var call = _mapper.MapUpdates(TurnEvents.ToolCalled("c3", input));
            string[] expectedCall = running ? [] : ["call:c3:read"];
            await Assert.That(Semantics(call)).IsEquivalentTo(expectedCall, CollectionOrdering.Matching);
        }
        var live = _mapper.MapUpdates(TurnEvents.ToolSucceeded("c3", "ok"));
        await Assert.That(Semantics(live)).IsEquivalentTo(["result:c3:ok"], CollectionOrdering.Matching);
        await Assert.That(Semantics(_mapper.CloseOpenCalls())).IsEquivalentTo(["result:c2:Tool execution interrupted: shell"], CollectionOrdering.Matching);
        await Assert.That(_mapper.CloseOpenCalls()).IsEmpty();
    }

    [Test]
    public async Task Reconcile_Should_Recover_Usage_Once_Per_Assistant()
    {
        var step = (SessionStepEnded)TurnEvents.StepEnded(10.6, 4.4, 2.5, 3.5, 0.01);
        _mapper.MapUpdates(step);
        var a1 = TurnEvents.StoredAssistant("msg_assistant", [], step.Data.Tokens, SessionMessageAssistantFinish.Stop);
        var a2 = a1 with { Id = "a2" };
        _mapper.BeginRecovery();

        var updates = _mapper.Reconcile([a1, a2, a1 with { Id = "unfinished", Finish = null }, a1 with { Id = "no-tokens", Tokens = null }]);
        var content = (UsageContent)updates.Single().Contents.Single();

        (string Field, long? Count)[] counts =
        [
            ("input", content.Details.InputTokenCount),
            ("output", content.Details.OutputTokenCount),
            ("reasoning", content.Details.ReasoningTokenCount),
            ("cache-read", content.Details.CachedInputTokenCount),
            ("total", content.Details.TotalTokenCount),
        ];
        (string Field, long? Count)[] expected = [("input", 11), ("output", 4), ("reasoning", 3), ("cache-read", 4), ("total", null)];
        await Assert.That(counts).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(content.RawRepresentation).IsSameReferenceAs(a2);
        await Assert.That(updates.Single().MessageId).IsEqualTo("a2");
        await Assert.That(updates.Single().Role).IsEqualTo(ChatRole.Assistant);
        var buffered = step with { Data = step.Data with { AssistantMessageId = "a2" } };
        var raw = _mapper.MapUpdates(buffered).Single().Contents.Single();
        await Assert.That(raw.GetType()).IsEqualTo(typeof(AIContent));
        await Assert.That(raw.RawRepresentation).IsSameReferenceAs(buffered);
        await Assert.That(_mapper.Reconcile([a1, a2])).IsEmpty();
    }

    [Test]
    public async Task Reconcile_Should_Keep_Unknown_Carriers_Raw()
    {
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "future" });
        var message = new UnknownSessionMessageInfo("future", payload);
        var part = new UnknownSessionMessageAssistantContent("future", payload);
        var state = new UnknownSessionMessageToolState("future", JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "future" }));

        var updates = _mapper.Reconcile([message, TurnEvents.StoredAssistant("msg_assistant", [part, TurnEvents.StoredTool("c1", "read", state)])]);

        await Assert.That(updates.Select(update => update.Contents.Single().RawRepresentation)).IsEquivalentTo(new object?[] { message, part, state }, CollectionOrdering.Matching);
        await Assert.That(updates.Select(update => update.Contents.Single().GetType())).IsEquivalentTo([typeof(AIContent), typeof(AIContent), typeof(AIContent)], CollectionOrdering.Matching);
        await Assert.That(updates.Select(update => update.MessageId)).IsEquivalentTo([null, "msg_assistant", "msg_assistant"], CollectionOrdering.Matching);
    }

    private static IEnumerable<string> Semantics(IEnumerable<AgentResponseUpdate> updates) => updates.SelectMany(update => update.Contents).Select(content => content switch
    {
        TextContent text => "text:" + text.Text,
        TextReasoningContent reasoning => "reasoning:" + reasoning.Text,
        FunctionCallContent call => "call:" + call.CallId + ":" + call.Name,
        FunctionResultContent result => "result:" + result.CallId + ":" + result.Result,
        _ => null,
    }).OfType<string>();

    private static IEvent Delta(string text, bool reasoning, long ordinal = 0) => reasoning
        ? TurnEvents.Reasoning(text, "msg_assistant", ordinal) : TurnEvents.Text(text, ordinal: ordinal);

    private static IEvent End(string text, bool reasoning, long ordinal = 0) => reasoning
        ? TurnEvents.ReasoningEnded(text, "msg_assistant", ordinal) : TurnEvents.TextEnded(text, ordinal: ordinal);

    private static IEvent Start(bool reasoning, long ordinal) => reasoning
        ? TurnEvents.ReasoningStarted("msg_assistant", ordinal) : TurnEvents.TextStarted(ordinal: ordinal);

    private static SessionMessageAssistant AssistantPart(string text, bool reasoning) => TurnEvents.StoredAssistant("msg_assistant",
        [reasoning ? new SessionMessageAssistantReasoning { Text = text } : new SessionMessageAssistantText { Text = text }]);

    private static SessionMessageAssistantTool CompletedTool(string id, IReadOnlyDictionary<string, JsonElement> input, string text) =>
        TurnEvents.StoredTool(id, "read", new SessionMessageToolStateCompleted { Input = input, Content = [new ToolTextContent { Text = text }] });

    private static SessionUsageUpdated SessionTotal()
    {
        var step = (SessionStepEnded)TurnEvents.StepEnded(100, 40, 20, 30, 0.1);
        return new SessionUsageUpdated
        {
            Id = "event_usage_updated",
            Created = 1,
            Data = new SessionUsageUpdatedData { SessionId = FakeOpenCode.SessionId, Cost = step.Data.Cost, Tokens = step.Data.Tokens },
        };
    }
}
