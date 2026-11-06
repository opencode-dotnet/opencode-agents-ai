# Platform and Packaging Architecture

Date: 2026-11-06

Canonical current rules for product identity, projects, the build toolchain, dependencies,
versioning, NuGet packaging, and licensing. The agent boundary lives in `agent-integration.md`.
This document states the decided contract; which parts are implemented is operational state in
`../ROADMAP.md`.

## Identity

| Identity | Value |
|---|---|
| Repository | `opencode-dotnet/opencode-agents-ai` |
| Product project, assembly, and root namespace | `OpenCode.Agents.AI` |
| NuGet package | `OpenCodeDotNet.Agents.AI` |

The NuGet ID and the CLR names are deliberately distinct: nuget.org reserves the `OpenCode.`
prefix for an unrelated owner.

## Projects

| Project | Role |
|---|---|
| `src/OpenCode.Agents.AI` | The MAF provider library; the only distributed product |
| `tests/OpenCode.Agents.AI.Tests` | Product tests, including the public API baseline |

The test project is not a package artifact.

A folder, helper, or project is created only for a consumer: there is no shared test library,
benchmark project, or generator without one.
`Directory.Build.targets` and `.globalconfig` exist only for an actual late-bound invariant or a
validated compilation-wide arbitration (`../engineering/quality-gates.md`).

## .NET SDK

`global.json` selects the .NET SDK. A move to another feature band, or to a newer .NET major, is
an explicit change, not drift.

## Dependencies

- `Directory.Packages.props` is the one central package-management owner, with literal pinned
  versions; project references are versionless. Exact versions are read there, not from this
  document. A new package enters through `dotnet add package`.
- Only genuinely aligned release families share a version variable. There are no floating
  versions, no unused version variables, no transitive pinning, and no lock-file policy.
- Declare explicitly every package this repository's source uses directly; trust the transitive
  graph otherwise. A new shipped dependency is a maintainer decision recorded with its consumer; no
  package is added for a capability no scheduled work consumes.
- The provider consumes the OpenCode .NET SDK only as the published package `OpenCodeDotNet.Sdk`,
  never its source or internals; it is added with its first consumer.
- MAF packages are added with their first consumer. The dependency direction and the MAF boundary
  are owned by `agent-integration.md`.
- Restore uses `nuget.config` with a cleared source list and nuget.org only, unless a concrete new
  source is approved. NuGetAudit fails restore on an advisory (`../engineering/quality-gates.md`),
  and the remedy is a prompt pin bump (`../engineering/workflow.md`).

## Versioning

Root `Directory.Build.props` is the single version owner (`VersionPrefix` and `VersionSuffix`).
The product and package versions are checked projections of it, not independent release
identities. This package versions on its own. A preview version authorizes no publication.

## Packages

- `OpenCode.Agents.AI` is the MAF provider library. Its published package id is
  `OpenCodeDotNet.Agents.AI`; the packable project states its own `PackageId`.
- Exact package references and dependency versions are read from project files and
  `Directory.Packages.props`. Documentation records policy, not a second version inventory.
- A future package is added only for a real distribution boundary; repository layout alone does
  not justify another artifact.
- The README in the package is pinned to the packed commit so its repository links open the
  documentation that shipped with it.
- The public surface is held to one reviewed public API baseline (`../engineering/testing-style.md`).
  Package validation and its published-release baseline follow `../engineering/quality-gates.md`.

## Licensing

The repository uses the MIT license in `LICENSE`; `THIRD-PARTY-NOTICES.md` carries the notices of
derived content. Repository packages ship the license through `PackageLicenseFile`. Project and
pack files own the mechanical packaging configuration.
