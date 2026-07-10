using Microsoft.Extensions.Logging.Abstractions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Steps;
using Xunit;

namespace Slopworks.Core.Tests;

public class WslFeatureStepTests
{
    private sealed class FakeWslBackend(WslStatusInfo status) : IWslBackend
    {
        public Task<WslStatusInfo> GetStatusAsync(CancellationToken ct) => Task.FromResult(status);
        public Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static StepContext Context() => new()
    {
        Paths = new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests")),
        Config = new SlopworksConfig(),
        Profile = SystemProfile.Unknown,
        Logger = NullLogger.Instance,
        Journal = new InMemoryJournal(),
        Probes = new FakeProcessRunner(),
    };

    [Fact]
    public async Task LegacyInboxWsl_PlansWebDownloadUpgrade_NotInstall()
    {
        // The inbox wsl.exe rejects --no-distribution; the upgrade path is --update --web-download.
        var step = new WslFeatureStep(new FakeWslBackend(
            new WslStatusInfo(WslInstallKind.LegacyInbox, null, null, 2, false, "")));
        var ctx = Context();

        var detection = await step.DetectAsync(ctx, CancellationToken.None);
        var plan = await step.PlanAsync(ctx, detection, CancellationToken.None);

        Assert.Equal(StepState.Partial, detection.State);
        var action = Assert.Single(plan);
        Assert.Contains("--update --web-download", action.Detail);
        Assert.DoesNotContain("--no-distribution", action.Detail);
    }

    [Fact]
    public async Task NoWslAtAll_PlansFullInstall()
    {
        var step = new WslFeatureStep(new FakeWslBackend(
            new WslStatusInfo(WslInstallKind.NotInstalled, null, null, null, false, "")));
        var ctx = Context();

        var detection = await step.DetectAsync(ctx, CancellationToken.None);
        var plan = await step.PlanAsync(ctx, detection, CancellationToken.None);

        Assert.Equal(StepState.Missing, detection.State);
        var action = Assert.Single(plan);
        Assert.Contains("--install --no-distribution", action.Detail);
    }
}
