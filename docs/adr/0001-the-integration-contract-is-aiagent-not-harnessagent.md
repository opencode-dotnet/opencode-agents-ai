# The integration contract is AIAgent, not HarnessAgent

Date: 2026-11-06

MAF has two candidate extension points for this provider: `AIAgent` and `HarnessAgent`.
`OpenCodeAgent` derives from `AIAgent`, because `HarnessAgent` is MAF's own harness, a peer of
OpenCode rather than a host for it.

## Considered Options

- `HarnessAgent` (`Microsoft.Agents.AI.Harness`, stable) is MAF's harness over an `IChatClient`,
  with function invocation, compaction, todo, modes, file memory, skills, tool approval and a
  loop. It is rejected because wrapping OpenCode in it would put a harness inside a harness.

## Consequences

OpenCode's harness behaviour reaches MAF as one agent's run. The current boundary is in
[`agent-integration.md`](../architecture/agent-integration.md).

Evidence: internal research, 2026-11-06, "opencode-agents-ai feasibility and demand research".
