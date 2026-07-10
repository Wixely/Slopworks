using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Steps;

/// <summary>
/// Ensures modern (Store/MSI) WSL is installed. The legacy inbox wsl.exe and the
/// not-installed case share the same corrective action: "wsl --install --no-distribution",
/// which needs elevation and typically a reboot on first enablement.
/// </summary>
public sealed class WslFeatureStep(IWslBackend wsl) : ISetupStep
{
    public string Id => "wsl.feature";
    public string Title => "WSL 2";
    public IReadOnlyList<string> DependsOn => ["preflight"];

    public bool AppliesTo(SystemProfile profile) => OperatingSystem.IsWindows();

    public async Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var status = await wsl.GetStatusAsync(ct);

        if (status.VirtualizationError)
        {
            return StepDetection.Broken(
                "WSL reports virtualization is unavailable. Enable VT-x/AMD-V in BIOS/UEFI, then retry.",
                status.RawOutput);
        }

        return status.Kind switch
        {
            WslInstallKind.Modern => StepDetection.Ok(
                $"Modern WSL installed (WSL {status.WslVersion ?? "?"}, kernel {status.KernelVersion ?? "?"}).",
                status.RawOutput),
            WslInstallKind.LegacyInbox => StepDetection.Partial(
                "Legacy inbox WSL detected — needs upgrade to Store/MSI WSL for --import and systemd support.",
                status.RawOutput),
            _ => StepDetection.Missing("WSL is not installed.", status.RawOutput),
        };
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var action = new PlannedAction(
            ActionId: "wsl.feature.install",
            StepId: Id,
            Kind: ActionKind.ExecuteElevated,
            Description: "Install WSL 2 (no default distribution)",
            Detail: "wsl.exe --install --no-distribution",
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                var result = await exec.Processes.RunAsync(
                    WslCommands.Management(["--install", "--no-distribution"]) with { RequiresElevation = true },
                    exec.Output, token);

                if (!result.Succeeded)
                {
                    return ActionResult.Failure(
                        $"wsl --install failed (exit {result.ExitCode}): {Truncate(result.Stderr + result.Stdout)}");
                }

                return ActionResult.NeedsReboot("WSL installed. Windows needs a restart to finish enabling it.");
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }

    private static string Truncate(string text) => text.Length <= 500 ? text.Trim() : text[..500].Trim() + "…";
}

/// <summary>Builders for wsl.exe invocations with the correct output encoding.</summary>
public static class WslCommands
{
    /// <summary>Management commands (--status, --install, --list ...) emit UTF-16LE on Windows.</summary>
    public static ProcessSpec Management(IReadOnlyList<string> args)
        => new("wsl.exe", args, StdoutEncoding: System.Text.Encoding.Unicode);

    /// <summary>Commands executed inside a distro emit UTF-8.</summary>
    public static ProcessSpec InDistro(string distro, IReadOnlyList<string> command, string user = "root")
        => new("wsl.exe", ["-d", distro, "-u", user, "--", .. command], StdoutEncoding: System.Text.Encoding.UTF8);
}
