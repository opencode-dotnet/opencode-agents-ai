# OpenCode Agents AI

Date: 2026-11-06

Shared vocabulary for the OpenCode MAF provider. These terms distinguish its MAF boundary, the
upstream OpenCode domain and the .NET toolchain.

## Language

### SDKs and participants

**OpenCode .NET SDK**:
The typed .NET client library for the OpenCode API used by SDK-backed adapter capabilities.
_Avoid_: SDK (when ambiguous), .NET SDK (when referring to the OpenCode client library)

**.NET SDK**:
The .NET development and tool-acquisition toolchain, distinct from the OpenCode .NET SDK client library.
_Avoid_: OpenCode .NET SDK (when referring to the toolchain), .NET runtime (when referring to the SDK toolchain)

**Microsoft Agent Framework (MAF)**:
The orchestration runtime for agents and workflows.
_Avoid_: the framework (when referring to this product)

**Agent**:
An MAF `AIAgent`.
_Avoid_: agent (when referring to an OpenCode subagent or an OpenCode agent profile)

**OpenCode agent**:
This product's `AIAgent` over OpenCode sessions.
_Avoid_: agent (when referring to an OpenCode subagent or an OpenCode agent profile)

**Agent session**:
An MAF `AgentSession` holding the reference to an OpenCode session.
_Avoid_: session (when ambiguous), OpenCode session (when referring to the MAF session)

**OpenCode session**:
The server-side conversation that an agent session refers to.
_Avoid_: session (when ambiguous), agent session (when referring to the OpenCode conversation)

**Harness**:
The runtime that turns a model into an agent; OpenCode is the harness, and MAF's `HarnessAgent` is a peer harness, not a host.
_Avoid_: host (when referring to `HarnessAgent` as a container for OpenCode)

**Run**:
One `RunAsync` call.
_Avoid_: run (when ambiguous), workflow run (when referring to one agent call)

**Workflow run**:
An MAF workflow execution.
_Avoid_: run (when ambiguous), agent run (when referring to a workflow execution)

**Response update**:
An MAF `AgentResponseUpdate`.
_Avoid_: event (when ambiguous), OpenCode event (when referring to a response update)

**OpenCode event**:
A server-sent event of the OpenCode server.
_Avoid_: event (when ambiguous), response update (when referring to an OpenCode event)

**Workflow**:
An MAF workflow that orchestrates executors.
_Avoid_: workflow (when referring only to one agent's run)

**Executor**:
An execution node in an MAF workflow.
_Avoid_: agent (when the executor is not an `AIAgent`)

**Checkpoint**:
Persisted MAF workflow state used to restore a workflow execution.
_Avoid_: OpenCode session (when referring to a workflow checkpoint)

**OpenCode server process**:
A running OpenCode API service that hosts OpenCode instances.
_Avoid_: server (when ambiguous), OpenCode instance (when referring to the process)
