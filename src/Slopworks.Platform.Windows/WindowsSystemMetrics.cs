using System.Runtime.InteropServices;
using Slopworks.Core.Platform;

namespace Slopworks.Platform.Windows;

/// <summary>
/// CPU via GetSystemTimes deltas (kernel time includes idle, so busy = total - idle);
/// RAM via GlobalMemoryStatusEx. No WMI, no performance-counter registry.
/// </summary>
public sealed class WindowsSystemMetrics : ISystemMetrics
{
    private ulong _lastIdle;
    private ulong _lastTotal;
    private bool _hasBaseline;

    public SystemUsage Sample()
    {
        var cpu = SampleCpu();

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref memory))
            return new SystemUsage(cpu, 0, 0, 0);

        var used = (long)(memory.ullTotalPhys - memory.ullAvailPhys);
        return new SystemUsage(cpu, memory.dwMemoryLoad, used, (long)memory.ullTotalPhys);
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return 0;

        var idle = ToTicks(idleFt);
        var total = ToTicks(kernelFt) + ToTicks(userFt); // kernel already includes idle

        if (!_hasBaseline)
        {
            (_lastIdle, _lastTotal, _hasBaseline) = (idle, total, true);
            return 0;
        }

        var percent = CpuMath.Percent(idle - _lastIdle, total - _lastTotal);
        (_lastIdle, _lastTotal) = (idle, total);
        return percent;
    }

    private static ulong ToTicks(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
