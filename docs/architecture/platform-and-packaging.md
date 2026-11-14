# Platform and Packaging Architecture

Date: 2026-11-14

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
| `tools/OpenCode.Agents.AI.Tools` | The repository tool behind `tools/opencode-tool.cs`; not a package artifact |
| `tests/OpenCode.Agents.AI.Tools.Tests` | The tool's tests |

The test project is not a package artifact.

A project under `playground/` is a consumer example. It references the library by project
reference, follows the library's quality rules, is never packed, and nothing under `src/` depends
on it.

A folder, helper, or project is created only for a consumer: there is no shared test library,
benchmark project, or generator without one.
`Directory.Build.targets` and `.globalconfig` exist only for an actual late-bound invariant or a
validated compilation-wide arbitration (`../engineering/quality-gates.md`).

The repository tool foundation is copied from `opencode-dotnet/opencode-sdk-dotnet` commit
`b86e416427ee`, with namespace root `OpenCode.Sdk.Tools` changed to `OpenCode.Agents.AI.Tools`.
The copies stay separate for now; shared-foundation work is tracked in
[opencode-dotnet/opencode-agents-ai#5](https://github.com/opencode-dotnet/opencode-agents-ai/issues/5).
These are the differences from the SDK:

| # | Difference from the SDK | Reason |
|---|---|---|
| 1 | Not copied: `Generator/`, `Output/`, `Benchmarks/`, `TestRoster/`, `Serialization/`, and the commands `generate`, `refresh-spec`, `compare-benchmarks`, `verify-test-roster`, `merge-group-reuse`, with their `ToolApp` registrations, tests and test support | This repository generates nothing and has no spec and no benchmark; the roster and merge-group commands are copied when they are needed (#5) |
| 2 | `IProcessRunner` and `ProcessResult` move from `Generator/Refresh/Abstractions/` to `Infrastructure/Processes/Abstractions/` (namespace `OpenCode.Agents.AI.Tools.Infrastructure.Processes.Abstractions`); `CliWrapProcessRunner` moves from `Generator/Refresh/` to `Infrastructure/Processes/` (namespace `OpenCode.Agents.AI.Tools.Infrastructure.Processes`); their text is otherwise unchanged | Their SDK home is the generator's folder, which is not copied; seams keep their `Abstractions/` folder |
| 3 | No source split: `CloneScopeFilter`, `CloneSourceSplit` and `CloneDetector.ReadSourceSplitAsync` are not copied; `CloneScope` loses `Filter`; `CloneDetector.RunAsync` loses its `split` parameter; the detector's ignore list is the common globs plus `scope.Ignore` | The split reads the generator's manifest; this repository has no generated source |
| 4 | `CloneScopes.All` holds three scopes: `src`, `tools`, `tests`. No `generated` scope | The same |
| 5 | `VerifyClonesCommand.SolutionFileName` and the test support's `RepositoryRoot.SolutionFileName` are `OpenCode.Agents.AI.slnx`; the command's description is "Run the pinned clone detector: hand-written src/, tools/ and tests/ against their baselines." | This repository's solution; no reported-only scope |
| 6 | `Properties/AssemblyInfo.cs` names one friend, `OpenCode.Agents.AI.Tools.Tests`; the project file has no `AssemblyMetadata` item | No performance test project; only the generator's formatter reads that metadata |
| 7 | `tools/jscpd/package.json`: name `opencode-agents-ai-clone-detector`, and a `packageManager` field that the Bun setup action reads | This repository has no `external/opencode` to read the Bun version from |
| 8 | Three `ToolAppTests` (failure mapping, cancellation, global logging options) run `verify-clones` and raise their failures through the process-runner seam; the other four keep the SDK's own oracles | The SDK's tests use the `generate` command and its formatter seam, which are not copied |
| 9 | The Bun setup action uses `oven-sh/setup-bun@v2`, the major tag this repository's action rule requires; the SDK pins a SHA | Actions follow `../engineering/quality-gates.md` |
| 10 | Comments and the setup action's texts that describe the SDK's generator, manifest, submodule or pinned server are reworded to this repository's facts | Comments state the local status quo (`../engineering/documentation.md`) |

## .NET SDK

`global.json` selects the .NET SDK. A move to another feature band, or to a newer .NET major, is
an explicit change, not drift.

## Target frameworks

The package matrix follows Microsoft's support lifecycle: every modern .NET version Microsoft
supports, plus `net472` and `netstandard2.0`. It is:

```text
netstandard2.0;net472;net10.0;net11.0
```

A version joins at its first go-live release candidate and leaves no later than its end of
support; a non-preview package is built only on a GA SDK. `global.json` pins the GA .NET 11 SDK.
`Directory.Build.props` holds every target-framework literal: `DefaultTargetFramework` (the LTS
release), `LatestTargetFramework`, `AllTargetFrameworks` for the package, and
`TestTargetFrameworks` for the test project, which adds `net472` on Windows.

`net472` owns .NET Framework-specific compile and runtime behavior. `netstandard2.0` is the broad
compatibility bridge and has no runtime of its own; net472 legs proxy its downlevel behavior. It is
a compatibility asset, not a supported runtime: a consumer on another runtime can install it, but
support and testing cover the targeted runtimes only.

Every asset of a package exposes the same public API, held to that package's one reviewed baseline:
a test reads each shipped asset from the build output and compares it. The reviewed differences are
the trimming and native AOT annotations (`RequiresUnreferencedCode`, `RequiresDynamicCode`) and the
`Experimental` marks, which are public attributes on .NET only; the `netstandard2.0` and `net472`
assets carry internal copies, so their members render without them. Any other difference fails the
test.

Modern C# on downlevel targets is deliberate and supported inside this repository by the private,
source-only Polyfill package. Exact package versions belong to `Directory.Packages.props`, not this
document. A polyfilled member whose downlevel behaviour is a hazard is banned in
`BannedSymbols.txt`, which names its replacement when one exists.

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
- The clone detector is the repository's one npm dependency, pinned exactly in
  `tools/jscpd/package.json` with its own `bun.lock`.
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
- The public surface is held to one reviewed public API baseline (`../engineering/testing-style.md`).
  Package validation and its published-release baseline follow `../engineering/quality-gates.md`.

## Licensing

The repository uses the MIT license in `LICENSE`; `THIRD-PARTY-NOTICES.md` carries the notices of
derived content. Repository packages ship the license through `PackageLicenseFile`. Project and
pack files own the mechanical packaging configuration.
