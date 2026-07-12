using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Linux;

/// <summary>
/// LAN exposure on a Linux host. The real gate is the container's publish address
/// (127.0.0.1 vs 0.0.0.0, applied by the server controller on next start); this class adds
/// the only firewall piece needed — a ufw allow rule for the single port, and only when ufw
/// is actually active. Strictly additive, precisely reversible, never touches routing.
/// </summary>
public sealed class LinuxNetworkExposure : INetworkExposure
{
    public async Task<bool> IsEnabledAsync(IProcessRunner processes, int port, CancellationToken ct)
    {
        // Real state first: is anything listening on all interfaces for this port?
        try
        {
            var listeners = await processes.RunAsync(new ProcessSpec("ss", ["-ltn"]), null, ct);
            if (listeners.Succeeded && (listeners.Stdout.Contains($"0.0.0.0:{port}") || listeners.Stdout.Contains($"[::]:{port}")))
                return true;

            var ufw = await processes.RunAsync(new ProcessSpec("ufw", ["status"]), null, ct);
            return ufw.Succeeded && UfwParser.IsActive(ufw.Stdout) && ufw.Stdout.Contains($"{port}/tcp");
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public async Task<ProcessResult> EnableAsync(IProcessRunner processes, int port, CancellationToken ct)
    {
        if (!await IsUfwActiveAsync(processes, ct))
            return new ProcessResult(0, "ufw inactive; no firewall rule needed.", "", TimeSpan.Zero);

        return await processes.RunAsync(
            new ProcessSpec("ufw", ["allow", $"{port}/tcp", "comment", "Slopworks vLLM"], RequiresElevation: true),
            null, ct);
    }

    public async Task<ProcessResult> DisableAsync(IProcessRunner processes, int port, CancellationToken ct)
    {
        if (!await IsUfwActiveAsync(processes, ct))
            return new ProcessResult(0, "ufw inactive; nothing to remove.", "", TimeSpan.Zero);

        return await processes.RunAsync(
            new ProcessSpec("ufw", ["delete", "allow", $"{port}/tcp"], RequiresElevation: true),
            null, ct);
    }

    public string DescribeEnable(int port) =>
        $"container publishes on 0.0.0.0:{port} (applied on next server start)\n" +
        $"ufw allow {port}/tcp   # only when ufw is active";

    private static async Task<bool> IsUfwActiveAsync(IProcessRunner processes, CancellationToken ct)
    {
        try
        {
            var result = await processes.RunAsync(new ProcessSpec("ufw", ["status"], RequiresElevation: true), null, ct);
            return result.Succeeded && UfwParser.IsActive(result.Stdout);
        }
        catch (Win32Exception)
        {
            return false; // no ufw installed
        }
    }

    public IReadOnlyList<string> GetLanAddresses()
    {
        var addresses = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.Name.StartsWith("docker", StringComparison.OrdinalIgnoreCase)
                || nic.Name.StartsWith("podman", StringComparison.OrdinalIgnoreCase)
                || nic.Name.StartsWith("veth", StringComparison.OrdinalIgnoreCase)
                || nic.Name.StartsWith("br-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    addresses.Add(addr.Address.ToString());
            }
        }

        return addresses;
    }
}
