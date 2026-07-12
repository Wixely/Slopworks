namespace Slopworks.Core.Cli;

public enum CliCommand
{
    /// <summary>No recognized command — the app should start its GUI.</summary>
    None,
    Start,
    Stop,
    Status,
    Help,
}

public sealed record CliInvocation(CliCommand Command, string? Model);

/// <summary>
/// Minimal headless command line: <c>start [--model &lt;id&gt;]</c>, <c>stop</c>,
/// <c>status</c>, <c>--help</c>. Anything unrecognized leaves Command = None so the GUI
/// launches as before.
/// </summary>
public static class CliParser
{
    public static CliInvocation Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new CliInvocation(CliCommand.None, null);

        var command = args[0].ToLowerInvariant() switch
        {
            "start" => CliCommand.Start,
            "stop" => CliCommand.Stop,
            "status" => CliCommand.Status,
            "help" or "--help" or "-h" or "/?" => CliCommand.Help,
            _ => CliCommand.None,
        };

        string? model = null;
        for (var i = 1; i < args.Count - 1; i++)
        {
            if (args[i] is "--model" or "-m")
                model = args[i + 1];
        }

        return new CliInvocation(command, model);
    }

    /// <summary>True when the args select a headless command (so no GUI should be shown).</summary>
    public static bool IsCliCommand(IReadOnlyList<string> args) => Parse(args).Command != CliCommand.None;
}
