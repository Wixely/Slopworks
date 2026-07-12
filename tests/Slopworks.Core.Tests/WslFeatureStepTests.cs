using Microsoft.Extensions.Logging.Abstractions;
using Slopworks.Core.Actions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Steps;
using Slopworks.Platform.Abstractions;
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

    [Fact]
    public void Management_ForcesUtf8_OutputAndEnvironment()
    {
        var spec = WslCommands.Management(["--status"]);

        Assert.Equal(System.Text.Encoding.UTF8, spec.StdoutEncoding);
        Assert.NotNull(spec.Env);
        Assert.Equal("1", spec.Env!["WSL_UTF8"]);
    }

    [Theory]
    [InlineData("Changes will not be effective until the system is rebooted.", true)]
    [InlineData("Please restart your computer to finish.", true)]
    [InlineData("Windows Subsystem for Linux has been installed.", false)]
    public void IndicatesRebootRequired_MatchesRebootLanguage(string output, bool expected)
        => Assert.Equal(expected, WslCommands.IndicatesRebootRequired(output));

    [Fact]
    public async Task LegacyUpgrade_WhenOutputSaysReboot_ReturnsNeedsReboot()
    {
        // Reproduces the reported machine: the in-place upgrade also enabled
        // VirtualMachinePlatform, which announces a pending reboot.
        var step = new WslFeatureStep(new FakeWslBackend(
            new WslStatusInfo(WslInstallKind.LegacyInbox, null, null, 2, false, "")));
        var ctx = Context();
        var action = (await step.PlanAsync(ctx, await step.DetectAsync(ctx, CancellationToken.None), CancellationToken.None)).Single();

        var runner = new FakeProcessRunner
        {
            Result = new ProcessResult(0, "Changes will not be effective until the system is rebooted.", "", TimeSpan.Zero),
        };
        var result = await action.Execute(ExecContext(runner), CancellationToken.None);

        Assert.True(result.RebootRequired);
    }

    [Fact]
    public async Task LegacyUpgrade_WhenNoRebootNeeded_ReturnsSuccess()
    {
        var step = new WslFeatureStep(new FakeWslBackend(
            new WslStatusInfo(WslInstallKind.LegacyInbox, null, null, 2, false, "")));
        var ctx = Context();
        var action = (await step.PlanAsync(ctx, await step.DetectAsync(ctx, CancellationToken.None), CancellationToken.None)).Single();

        var runner = new FakeProcessRunner
        {
            Result = new ProcessResult(0, "Windows Subsystem for Linux has been installed.", "", TimeSpan.Zero),
        };
        var result = await action.Execute(ExecContext(runner), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.RebootRequired);
    }

    private static ActionExecutionContext ExecContext(IProcessRunner runner) => new()
    {
        Processes = runner,
        Logger = NullLogger.Instance,
        Paths = new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests")),
        Output = new InlineProgress<string>(_ => { }),
    };
}
