using Slopworks.Core.Actions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;

namespace Slopworks.Core.Steps;

/// <summary>
/// Base provisioning of the imported distro: systemd via wsl.conf, the 'slop' user, and
/// core packages. Requires a distro terminate-bounce for wsl.conf to take effect, then
/// detection demands a live systemd (running or degraded).
/// </summary>
public sealed class DistroBaseStep(ILinuxCommandFactory linux) : ISetupStep
{
    public string Id => "distro.base";
    public string Title => "Distro base setup";
    public IReadOnlyList<string> DependsOn => ["wsl.import"];

    // Windows-only: a Linux host already has systemd, and Slopworks never re-provisions
    // (or restarts!) the user's own machine.
    public bool AppliesTo(SystemProfile profile) => OperatingSystem.IsWindows();

    public async Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var probe = await ctx.Probes.RunAsync(
            linux.Command("cat /etc/slopworks/provisioned-base 2>/dev/null; echo ---; systemctl is-system-running 2>&1 || true"),
            null, ct);

        if (!probe.Succeeded)
            return StepDetection.Missing("Distro is not reachable yet.", probe.Stderr.Trim());

        var parts = probe.Stdout.Split("---", 2);
        var marker = parts[0].Trim();
        var systemd = parts.Length > 1 ? parts[1].Trim() : "";
        var systemdLive = systemd.Contains("running") || systemd.Contains("degraded");

        return (marker, systemdLive) switch
        {
            (ProvisionScripts.BaseMarker, true) => StepDetection.Ok(
                $"Base provisioning complete, systemd is {systemd}.", probe.Stdout.Trim()),
            (ProvisionScripts.BaseMarker, false) => StepDetection.Partial(
                $"Provisioned but systemd is not live ({systemd}) — the distro needs a restart.", probe.Stdout.Trim()),
            ("", _) => StepDetection.Missing("Distro has not been provisioned.", probe.Stdout.Trim()),
            _ => StepDetection.Partial($"Provisioned with older version '{marker}'; re-run to upgrade.", probe.Stdout.Trim()),
        };
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var actions = new List<PlannedAction>();
        var script = ProvisionScripts.Render(ProvisionScripts.Base, ctx.Config);

        // Only the systemd bounce is needed when the marker is already current.
        var needsScript = !detection.Summary.Contains("systemd is not live");
        if (needsScript)
        {
            actions.Add(new PlannedAction(
                ActionId: "distro.base.provision",
                StepId: Id,
                Kind: ActionKind.Execute,
                Description: "Provision the distro: enable systemd, create the 'slop' user, install base packages",
                Detail: script,
                InsideSlopworksRoot: false,
                Execute: async (exec, token) =>
                {
                    var result = await exec.Processes.RunAsync(linux.Script(script), exec.Output, token);
                    return result.Stdout.Contains("PROVISION_BASE_OK")
                        ? ActionResult.Success("Base provisioning script completed.")
                        : ActionResult.Failure($"Provisioning failed (exit {result.ExitCode}): {Tail(result)}");
                }));
        }

        actions.Add(new PlannedAction(
            ActionId: "distro.base.restart",
            StepId: Id,
            Kind: ActionKind.Execute,
            Description: "Restart the distro so systemd and wsl.conf take effect",
            Detail: $"wsl.exe --terminate {SlopworksPaths.DistroName}",
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                await exec.Processes.RunAsync(linux.Terminate(), exec.Output, token);

                // Re-enter the distro and wait for systemd to settle.
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    var probe = await exec.Processes.RunAsync(
                        linux.Command("systemctl is-system-running 2>&1 || true"), null, token);
                    var state = probe.Stdout.Trim();
                    if (state.Contains("running") || state.Contains("degraded"))
                        return ActionResult.Success($"systemd is {state}.");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }

                return ActionResult.Failure("systemd did not come up within 60 s of restarting the distro.");
            }));

        return Task.FromResult<IReadOnlyList<PlannedAction>>(actions);
    }

    internal static string Tail(Slopworks.Platform.Abstractions.ProcessResult result)
    {
        var text = (result.Stdout + "\n" + result.Stderr).Trim();
        return text.Length <= 800 ? text : "…" + text[^800..];
    }
}
