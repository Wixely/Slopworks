using Slopworks.Core.Engine;
using Slopworks.Core.Platform;

namespace Slopworks.Core.Steps;

/// <summary>
/// The ordered set of setup steps for this platform. Registration order is preserved among
/// independent steps; DependsOn edges drive the rest.
/// </summary>
public static class StepCatalog
{
    public static IReadOnlyList<ISetupStep> CreateWindowsSteps(IWslBackend wsl) =>
    [
        new PreflightStep(),
        new WslFeatureStep(wsl),
        new WslKernelStep(wsl),
        // Phase 3+: rootfs.download, wsl.import, distro.base, distro.podman,
        // distro.nvidia, image.pull, gpu.smoke, vllm.smoke
    ];
}
