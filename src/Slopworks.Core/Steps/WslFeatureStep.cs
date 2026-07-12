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
        // The legacy inbox wsl.exe predates --no-distribution; its supported upgrade path to
        // modern WSL is --update --web-download (which also skips the Microsoft Store).
        var isLegacyUpgrade = detection.State == StepState.Partial;

        IReadOnlyList<string> args = isLegacyUpgrade
            ? ["--update", "--web-download"]
            : ["--install", "--no-distribution"];

        var action = new PlannedAction(
            ActionId: "wsl.feature.install",
            StepId: Id,
            Kind: ActionKind.ExecuteElevated,
            Description: isLegacyUpgrade
                ? "Upgrade the legacy inbox WSL to modern WSL 2"
                : "Install WSL 2 (no default distribution)",
            Detail: $"wsl.exe {string.Join(' ', args)}",
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                var result = await exec.Processes.RunAsync(
                    WslCommands.Management(args) with { RequiresElevation = true },
                    exec.Output, token);

                if (!result.Succeeded)
                {
                    return ActionResult.Failure(
                        $"wsl {args[0]} failed (exit {result.ExitCode}): {TextUtil.Condense(result.Stderr + result.Stdout)}");
                }

                // A fresh feature install always needs a reboot. An in-place upgrade usually
                // doesn't — unless it also had to enable VirtualMachinePlatform, which wsl
                // announces ("Changes will not be effective until the system is rebooted").
                // Returning NeedsReboot short-circuits verification, so the immediate
                // "virtualization unavailable" reading no longer looks like a failure.
                var needsReboot = !isLegacyUpgrade || WslCommands.IndicatesRebootRequired(result.Stdout + result.Stderr);
                return needsReboot
                    ? ActionResult.NeedsReboot("WSL installed. Windows needs a restart to finish enabling it, then re-run setup.")
                    : ActionResult.Success("Modern WSL installed over the legacy inbox version.");
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }
}

/// <summary>Builders for wsl.exe invocations with correct, deterministic UTF-8 output.</summary>
public static class WslCommands
{
    // WSL_UTF8=1 makes modern wsl.exe emit UTF-8 (not UTF-16LE) and plain, pipe-friendly
    // progress text instead of the ANSI bar that garbles when redirected. Legacy pre-2022
    // inbox wsl ignores it, but classification there is driven by exit codes, not text.
    private static readonly IReadOnlyDictionary<string, string> Utf8Env =
        new Dictionary<string, string> { ["WSL_UTF8"] = "1" };

    /// <summary>Management commands (--status, --install, --list, --update ...).</summary>
    public static ProcessSpec Management(IReadOnlyList<string> args)
        => new("wsl.exe", args, StdoutEncoding: System.Text.Encoding.UTF8, Env: Utf8Env);

    /// <summary>Commands executed inside a distro.</summary>
    public static ProcessSpec InDistro(string distro, IReadOnlyList<string> command, string user = "root")
        => new("wsl.exe", ["-d", distro, "-u", user, "--", .. command],
            StdoutEncoding: System.Text.Encoding.UTF8, Env: Utf8Env);

    /// <summary>True when wsl's own output says a Windows restart is required to take effect.</summary>
    public static bool IndicatesRebootRequired(string output)
        => output.Contains("reboot", StringComparison.OrdinalIgnoreCase)
        || output.Contains("restart", StringComparison.OrdinalIgnoreCase);
}
