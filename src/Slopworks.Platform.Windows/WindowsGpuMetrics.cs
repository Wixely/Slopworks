using System.ComponentModel;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Windows;

/// <summary>
/// Samples per-GPU usage via nvidia-smi. Deliberately bypasses the command audit log —
/// a once-a-second read-only metrics poll would drown it. When nvidia-smi is missing the
/// sampler backs off and only re-probes occasionally (a driver can appear mid-session).
/// </summary>
public sealed class WindowsGpuMetrics : IGpuMetrics
{
    private const int ReprobeInterval = 30;

    private readonly SystemProcessRunner _runner = new();
    private bool _unavailable;
    private int _skipped;

    public async Task<IReadOnlyList<GpuUsage>> SampleAsync(CancellationToken ct)
    {
        if (_unavailable && ++_skipped % ReprobeInterval != 0)
            return [];

        try
        {
            var result = await _runner.RunAsync(
                new ProcessSpec("nvidia-smi.exe",
                    ["--query-gpu=index,name,utilization.gpu,memory.used,memory.total",
                     "--format=csv,noheader,nounits"]),
                null, ct);

            if (!result.Succeeded)
            {
                _unavailable = true;
                return [];
            }

            _unavailable = false;
            return GpuUsageParser.ParseCsv(result.Stdout);
        }
        catch (Win32Exception)
        {
            _unavailable = true;
            return [];
        }
    }
}
