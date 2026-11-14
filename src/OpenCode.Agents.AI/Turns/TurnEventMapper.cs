using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class TurnEventMapper(string agentId, string promptId)
{
    public AgentResponseUpdate? Map(IEvent @event)
    {
        var content = @event switch
        {
            SessionTextDelta text => new TextContent(text.Data.Delta),
            SessionReasoningDelta reasoning => new TextReasoningContent(reasoning.Data.Delta),
            _ => new AIContent(),
        };
        content.RawRepresentation = @event;

        return new AgentResponseUpdate(ChatRole.Assistant, [content])
        {
            AgentId = agentId,
            ResponseId = promptId,
            MessageId = @event switch
            {
                SessionTextDelta text => text.Data.AssistantMessageId,
                SessionReasoningDelta reasoning => reasoning.Data.AssistantMessageId,
                _ => null,
            },
        };
    }
}
