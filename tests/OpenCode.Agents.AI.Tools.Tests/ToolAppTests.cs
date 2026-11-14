using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Agents.AI.Tools.Clones;
using OpenCode.Agents.AI.Tools.Commands;
using OpenCode.Agents.AI.Tools.Infrastructure;
using OpenCode.Agents.AI.Tools.Infrastructure.Logging;
using OpenCode.Agents.AI.Tools.Infrastructure.Processes.Abstractions;
using OpenCode.Agents.AI.Tools.Tests.Support;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace OpenCode.Agents.AI.Tools.Tests;

public sealed class ToolAppTests
{
    [Test]
    [Arguments("clone", 1, "Refused: bad [clone]\n")]
    [Arguments("bug", -1, "Error: bad [bug]\n")]
    [Arguments("timeout", -1, "Error: bad [timeout]\n")]
    [Arguments("cancel", 130, "")]
    [Arguments("cancelled-refusal", 130, "")]
    public async Task RunAsync_Should_Map_Failures_Without_Propagation(string kind, int exitCode, string output)
    {
        using var cancellation = new CancellationTokenSource();
        var message = $"bad [{kind}]";
        Exception exception = kind switch
        {
            "clone" => new CloneDetectorException(message),
            "cancel" => new OperationCanceledException(),
            "timeout" => new OperationCanceledException(message),
            "cancelled-refusal" => new CloneDetectorException(message),
            _ => new InvalidOperationException(message, new IOException("hidden cause")),
        };
        var scenario = await new CloneScenario().BuildAsync();
        FailNextRun(scenario, async () =>
        {
            if (kind is "cancel" or "cancelled-refusal")
            {
                await cancellation.CancelAsync();
            }
            throw exception;
        });
        var result = await RunWithoutPropagationAsync(scenario, cancellationToken: cancellation.Token);

        await Assert.That(result.ExitCode).IsEqualTo(exitCode);
        await Assert.That(result.Output.Replace("\r\n", "\n", StringComparison.Ordinal)).IsEqualTo(output);
    }

    [Test]
    public async Task RunAsync_Should_Return_Cancellation_When_A_Refusal_Follows_Cancellation()
    {
        var scenario = await new CloneScenario().BuildAsync();
        using var cancellation = new CancellationTokenSource();
        FailNextRun(scenario, async () =>
        {
            await cancellation.CancelAsync();
            throw new CloneDetectorException("detector interrupted");
        });

        var result = await RunWithoutPropagationAsync(scenario, cancellationToken: cancellation.Token);

        await Assert.That(result.ExitCode).IsEqualTo(130);
        await Assert.That(result.Output).IsEmpty();
    }

    [Test]
    public async Task RunAsync_Should_Preserve_Parse_Error_Output_Without_A_Resolver()
    {
        using var registrar = ToolApp.CreateRegistrar();
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);
        var baseline = new CommandAppTester();
        baseline.Configure(configurator => configurator.SetApplicationName("opencode-tool"));

        var result = await tester.RunAsync(["--unknown"]);
        var expected = await baseline.RunAsync(["--unknown"]);

        await Assert.That(result.ExitCode).IsEqualTo(-1);
        await Assert.That(result.Output).IsEqualTo(expected.Output);
    }

    [Test]
    public async Task CreateServices_Should_Resolve_Full_Hosting_Composition()
    {
        await using var provider = ToolApp.CreateServices().BuildServiceProvider();

        var fileSystem = provider.GetRequiredService<IFileSystem>();
        var console = provider.GetRequiredService<IAnsiConsole>();
        var logger = provider.GetRequiredService<ILogger<VerifyClonesCommand>>();
        var loggingOptions = provider.GetRequiredService<ToolLoggingOptions>();
        var detector = provider.GetRequiredService<CloneDetector>();
        var runner = provider.GetRequiredService<IProcessRunner>();
        var interceptors = provider.GetServices<ICommandInterceptor>().ToArray();

        await Assert.That(fileSystem).IsTypeOf<RealFileSystem>();
        await Assert.That(console).IsSameReferenceAs(AnsiConsole.Console);
        await Assert.That(logger).IsNotNull();
        await Assert.That(loggingOptions).IsNotNull();
        await Assert.That(detector).IsNotNull();
        await Assert.That(runner).IsNotNull();
        await Assert.That(interceptors.Length).IsEqualTo(1);
        await Assert.That(interceptors[0]).IsTypeOf<GlobalOptionsInterceptor>();
    }

    [Test]
    public async Task CreateRegistrar_Should_Apply_Seam_Overrides_After_Production_Registrations()
    {
        var expected = new MockFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services => services.AddSingleton<IFileSystem>(expected));
        var resolver = registrar.Build();

        var actual = resolver.Resolve(typeof(IFileSystem));

        await Assert.That(actual).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task RunAsync_Should_Apply_Global_Logging_Options_Before_Command_Execution()
    {
        const string logPath = "logs/tool.log";
        var scenario = await new CloneScenario().WithPassingRuns().BuildAsync();
        var fileSystem = scenario.FileSystem;
        var result = await RunWithoutPropagationAsync(scenario, ["verify-clones", "--log-level", "debug", "--log-file", logPath]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(fileSystem.Directory.EnumerateFiles(fileSystem.Path.GetFullPath("logs")))
            .Contains(fileSystem.Path.GetFullPath(logPath));
        var log = await fileSystem.File.ReadAllTextAsync(logPath, CancellationToken.None);
        await Assert.That(log).Contains("Verify-clones invoked at");
    }

    [Test]
    public async Task RunAsync_Should_Return_Nonzero_When_Command_Is_Unknown()
    {
        using var registrar = ToolApp.CreateRegistrar();
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["does-not-exist"]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
    }

    private static void FailNextRun(CloneScenario scenario, Func<Task> failure) =>
        scenario.Runner.Expect("bun", () =>
        [
            "tools/jscpd/node_modules/jscpd/run-jscpd.js", "src", "--config",
            scenario.FileSystem.Directory.EnumerateFiles(scenario.FileSystem.Path.GetTempPath(), "src.json", SearchOption.AllDirectories).Single(),
            "--fail-on-empty", "--baseline", "tools/jscpd/src.baseline.json", "--fail-on-new-clones",
        ], scenario.Root, sideEffect: failure);

    private static async Task<CommandAppResult> RunWithoutPropagationAsync(CloneScenario scenario,
        string[]? arguments = null, CancellationToken cancellationToken = default)
    {
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(scenario.FileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProcessRunner>(scenario.Runner);
        });
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings { TrimConsoleOutput = false });
        tester.Configure(configurator => ToolApp.Configure(configurator, cancellationToken));
        return await tester.RunAsync(arguments ?? ["verify-clones"], cancellationToken);
    }
}
