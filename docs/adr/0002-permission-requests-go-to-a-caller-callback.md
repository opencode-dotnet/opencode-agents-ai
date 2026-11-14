# Permission requests go to a caller callback, not MAF approval content

Date: 2026-11-10

OpenCode asks for permission in the middle of a turn, and MAF has two ways to reach the caller: a
callback on the provider's options, or the `ToolApprovalRequestContent` round trip that ends the
run. `OpenCodeAgent` calls a caller-supplied callback and rejects the request when there is none,
because OpenCode owns the tool loop, and MAF's own GitHub Copilot provider handles its harness's
permissions the same way for the same reason.

## Considered Options

- The approval content round trip is rejected. It assumes one approval per function call and a
  call and result pair in the answering run. OpenCode can ask several times for one tool call, and
  can ask without a tool call, so the mapping would need invented call ids and results with no
  precedent in any MAF provider.

## Consequences

A workflow does not see a pending permission request as an external request, and cannot
checkpoint while one waits: the turn blocks until the callback returns. A pending request is not
part of any checkpoint; a resumed turn asks again.

Evidence: MAF `dotnet-1.27.0`, `GitHubCopilotAgent` in `Microsoft.Agents.AI.GitHub.Copilot`, and
the README of its sample `dotnet/samples/02-agents/AgentProviders/github-copilot/Agent_With_GitHubCopilot`.
