using Microsoft.Extensions.Logging.Abstractions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Steps;
using Xunit;

namespace Slopworks.Core.Tests;

public class DistroSourceTests
{
    private sealed class NoDistrosWslBackend : IWslBackend
    {
        public Task<WslStatusInfo> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new WslStatusInfo(WslInstallKind.Modern, "2.7.10.0", "6.18.33.2", 2, false, ""));

        public Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static StepContext Context(SlopworksConfig config) => new()
    {
        Paths = new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests", Guid.NewGuid().ToString("N"))),
        Config = config,
        Profile = SystemProfile.Unknown,
        Logger = NullLogger.Instance,
        Journal = new InMemoryJournal(),
        Probes = new FakeProcessRunner(),
    };

    [Fact]
    public async Task WslOnlineMode_RootfsDownload_IsAlreadySatisfied()
    {
        var ctx = Context(new SlopworksConfig()); // default: wsl-online

        var detection = await new RootfsDownloadStep(null!, null!).DetectAsync(ctx, CancellationToken.None);

        Assert.Equal(StepState.Ok, detection.State);
        Assert.Contains("catalog", detection.Summary);
    }

    [Fact]
    public async Task TarballMode_RootfsDownload_StillDetectsNormally()
    {
        var config = new SlopworksConfig { Distro = new DistroConfig { Source = DistroConfig.SourceTarball } };

        var detection = await new RootfsDownloadStep(null!, null!).DetectAsync(Context(config), CancellationToken.None);

        Assert.Equal(StepState.Missing, detection.State);
    }

    [Fact]
    public async Task WslOnlineMode_Import_PlansNativeCatalogInstall()
    {
        var step = new DistroImportStep(new NoDistrosWslBackend());
        var ctx = Context(new SlopworksConfig());

        var detection = await step.DetectAsync(ctx, CancellationToken.None);
        var plan = await step.PlanAsync(ctx, detection, CancellationToken.None);

        var action = Assert.Single(plan);
        Assert.Contains("--install", action.Detail);
        Assert.Contains("--distribution Ubuntu-24.04", action.Detail);
        Assert.Contains($"--name {SlopworksPaths.DistroName}", action.Detail);
        Assert.Contains("--location", action.Detail);
        Assert.Contains("--web-download", action.Detail);
    }

    [Fact]
    public async Task TarballMode_Import_PlansClassicImport()
    {
        var step = new DistroImportStep(new NoDistrosWslBackend());
        var config = new SlopworksConfig { Distro = new DistroConfig { Source = DistroConfig.SourceTarball } };
        var ctx = Context(config);

        var detection = await step.DetectAsync(ctx, CancellationToken.None);
        var plan = await step.PlanAsync(ctx, detection, CancellationToken.None);

        var action = Assert.Single(plan);
        Assert.Contains("--import", action.Detail);
    }
}
