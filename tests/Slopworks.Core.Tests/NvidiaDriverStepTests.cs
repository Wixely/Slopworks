using Microsoft.Extensions.Logging.Abstractions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Steps;
using Xunit;

namespace Slopworks.Core.Tests;

public class NvidiaDriverStepTests
{
    private static StepContext Context(SystemProfile profile, SlopworksConfig? config = null) => new()
    {
        Paths = new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests")),
        Config = config ?? new SlopworksConfig(),
        Profile = profile,
        Logger = NullLogger.Instance,
        Journal = new InMemoryJournal(),
        Probes = new FakeProcessRunner(),
    };

    [Fact]
    public void NoNvidiaHardware_StepDoesNotApply()
    {
        Assert.False(new NvidiaDriverStep().AppliesTo(new SystemProfile()));
    }

    [Fact]
    public async Task HardwareWithoutDriver_IsBrokenButBypassable()
    {
        var profile = new SystemProfile { NvidiaHardwarePresent = true };

        var detection = await new NvidiaDriverStep().DetectAsync(Context(profile), CancellationToken.None);

        Assert.Equal(StepState.Broken, detection.State);
        Assert.Equal(NvidiaDriverStep.BypassKeyName, detection.BypassKey);
        Assert.Contains("CPU-only", detection.Summary);
    }

    [Fact]
    public async Task HardwareWithoutDriver_Bypassed_ContinuesCpuOnly()
    {
        var profile = new SystemProfile { NvidiaHardwarePresent = true };
        var config = new SlopworksConfig { Bypasses = [NvidiaDriverStep.BypassKeyName] };

        var detection = await new NvidiaDriverStep().DetectAsync(Context(profile, config), CancellationToken.None);

        Assert.Equal(StepState.Ok, detection.State);
        Assert.Contains("bypassed", detection.Summary);
    }

    [Fact]
    public async Task ModernDriver_IsOk()
    {
        var profile = new SystemProfile
        {
            NvidiaHardwarePresent = true,
            Gpu = new GpuInfo("NVIDIA GeForce RTX 4090", "560.94", 24564),
        };

        var detection = await new NvidiaDriverStep().DetectAsync(Context(profile), CancellationToken.None);

        Assert.Equal(StepState.Ok, detection.State);
        Assert.Null(detection.BypassKey);
    }

    [Fact]
    public async Task PreCudaWslDriver_IsBrokenButBypassable()
    {
        var profile = new SystemProfile
        {
            NvidiaHardwarePresent = true,
            Gpu = new GpuInfo("NVIDIA GeForce GTX 1080", "388.13", 8192),
        };

        var detection = await new NvidiaDriverStep().DetectAsync(Context(profile), CancellationToken.None);

        Assert.Equal(StepState.Broken, detection.State);
        Assert.Equal(NvidiaDriverStep.BypassKeyName, detection.BypassKey);
    }

    [Fact]
    public async Task Plan_TriesWingetThenFallsBackToDriverPage()
    {
        var profile = new SystemProfile { NvidiaHardwarePresent = true };
        var step = new NvidiaDriverStep();
        var ctx = Context(profile);

        var detection = await step.DetectAsync(ctx, CancellationToken.None);
        var plan = await step.PlanAsync(ctx, detection, CancellationToken.None);

        var action = Assert.Single(plan);
        Assert.Contains("winget", action.Detail);
        Assert.Contains(NvidiaDriverStep.DriverDownloadPage, action.Detail);
    }
}
