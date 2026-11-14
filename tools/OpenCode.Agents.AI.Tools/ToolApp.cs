using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Agents.AI.Tools.Clones;
using OpenCode.Agents.AI.Tools.Commands;
using OpenCode.Agents.AI.Tools.Infrastructure;
using OpenCode.Agents.AI.Tools.Infrastructure.Logging;
using OpenCode.Agents.AI.Tools.Infrastructure.Processes;
using OpenCode.Agents.AI.Tools.Infrastructure.Processes.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Extensions.DependencyInjection;
using Testably.Abstractions;

namespace OpenCode.Agents.AI.Tools;

/// <summary>Composition root for the repo tool: DI registrar plus the command surface.</summary>
public static class ToolApp
{
    /// <summary>Builds the tool's complete production service collection.</summary>
    internal static ServiceCollection CreateServices(Action<IServiceCollection>? overrideServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem, RealFileSystem>();
        services.AddSingleton(AnsiConsole.Console);
        services.AddSingleton<ToolLoggingOptions>();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<ILoggerProvider, SpectreConsoleLoggerProvider>();
        services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
        services.AddSingleton<IProcessRunner, CliWrapProcessRunner>();
        services.AddSingleton<CloneDetector>();
        services.AddSingleton<ICommandInterceptor, GlobalOptionsInterceptor>();

        overrideServices?.Invoke(services);
        return services;
    }

    /// <summary>Builds the DI registrar; tests inject service overrides.</summary>
    internal static DependencyInjectionRegistrar CreateRegistrar(Action<IServiceCollection>? overrideServices = null)
    {
        return new DependencyInjectionRegistrar(CreateServices(overrideServices));
    }

    /// <summary>Registers the command surface; shared by the app and <c>CommandAppTester</c>.</summary>
    internal static void Configure(IConfigurator configurator) => Configure(configurator, CancellationToken.None);

    internal static void Configure(IConfigurator configurator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configurator);

        configurator.SetApplicationName("opencode-tool");
        configurator.SetExceptionHandler((exception, _) =>
            new ToolExceptionHandler(configurator.Settings.Console ?? AnsiConsole.Console)
                .Handle(exception, configurator.Settings.CancellationExitCode, cancellationToken));
        configurator
            .AddCommand<VerifyClonesCommand>("verify-clones")
            .WithDescription("Run the pinned clone detector: hand-written src/, tools/ and tests/ against their baselines.");
    }

    /// <summary>Entry point used by tools/opencode-tool.cs.</summary>
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        RunAsync(args, cancellationToken, null);

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken,
        Action<IServiceCollection>? overrideServices = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var registrar = CreateRegistrar(overrideServices);
        var app = new CommandApp(registrar);
        app.Configure(configurator => Configure(configurator, cancellationToken));
        return await app.RunAsync(args, cancellationToken).ConfigureAwait(false);
    }
}
