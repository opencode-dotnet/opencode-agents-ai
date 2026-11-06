# Agent Integration Architecture

Date: 2026-11-06

Canonical current rules for the agent contract, session ownership, the harness and orchestration
boundary, dependencies, and upstream parity. Projects and packaging live in
`platform-and-packaging.md`. This document states the decided contract; which parts are
implemented is operational state in `../ROADMAP.md`.

## Integration contract

`OpenCodeAgent` derives from MAF's `AIAgent`, so one OpenCode coding session can be a node in an
MAF workflow. `HarnessAgent` is a peer harness, not a host for OpenCode; wrapping one harness in
another would duplicate their responsibilities
([ADR-0001](../adr/0001-the-integration-contract-is-aiagent-not-harnessagent.md)).

## Session ownership

One `OpenCodeAgent` drives OpenCode sessions. The MAF agent session holds only the OpenCode session
reference; OpenCode stays the source of truth for conversation state, so this package does not
keep a second conversation state.

## Harness and orchestration boundary

OpenCode stays the harness: its tool loop, context, compaction, permissions and session state are
not reproduced here. MAF stays the orchestration runtime: its workflows, checkpoints, fan-out and
human-in-the-loop are not reimplemented here. Each runtime keeps the responsibilities it already
owns; the layer above MAF belongs to a separate, later repository.

## Thin boundary

MAF moves monthly and ships breaking changes. MAF types stay inside this one package, so a MAF
break is absorbed here.

## Dependency direction

This package depends on the published OpenCode .NET SDK; the SDK never depends on MAF. The
dependency runs one way so the OpenCode client remains independent of the orchestration runtime.
`platform-and-packaging.md` owns the published-package-only dependency rule.

## Upstream as the north star

Hand-written behaviour follows the first-party behaviour at the pinned versions. For the agent
contract, that is MAF's first-party external-runtime providers: `GitHubCopilotAgent` in
`Microsoft.Agents.AI.GitHub.Copilot`. For session behaviour, it is OpenCode's first-party clients.
These are the behaviour consumers of each runtime already meet, rather than a second contract
invented by this adapter.

A divergence is the maintainer's decision, recorded in this document with its reason and the
trigger that reverses it.
