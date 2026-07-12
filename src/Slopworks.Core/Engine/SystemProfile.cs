namespace Slopworks.Core.Engine;

public sealed record GpuInfo(string Name, string DriverVersion, int MemoryMiB);

/// <summary>
/// Facts about the machine, filled in by the preflight step and consulted by
/// ISetupStep.AppliesTo and action planning. Never cached across runs.
/// </summary>
public sealed record SystemProfile
{
    public string OsDescription { get; init; } = "";
    public int OsBuild { get; init; }
    public GpuInfo? Gpu { get; init; }
    public long FreeDiskBytes { get; init; }
    public long TotalMemoryBytes { get; init; }

    /// <summary>
    /// True when an NVIDIA PCI device is present even if no driver is installed (nvidia-smi
    /// absent). Lets setup distinguish "no NVIDIA card" from "card without driver".
    /// </summary>
    public bool NvidiaHardwarePresent { get; init; }

    /// <summary>Null when not yet probed; false when virtualization is definitively unavailable.</summary>
    public bool? VirtualizationEnabled { get; init; }

    /// <summary>Set on Ubuntu hosts (from /etc/os-release); null elsewhere.</summary>
    public Version? UbuntuVersion { get; init; }

    public bool GpuPresent => Gpu is not null;

    public static SystemProfile Unknown { get; } = new();
}
