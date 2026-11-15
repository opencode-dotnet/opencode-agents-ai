using Microsoft.Agents.AI;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed record TurnRefreshResult(
    bool Delivered, ISessionMessageInfo? End,
    IReadOnlyList<AgentResponseUpdate> Updates, Exception? Failure);
