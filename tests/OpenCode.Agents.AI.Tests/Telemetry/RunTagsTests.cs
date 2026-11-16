using OpenCode.Agents.AI.Telemetry;
using OpenCode.Agents.AI.Tests.Support;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Tests.Telemetry;

public sealed class RunTagsTests
{
    [Test]
    public async Task Step_Should_Set_The_Model_And_Its_Provider()
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();
        var tags = new RunTags(activity);

        tags.Step(new ModelRef { Id = "model_a", ProviderId = "provider_a" });
        tags.Step(new ModelRef { Id = "model_b", ProviderId = "provider_b" });

        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(
            new KeyValuePair<string, object?>[] { new("gen_ai.response.model", "model_b"), new("opencode.model.provider_id", "provider_b") },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0.01, 0.02, 0.03)]
    [Arguments(0, 0, 0)]
    public async Task Cost_Should_Add_Up_Across_Steps(double first, double second, double total)
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();
        activity?.SetTag("opencode.cost", 99d);
        var tags = new RunTags(activity);

        tags.Cost(first);
        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(
            new KeyValuePair<string, object?>[] { new("opencode.cost", first) }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        tags.Cost(second);
        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(
            new KeyValuePair<string, object?>[] { new("opencode.cost", total) }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ServerVersion_Should_Set_The_Server_Version()
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();

        new RunTags(activity).ServerVersion("2.3.0");

        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(
            new KeyValuePair<string, object?>[] { new("opencode.server.version", "2.3.0") }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunTags_Should_Do_Nothing_When_There_Is_No_Activity()
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();
        var tags = new RunTags(null);

        tags.Session("ses_test");
        tags.Step(new ModelRef { Id = "model_a", ProviderId = "provider_a" });
        tags.Cost(0.01);
        tags.ServerVersion("2.3.0");

        await Assert.That(activity?.TagObjects.ToArray()).IsEmpty();
    }

    [Test]
    public async Task Session_Should_Set_The_Conversation_Id()
    {
        using var capture = new TelemetryCapture();
        using var activity = capture.Source.StartActivity();

        new RunTags(activity).Session("ses_test");

        await Assert.That(activity?.TagObjects.ToArray()).IsEquivalentTo(
            new KeyValuePair<string, object?>[] { new("gen_ai.conversation.id", "ses_test") }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
