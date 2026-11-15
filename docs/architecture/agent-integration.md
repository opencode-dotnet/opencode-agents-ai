# Agent Integration Architecture

Date: 2026-11-15

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

## A run

One run sends one prompt. Its original turn ends on exactly one of
`session.execution.succeeded`, `session.execution.failed` or `session.execution.interrupted`.
Normal run completion follows a later background-work quietness check. A failed original turn and
a `Shutdown` interruption throw without draining background work; any other interruption enters
the normal quietness check.

The prompt uses `Delivery.Queue`: a busy session queues it behind the existing turn, rather than
steering it into another prompt's work as the CLI does. This preserves prompt isolation. Reverse
this difference only with an approved change to that isolation contract, or if upstream changes
make Queue cease to provide the boundary.

Cancellation therefore gates root interruption on this run observing its exact delivery. The CLI
interrupts without that gate (`packages/cli/src/run/noninteractive.ts:222,262–266` at v2.3.0): it
steers, while before our queued prompt's delivery the active turn is certainly someone else's.
Reverse this gate if delivery switches to steer. Queue isolates delivery order, not an interrupt's
reach after delivery.

Background selection uses the server's admitted `Prompt.Time.Created`: descendant jobs with
`Time.Started >= Created` belong to the wait, without a running-only status filter. This is the
CLI's time-boundary heuristic, not exclusive causal attribution: another client can start work in
the same tree after admission. Selected owner and subagent-child sessions stay remembered in
first-seen order. Quietness checks jobs, then remembered-session inboxes, then owned active
sessions. Root idleness alone does not establish descendant completion.

The waiter captures a fresh terminal generation before each root idle wait and check. A terminal
during the queries authorizes one immediate recheck, not indefinite polling. Pending work with no
new terminal waits on events while permission handling continues. We wake for remembered **or
owned** sessions, whereas the CLI wakes only for remembered sessions. This bounded widening keeps
a child's terminal from being lost before its job is first listed. Reverse it when upstream
supplies equivalent pre-list descendant tracking, or when it attaches unrelated work and the scope
rule is revised. Remembered membership alone never grants permission ownership.

The following approved differences delimit this wait:

- **Original-only output and outcome (D1).** Nothing is emitted during settling: no waiting
  content, child or root-wake output, or projected-message replay. Later job errors and wake failures
  do not replace the original outcome. Listening ends at the final quiet check, not after the CLI's
  final reconciliation. An ask arriving after return need not be answered. This adds a completion
  wait, not a multi-turn MAF response. Reverse with an approved multi-turn projection and
  finalization-lifetime contract.
- **Original failure exit (D2).** Failed and Shutdown original turns throw without background
  draining, so established failures are not delayed. Reverse with an approved failure-drain policy.
- **Fail-closed queries (D3).** Root permission-list failures and unsupported-content-type job-list
  failures propagate rather than becoming empty lists as in the CLI. The pinned server supports
  these endpoints; false emptiness can strand work. Reverse only with an equivalent recorded
  recovery path. Wait, inbox and active failures already propagate upstream.
- **Defensive quiet stream end.** When the event stream ends quietly and the background check is
  quiet, the run returns; OpenCode's CLI throws (`packages/cli/src/run/noninteractive.ts:148–151`
  at v2.3.0). The SDK 1.1.0 live stream does not end normally: it reconnects or throws, as its
  `docs/guide/streaming.md` specifies, so a quiet end is defensive only. Reverse this difference
  when the SDK gives a normal stream end a meaning.

### Accepted message-less declines

A selected subagent row no longer holds the pending-job check when this run received a successful
reply envelope for a message-less Reject to its `ChildSessionId`, regardless of the job status.
Absent, null and empty messages qualify; whitespace is feedback. Typed not-found replies,
unsuccessful replies, messaged Rejects and another client's decisions do not create this per-run
record. Owner and child inboxes and owned active work still count. A drained
`session.execution.started` for that session clears its record in either the original or background
phase. Clearing a record in the background phase authorizes exactly one recheck, including when
the start arrives during a quietness check. No original-turn terminal history, status inspection
or connection inference participates.

