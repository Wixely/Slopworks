namespace Slopworks.Core.Cli;

public enum CliCommand
{
    /// <summary>No recognized command — the app should start its GUI.</summary>
    None,
    Start,
    Stop,

    /// <summary>Container + API health (one shot).</summary>
    Status,

    /// <summary>Model ids the server currently reports.</summary>
    Models,

    /// <summary>Block until the API answers or a timeout elapses.</summary>
    WaitReady,
    Help,
}

public sealed record CliInvocation(
    CliCommand Command,
    string? Model = null,
    bool Json = false,
    int? TimeoutSeconds = null);

/// <summary>
/// Headless command line for embedding/automation:
/// <c>start [--model &lt;id&gt;]</c>, <c>stop</c>, <c>status [--json]</c>,
/// <c>models [--json]</c>, <c>wait-ready [--timeout &lt;s&gt;]</c>, <c>--help</c>.
/// Anything unrecognized leaves Command = None so the GUI launches as before.
/// </summary>
public static class CliParser
{
    public static CliInvocation Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new CliInvocation(CliCommand.None);

        var command = args[0].ToLowerInvariant() switch
        {
            "start" => CliCommand.Start,
            "stop" => CliCommand.Stop,
            "status" => CliCommand.Status,
            "models" => CliCommand.Models,
            "wait-ready" or "ready" => CliCommand.WaitReady,
            "help" or "--help" or "-h" or "/?" => CliCommand.Help,
            _ => CliCommand.None,
        };

        string? model = null;
        var json = false;
        int? timeout = null;

        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--model" or "-m" when i + 1 < args.Count:
                    model = args[++i];
                    break;
                case "--timeout" or "-t" when i + 1 < args.Count && int.TryParse(args[i + 1], out var t):
                    timeout = t;
                    i++;
                    break;
            }
        }

        return new CliInvocation(command, model, json, timeout);
    }

    /// <summary>True when the args select a headless command (so no GUI should be shown).</summary>
    public static bool IsCliCommand(IReadOnlyList<string> args) => Parse(args).Command != CliCommand.None;
}
