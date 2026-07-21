using Slopworks.Core.Engine;

namespace Slopworks.Core.Platform;

public enum WslInstallKind
{
    /// <summary>wsl.exe not present at all.</summary>
    NotInstalled,

    /// <summary>Legacy inbox wsl.exe (no --version support); needs upgrade to Store/MSI WSL.</summary>
    LegacyInbox,

    /// <summary>Store/MSI WSL with full management CLI.</summary>
    Modern,
}

public sealed record WslStatusInfo(
    WslInstallKind Kind,
    string? WslVersion,
    string? KernelVersion,
    int? DefaultVersion,
    bool VirtualizationError,
    string RawOutput);

/// <summary>
/// Read-only WSL probes used by step detection. Implemented per platform; the Windows
/// implementation shells out to wsl.exe with UTF-16LE output decoding.
/// </summary>
public interface IWslBackend
{
    Task<WslStatusInfo> GetStatusAsync(CancellationToken ct);

    /// <summary>Registered distro names (wsl --list --quiet); empty when WSL is absent/broken.</summary>
    Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct);
}

/// <summary>Builds the SystemProfile consulted by steps. Always probed live, never cached.</summary>
public interface ISystemInfoProvider
{
    Task<SystemProfile> GetProfileAsync(CancellationToken ct);

    /// <summary>
    /// All GPUs with index/name/PCI-bus-id for the Settings selectors. Empty when nvidia-smi
    /// is unavailable; degrades to name-only (no PCI) if the driver is too old to report it.
    /// </summary>
    Task<IReadOnlyList<GpuDevice>> EnumerateGpusAsync(CancellationToken ct);
}
