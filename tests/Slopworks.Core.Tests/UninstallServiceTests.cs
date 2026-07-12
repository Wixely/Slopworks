using Slopworks.Core.Config;
using Slopworks.Core.Platform;
using Slopworks.Core.Uninstall;
using Slopworks.Platform.Abstractions;
using Xunit;

namespace Slopworks.Core.Tests;

public class UninstallServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("slopworks-uninstall-").FullName;

    private sealed class FakeNetworkExposure : INetworkExposure
    {
        public bool Enabled { get; set; }
        public int DisableCalls { get; private set; }

        public Task<bool> IsEnabledAsync(IProcessRunner p, int port, CancellationToken ct) => Task.FromResult(Enabled);

        public Task<ProcessResult> EnableAsync(IProcessRunner p, int port, CancellationToken ct)
            => Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));

        public Task<ProcessResult> DisableAsync(IProcessRunner p, int port, CancellationToken ct)
        {
            DisableCalls++;
            Enabled = false;
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }

        public string DescribeEnable(int port) => "";
        public IReadOnlyList<string> GetLanAddresses() => [];
    }

    private sealed class FakeShell : IShellIntegration
    {
        public bool Installed { get; set; }
        public void InstallResumeOnStartup() => Installed = true;
        public void RemoveResumeOnStartup() => Installed = false;
        public bool ResumeOnStartupInstalled => Installed;
        public Task RequestRebootAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeWsl(IReadOnlyList<string> distros) : IWslBackend
    {
        public Task<WslStatusInfo> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new WslStatusInfo(WslInstallKind.Modern, "2.7.10.0", null, 2, false, ""));

        public Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct) => Task.FromResult(distros);
    }

    private (UninstallService Service, FakeNetworkExposure Network, FakeShell Shell) Build(
        IReadOnlyList<string>? distros = null)
    {
        var paths = new SlopworksPaths(_root);
        paths.EnsureCreated();
        var network = new FakeNetworkExposure();
        var shell = new FakeShell();
        var service = new UninstallService(
            paths, new SlopworksConfig(),
            new WslLinuxCommandFactory(SlopworksPaths.DistroName),
            network, shell, new FakeWsl(distros ?? []),
            appDataPointerDir: Path.Combine(_root, "fake-appdata"));
        return (service, network, shell);
    }

    [Fact]
    public async Task Status_ListsEveryChangeSlopworksCanMake()
    {
        var (service, _, _) = Build();

        var statuses = await service.GetStatusAsync(new FakeProcessRunner(), CancellationToken.None);

        string[] expected = OperatingSystem.IsWindows()
            ? [UninstallService.NetworkId, UninstallService.ServerId, UninstallService.DistroId,
               UninstallService.DownloadsId, UninstallService.StartupId, UninstallService.DataId, UninstallService.WslId]
            : [UninstallService.NetworkId, UninstallService.ServerId, UninstallService.ImagesId,
               UninstallService.StartupId, UninstallService.DataId];
        Assert.Equal(expected, statuses.Select(s => s.Id));
    }

    [Fact]
    public async Task WslStatus_WarnsLoudlyWhenOtherDistrosExist()
    {
        var (service, _, _) = Build(distros: ["Ubuntu", "docker-desktop"]);

        var statuses = await service.GetStatusAsync(new FakeProcessRunner(), CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            // No WSL layer exists on a Linux host, so no WSL cleanup item should either.
            Assert.DoesNotContain(statuses, s => s.Id == UninstallService.WslId);
            return;
        }

        var wslStatus = statuses.Single(s => s.Id == UninstallService.WslId);
        Assert.Contains("IN USE BY OTHER SYSTEMS", wslStatus.Warning);
        Assert.Contains("Ubuntu", wslStatus.Warning);
    }

    [Fact]
    public async Task RemoveNetwork_DelegatesToDisable()
    {
        var (service, network, _) = Build();
        network.Enabled = true;

        var result = await service.RemoveAsync(UninstallService.NetworkId, new FakeProcessRunner(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, network.DisableCalls);
        Assert.False(network.Enabled);
    }

    [Fact]
    public async Task RemoveStartup_RemovesScript()
    {
        var (service, _, shell) = Build();
        shell.Installed = true;

        var result = await service.RemoveAsync(UninstallService.StartupId, new FakeProcessRunner(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(shell.Installed);
    }

    [Fact]
    public async Task RemoveEverything_ExcludesWslUnlessOptedIn()
    {
        var (service, _, _) = Build();

        var withoutWsl = await service.RemoveEverythingAsync(false, new FakeProcessRunner(), null, CancellationToken.None);
        Assert.DoesNotContain(withoutWsl, r => r.Id == UninstallService.WslId);
    }

    [Fact]
    public async Task RemoveEverything_WithOptIn_RunsWslLast_OnWindowsOnly()
    {
        var (service, _, _) = Build();

        var results = await service.RemoveEverythingAsync(true, new FakeProcessRunner(), null, CancellationToken.None);

        if (OperatingSystem.IsWindows())
            Assert.Equal(UninstallService.WslId, results[^1].Id);
        else
            Assert.DoesNotContain(results, r => r.Id == UninstallService.WslId);
    }

    [Fact]
    public async Task RemoveData_DeletesRootDirectory()
    {
        var (service, _, _) = Build();
        Assert.True(Directory.Exists(_root));

        var result = await service.RemoveAsync(UninstallService.DataId, new FakeProcessRunner(), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
