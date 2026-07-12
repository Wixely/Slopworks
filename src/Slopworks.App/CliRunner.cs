using System.Runtime.InteropServices;
using Slopworks.Core.Cli;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Logging;
using Slopworks.Platform.Abstractions;

namespace Slopworks.App;

/// <summary>
/// Headless command line for driving the vLLM server without a window. Used for scripting /
/// automation; the GUI is never shown when a command is present. Exit codes: 0 success,
/// 1 failure, 2 usage error.
/// </summary>
public static class CliRunner
{
    public static bool IsCliCommand(string[] args) => CliParser.IsCliCommand(args);

    public static int Run(string[] args)
    {
        AttachToParentConsole();

        var invocation = CliParser.Parse(args);
        if (invocation.Command == CliCommand.Help)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var host = SlopworksHost.Create();
            var runner = new RecordingProcessRunner(host.ProcessRunner, host.CommandLog, "cli", "cli");

            return invocation.Command switch
            {
                CliCommand.Start => StartAsync(host, runner, invocation.Model).GetAwaiter().GetResult(),
                CliCommand.Stop => StopAsync(host, runner).GetAwaiter().GetResult(),
                CliCommand.Status => StatusAsync(host, runner, invocation.Json).GetAwaiter().GetResult(),
                CliCommand.Models => ModelsAsync(host, invocation.Json).GetAwaiter().GetResult(),
                CliCommand.WaitReady => WaitReadyAsync(host, invocation.TimeoutSeconds).GetAwaiter().GetResult(),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"slopworks: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> StartAsync(SlopworksHost host, IProcessRunner runner, string? model)
    {
        if (model is not null && model != host.Config.Server.Model)
        {
            host.Config.Server.Model = model;
            ConfigStore.Save(host.Paths, host.Config);
        }

        var chosen = model ?? host.Config.Server.Model;
        var profile = await host.SystemInfo.GetProfileAsync(CancellationToken.None);
        Console.WriteLine($"Starting vLLM ({chosen}, {(profile.GpuPresent ? "GPU" : "CPU")} mode)…");

        var result = await host.Server.StartAsync(
            runner, profile, chosen, new InlineProgress<string>(Console.WriteLine), CancellationToken.None);

        if (result.Succeeded)
        {
            Console.WriteLine($"Started. First model load can take a while; check '{host.Server.BaseUrl}/v1/models'.");
            return 0;
        }

        Console.Error.WriteLine($"Start failed: {(result.Stderr + result.Stdout).Trim()}");
        return 1;
    }

    private static async Task<int> StopAsync(SlopworksHost host, IProcessRunner runner)
    {
        await host.Server.StopAsync(runner, new InlineProgress<string>(Console.WriteLine), CancellationToken.None);
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static async Task<int> StatusAsync(SlopworksHost host, IProcessRunner runner, bool json)
    {
        var health = await host.Server.GetHealthAsync(runner, CancellationToken.None);

        if (json)
        {
            var state = new CliServerState(
                health.ContainerState, health.ApiHealthy,
                $"{host.Server.BaseUrl}/v1", host.Config.Server.Model, host.Config.Server.Port);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(state, CliJsonContext.Default.CliServerState));
        }
        else
        {
            Console.WriteLine($"Container: {health.ContainerState}");
            Console.WriteLine($"API:       {(health.ApiHealthy ? "healthy" : "not responding")} at {host.Server.BaseUrl}/v1");
        }

        return health.ApiHealthy ? 0 : 1;
    }

    private static async Task<int> ModelsAsync(SlopworksHost host, bool json)
    {
        var models = await host.Server.GetServedModelsAsync(CancellationToken.None);

        if (json)
        {
            var report = new CliModelsReport(models.Count > 0, models);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, CliJsonContext.Default.CliModelsReport));
        }
        else if (models.Count == 0)
        {
            Console.WriteLine("(no models served — the API is not responding)");
        }
        else
        {
            foreach (var model in models)
                Console.WriteLine(model);
        }

        return models.Count > 0 ? 0 : 1;
    }

    private static async Task<int> WaitReadyAsync(SlopworksHost host, int? timeoutSeconds)
    {
        var seconds = timeoutSeconds is > 0 ? timeoutSeconds.Value : 300;
        Console.WriteLine($"Waiting up to {seconds}s for {host.Server.BaseUrl}/v1/models …");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!deadline.IsCancellationRequested)
        {
            if (await host.Server.IsApiHealthyAsync(CancellationToken.None))
            {
                Console.WriteLine("Ready.");
                return 0;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.Error.WriteLine($"Timed out after {seconds}s — the API did not become ready.");
        return 1;
    }

    private static int Usage()
    {
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Slopworks — headless commands (no window is shown):

              slopworks start [--model <hf-id>]   Start the vLLM server
              slopworks stop                      Stop the vLLM server
              slopworks status [--json]           Container + API health (exit 0 = healthy)
              slopworks models [--json]           Model ids the server reports (exit 0 = >=1)
              slopworks wait-ready [--timeout <s>] Block until the API answers (default 300s)
              slopworks --help                    Show this help

            --json prints machine-readable output for embedding.
            Run with no arguments to open the graphical app.
            """);
    }

    /// <summary>
    /// The app is a Windows GUI subsystem binary, so it has no console of its own. Attaching
    /// to the launching terminal's console lets Console output appear when run from one; it
    /// harmlessly no-ops when launched without a parent console (e.g. double-clicked).
    /// </summary>
    private static void AttachToParentConsole()
    {
        if (OperatingSystem.IsWindows())
            AttachConsole(AttachParentProcess);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
