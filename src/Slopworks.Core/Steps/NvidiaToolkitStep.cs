using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;

namespace Slopworks.Core.Steps;

/// <summary>
/// GPU-only: installs the NVIDIA container toolkit in the distro and generates the CDI spec
/// Podman uses for --device nvidia.com/gpu=all. The CDI spec is keyed to the Windows driver
/// version and regenerated after driver updates. The Windows driver itself is never touched.
/// </summary>
public sealed class NvidiaToolkitStep(ILinuxCommandFactory linux, string nvidiaSmiPath = NvidiaToolkitStep.WslNvidiaSmi) : ISetupStep
{
    /// <summary>Where the Windows driver surfaces nvidia-smi inside a WSL distro.</summary>
    public const string WslNvidiaSmi = "/usr/lib/wsl/lib/nvidia-smi";

    /// <summary>On a Linux host the driver's own binary is on PATH.</summary>
    public const string HostNvidiaSmi = "nvidia-smi";

    public string Id => "distro.nvidia";
    public string Title => "NVIDIA container toolkit";
    public IReadOnlyList<string> DependsOn => ["distro.podman"];

    public bool AppliesTo(SystemProfile profile) => profile.GpuPresent;

    public async Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var probe = await ctx.Probes.RunAsync(linux.Command(
            $"{nvidiaSmiPath} -L 2>&1 | head -2; echo ---;" +
            " command -v nvidia-ctk >/dev/null && echo CTK_OK || echo CTK_MISSING; echo ---;" +
            " test -f /etc/cdi/nvidia.yaml && echo CDI_OK || echo CDI_MISSING; echo ---;" +
            " cat /etc/slopworks/provisioned-nvidia 2>/dev/null"), null, ct);

        if (!probe.Succeeded)
            return StepDetection.Missing("Distro is not reachable yet.", probe.Stderr.Trim());

        var parts = probe.Stdout.Split("---");
        var smi = parts.ElementAtOrDefault(0)?.Trim() ?? "";
        var ctk = parts.ElementAtOrDefault(1)?.Trim() ?? "";
        var cdi = parts.ElementAtOrDefault(2)?.Trim() ?? "";
        var marker = parts.ElementAtOrDefault(3)?.Trim() ?? "";

        if (!smi.Contains("GPU", StringComparison.OrdinalIgnoreCase))
        {
            return StepDetection.Broken(
                OperatingSystem.IsWindows()
                    ? "GPU passthrough is not working: /usr/lib/wsl/lib/nvidia-smi sees no GPU inside the distro. " +
                      "Update the Windows NVIDIA driver (it provides the WSL GPU stack)."
                    : "nvidia-smi sees no GPU — the NVIDIA driver is not working; repair the driver step first.",
                probe.Stdout.Trim());
        }

        if (ctk != "CTK_OK" || cdi != "CDI_OK")
            return StepDetection.Missing("NVIDIA container toolkit / CDI spec not set up.", probe.Stdout.Trim());

        var expectedMarker = $"{ProvisionScripts.NvidiaMarkerPrefix} driver={ctx.Profile.Gpu!.DriverVersion}";
        if (marker != expectedMarker)
        {
            return StepDetection.Partial(
                $"CDI spec was generated for a different driver ({marker}); regenerate for {ctx.Profile.Gpu.DriverVersion}.",
                probe.Stdout.Trim());
        }

        return StepDetection.Ok($"Toolkit + CDI ready for driver {ctx.Profile.Gpu.DriverVersion}.", smi);
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var script = ProvisionScripts.Render(ProvisionScripts.Nvidia, ctx.Config,
            new Dictionary<string, string> { ["DRIVER_VERSION"] = ctx.Profile.Gpu?.DriverVersion ?? "unknown" });

        var action = new PlannedAction(
            ActionId: "distro.nvidia.install",
            StepId: Id,
            Kind: ActionKind.Execute,
            Description: "Install NVIDIA container toolkit and generate the CDI GPU spec inside the distro",
            Detail: script,
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                var result = await exec.Processes.RunAsync(linux.Script(script), exec.Output, token);
                return result.Stdout.Contains("PROVISION_NVIDIA_OK")
                    ? ActionResult.Success("NVIDIA toolkit + CDI spec ready.")
                    : ActionResult.Failure($"NVIDIA toolkit setup failed (exit {result.ExitCode}): {DistroBaseStep.Tail(result)}");
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }
}
