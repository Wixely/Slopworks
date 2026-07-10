using System.ComponentModel;
using Slopworks.Core.Platform;
using Slopworks.Core.Steps;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Windows.Wsl;

/// <summary>
/// Probes WSL state via wsl.exe. Distinguishes modern (Store/MSI) WSL, the legacy inbox
/// wsl.exe (which lacks --version), and no WSL at all.
/// </summary>
public sealed class WindowsWslBackend(IProcessRunner probes) : IWslBackend
{
    public async Task<WslStatusInfo> GetStatusAsync(CancellationToken ct)
    {
        ProcessResult version;
        try
        {
            version = await probes.RunAsync(WslCommands.Management(["--version"]), null, ct);
        }
        catch (Win32Exception)
        {
            return new WslStatusInfo(WslInstallKind.NotInstalled, null, null, null, false, "wsl.exe not found on PATH");
        }

        var status = await TryStatusAsync(ct);

        if (version.Succeeded)
        {
            var (wslVersion, kernelVersion) = WslOutputParser.ParseVersionOutput(version.Stdout);
            var raw = version.Stdout + Environment.NewLine + status;
            return new WslStatusInfo(
                WslInstallKind.Modern,
                wslVersion,
                kernelVersion,
                WslOutputParser.ParseDefaultVersion(status),
                WslOutputParser.IndicatesVirtualizationProblem(raw),
                raw);
        }

        // wsl.exe exists but has no --version: the legacy inbox build.
        var legacyRaw = version.Stdout + version.Stderr + Environment.NewLine + status;
        return new WslStatusInfo(
            WslInstallKind.LegacyInbox,
            null,
            null,
            WslOutputParser.ParseDefaultVersion(status),
            WslOutputParser.IndicatesVirtualizationProblem(legacyRaw),
            legacyRaw);
    }

    public async Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct)
    {
        try
        {
            var result = await probes.RunAsync(WslCommands.Management(["--list", "--quiet"]), null, ct);
            return result.Succeeded ? WslOutputParser.ParseDistroList(result.Stdout) : [];
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    private async Task<string> TryStatusAsync(CancellationToken ct)
    {
        try
        {
            var result = await probes.RunAsync(WslCommands.Management(["--status"]), null, ct);
            return result.Stdout + result.Stderr;
        }
        catch (Win32Exception)
        {
            return "";
        }
    }
}
