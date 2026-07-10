using Slopworks.Core.Actions;
using Slopworks.Core.Engine;

namespace Slopworks.Core.Steps;

/// <summary>
/// Validates the machine can host the stack. Two kinds of checks, deliberately separated:
/// technical requirements (OS build, virtualization) are hard blockers, while resource
/// opinions (disk headroom, RAM, GPU) only warn or are bypassable — Slopworks doesn't know
/// what model you intend to run.
/// </summary>
public sealed class PreflightStep : ISetupStep
{
    public const string DiskBypassKey = "preflight.disk";

    public const long HardMinDiskBytes = 30L * 1024 * 1024 * 1024;
    public const long RecommendedDiskBytes = 60L * 1024 * 1024 * 1024;
    public const long RecommendedMemoryBytes = 16L * 1024 * 1024 * 1024;
    public const int MinWindowsBuild = 19041;

    public string Id => "preflight";
    public string Title => "System requirements";
    public IReadOnlyList<string> DependsOn => [];

    public bool AppliesTo(SystemProfile profile) => true;

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var profile = ctx.Profile;
        var freeGb = profile.FreeDiskBytes / (1024 * 1024 * 1024);
        var evidence = new List<string>
        {
            $"OS: {profile.OsDescription} (build {profile.OsBuild})",
            $"Free disk on root drive: {freeGb} GB",
            $"RAM: {profile.TotalMemoryBytes / (1024 * 1024 * 1024)} GB",
            profile.Gpu is { } gpu
                ? $"GPU: {gpu.Name}, driver {gpu.DriverVersion}, {gpu.MemoryMiB} MiB"
                : "GPU: none detected (nvidia-smi absent or failed) — CPU-only mode",
        };

        // Hard technical requirements — never bypassable, the stack cannot work without them.
        if (OperatingSystem.IsWindows() && profile.OsBuild < MinWindowsBuild)
        {
            return Task.FromResult(StepDetection.Broken(
                $"Windows build {profile.OsBuild} is too old; WSL2 needs {MinWindowsBuild} or later.",
                [.. evidence]));
        }

        if (profile.VirtualizationEnabled == false)
        {
            return Task.FromResult(StepDetection.Broken(
                "Hardware virtualization is disabled. Enable it in BIOS/UEFI (often called VT-x, AMD-V or SVM).",
                [.. evidence]));
        }

        // Opinionated resource checks — Slopworks doesn't know what model you'll run.
        var warnings = new List<string>();

        if (profile.FreeDiskBytes < HardMinDiskBytes)
        {
            if (!ctx.Config.Bypasses.Contains(DiskBypassKey))
            {
                return Task.FromResult(StepDetection.Broken(
                    $"Only {freeGb} GB free — under the suggested 30 GB minimum for distro + image + a small model. " +
                    "If you know your setup fits, bypass this check.",
                    [.. evidence]) with
                { BypassKey = DiskBypassKey });
            }

            warnings.Add($"only {freeGb} GB free (check bypassed)");
        }
        else if (profile.FreeDiskBytes < RecommendedDiskBytes)
        {
            warnings.Add($"{freeGb} GB free — larger models will not fit");
        }

        if (profile.TotalMemoryBytes > 0 && profile.TotalMemoryBytes < RecommendedMemoryBytes)
            warnings.Add($"{profile.TotalMemoryBytes / (1024 * 1024 * 1024)} GB RAM — WSL + vLLM may be tight, prefer small models");
        if (!profile.GpuPresent)
            warnings.Add("no NVIDIA GPU — inference will run on CPU (slow, demo-grade)");

        var summary = warnings.Count == 0
            ? "Machine meets requirements."
            : $"Machine is usable with caveats: {string.Join("; ", warnings)}.";

        return Task.FromResult(StepDetection.Ok(summary, [.. evidence]));
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PlannedAction>>([]);
}
