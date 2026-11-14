using System.Text.Json;
using Microsoft.Agents.AI;
using OpenCode.Agents.AI.Tests.Support;

namespace OpenCode.Agents.AI.Tests;

public sealed class OpenCodeAgentSessionTests
{
    [Test]
    public async Task SerializeSessionAsync_Should_Write_Only_The_Session_Id_And_State_Bag()
    {
        await using var agent = new OpenCodeAgent(new FakeOpenCode().Client);
        var session = await agent.CreateSessionAsync("ses_1");
        session.StateBag.SetValue("marker", "saved");

        var serialized = await agent.SerializeSessionAsync(session);

        await Assert.That(serialized.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(serialized.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal))
            .IsEquivalentTo(["sessionId", "stateBag"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(serialized.GetProperty("sessionId").GetString()).IsEqualTo("ses_1");
        await Assert.That(serialized.GetProperty("stateBag").GetProperty("marker").GetString()).IsEqualTo("saved");
    }

    [Test]
    public async Task DeserializeSessionAsync_Should_Restore_The_Session_Id()
    {
        await using var agent = new OpenCodeAgent(new FakeOpenCode().Client);
        var session = await agent.CreateSessionAsync("ses_1");
        session.StateBag.SetValue("marker", "saved");

        var restored = (OpenCodeAgentSession)await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));

        await Assert.That(restored.SessionId).IsEqualTo("ses_1");
        await Assert.That(restored.StateBag.GetValue<string>("marker")).IsEqualTo("saved");
    }

    [Test]
    public async Task DeserializeSessionAsync_Should_Throw_ArgumentException_When_State_Is_Not_An_Object()
    {
        await using var agent = new OpenCodeAgent(new FakeOpenCode().Client);
        using var state = JsonDocument.Parse("[]");

        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await agent.DeserializeSessionAsync(state.RootElement));

        await Assert.That(error?.Message).StartsWith("The serialized session state must be a JSON object.");
        await Assert.That(error?.ParamName).IsEqualTo("serializedState");
    }

    [Test]
    public async Task SerializeSessionAsync_Should_Throw_When_The_Session_Is_Another_Agents()
    {
        await using var agent = new OpenCodeAgent(new FakeOpenCode().Client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.SerializeSessionAsync(new ForeignSession()));

        await Assert.That(error?.Message).IsEqualTo("The provided session type 'ForeignSession' is not compatible with this agent. Only sessions of type 'OpenCodeAgentSession' can be serialized by this agent.");
    }

    private sealed class ForeignSession : AgentSession;
}
