namespace Slopworks.Core.Platform;

public sealed record GpuUsage(
    int Index,
    string Name,
    double UtilizationPercent,
    long VramUsedMiB,
    long VramTotalMiB)
{
    public double VramPercent => VramTotalMiB > 0 ? Math.Clamp(100.0 * VramUsedMiB / VramTotalMiB, 0, 100) : 0;
}

/// <summary>
/// Per-GPU usage sampling (NVIDIA only; one entry per card on multi-GPU machines).
/// Empty when no NVIDIA driver/tooling is available.
/// </summary>
public interface IGpuMetrics
{
    Task<IReadOnlyList<GpuUsage>> SampleAsync(CancellationToken ct);
}

/// <summary>
/// Parses "nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total
/// --format=csv,noheader,nounits" — one line per GPU.
/// </summary>
public static class GpuUsageParser
{
    public static IReadOnlyList<GpuUsage> ParseCsv(string stdout)
    {
        var gpus = new List<GpuUsage>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 5
                || !int.TryParse(parts[0], out var index)
                || !double.TryParse(parts[2], out var utilization)
                || !long.TryParse(parts[3], out var used)
                || !long.TryParse(parts[4], out var total))
            {
                continue;
            }

            gpus.Add(new GpuUsage(index, parts[1], Math.Clamp(utilization, 0, 100), used, total));
        }

        return gpus;
    }
}
