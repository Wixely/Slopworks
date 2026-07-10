using Slopworks.Core.Artifacts;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;

namespace Slopworks.Core.Steps;

/// <summary>
/// The ordered set of setup steps for this platform. Registration order is preserved among
/// independent steps; DependsOn edges drive the rest.
/// </summary>
public static class StepCatalog
{
    public static IReadOnlyList<ISetupStep> CreateWindowsSteps(
        IWslBackend wsl, IArtifactResolver resolver, Downloader downloader) =>
    [
        new PreflightStep(),
        new WslFeatureStep(wsl),
        new WslKernelStep(wsl),
        new RootfsDownloadStep(resolver, downloader),
        new DistroImportStep(wsl),
        // Phase 5+: distro.base, distro.podman, distro.nvidia,
        // image.pull, gpu.smoke, vllm.smoke
    ];
}
