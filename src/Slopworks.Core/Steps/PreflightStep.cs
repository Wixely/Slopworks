using Slopworks.Core.Actions;
using Slopworks.Core.Engine;

namespace Slopworks.Core.Steps;

/// <summary>
/// Validates the machine can host the stack at all. Purely informational: soft concerns
/// (low disk headroom, no GPU) surface as evidence on an Ok result; hard blockers
/// (ancient OS, no disk) are Broken and halt the run with guidance.
/// </summary>
public sealed class PreflightStep : ISetupStep
{
    public const long HardMinDiskBytes = 30L * 1024 * 1024 * 1024;
    public const long RecommendedDiskBytes = 60L * 1024 * 1024 * 1024;
    public const int MinWindowsBuild = 19041;

    public string Id => "preflight";
    public string Title => "System requirements";
    public IReadOnlyList<string> DependsOn => [];

    public bool AppliesTo(SystemProfile profile) => true;

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var profile = ctx.Profile;
        var evidence = new List<string>
        {
            $"OS: {profile.OsDescription} (build {profile.OsBuild})",
            $"Free disk on root drive: {profile.FreeDiskBytes / (1024 * 1024 * 1024)} GB",
            profile.Gpu is { } gpu
                ? $"GPU: {gpu.Name}, driver {gpu.DriverVersion}, {gpu.MemoryMiB} MiB"
                : "GPU: none detected (nvidia-smi absent or failed) — CPU-only mode",
        };

        if (OperatingSystem.IsWindows() && profile.OsBuild < MinWindowsBuild)
        {
            return Task.FromResult(StepDetection.Broken(
                $"Windows build {profile.OsBuild} is too old; WSL2 needs {MinWindowsBuild} or later.",
                [.. evidence]));
        }

        if (profile.FreeDiskBytes < HardMinDiskBytes)
        {
            return Task.FromResult(StepDetection.Broken(
                $"Only {profile.FreeDiskBytes / (1024 * 1024 * 1024)} GB free; at least 30 GB is required " +
                "(distro + container image + a small model).",
                [.. evidence]));
        }

        if (profile.VirtualizationEnabled == false)
        {
            return Task.FromResult(StepDetection.Broken(
                "Hardware virtualization is disabled. Enable it in BIOS/UEFI (often called VT-x, AMD-V or SVM).",
                [.. evidence]));
        }

        var warnings = new List<string>();
        if (profile.FreeDiskBytes < RecommendedDiskBytes)
            warnings.Add("less than 60 GB free — larger models will not fit");
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
