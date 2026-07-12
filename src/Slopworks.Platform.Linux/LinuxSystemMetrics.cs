using System.ComponentModel;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Linux;

/// <summary>CPU from /proc/stat deltas (shared CpuMath), RAM from /proc/meminfo.</summary>
public sealed class LinuxSystemMetrics : ISystemMetrics
{
    private ulong _lastIdle;
    private ulong _lastTotal;
    private bool _hasBaseline;

    public SystemUsage Sample()
    {
        var cpu = SampleCpu();

        try
        {
            if (MemInfoParser.Parse(File.ReadAllText("/proc/meminfo")) is var (total, available) && total > 0)
            {
                var used = total - available;
                return new SystemUsage(cpu, 100.0 * used / total, used, total);
            }
        }
        catch (IOException)
        {
        }

        return new SystemUsage(cpu, 0, 0, 0);
    }

    private double SampleCpu()
    {
        (ulong Idle, ulong Total)? sample;
        try
        {
            sample = ProcStatParser.ParseCpuLine(File.ReadAllText("/proc/stat"));
        }
        catch (IOException)
        {
            return 0;
        }

        if (sample is not var (idle, total))
            return 0;

        if (!_hasBaseline)
        {
            (_lastIdle, _lastTotal, _hasBaseline) = (idle, total, true);
            return 0;
        }

        var percent = CpuMath.Percent(idle - _lastIdle, total - _lastTotal);
        (_lastIdle, _lastTotal) = (idle, total);
        return percent;
    }
}

/// <summary>Same nvidia-smi CSV sampling as Windows, minus the .exe suffix.</summary>
public sealed class LinuxGpuMetrics : IGpuMetrics
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
                new ProcessSpec("nvidia-smi",
                    ["--query-gpu=index,name,utilization.gpu,memory.used,memory.total",
                     "--format=csv,noheader,nounits"]),
                null, ct);

            _unavailable = !result.Succeeded;
            return result.Succeeded ? GpuUsageParser.ParseCsv(result.Stdout) : [];
        }
        catch (Win32Exception)
        {
            _unavailable = true;
            return [];
        }
    }
}
