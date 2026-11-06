# OpenCode Agents AI

> **Unofficial.** This project is not affiliated with or endorsed by the
> [opencode](https://opencode.ai) team.

`opencode-agents-ai` is a Microsoft Agent Framework (MAF) provider for
[OpenCode](https://github.com/anomalyco/opencode). Its `OpenCodeAgent` derives from MAF's `AIAgent`
and makes one OpenCode coding session one MAF agent, so it can be a node in MAF workflows. It is a
thin library adapter over the published OpenCode .NET SDK, intended for the NuGet package
`OpenCodeDotNet.Agents.AI`, not a framework. The integration boundary is described in
[`docs/architecture/agent-integration.md`](docs/architecture/agent-integration.md).

## Status

**Pre-release bootstrap. Nothing is installable yet, and no package is published.**

[`docs/ROADMAP.md`](docs/ROADMAP.md) has the current state and what comes next. Usage arrives with
the spike.

## Developing

Building the repository needs the .NET SDK that `global.json` selects.

Before a pull request opens, run the local check that
[`docs/engineering/quality-gates.md`](docs/engineering/quality-gates.md) describes; it has the
exact commands.

Contributors and coding agents start at [`AGENTS.md`](AGENTS.md), which routes each kind of work to
its canon; [`CONTEXT.md`](CONTEXT.md) defines the terms this repository uses.

See [Contributing](.github/CONTRIBUTING.md) for issues, review and pull requests. CI checks Release
builds and the test module on Windows, Linux and macOS, with net472 on Windows; these checks do
not prove package publication. [Quality gates](docs/engineering/quality-gates.md#continuous-integration)
own the CI contract, and the [roadmap](docs/ROADMAP.md) records hosted verification and protection status.

## License

Licensed under MIT, see [LICENSE](LICENSE) for the full text. Content derived from the OpenCode .NET
SDK repository carries its own notice in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
