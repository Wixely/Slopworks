using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Steps;

/// <summary>
/// Ensures a usable Windows NVIDIA driver when NVIDIA hardware is present (the Windows
/// driver is what provides CUDA inside WSL — nothing is ever installed in the distro).
/// Tries winget for an unattended latest-driver install; when that isn't possible it opens
/// NVIDIA's driver page for a manual pick. Missing/old drivers are bypassable — the stack
/// runs CPU-only, and Slopworks doesn't get to insist.
/// </summary>
public sealed class NvidiaDriverStep : ISetupStep
{
    public const string BypassKeyName = "gpu.driver";
    public const string DriverDownloadPage = "https://www.nvidia.com/Download/index.aspx";

    /// <summary>Minimum Windows driver major version with WSL2 CUDA support.</summary>
    public const int MinDriverMajor = 470;

    /// <summary>Candidate winget package ids, probed at runtime — availability varies.</summary>
    public static readonly IReadOnlyList<string> WingetCandidates = ["Nvidia.GeForceDriver", "Nvidia.App"];

    public string Id => "gpu.driver";
    public string Title => "NVIDIA driver";
    public IReadOnlyList<string> DependsOn => ["preflight"];

    /// <summary>
    /// Always shown on Windows — hardware detection is a heuristic, and hiding the step
    /// would leave no way to correct it when it's wrong.
    /// </summary>
    public bool AppliesTo(SystemProfile profile) => OperatingSystem.IsWindows();

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var profile = ctx.Profile;
        var bypassed = ctx.Config.Bypasses.Contains(BypassKeyName);
        var forced = ctx.Config.Forces.Contains(BypassKeyName);

        if (!profile.NvidiaHardwarePresent && !forced)
        {
            // Our heuristic says no card — but let the user overrule it rather than trusting
            // ourselves blindly (disabled devices, odd enumeration, eGPUs...).
            return Task.FromResult(StepDetection.Ok(
                "No NVIDIA hardware detected (PCI scan) — running in CPU mode. " +
                "If you DO have an NVIDIA card, use 'Check anyway' to run driver setup regardless.",
                "pnputil found no display device with vendor id 10DE") with
            { ForceKey = BypassKeyName });
        }

        var forcedNote = !profile.NvidiaHardwarePresent && forced
            ? " (hardware check overridden by you — remove 'gpu.driver' from forces in config.json to reset)"
            : "";

        if (profile.Gpu is { } gpu)
        {
            var major = ParseDriverMajor(gpu.DriverVersion);
            if (major is null || major >= MinDriverMajor)
            {
                return Task.FromResult(StepDetection.Ok(
                    $"Driver {gpu.DriverVersion} installed for {gpu.Name} (WSL2 CUDA capable).",
                    $"nvidia-smi reports {gpu.Name}, driver {gpu.DriverVersion}, {gpu.MemoryMiB} MiB"));
            }

            if (bypassed)
            {
                return Task.FromResult(StepDetection.Ok(
                    $"Driver {gpu.DriverVersion} is older than {MinDriverMajor} (check bypassed) — GPU steps may fail; CPU mode is unaffected."));
            }

            return Task.FromResult(StepDetection.Broken(
                $"Driver {gpu.DriverVersion} predates WSL2 CUDA support (needs {MinDriverMajor}+). " +
                "Update it, or bypass to continue.",
                $"nvidia-smi reports {gpu.Name}, driver {gpu.DriverVersion}") with
            { BypassKey = BypassKeyName });
        }

        if (bypassed)
        {
            return Task.FromResult(StepDetection.Ok(
                "NVIDIA card present without a driver (check bypassed) — continuing CPU-only. " +
                "Install a driver and re-run setup to enable the GPU."));
        }

        return Task.FromResult(StepDetection.Broken(
            "An NVIDIA card is present but no driver is installed (nvidia-smi not found). " +
            $"Install the driver to use the GPU, or bypass to continue CPU-only.{forcedNote}",
            profile.NvidiaHardwarePresent
                ? "pnputil found a PCI display device with vendor id 10DE (NVIDIA)"
                : "no NVIDIA device found by pnputil; check forced by user") with
        { BypassKey = BypassKeyName });
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var action = new PlannedAction(
            ActionId: "gpu.driver.install",
            StepId: Id,
            Kind: ActionKind.ExecuteElevated,
            Description: "Install the latest NVIDIA driver (winget if available; otherwise opens NVIDIA's driver page)",
            Detail: $"winget install --exact --id {string.Join(" | ", WingetCandidates)} --silent  →  fallback: open {DriverDownloadPage}",
            InsideSlopworksRoot: false,
            Execute: ExecuteAsync);

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }

    private static async Task<ActionResult> ExecuteAsync(ActionExecutionContext exec, CancellationToken ct)
    {
        var winget = await TryRunAsync(exec,
            new ProcessSpec("winget.exe", ["--version"]), ct);

        if (winget is { Succeeded: true })
        {
            foreach (var packageId in NvidiaDriverStep.WingetCandidates)
            {
                ct.ThrowIfCancellationRequested();

                var found = await TryRunAsync(exec, new ProcessSpec("winget.exe",
                    ["search", "--exact", "--id", packageId, "--accept-source-agreements"]), ct);
                if (found is not { Succeeded: true } || !found.Stdout.Contains(packageId, StringComparison.OrdinalIgnoreCase))
                    continue;

                exec.Output.Report($"Installing {packageId} via winget (this downloads the full driver package)…");
                var install = await TryRunAsync(exec, new ProcessSpec("winget.exe",
                    ["install", "--exact", "--id", packageId, "--silent",
                     "--accept-package-agreements", "--accept-source-agreements"],
                    RequiresElevation: true), ct);

                if (install is { Succeeded: true })
                {
                    return ActionResult.Success(
                        $"{packageId} installed. Re-run setup so the GPU is re-detected " +
                        "(a reboot helps if nvidia-smi still isn't found).");
                }

                var why = install is null ? "winget unavailable" : install.Stderr + install.Stdout;
                exec.Output.Report($"winget install of {packageId} failed ({TextUtil.Condense(why, 200)}); trying next option…");
            }
        }
        else
        {
            exec.Output.Report("winget is not available on this machine.");
        }

        // No unattended path worked — hand over to NVIDIA's own picker.
        exec.Output.Report($"Opening {NvidiaDriverStep.DriverDownloadPage} — pick your card, install, then re-run setup.");
        await TryRunAsync(exec,
            new ProcessSpec("cmd.exe", ["/c", "start", "", NvidiaDriverStep.DriverDownloadPage]), ct);

        return ActionResult.Failure(
            "Automatic driver install was not possible; the NVIDIA driver download page was opened instead. " +
            "Install the driver manually and re-run setup — or press Bypass on this step to continue CPU-only.");
    }

    private static async Task<Slopworks.Platform.Abstractions.ProcessResult?> TryRunAsync(
        ActionExecutionContext exec, ProcessSpec spec, CancellationToken ct)
    {
        try
        {
            return await exec.Processes.RunAsync(spec, exec.Output, ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // executable not present
        }
    }

    private static int? ParseDriverMajor(string driverVersion)
    {
        var dot = driverVersion.IndexOf('.');
        return int.TryParse(dot > 0 ? driverVersion[..dot] : driverVersion, out var major) ? major : null;
    }
}
