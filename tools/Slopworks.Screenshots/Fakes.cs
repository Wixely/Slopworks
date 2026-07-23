using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Screenshots;

// Canned, sanitized system data so the screenshots look populated without touching the real machine.

internal sealed class FakeSystemInfo : ISystemInfoProvider
{
    public Task<SystemProfile> GetProfileAsync(CancellationToken ct) => Task.FromResult(new SystemProfile
    {
        OsDescription = "Windows 11 Pro (build 26100)",
        OsBuild = 26100,
        Gpu = new GpuInfo("NVIDIA GeForce RTX 5090", "560.94", 32768),
        FreeDiskBytes = 812L * 1024 * 1024 * 1024,
        TotalMemoryBytes = 64L * 1024 * 1024 * 1024,
        NvidiaHardwarePresent = true,
        HasNvLink = true,
        VirtualizationEnabled = true,
    });

    public Task<IReadOnlyList<GpuDevice>> EnumerateGpusAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GpuDevice>>(
    [
        new(0, "NVIDIA GeForce RTX 5090", "00000000:01:00.0", 32768),
        new(1, "NVIDIA GeForce RTX 3090", "00000000:21:00.0", 24576),
        new(2, "NVIDIA GeForce RTX 3090", "00000000:41:00.0", 24576),
    ]);
}

internal sealed class FakeMetrics : ISystemMetrics
{
    public SystemUsage Sample() => new(CpuPercent: 34, RamPercent: 58,
        UsedRamBytes: 37L * 1024 * 1024 * 1024, TotalRamBytes: 64L * 1024 * 1024 * 1024);
}

internal sealed class FakeGpuMetrics : IGpuMetrics
{
    public Task<IReadOnlyList<GpuUsage>> SampleAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GpuUsage>>(
    [
        new(0, "NVIDIA GeForce RTX 5090", 86, 24960, 32768),
        new(1, "NVIDIA GeForce RTX 3090", 74, 17100, 24576),
        new(2, "NVIDIA GeForce RTX 3090", 71, 16800, 24576),
    ]);
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct)
        => Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
}

internal sealed class FakeNetwork : INetworkExposure
{
    public Task<bool> IsEnabledAsync(IProcessRunner processes, int port, CancellationToken ct) => Task.FromResult(false);
    public Task<ProcessResult> EnableAsync(IProcessRunner processes, int port, CancellationToken ct) => Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
    public Task<ProcessResult> DisableAsync(IProcessRunner processes, int port, CancellationToken ct) => Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
    public string DescribeEnable(int port) => "";
    public IReadOnlyList<string> GetLanAddresses() => ["192.168.1.50"];
}

internal sealed class FakeShell : IShellIntegration
{
    public void InstallResumeOnStartup() { }
    public void RemoveResumeOnStartup() { }
    public bool ResumeOnStartupInstalled => false;
    public Task RequestRebootAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeWsl : IWslBackend
{
    public Task<WslStatusInfo> GetStatusAsync(CancellationToken ct)
        => Task.FromResult(new WslStatusInfo(WslInstallKind.Modern, "2.3.26.0", "5.15.153.1-microsoft-standard-WSL2", 2, false, ""));

    public Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(["slopworks"]);
}
