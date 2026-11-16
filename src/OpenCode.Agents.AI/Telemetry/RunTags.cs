using System.Diagnostics;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Telemetry;

internal sealed class RunTags(Activity? activity)
{
    private double _cost;

    public void Session(string sessionId) => activity?.SetTag("gen_ai.conversation.id", sessionId);

    public void Step(ModelRef model)
    {
        activity?.SetTag("gen_ai.response.model", model.Id);
        activity?.SetTag("opencode.model.provider_id", model.ProviderId);
    }

    public void Cost(double stepCost)
    {
        _cost += stepCost;
        activity?.SetTag("opencode.cost", _cost);
    }

    public void ServerVersion(string version) => activity?.SetTag("opencode.server.version", version);
}