This divergence addresses [upstream issue 54180](https://github.com/anomalyco/opencode/issues/54180):
an accepted message-less decline normally self-interrupts the child, without reporting to its
parent, and can leave a retained job row with no later wake. Its reversal trigger is the fix of
anomalyco/opencode#54180, when upstream reports and settles declined children normally.

The record is evidence of our accepted reply, not proof that the child stopped. A closed Location
can continue the step; restart or a repeated subagent call can resume the child; a decline inside
an image job can leave its parent's step alone; and a stream failure can lead to a later report.
Observed starts clear the record, but continuations without an observed start and reports arriving
after quietness can outlive the run. That imprecision is accepted under the original-only output
boundary above; the next run's root reconciliation is not a descendant permission snapshot.

A non-declined child's terminal can precede job-row settlement. The event-authorized recheck can
still see Running with no later job event. A later qualifying terminal or caller cancellation may
then be required, as in the CLI. Retained error rows, failed image notifications, restart notices
without a wake and stuck inboxes have similar liveness limits. This is a residual limitation, not
a status-polling or forced-acknowledgment policy. Successful job-to-inbox-to-active handoffs do not
establish an atomic cross-endpoint snapshot for every error, revert or recovery branch.

## Permission requests

The caller's callback answers a permission request; with no callback the agent rejects it, so
nothing is approved; a missing callback is not itself an exception
([ADR-0002](../adr/0002-permission-requests-go-to-a-caller-callback.md)). The agent answers only
requests of its own OpenCode session and of that session's children, because one OpenCode server
carries the requests of every client.

After prompt admission, before reading or mapping queued turn events, the run lists and answers
its root session's pending requests in returned order. It does not enumerate descendants' pending
requests, so it cannot recover a child ask emitted before subscription. Live owned asks remain
answerable through background settling.

The shared run-scoped responder attempts each request id once, across snapshot and live events,
and reconciles serially. Unlike the CLI's concurrent reconciliation and lack of id deduplication,
this avoids concurrent or repeated caller side effects and preserves sequential responder state.
Reverse these differences with an explicitly revised callback/idempotency/concurrency contract or
upstream semantics that make them unnecessary. Caller replies are passed through unchanged,
including message-less Rejects. The no-handler Reject carries feedback. Callback-failure cleanup
also carries feedback so it does not cause an unreported shutdown decline; remove that special
feedback only when upstream no longer turns message-less cleanup declines into that outcome.

Upstream v2.3.0 `packages/core/src/permission.ts:317–335,340–362` settles same-session sibling
requests on Reject and reevaluates pending requests after Always saves a rule. These server rules
can race our list and reply; they do not imply that this adapter evaluates or persists rules.

A failed reply to OpenCode leaves the run as an exception; a reply that the permission request was not found is ignored, because the request is no longer pending; other not-found replies, such as a missing session, still leave the run as an exception. OpenCode's non-interactive client ignores every reply failure; the agent surfaces them because a request whose answer was lost otherwise blocks its session without a trace. The step is reverted if OpenCode starts retrying or reconciling lost replies itself.

## Cancellation

A cancelled run interrupts the turn on the server. This is the one step beyond
`GitHubCopilotAgent`, which only passes the token to its calls: without the interrupt, a cancelled
run would leave the turn running on the server. The step is dropped if OpenCode starts ending a
turn when its client goes away.

**Finite cancellation sweep (M1, D4).** After acknowledged prompt admission, caller cancellation
awaits a best-effort sweep, including when a cancelled streaming consumer disposes while holding an
update. Before observed delivery, its sole action is cancelling that exact prompt's inbox id. After
observed delivery it awaits that inbox cancellation, then one descendant-job snapshot, then starts
and awaits all selected mutations concurrently: the root interrupt, each selected subagent child's
interrupt, and each selected shell's removal. Selection uses `Time.Started >= Prompt.Time.Created`,
without status or accepted message-less-decline exemptions. Images and unknown kinds receive no
mutation. Shell removals share one root-session location lookup, started only if a shell is selected;
a failed lookup skips every removal but not the interrupts. Removal addresses `ShellId`, not the job
registry id, and destroys retained shell output. Interrupt requests omit Resume and do not retry an
idle no-op. This partially mirrors `packages/cli/src/run/noninteractive.ts:262–275` at v2.3.0.

The cleanup runs in at most four dependent stages: inbox cancel, job snapshot, root-location lookup,
and the concurrent mutation group. Each stage is bounded only by the SDK's inactivity window
(`NetworkTimeout`), which restarts on every bit of network progress (SDK 1.1.0
`docs/guide/requests.md:209–210`). A server that stops responding delays caller cancellation by about
four windows; a server that keeps answering slowly has no total bound. This is a known limit, not a
wall-clock guarantee.

Every cleanup call uses `CancellationToken.None` so the already-cancelled caller token cannot
prevent sending it. Each action tolerates only `OpenCodeException`, including the SDK's own
inactivity-timeout `OpenCodeTransportException`, and proceeds independently. Such failures never
replace the caller's cancellation. Cancellation tolerates them while permission-reply failures
surface because a lost reply blocks a session without a trace, whereas a failed cleanup leaves the
caller already informed by cancellation. Independent local cleanup faults remain observable in
their established order: the background waiter cancels and joins its idle/readiness operations
before the sweep, and responder rejection finishes before the sweep; the bus closes afterwards.

**Admission uncertainty (M3).** `PromptAsync` retains the caller token and arms the sweep only on a
successful admission response. This differs from the CLI, which checks before sending but does not
pass its abort signal to the prompt (`noninteractive.ts:214,217–224,261`): the GitHubCopilotAgent
north star passes caller cancellation to its calls. Admission is idempotent and uninterruptible
upstream (`packages/core/src/session/session.ts:155–167`), so a cancelled request can lose the
response despite admission. Such a prompt receives no sweep. Reverse this divergence when the SDK
or OpenCode supplies an admission result that survives a cancelled request.

**No cancellation drain.** The sweep ends without following wakes or waiting for quiet, unlike the
CLI (`noninteractive.ts:165–166,257–258,276–282`). The GitHubCopilotAgent north star returns control
to the caller on cancellation; no second cancellation lifecycle is added. Reverse this divergence
with an atomic tree-cancellation API or an approved cancellation-drain contract. Images, late jobs,
later wakes and work whose discovery or mutation failed can survive. Time-based selection can
include another client's work. A root interrupt targets the session's active execution, not a prompt
identity; after delivery it can hit another client's turn, including in the background phase or just
after our original turn ends, as in the CLI. The delivery gate removes only the certainly-foreign
pre-delivery case. Delivery unseen during reconciliation or a permission callback before the turn
reader runs, or racing cancellation after its delivery flag was read, can leave our own started turn
running because inbox cancellation is then a no-op. Local idle-wait cancellation itself proves
neither server termination nor continuation.

## Not supported

`AgentRunOptions` is ignored, `ResponseFormat` included. OpenCode forms and questions are not MAF
content.

**Forms (D5).** No form/global-form reconciliation, cancellation or replies are added; this contract
handles permissions only. Unlike the CLI's question cancellation and unsupported-form failure,
an ignored form can block a descendant indefinitely. Caller cancellation ends the local wait, not
the form. Reverse this difference when forms receive an approved contract.

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

### Mirrored behavior at OpenCode v2.3.0

The limits and approved differences above qualify this list; it is not full CLI parity.

1. Permission ownership follows the root-or-parent-chain rule — `packages/cli/src/run/noninteractive.ts:84–95`.
2. Permission asks are answered before the own-session event filter — `packages/cli/src/run/noninteractive.ts:153–172`.
3. Completion waits for admission-time-selected descendant jobs — `packages/cli/src/run/noninteractive.ts:181–207,238–254`.
4. Pending root permissions are reconciled after admission — `packages/cli/src/run/noninteractive.ts:224–235`.
5. Tool names come from input-started, with `tool` if absent — `packages/cli/src/run/noninteractive.ts:527–534`.
6. Subagents run in the background and can ask after parent turn end — `packages/core/src/tool/plugin/subagent.ts:52–55,201–224`.
7. Reject settles the session's other pending requests — `packages/core/src/permission.ts:317–335`.
8. Open calls close with interrupted-tool text — `packages/core/src/session/runner/llm.ts:425–435`; the common prefix matches, but the adapter does not append upstream's child-session suffix.
9. Attribution uses server admission time, Started >= Created, without a running-only filter — `packages/cli/src/run/noninteractive.ts:184–198`.
10. Quietness checks selected jobs, remembered inboxes, then owned active drains — `packages/cli/src/run/noninteractive.ts:181–207`.
11. Permission listening stays alive during idle waits and terminal-wake cycles — `packages/cli/src/run/noninteractive.ts:142–178,242–253`.
12. CLI captures execution-end before queries and wakes only for remembered sessions — `packages/cli/src/run/noninteractive.ts:80,164–170,242–253`; our separately recorded remembered-or-owned widening still consumes each generation once.
13. Cancellation partially mirrors the CLI's inbox, selected-child interrupt and shell-removal actions — `packages/cli/src/run/noninteractive.ts:262–275`; the delivery gate, admission uncertainty and no-drain boundary above qualify it.

### Experimental SDK declarations used by the provider

The provider accepts OPENCODE001 for these SDK declarations in its background job-list, session-wait and cancellation-sweep paths; the inventory is audited against product source whenever a use changes.

```text
OpenCode.Sdk.OpenCodeClient.Experimental
OpenCode.Sdk.ExperimentalClient
OpenCode.Sdk.ExperimentalJobListRequest
OpenCode.Sdk.ExperimentalJobListResponse
OpenCode.Sdk.ExperimentalSessionWaitResponse
OpenCode.Sdk.Models.IJobInfo
OpenCode.Sdk.Models.JobTime
OpenCode.Sdk.Models.JobSubagent
OpenCode.Sdk.Models.JobShell
```

The canonical inventory is embedded directly in the SDK-marker tripwire. The SDK's consumer opt-in
is accepted only in narrow referencing expressions or declarations through OPENCODE001 pragma
pairs, never project-wide NoWarn or Experimental annotations on adapter types. Review a changed SDK
marker before removing or narrowing its opt-in and inventory row. Test-only job-status construction
is not another product dependency or inventory entry.
