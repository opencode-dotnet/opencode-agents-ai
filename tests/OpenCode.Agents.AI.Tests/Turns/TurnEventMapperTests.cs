using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk.Models;

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
        await Assert.That(content.Arguments!.Keys).IsEquivalentTo(["command"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
        var step = (SessionStepEnded)TurnEvents.StepEnded(100, 40, 20, 30, 0.1);
        var total = new SessionUsageUpdated
        {
            Id = "event_usage_updated",
            Created = 1,
            Data = new SessionUsageUpdatedData { SessionId = FakeOpenCode.SessionId, Cost = step.Data.Cost, Tokens = step.Data.Tokens },
        };

        var update = _mapper.Map(total)!;

        await Assert.That(update.Contents.Count).IsEqualTo(1);
        await Assert.That(update.Contents[0].GetType()).IsEqualTo(typeof(AIContent));
        await Assert.That(update.Contents[0].RawRepresentation).IsSameReferenceAs(total);
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
}
