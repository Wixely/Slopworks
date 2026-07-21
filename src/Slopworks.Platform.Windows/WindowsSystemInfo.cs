using System.ComponentModel;
using System.Runtime.InteropServices;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Windows;

/// <summary>
/// Builds the SystemProfile. GPU detection uses nvidia-smi.exe (installed into System32 by
/// the NVIDIA driver) — no WMI. Its absence or failure simply means CPU-only mode.
/// </summary>
public sealed class WindowsSystemInfo(SlopworksPaths paths, IProcessRunner probes) : ISystemInfoProvider
{
    public async Task<SystemProfile> GetProfileAsync(CancellationToken ct)
    {
        var gpu = await DetectGpuAsync(ct);
        return new SystemProfile
        {
            OsDescription = RuntimeInformation.OSDescription,
            OsBuild = Environment.OSVersion.Version.Build,
            Gpu = gpu,
            NvidiaHardwarePresent = gpu is not null || await DetectNvidiaHardwareAsync(ct),
            FreeDiskBytes = GetFreeDiskBytes(),
            TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        };
    }

    public async Task<IReadOnlyList<GpuDevice>> EnumerateGpusAsync(CancellationToken ct)
        => await NvidiaSmiInventory.EnumerateAsync(probes, "nvidia-smi.exe", ct);

    /// <summary>
    /// Driver-independent hardware check: pnputil enumerates PCI display devices with their
    /// vendor ids even when Windows only has a basic display driver for them.
    /// </summary>
    private async Task<bool> DetectNvidiaHardwareAsync(CancellationToken ct)
    {
        try
        {
            var result = await probes.RunAsync(
                new ProcessSpec("pnputil.exe", ["/enum-devices", "/class", "Display"]), null, ct);
            return result.Succeeded && NvidiaHardwareDetector.ContainsNvidiaDevice(result.Stdout);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private async Task<GpuInfo?> DetectGpuAsync(CancellationToken ct)
    {
        try
        {
            var result = await probes.RunAsync(
                new ProcessSpec("nvidia-smi.exe",
                    ["--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits"]),
                null, ct);

            return result.Succeeded ? NvidiaSmiParser.ParseQueryCsv(result.Stdout) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private long GetFreeDiskBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }
}

public static class NvidiaHardwareDetector
{
    /// <summary>PCI vendor id 10DE = NVIDIA; present in pnputil instance ids regardless of driver state.</summary>
    public static bool ContainsNvidiaDevice(string pnputilOutput)
        => pnputilOutput.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
}

public static class NvidiaSmiParser
{
    /// <summary>
    /// Parses "nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits".
    /// Multi-GPU machines report one line per GPU; the first is used.
    /// </summary>
    public static GpuInfo? ParseQueryCsv(string stdout)
    {
        var line = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null)
            return null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !int.TryParse(parts[2], out var memoryMiB))
            return null;

        return new GpuInfo(parts[0], parts[1], memoryMiB);
    }
}
