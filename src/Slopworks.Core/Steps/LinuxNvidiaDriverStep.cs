using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Steps;

/// <summary>
/// Ubuntu counterpart of the Windows driver step — same id and bypass/force keys, so the
/// Dashboard behaves identically. Safe mode offers a choice (ubuntu-drivers install / open
/// NVIDIA's page / manual); auto mode takes ubuntu-drivers automatically — the Setup page
/// warns about that. CDI (rootless podman GPU) wants driver 525+.
/// </summary>
public sealed class LinuxNvidiaDriverStep(ILinuxCommandFactory linux) : ISetupStep
{
    public const int MinDriverMajor = 525;

    public string Id => "gpu.driver";
    public string Title => "NVIDIA driver";
    public IReadOnlyList<string> DependsOn => ["preflight"];

    public bool AppliesTo(SystemProfile profile) => !OperatingSystem.IsWindows();

    private const string InstallScript = """
        #!/usr/bin/env bash
        set -euo pipefail
        export DEBIAN_FRONTEND=noninteractive
        if ! command -v ubuntu-drivers >/dev/null 2>&1; then
          apt-get update
          apt-get install -y ubuntu-drivers-common
        fi
        ubuntu-drivers install
        echo DRIVER_INSTALL_OK
        """;

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var profile = ctx.Profile;
        var bypassed = ctx.Config.Bypasses.Contains(NvidiaDriverStep.BypassKeyName);
        var forced = ctx.Config.Forces.Contains(NvidiaDriverStep.BypassKeyName);
        var autoNote = ctx.Config.IsAutoMode
            ? " Auto mode will install the recommended driver via ubuntu-drivers automatically."
            : "";

        if (!profile.NvidiaHardwarePresent && !forced)
        {
            return Task.FromResult(StepDetection.Ok(
                "No NVIDIA hardware detected (lspci) — running in CPU mode. " +
                "If you DO have an NVIDIA card, use 'Check anyway' to run driver setup regardless.",
                "lspci found no device with vendor id 10de") with
            { ForceKey = NvidiaDriverStep.BypassKeyName });
        }

        if (profile.Gpu is { } gpu)
        {
            var dot = gpu.DriverVersion.IndexOf('.');
            var major = int.TryParse(dot > 0 ? gpu.DriverVersion[..dot] : gpu.DriverVersion, out var m) ? m : (int?)null;
            if (major is null || major >= MinDriverMajor)
            {
                return Task.FromResult(StepDetection.Ok(
                    $"Driver {gpu.DriverVersion} installed for {gpu.Name} (CDI capable)."));
            }

            if (bypassed)
            {
                return Task.FromResult(StepDetection.Ok(
                    $"Driver {gpu.DriverVersion} is older than {MinDriverMajor} (check bypassed) — GPU steps may fail; CPU mode is unaffected."));
            }

            return Task.FromResult(StepDetection.Broken(
                $"Driver {gpu.DriverVersion} predates CDI support (needs {MinDriverMajor}+). Update it, or bypass to continue.{autoNote}") with
            { BypassKey = NvidiaDriverStep.BypassKeyName });
        }

        if (bypassed)
        {
            return Task.FromResult(StepDetection.Ok(
                "NVIDIA card present without a driver (check bypassed) — continuing CPU-only."));
        }

        return Task.FromResult(StepDetection.Broken(
            "An NVIDIA card is present but no driver is loaded (nvidia-smi not found). " +
            $"Install the driver to use the GPU, or bypass to continue CPU-only.{autoNote}",
            profile.NvidiaHardwarePresent ? "lspci found a device with vendor id 10de" : "check forced by user") with
        { BypassKey = NvidiaDriverStep.BypassKeyName });
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var action = new PlannedAction(
            ActionId: "gpu.driver.install",
            StepId: Id,
            Kind: ActionKind.ExecuteElevated,
            Description: "Install the NVIDIA driver — pick how",
            Detail: "default: ubuntu-drivers install (auto mode takes this); alternatives: NVIDIA page / Additional Drivers",
            InsideSlopworksRoot: false,
            Execute: InstallViaUbuntuDriversAsync)
        {
            Choices =
            [
                new ActionChoice(
                    "Install Ubuntu's recommended driver (ubuntu-drivers)",
                    InstallScript,
                    InstallViaUbuntuDriversAsync),
                new ActionChoice(
                    "Open NVIDIA's driver page (install manually)",
                    NvidiaDriverStep.DriverDownloadPage,
                    OpenDriverPageAsync),
                new ActionChoice(
                    "I'll use Software & Updates → Additional Drivers myself",
                    "No command runs; the step reports what to do next.",
                    (_, _) => Task.FromResult(ActionResult.Failure(
                        "Waiting on a manual driver install (Software & Updates → Additional Drivers). " +
                        "Re-run setup afterwards — or press Bypass on this step for CPU-only."))),
            ],
        };

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }

    private async Task<ActionResult> InstallViaUbuntuDriversAsync(ActionExecutionContext exec, CancellationToken ct)
    {
        var result = await exec.Processes.RunAsync(linux.Script(InstallScript, user: "root"), exec.Output, ct);
        if (!result.Stdout.Contains("DRIVER_INSTALL_OK"))
        {
            return ActionResult.Failure(
                $"ubuntu-drivers install failed (exit {result.ExitCode}): {TextUtil.Condense(result.Stderr + result.Stdout)} " +
                "— or run manually: sudo ubuntu-drivers install");
        }

        return ActionResult.NeedsReboot(
            "Driver installed. A reboot loads the kernel module — with Secure Boot you may be asked to " +
            "enroll a MOK key during boot (choose Enroll and use the password you set).");
    }

    private static async Task<ActionResult> OpenDriverPageAsync(ActionExecutionContext exec, CancellationToken ct)
    {
        exec.Output.Report($"Opening {NvidiaDriverStep.DriverDownloadPage}…");
        try
        {
            await exec.Processes.RunAsync(
                new ProcessSpec("xdg-open", [NvidiaDriverStep.DriverDownloadPage]), exec.Output, ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return ActionResult.Failure(
            "Waiting on a manual driver install — install from the NVIDIA page, then re-run setup " +
            "(or press Bypass on this step to continue CPU-only).");
    }
}
