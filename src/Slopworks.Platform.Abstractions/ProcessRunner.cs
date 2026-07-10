using System.Text;

namespace Slopworks.Platform.Abstractions;

/// <summary>
/// Description of an external process invocation. StdoutEncoding matters on Windows:
/// wsl.exe management commands (--status, --list, ...) emit UTF-16LE, while commands run
/// inside a distro emit UTF-8.
/// </summary>
public sealed record ProcessSpec(
    string Exe,
    IReadOnlyList<string> Args,
    string? WorkingDir = null,
    Encoding? StdoutEncoding = null,
    IReadOnlyDictionary<string, string>? Env = null,
    bool RequiresElevation = false,
    string? StdinText = null)
{
    /// <summary>Human-readable command line, shown verbatim on confirmation cards and in logs.</summary>
    public string CommandLineDisplay => Args.Count == 0
        ? Exe
        : $"{Exe} {string.Join(' ', Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs the process to completion. Output lines are streamed to <paramref name="liveOutput"/>
    /// as they arrive. Cancellation kills the entire process tree.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct);
}
