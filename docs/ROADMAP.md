# Roadmap

Date: 2026-11-06

Operational state: what exists today, what is queued next, what is still open, and what is known to
be incomplete. This file is a summary and shrinks as work lands. `../AGENTS.md` routes to the
current architecture and engineering canon; decision records live in `adr/`. Work items are tracked
as GitHub Issues in this repository; there is no project board. No date is promised for any item.

## Status

**Pre-release bootstrap. Nothing is installable yet, and no package is published.**

The canon exists. No code exists yet; the build backbone is next.

Until CI and the merge queue exist, feature branches merge with the maintainer's approval.

## Bootstrap, in order

1. **Canon** - this change.
2. **Target frameworks** - decided from the published assets of the dependencies.
3. **Build backbone** - the library project and the test project with the public API baseline,
   the analyzer wall, the format gates, Slopwatch and package validation.
4. **CI** - the CI workflow with its `CI result` check and dependency review, the contribution
   files, then the merge queue and branch protection on `master` (`engineering/workflow.md`).
5. **Spike** - `OpenCodeAgent` non-streaming, then streaming; a workflow with a deterministic .NET
   executor between two OpenCode agents; checkpoint and reconnect.
6. **Own use** - one real loop run with the product: implement, gate, review, fix-up.

## Later

- **First preview package** - publication is separately authorized.
- The layer above MAF is out of this repository's scope.

## Known Gaps

- **No build yet.** The local check in `engineering/quality-gates.md` is decided but not in place.
- **No CI yet.** The CI contract in `engineering/quality-gates.md` is decided but not in place;
  no change can be called complete by a green `CI result`.
- **No clone gate yet.** It is deferred to the spike, no later than its first pull request that
  adds hand-written production or test code.
