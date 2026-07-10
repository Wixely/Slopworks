using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Slopworks.Core.Elevation;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Windows.Elevation;

/// <summary>
/// Runs elevation-requiring specs by relaunching the current executable as an elevated
/// worker (UAC prompt) and reading the result file back. If the app is already elevated,
/// commands run directly. Output arrives at completion, not live — the UAC boundary does
/// not allow streaming.
/// </summary>
public sealed class ElevatedProcessRunner(string elevatedDir) : IProcessRunner
{
    /// <summary>Win32 error when the user declines the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct)
    {
        if (IsElevated())
            return await new SystemProcessRunner().RunAsync(spec with { RequiresElevation = false }, liveOutput, ct);

        Directory.CreateDirectory(elevatedDir);
        var requestFile = Path.Combine(elevatedDir, $"req-{Guid.NewGuid():N}.json");
        ElevationProtocol.WriteRequest(requestFile, new ElevatedRequest
        {
            Exe = spec.Exe,
            Args = [.. spec.Args],
            WorkingDir = spec.WorkingDir,
            StdinText = spec.StdinText,
            Utf16Output = Equals(spec.StdoutEncoding, Encoding.Unicode),
        });

        try
        {
            liveOutput?.Report("Requesting administrator rights (UAC prompt)…");
            var stopwatch = Stopwatch.StartNew();

            Process worker;
            try
            {
                worker = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = $"--elevated-worker \"{requestFile}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                })!;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new ProcessResult(ErrorCancelled, "",
                    "Administrator rights were declined at the UAC prompt.", stopwatch.Elapsed);
            }

            using (worker)
            {
                await using var killOnCancel = ct.Register(() =>
                {
                    try
                    {
                        worker.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                        // Cannot kill an elevated process from a non-elevated one; it will finish on its own.
                    }
                });
                await worker.WaitForExitAsync(ct);
            }

            var response = ElevationProtocol.TryReadResponse(requestFile)
                ?? new ElevatedResponse { ExitCode = -1, Stderr = "Elevated worker produced no result file." };

            foreach (var line in response.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                liveOutput?.Report(line);

            return new ProcessResult(response.ExitCode, response.Stdout, response.Stderr, stopwatch.Elapsed);
        }
        finally
        {
            TryDelete(requestFile);
            TryDelete(ElevationProtocol.ResponsePath(requestFile));
        }
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Routes elevation-requiring specs to the UAC relaunch path, everything else directly.</summary>
public sealed class CompositeProcessRunner(SystemProcessRunner direct, ElevatedProcessRunner elevated) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct)
        => spec.RequiresElevation
            ? elevated.RunAsync(spec, liveOutput, ct)
            : direct.RunAsync(spec, liveOutput, ct);
}
