using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Platform;

/// <summary>
/// Optional LAN exposure of the vLLM port. Implementations must be strictly additive and
/// port-scoped — one reversible forward + one firewall allow rule for exactly that port,
/// never a change to global networking (nothing that could disturb other traffic).
/// </summary>
public interface INetworkExposure
{
    Task<bool> IsEnabledAsync(IProcessRunner processes, int port, CancellationToken ct);

    /// <summary>Idempotent; requires elevation (single UAC prompt).</summary>
    Task<ProcessResult> EnableAsync(IProcessRunner processes, int port, CancellationToken ct);

    /// <summary>Removes exactly what EnableAsync added; requires elevation.</summary>
    Task<ProcessResult> DisableAsync(IProcessRunner processes, int port, CancellationToken ct);

    /// <summary>Verbatim commands, for tooltips/audit.</summary>
    string DescribeEnable(int port);

    /// <summary>Best-effort LAN IPv4 addresses of this machine, for display.</summary>
    IReadOnlyList<string> GetLanAddresses();
}
