using System.Net.NetworkInformation;
using System.Net.Sockets;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Windows;

/// <summary>
/// LAN exposure via netsh: a v4tov4 portproxy from 0.0.0.0:port to 127.0.0.1:port (where the
/// WSL localhost relay already listens) plus a single inbound firewall rule for that port.
/// Both are additive, idempotent (delete-then-add) and removed precisely on disable — global
/// network/WSL settings are never touched.
/// </summary>
public sealed class WindowsNetworkExposure : INetworkExposure
{
    private static string RuleName(int port) => $"Slopworks vLLM {port}";

    public async Task<bool> IsEnabledAsync(IProcessRunner processes, int port, CancellationToken ct)
    {
        var result = await processes.RunAsync(
            new ProcessSpec("netsh.exe", ["interface", "portproxy", "show", "v4tov4"]), null, ct);
        return result.Succeeded && HasProxyForPort(result.Stdout, port);
    }

    public Task<ProcessResult> EnableAsync(IProcessRunner processes, int port, CancellationToken ct)
        => RunElevatedChainAsync(processes, EnableCommands(port), ct);

    public Task<ProcessResult> DisableAsync(IProcessRunner processes, int port, CancellationToken ct)
        => RunElevatedChainAsync(processes, DisableCommands(port), ct);

    public string DescribeEnable(int port) => string.Join(Environment.NewLine, EnableCommands(port));

    private static string[] EnableCommands(int port) =>
    [
        // Delete-then-add keeps this idempotent; deletes are allowed to fail silently.
        $"netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport={port} >nul 2>&1",
        $"netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport={port} connectaddress=127.0.0.1 connectport={port}",
        $"netsh advfirewall firewall delete rule name=\"{RuleName(port)}\" >nul 2>&1",
        $"netsh advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=TCP localport={port}",
    ];

    private static string[] DisableCommands(int port) =>
    [
        $"netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport={port}",
        $"netsh advfirewall firewall delete rule name=\"{RuleName(port)}\"",
    ];

    private static async Task<ProcessResult> RunElevatedChainAsync(
        IProcessRunner processes, string[] commands, CancellationToken ct)
    {
        // One cmd invocation = one UAC prompt. '&' chains regardless of individual failures;
        // the exit code reflects the final (meaningful) command.
        var chain = string.Join(" & ", commands);
        return await processes.RunAsync(
            new ProcessSpec("cmd.exe", ["/c", chain], RequiresElevation: true), null, ct);
    }

    /// <summary>Pure parser over "netsh interface portproxy show v4tov4" output (testable).</summary>
    public static bool HasProxyForPort(string netshOutput, int port)
    {
        foreach (var line in netshOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 4
                && columns[0] == "0.0.0.0"
                && columns[1] == port.ToString()
                && columns[2] is "127.0.0.1" or "localhost")
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<string> GetLanAddresses()
    {
        var addresses = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.Name.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                || nic.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
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
