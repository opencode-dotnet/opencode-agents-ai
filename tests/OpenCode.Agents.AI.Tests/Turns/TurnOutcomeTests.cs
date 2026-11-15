using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Test]
    public async Task Failure_Should_Throw_For_A_Drifted_Failed_Turn_End()
    {
        var turnEnd = DriftedEnd("session.execution.failed", new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = "drifted model failure" },
        });

        var failure = TurnOutcome.Failure(turnEnd);

        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(failure?.Message).IsEqualTo("Session error: drifted model failure");
    }

    [Test]
    public async Task Failure_Should_Use_The_Fallback_Message_When_A_Drifted_Failure_Has_No_Message()
    {
        var turnEnd = DriftedEnd("session.execution.failed", new JsonObject { ["error"] = new JsonObject() });

        var failure = TurnOutcome.Failure(turnEnd);

        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(failure?.Message).IsEqualTo("Session error: the turn failed.");
    }

    [Test]
    public async Task Failure_Should_Throw_For_A_Drifted_Shutdown_Interruption()
    {
        var turnEnd = DriftedEnd("session.execution.interrupted", new JsonObject { ["reason"] = "shutdown" });

        var failure = TurnOutcome.Failure(turnEnd);

        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(failure?.Message).IsEqualTo("Session error: the OpenCode server is shutting down");
    }

    [Test]
    public async Task Failure_Should_End_Normally_For_A_Drifted_User_Interruption()
    {
        var turnEnd = DriftedEnd("session.execution.interrupted", new JsonObject { ["reason"] = "user" });

        var failure = TurnOutcome.Failure(turnEnd);

        await Assert.That(failure).IsNull();
    }

    [Test]
    [Arguments("succeeded", false, null)]
    [Arguments("interrupted", false, null)]
    [Arguments("future", false, null)]
    [Arguments("failed", true, "Session error: model overloaded")]
    [Arguments("failed", false, "Session error: the turn failed while the event stream was reconnecting")]
    public async Task Failure_Should_Use_The_First_Stored_Idles_Outcome(string outcome, bool withError, string? expected)
    {
        var idle = TurnEvents.StoredIdle("idle1", new SessionMessageIdleOutcome(outcome),
            withError ? new SessionStructuredError { Type = "model_error", Message = "model overloaded" } : null);

        var failure = TurnOutcome.Failure(idle);

        await Assert.That(failure?.Message).IsEqualTo(expected);
        if (expected is not null) { await Assert.That(failure).IsTypeOf<InvalidOperationException>(); }
    }

    private static UnknownEvent DriftedEnd(string type, JsonObject data) => new(type, JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = type,
        ["data"] = data,
    }));
}
