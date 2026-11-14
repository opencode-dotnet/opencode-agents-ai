# Roadmap

Date: 2026-11-14

Operational state: what exists today, what is queued next, what is still open, and what is known to
be incomplete. This file is a summary and shrinks as work lands. `../AGENTS.md` routes to the
current architecture and engineering canon; decision records live in `adr/`. Work items are tracked
as GitHub Issues in this repository; there is no project board. No date is promised for any item.

## Status

**Bootstrap complete. Nothing is installable yet, and no package is published.**

The canon, build backbone and CI workflows exist: the empty library, public API baseline on every
shipped asset, analyzer wall, format gates, Slopwatch, package validation, three-OS CI with its
`CI result` check, dependency review and contribution files. Everything lands on `master` through
the merge queue, which requires `CI result`. No provider behavior exists yet; the first version is
next.

## Bootstrap, in order

1. **Canon** - done.
2. **Target frameworks** - done (decided from the published assets of the dependencies).
3. **Build backbone** - done: the library project and the test project with the public API baseline,
   the analyzer wall, the format gates, Slopwatch and package validation.
4. **CI** - done: the CI workflow with its `CI result` check, dependency review, contribution files,
   and the merge queue and branch protection on `master` (`engineering/workflow.md`).
5. **First version** - `OpenCodeAgent`, non-streaming and streaming, with reconnect, and its
   tests; the review-loop example app: a workflow with a deterministic .NET executor between two
   OpenCode agents, with checkpoints. Its last step is one recorded proof run against a real
   OpenCode server.
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
- **Clone baselines pending.** The repository tool, its tests and the CI steps exist; the three
  baselines are created with the first library source.
