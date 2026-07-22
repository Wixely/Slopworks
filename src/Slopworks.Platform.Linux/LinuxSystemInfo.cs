using System.ComponentModel;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Linux;

public sealed class LinuxSystemInfo(SlopworksPaths paths, IProcessRunner probes) : ISystemInfoProvider
{
    public async Task<SystemProfile> GetProfileAsync(CancellationToken ct)
    {
        var gpu = await DetectGpuAsync(ct);
        var osRelease = ReadOsRelease();
        return new SystemProfile
        {
            OsDescription = OsReleaseParser.GetPrettyName(osRelease),
            OsBuild = 0, // not meaningful off-Windows; preflight skips the build check
            UbuntuVersion = OsReleaseParser.GetUbuntuVersion(osRelease),
            Gpu = gpu,
            NvidiaHardwarePresent = gpu is not null || await DetectNvidiaHardwareAsync(ct),
            HasNvLink = gpu is not null && await NvidiaSmiInventory.HasNvLinkAsync(probes, "nvidia-smi", ct),
            FreeDiskBytes = GetFreeDiskBytes(),
            TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        };
    }

    private static string ReadOsRelease()
    {
        try
        {
            return File.ReadAllText("/etc/os-release");
        }
        catch (IOException)
        {
            return "";
        }
    }

    public async Task<IReadOnlyList<GpuDevice>> EnumerateGpusAsync(CancellationToken ct)
        => await NvidiaSmiInventory.EnumerateAsync(probes, "nvidia-smi", ct);

    private async Task<GpuInfo?> DetectGpuAsync(CancellationToken ct)
    {
        try
        {
            var result = await probes.RunAsync(
                new ProcessSpec("nvidia-smi",
                    ["--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits"]),
                null, ct);

            return result.Succeeded ? LinuxNvidiaSmi.ParseQueryCsv(result.Stdout) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private async Task<bool> DetectNvidiaHardwareAsync(CancellationToken ct)
    {
        try
        {
            var result = await probes.RunAsync(new ProcessSpec("lspci", ["-nn"]), null, ct);
            return result.Succeeded && LspciParser.ContainsNvidiaDevice(result.Stdout);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private long GetFreeDiskBytes()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(paths.Root) ?? "/").AvailableFreeSpace;
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

/// <summary>Same CSV shape as the Windows parser; duplicated locally to avoid a Windows project reference.</summary>
public static class LinuxNvidiaSmi
{
    public static GpuInfo? ParseQueryCsv(string stdout)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null)
            return null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !int.TryParse(parts[2], out var memoryMiB))
            return null;

        return new GpuInfo(parts[0], parts[1], memoryMiB);
    }
}
