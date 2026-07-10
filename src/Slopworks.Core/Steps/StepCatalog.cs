using Slopworks.Core.Artifacts;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;

namespace Slopworks.Core.Steps;

/// <summary>
/// The ordered set of setup steps for this platform. Registration order is preserved among
/// independent steps; DependsOn edges drive the rest.
/// </summary>
public static class StepCatalog
{
    public static IReadOnlyList<ISetupStep> CreateWindowsSteps(
        IWslBackend wsl, IArtifactResolver resolver, Downloader downloader,
        ILinuxCommandFactory linux, VllmServerController server) =>
    [
        new PreflightStep(),
        new NvidiaDriverStep(),
        new WslFeatureStep(wsl),
        new WslKernelStep(wsl),
        new RootfsDownloadStep(resolver, downloader),
        new DistroImportStep(wsl),
        new DistroBaseStep(linux),
        new PodmanInstallStep(linux),
        new NvidiaToolkitStep(linux),
        new ImagePullStep(linux),
        new GpuSmokeTestStep(linux),
        new VllmSmokeTestStep(server),
    ];
}
