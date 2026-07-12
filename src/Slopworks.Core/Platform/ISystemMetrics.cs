namespace Slopworks.Core.Platform;

public sealed record SystemUsage(double CpuPercent, double RamPercent, long UsedRamBytes, long TotalRamBytes);

/// <summary>
/// Whole-machine usage sampling. CPU percentage is computed from the delta between
/// consecutive samples, so the first sample after startup reports 0.
/// </summary>
public interface ISystemMetrics
{
    SystemUsage Sample();
}

public static class CpuMath
{
    /// <summary>Busy share of a sampling interval, clamped to 0–100.</summary>
    public static double Percent(ulong idleDelta, ulong totalDelta)
    {
        // idle can exceed total across odd sampling boundaries; unsigned subtraction would wrap.
        if (totalDelta == 0 || idleDelta >= totalDelta)
            return 0;

        return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }
}
