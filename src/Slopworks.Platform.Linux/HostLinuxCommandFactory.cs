using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Linux;

/// <summary>
/// The "managed Linux environment" on a Linux host is the host itself. Commands for the
/// invoking user run directly; root work goes through pkexec (GUI polkit prompt).
/// Terminate is deliberately a no-op — Slopworks never restarts the user's machine session.
/// </summary>
public sealed class HostLinuxCommandFactory : ILinuxCommandFactory
{
    public ProcessSpec Command(string bashCommand, string user = "operator")
        => user == "root"
            ? new ProcessSpec("pkexec", ["bash", "-c", bashCommand])
            : new ProcessSpec("bash", ["-c", bashCommand]);

    public ProcessSpec Script(string scriptText, string user = "root")
    {
        var normalized = scriptText.ReplaceLineEndings("\n");
        return user == "root"
            ? new ProcessSpec("pkexec", ["bash", "-s"], StdinText: normalized)
            : new ProcessSpec("bash", ["-s"], StdinText: normalized);
    }

    /// <summary>No environment to bounce on a host — wsl.conf/systemd restarts don't exist here.</summary>
    public ProcessSpec Terminate() => new("true", []);
}

/// <summary>
/// Elevation on Linux: pkexec prefixes the command and polkit shows the GUI prompt; output
/// capture works directly — no worker-relaunch round trip like Windows UAC needs.
/// </summary>
public sealed class PkexecProcessRunner(SystemProcessRunner direct) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct)
    {
        if (!spec.RequiresElevation)
            return direct.RunAsync(spec, liveOutput, ct);

        var elevated = spec with
        {
            Exe = "pkexec",
            Args = [spec.Exe, .. spec.Args],
            RequiresElevation = false,
        };
        return direct.RunAsync(elevated, liveOutput, ct);
    }
}
