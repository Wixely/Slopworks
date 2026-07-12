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
                CliCommand.Status => StatusAsync(host, runner).GetAwaiter().GetResult(),
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

    private static async Task<int> StatusAsync(SlopworksHost host, IProcessRunner runner)
    {
        var health = await host.Server.GetHealthAsync(runner, CancellationToken.None);
        Console.WriteLine($"Container: {health.ContainerState}");
        Console.WriteLine($"API:       {(health.ApiHealthy ? "healthy" : "not responding")} at {host.Server.BaseUrl}/v1");
        return health.ApiHealthy ? 0 : 1;
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
              slopworks status                    Print server + API health (exit 0 = healthy)
              slopworks --help                    Show this help

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
