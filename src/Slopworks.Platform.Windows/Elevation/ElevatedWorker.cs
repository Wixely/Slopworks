using System.Text;
using Slopworks.Core.Elevation;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Windows.Elevation;

/// <summary>
/// Entry point for the "--elevated-worker &lt;requestFile&gt;" mode: executes exactly one
/// command described by the request file and writes the result beside it. Runs before any
/// UI framework is initialized.
/// </summary>
public static class ElevatedWorker
{
    public static int Run(string requestFile)
    {
        try
        {
            var request = ElevationProtocol.ReadRequest(requestFile);
            var spec = new ProcessSpec(
                request.Exe,
                request.Args,
                WorkingDir: request.WorkingDir,
                StdoutEncoding: request.Utf16Output ? Encoding.Unicode : Encoding.UTF8,
                Env: request.Env,
                StdinText: request.StdinText);

            var result = new SystemProcessRunner()
                .RunAsync(spec, null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            ElevationProtocol.WriteResponse(requestFile, new ElevatedResponse
            {
                ExitCode = result.ExitCode,
                Stdout = result.Stdout,
                Stderr = result.Stderr,
                DurationMs = result.Duration.TotalMilliseconds,
            });
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                ElevationProtocol.WriteResponse(requestFile, new ElevatedResponse
                {
                    ExitCode = -1,
                    Stderr = $"Elevated worker failed: {ex}",
                });
            }
            catch (IOException)
            {
            }

            return 1;
        }
    }
}
