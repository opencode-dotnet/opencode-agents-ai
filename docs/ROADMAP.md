# Roadmap

Date: 2026-11-06

Operational state: what exists today, what is queued next, what is still open, and what is known to
be incomplete. This file is a summary and shrinks as work lands. `../AGENTS.md` routes to the
current architecture and engineering canon; decision records live in `adr/`. Work items are tracked
as GitHub Issues in this repository; there is no project board. No date is promised for any item.

## Status

**Pre-release bootstrap. Nothing is installable yet, and no package is published.**

The canon, build backbone and CI workflows exist: the empty library, public API baseline on every
shipped asset, analyzer wall, format gates, Slopwatch, package validation, three-OS CI with its
`CI result` check, dependency review and contribution files. No provider behavior exists yet;
the first green hosted run, merge queue and branch protection are next.

Until the merge queue exists, a pull request merges on the maintainer's approval.

## Bootstrap, in order

1. **Canon** - done.
2. **Target frameworks** - done (decided from the published assets of the dependencies).
3. **Build backbone** - done: the library project and the test project with the public API baseline,
   the analyzer wall, the format gates, Slopwatch and package validation.
4. **CI** - the CI workflow with its `CI result` check, dependency review and contribution files
   are in this change. The merge queue and branch protection on `master` follow after the first
   green hosted run (`engineering/workflow.md`).
5. **Spike** - `OpenCodeAgent` non-streaming, then streaming; a workflow with a deterministic .NET
   executor between two OpenCode agents; checkpoint and reconnect.
6. **Own use** - one real loop run with the product: implement, gate, review, fix-up.

## Later

- **First preview package** - publication is separately authorized.
  - Pin the package README's links to the packed commit, with absolute repository links in
    `README.md`.
  - Warn a consumer whose target framework resolves to the netstandard2.0 asset on an unsupported
    runtime.
  - Add the packaging tests for both mechanisms.
- The layer above MAF is out of this repository's scope.

## Known Gaps

- **No provider behavior yet.** The library's public surface is empty; the build backbone and local
  check in `engineering/quality-gates.md` are in place.
- **No merge queue yet.** Until it exists, a pull request merges on the maintainer's approval;
  the merge queue and branch protection follow after the first green hosted run.
- **No clone gate yet.** It is deferred to the spike, no later than its first pull request that
  adds hand-written production or test code.
