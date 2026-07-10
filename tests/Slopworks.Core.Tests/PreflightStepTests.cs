using Microsoft.Extensions.Logging.Abstractions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Steps;
using Xunit;

namespace Slopworks.Core.Tests;

public class PreflightStepTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static StepContext Context(SystemProfile profile, SlopworksConfig? config = null) => new()
    {
        Paths = new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests")),
        Config = config ?? new SlopworksConfig(),
        Profile = profile,
        Logger = NullLogger.Instance,
        Journal = new InMemoryJournal(),
        Probes = new FakeProcessRunner(),
    };

    private static SystemProfile HealthyProfile => new()
    {
        OsDescription = "Windows 11",
        OsBuild = 26100,
        FreeDiskBytes = 200 * Gb,
        TotalMemoryBytes = 32 * Gb,
        Gpu = new GpuInfo("NVIDIA RTX 4090", "560.94", 24564),
    };

    [Fact]
    public async Task HealthyMachine_IsOk()
    {
        var detection = await new PreflightStep().DetectAsync(Context(HealthyProfile), CancellationToken.None);

        Assert.Equal(StepState.Ok, detection.State);
        Assert.Null(detection.BypassKey);
    }

    [Fact]
    public async Task LowDisk_IsBrokenButBypassable()
    {
        var profile = HealthyProfile with { FreeDiskBytes = 10 * Gb };

        var detection = await new PreflightStep().DetectAsync(Context(profile), CancellationToken.None);

        Assert.Equal(StepState.Broken, detection.State);
        Assert.Equal(PreflightStep.DiskBypassKey, detection.BypassKey);
        Assert.Contains("bypass", detection.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LowDisk_WithBypass_DowngradesToWarning()
    {
        var profile = HealthyProfile with { FreeDiskBytes = 10 * Gb };
        var config = new SlopworksConfig { Bypasses = [PreflightStep.DiskBypassKey] };

        var detection = await new PreflightStep().DetectAsync(Context(profile, config), CancellationToken.None);

        Assert.Equal(StepState.Ok, detection.State);
        Assert.Contains("bypassed", detection.Summary);
    }

    [Fact]
    public async Task OldWindowsBuild_IsBroken_AndNeverBypassable()
    {
        var profile = HealthyProfile with { OsBuild = 17763 };
        var config = new SlopworksConfig { Bypasses = [PreflightStep.DiskBypassKey, "preflight.os"] };

        var detection = await new PreflightStep().DetectAsync(Context(profile, config), CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(StepState.Broken, detection.State);
            Assert.Null(detection.BypassKey);
        }
    }

    [Fact]
    public async Task VirtualizationOff_IsBroken_AndNeverBypassable()
    {
        var profile = HealthyProfile with { VirtualizationEnabled = false };

        var detection = await new PreflightStep().DetectAsync(Context(profile), CancellationToken.None);

        Assert.Equal(StepState.Broken, detection.State);
        Assert.Null(detection.BypassKey);
    }

    [Fact]
    public async Task LowRamAndNoGpu_OnlyWarn_NeverBlock()
    {
        var profile = HealthyProfile with { TotalMemoryBytes = 8 * Gb, Gpu = null };

        var detection = await new PreflightStep().DetectAsync(Context(profile), CancellationToken.None);

        Assert.Equal(StepState.Ok, detection.State);
        Assert.Contains("RAM", detection.Summary);
        Assert.Contains("CPU", detection.Summary);
    }
}
