using System.Text.Json;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Agents.AI.Turns;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests.Turns;

public sealed class TurnOutcomeTests
{
    [Test]
    [Arguments("session.execution.succeeded", true)]
    [Arguments("session.execution.failed", true)]
    [Arguments("session.execution.interrupted", true)]
    [Arguments("server.connected", false)]
    [Arguments("session.execution.started", false)]
    [Arguments("session.idle", false)]
    public async Task EndsTurn_Should_Recognize_Only_The_Three_Turn_End_Wire_Types(string type, bool expected)
    {
        var @event = new UnknownEvent(type, JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = type }));

        var endsTurn = TurnOutcome.EndsTurn(@event);

        await Assert.That(endsTurn).IsEqualTo(expected);
    }

    [Test]
    public async Task Failure_Should_Return_The_OpenCode_Error_When_The_Turn_Fails()
    {
        var failure = TurnOutcome.Failure(TurnEvents.Failed("model overloaded"));

        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(failure?.Message).IsEqualTo("Session error: model overloaded");
    }

    [Test]
    public async Task Failure_Should_Return_An_Error_When_The_Server_Is_Shutting_Down()
    {
        var failure = TurnOutcome.Failure(TurnEvents.Interrupted(SessionExecutionInterruptedDataReason.Shutdown));

        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(failure?.Message).IsEqualTo("Session error: the OpenCode server is shutting down");
    }

    [Test]
    [Arguments("user")]
    [Arguments("superseded")]
    [Arguments("inactivity")]
    [Arguments("future-reason")]
    public async Task Failure_Should_Return_No_Error_When_The_Interruption_Is_Not_Shutdown(string reason)
    {
        var failure = TurnOutcome.Failure(TurnEvents.Interrupted(new SessionExecutionInterruptedDataReason(reason)));

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task Failure_Should_Return_No_Error_When_The_Turn_Succeeds()
    {
        var failure = TurnOutcome.Failure(TurnEvents.Succeeded());

        await Assert.That(failure).IsNull();
    }
}
