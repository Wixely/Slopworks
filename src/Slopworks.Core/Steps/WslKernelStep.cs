using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;

namespace Slopworks.Core.Steps;

/// <summary>
/// Ensures the WSL kernel is current and WSL 2 is the default version. Uses
/// --web-download so Store-blocked corporate machines still update.
/// </summary>
public sealed class WslKernelStep(IWslBackend wsl) : ISetupStep
{
    public string Id => "wsl.kernel";
    public string Title => "WSL kernel & defaults";
    public IReadOnlyList<string> DependsOn => ["wsl.feature"];

    public bool AppliesTo(SystemProfile profile) => OperatingSystem.IsWindows();

    public async Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var status = await wsl.GetStatusAsync(ct);

        if (status.Kind != WslInstallKind.Modern)
            return StepDetection.Missing("Modern WSL is not installed yet.", status.RawOutput);

        if (status.DefaultVersion is not 2)
        {
            return StepDetection.Partial(
                $"WSL default version is {status.DefaultVersion?.ToString() ?? "unknown"}; must be 2.",
                status.RawOutput);
        }

        return StepDetection.Ok(
            $"Kernel {status.KernelVersion ?? "?"} present, WSL 2 is the default.",
            status.RawOutput);
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var actions = new List<PlannedAction>
        {
            new(
                ActionId: "wsl.kernel.update",
                StepId: Id,
                Kind: ActionKind.Execute,
                Description: "Update the WSL kernel (bypassing the Microsoft Store)",
                Detail: "wsl.exe --update --web-download",
                InsideSlopworksRoot: false,
                Execute: async (exec, token) =>
                {
                    var result = await exec.Processes.RunAsync(
                        WslCommands.Management(["--update", "--web-download"]), exec.Output, token);
                    return result.Succeeded
                        ? ActionResult.Success()
                        : ActionResult.Failure($"wsl --update failed: {result.Stderr}{result.Stdout}".Trim());
                }),
            new(
                ActionId: "wsl.kernel.default2",
                StepId: Id,
                Kind: ActionKind.Execute,
                Description: "Make WSL 2 the default for new distros",
                Detail: "wsl.exe --set-default-version 2",
                InsideSlopworksRoot: false,
                Execute: async (exec, token) =>
                {
                    var result = await exec.Processes.RunAsync(
                        WslCommands.Management(["--set-default-version", "2"]), exec.Output, token);
                    return result.Succeeded
                        ? ActionResult.Success()
                        : ActionResult.Failure($"wsl --set-default-version failed: {result.Stderr}{result.Stdout}".Trim());
                }),
        };

        return Task.FromResult<IReadOnlyList<PlannedAction>>(actions);
    }
}
