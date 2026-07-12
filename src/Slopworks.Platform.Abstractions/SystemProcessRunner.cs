using System.Diagnostics;
using System.Text;

namespace Slopworks.Platform.Abstractions;

/// <summary>
/// Runs external processes with redirected, line-streamed output — pure BCL, shared by all
/// platforms. Callers set ProcessSpec.StdoutEncoding to UTF-16LE for wsl.exe management
/// commands and UTF-8 everywhere else. Cancellation kills the whole process tree.
/// Elevated execution is handled by each platform's elevation runner, not here.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct)
    {
        if (spec.RequiresElevation)
            throw new NotSupportedException(
                "Elevated processes must go through the platform elevation runner, not SystemProcessRunner.");

        var psi = new ProcessStartInfo
        {
            FileName = spec.Exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StdinText is not null,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDir ?? "",
            StandardOutputEncoding = spec.StdoutEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = spec.StdoutEncoding ?? Encoding.UTF8,
        };

        foreach (var arg in spec.Args)
            psi.ArgumentList.Add(arg);

        if (spec.Env is not null)
        {
            foreach (var (key, value) in spec.Env)
                psi.Environment[key] = value;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stdout)
                stdout.AppendLine(e.Data);
            liveOutput?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderr)
                stderr.AppendLine(e.Data);
            liveOutput?.Report(e.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (spec.StdinText is not null)
        {
            await process.StandardInput.WriteAsync(spec.StdinText.AsMemory(), ct);
            process.StandardInput.Close();
        }

        await using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        await process.WaitForExitAsync(ct);
        stopwatch.Stop();

        lock (stdout)
        {
            lock (stderr)
            {
                return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed);
            }
        }
    }
}
