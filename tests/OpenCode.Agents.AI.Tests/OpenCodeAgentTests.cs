using Microsoft.Agents.AI;
using NSubstitute;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests;

public sealed class OpenCodeAgentTests
{
    [Test]
    public async Task CreateSessionAsync_Should_Create_No_OpenCode_Session()
    {
        var fake = new FakeOpenCode();
        await using var agent = new OpenCodeAgent(fake.Client);

        var session = (OpenCodeAgentSession)await agent.CreateSessionAsync();

        await Assert.That(session.SessionId).IsNull();
        await fake.Sessions.DidNotReceive().CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateSessionAsync_Should_Attach_To_The_Given_Session_Id()
    {
        var fake = new FakeOpenCode();
        await using var agent = new OpenCodeAgent(fake.Client);

        var session = (OpenCodeAgentSession)await agent.CreateSessionAsync("ses_existing");

        await Assert.That(session.SessionId).IsEqualTo("ses_existing");
        await fake.Sessions.DidNotReceive().CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<OpenCodeRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetService_Should_Return_Provider_Metadata_Named_Opencode()
    {
        await using var agent = new OpenCodeAgent(new FakeOpenCode().Client);

        var metadata = (AIAgentMetadata?)agent.GetService(typeof(AIAgentMetadata));

        await Assert.That(metadata).IsNotNull();
        await Assert.That(metadata?.ProviderName).IsEqualTo("opencode");
    }

    [Test]
    public async Task DisposeAsync_Should_Dispose_The_Client_Only_When_Owned()
    {
        var owned = new FakeOpenCode().Client;
        var borrowed = new FakeOpenCode().Client;

        await new OpenCodeAgent(owned, ownsClient: true).DisposeAsync();
        await new OpenCodeAgent(borrowed).DisposeAsync();

        var calls = owned.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Dispose").ToList();
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0].GetArguments()).IsEquivalentTo(new object?[] { true }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(borrowed.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Dispose")).IsEmpty();
    }

    [Test]
    public async Task Constructor_Should_Use_The_Default_Name_And_Description()
    {
        await using var agent = new OpenCodeAgent(new FakeOpenCode().Client);

        await Assert.That(agent.Name).IsEqualTo("OpenCode Agent");
        await Assert.That(agent.Description).IsEqualTo("An AI agent powered by OpenCode");
    }
}
