using System.Text.Json;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Logging;

public sealed record CommandLogEntry(
    DateTimeOffset Timestamp,
    string Attribution,
    string Decision,
    string Exe,
    IReadOnlyList<string> Args,
    int ExitCode,
    double DurationMs,
    string OutputHead,
    string OutputTail);

/// <summary>Audit trail: one JSON line per external command, with the action/probe that authorized it.</summary>
public interface ICommandLog
{
    void Append(CommandLogEntry entry);
}

public sealed class FileCommandLog(string logsDir) : ICommandLog
{
    private readonly Lock _writeLock = new();

    public void Append(CommandLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, CommandLogJsonContext.Default.CommandLogEntry);
        lock (_writeLock)
        {
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(Path.Combine(logsDir, $"commands-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
    }
}

public sealed class NullCommandLog : ICommandLog
{
    public static NullCommandLog Instance { get; } = new();

    public void Append(CommandLogEntry entry)
    {
    }
}

/// <summary>
/// IProcessRunner decorator that records every invocation to the command log, attributed to
/// the action or probe that authorized it. The engine wraps the real runner with this before
/// handing it to actions and steps.
/// </summary>
public sealed class RecordingProcessRunner(IProcessRunner inner, ICommandLog log, string attribution, string decision) : IProcessRunner
{
    private const int OutputCaptureChars = 4096;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct)
    {
        var result = await inner.RunAsync(spec, liveOutput, ct);

        var combined = result.Stdout.Length > 0 ? result.Stdout : result.Stderr;
        log.Append(new CommandLogEntry(
            DateTimeOffset.UtcNow,
            attribution,
            decision,
            spec.Exe,
            spec.Args,
            result.ExitCode,
            result.Duration.TotalMilliseconds,
            OutputHead: combined[..Math.Min(combined.Length, OutputCaptureChars)],
            OutputTail: combined.Length > OutputCaptureChars * 2
                ? combined[^OutputCaptureChars..]
                : ""));

        return result;
    }
}
