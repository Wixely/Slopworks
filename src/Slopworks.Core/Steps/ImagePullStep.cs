using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.State;

namespace Slopworks.Core.Steps;

/// <summary>
/// Pulls the vLLM container image (GPU or CPU variant per the machine profile). The pulled
/// digest is journaled so ":latest" drift is visible and updates stay deliberate.
/// </summary>
public sealed class ImagePullStep(ILinuxCommandFactory linux) : ISetupStep
{
    public string Id => "image.pull";
    public string Title => "vLLM container image";
    public IReadOnlyList<string> DependsOn => ["distro.podman"];

    public bool AppliesTo(SystemProfile profile) => true;

    public static string SelectImage(StepContext ctx)
        => ctx.Profile.GpuPresent ? ctx.Config.Images.Gpu : ctx.Config.Images.Cpu;

    public async Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var image = SelectImage(ctx);
        var probe = await ctx.Probes.RunAsync(
            linux.Command($"podman image inspect --format '{{{{.Digest}}}}' {image} 2>/dev/null || echo IMAGE_MISSING"),
            null, ct);

        if (!probe.Succeeded)
            return StepDetection.Missing("Distro is not reachable yet.", probe.Stderr.Trim());

        var output = probe.Stdout.Trim();
        return output.Contains("IMAGE_MISSING") || output.Length == 0
            ? StepDetection.Missing($"Image {image} not pulled (roughly 10–20 GB download for the GPU image).")
            : StepDetection.Ok($"Image {image} present ({output}).", output);
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var image = SelectImage(ctx);
        var action = new PlannedAction(
            ActionId: "image.pull.pull",
            StepId: Id,
            Kind: ActionKind.Download,
            Description: $"Pull the vLLM container image ({(ctx.Profile.GpuPresent ? "GPU" : "CPU")} variant, large download)",
            Detail: $"podman pull {image}",
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                var pull = await exec.Processes.RunAsync(linux.Command($"podman pull {image}"), exec.Output, token);
                if (!pull.Succeeded)
                    return ActionResult.Failure($"podman pull failed (exit {pull.ExitCode}): {DistroBaseStep.Tail(pull)}");

                var digest = await exec.Processes.RunAsync(
                    linux.Command($"podman image inspect --format '{{{{.Digest}}}}' {image}"), null, token);
                ctx.Journal.Data.ResolvedArtifacts["container-image"] = new ResolvedArtifactEntry
                {
                    Url = image,
                    Sha256 = digest.Stdout.Trim(),
                    FileName = image,
                    ResolvedAt = DateTimeOffset.UtcNow,
                };
                await ctx.Journal.SaveAsync(token);

                return ActionResult.Success($"Pulled {image} ({digest.Stdout.Trim()}).");
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }
}
