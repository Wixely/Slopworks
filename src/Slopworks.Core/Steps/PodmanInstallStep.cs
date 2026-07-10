using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;

namespace Slopworks.Core.Steps;

/// <summary>Installs Podman inside the distro. Daemonless: nothing to enable or babysit.</summary>
public sealed class PodmanInstallStep(ILinuxCommandFactory linux) : ISetupStep
{
    public string Id => "distro.podman";
    public string Title => "Podman container runtime";
    public IReadOnlyList<string> DependsOn => ["distro.base"];

    public bool AppliesTo(SystemProfile profile) => true;

    public async Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var probe = await ctx.Probes.RunAsync(
            linux.Command("podman version --format '{{.Client.Version}}' 2>&1 || echo NO_PODMAN"), null, ct);

        if (!probe.Succeeded)
            return StepDetection.Missing("Distro is not reachable yet.", probe.Stderr.Trim());

        var output = probe.Stdout.Trim();
        return output.Contains("NO_PODMAN") || output.Length == 0
            ? StepDetection.Missing("Podman is not installed in the distro.")
            : StepDetection.Ok($"Podman {output} installed.", output);
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var script = ProvisionScripts.Render(ProvisionScripts.Podman, ctx.Config);
        var action = new PlannedAction(
            ActionId: "distro.podman.install",
            StepId: Id,
            Kind: ActionKind.Execute,
            Description: "Install Podman inside the distro (apt)",
            Detail: script,
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                var result = await exec.Processes.RunAsync(linux.Script(script), exec.Output, token);
                return result.Stdout.Contains("PROVISION_PODMAN_OK")
                    ? ActionResult.Success("Podman installed.")
                    : ActionResult.Failure($"Podman install failed (exit {result.ExitCode}): {DistroBaseStep.Tail(result)}");
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }
}
