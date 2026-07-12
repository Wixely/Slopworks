using Slopworks.Core.Artifacts;
using Slopworks.Core.Config;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;
using Slopworks.Core.Steps;
using Xunit;

namespace Slopworks.Core.Tests;

public class StepCatalogTests
{
    private sealed class StubWsl : IWslBackend
    {
        public Task<WslStatusInfo> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new WslStatusInfo(WslInstallKind.Modern, null, null, 2, false, ""));

        public Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static (WslLinuxCommandFactory Linux, VllmServerController Server, ArtifactResolver Resolver, Downloader Downloader) Deps()
    {
        var config = new SlopworksConfig();
        var linux = new WslLinuxCommandFactory(SlopworksPaths.DistroName);
        var paths = new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests"));
        var http = new HttpClient();
        return (linux,
            new VllmServerController(linux, config, http, paths),
            new ArtifactResolver(config, new InMemoryJournal(), http, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            new Downloader(http));
    }

    [Fact]
    public void LinuxCatalog_HasNoWslSteps_AndTopoSorts()
    {
        var (linux, server, _, _) = Deps();

        var steps = StepCatalog.CreateLinuxSteps(linux, server);
        var ids = steps.Select(s => s.Id).ToList();

        Assert.DoesNotContain("wsl.feature", ids);
        Assert.DoesNotContain("wsl.kernel", ids);
        Assert.DoesNotContain("wsl.import", ids);
        Assert.DoesNotContain("distro.base", ids);
        Assert.Contains("gpu.driver", ids);
        Assert.Contains("distro.podman", ids);
        Assert.Contains("vllm.smoke", ids);

        // Every declared dependency must exist within the catalog (topo sort would throw otherwise).
        var known = ids.ToHashSet();
        foreach (var step in steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!OperatingSystem.IsWindows() || dep != "distro.base")
                    Assert.Contains(dep, known);
            }
        }
    }

    [Fact]
    public void WindowsCatalog_IsUnchangedByThePort()
    {
        var (linux, server, resolver, downloader) = Deps();

        var ids = StepCatalog.CreateWindowsSteps(new StubWsl(), resolver, downloader, linux, server)
            .Select(s => s.Id).ToList();

        Assert.Equal(
            ["preflight", "gpu.driver", "wsl.feature", "wsl.kernel", "rootfs.download",
             "wsl.import", "distro.base", "distro.podman", "distro.nvidia",
             "image.pull", "gpu.smoke", "vllm.smoke"],
            ids);
    }
}
