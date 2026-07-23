using Microsoft.Extensions.Logging;
using Slopworks.Core.Actions;
using Slopworks.Core.Artifacts;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Http;
using Slopworks.Core.Logging;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;
using Slopworks.Core.State;
using Slopworks.Core.Steps;
using Slopworks.Platform.Abstractions;
using Slopworks.Platform.Linux;
using Slopworks.Platform.Windows;
using Slopworks.Platform.Windows.Elevation;
using Slopworks.Platform.Windows.Wsl;

namespace Slopworks.App;

/// <summary>
/// Composition root. Platform-specific services are chosen here; the Linux port swaps the
/// registrations in Create() and nothing else.
/// </summary>
public sealed class SlopworksHost
{
    public required SlopworksPaths Paths { get; init; }
    public required SlopworksConfig Config { get; init; }
    public required ProfileManager Profiles { get; init; }
    public required ILogger Logger { get; init; }
    public required IStateJournal Journal { get; init; }

    /// <summary>Null on Linux hosts — there is no WSL layer.</summary>
    public required IWslBackend? Wsl { get; init; }
    public required ISystemInfoProvider SystemInfo { get; init; }
    public required IProcessRunner ProcessRunner { get; init; }
    public required ICommandLog CommandLog { get; init; }
    public required IArtifactResolver Resolver { get; init; }
    public required Downloader Downloader { get; init; }
    public required IShellIntegration ShellIntegration { get; init; }
    public required ILinuxCommandFactory Linux { get; init; }
    public required VllmServerController Server { get; init; }
    public required ModelInspector ModelInspector { get; init; }
    public required INetworkExposure NetworkExposure { get; init; }
    public required ISystemMetrics Metrics { get; init; }
    public required IGpuMetrics GpuMetrics { get; init; }

    /// <summary>Non-null when the current mode is safe; the UI drains its Pending channel.</summary>
    public InteractiveGate? InteractiveGate { get; private set; }

    public static SlopworksHost Create()
    {
        var paths = new SlopworksPaths(RootLocator.Resolve());
        paths.EnsureCreated();

        var config = ConfigStore.LoadOrCreate(paths);
        var profiles = new ProfileManager(paths, config); // migrates config.json into a "default" profile
        var logger = new FileLoggerProvider(paths.LogsDir).CreateLogger("Slopworks");
        var commandLog = new FileCommandLog(paths.LogsDir);
        var direct = new SystemProcessRunner();
        var journal = FileStateJournal.Load(paths.JournalFile);
        var http = SlopworksHttpClient.Create(config.Network);

        // The single per-OS branch: every platform-specific service is chosen here.
        if (OperatingSystem.IsWindows())
        {
            var runner = new CompositeProcessRunner(direct, new ElevatedProcessRunner(paths.ElevatedDir));
            var probes = new RecordingProcessRunner(runner, commandLog, "probe", "read-only");
            var linux = new WslLinuxCommandFactory(SlopworksPaths.DistroName);

            return new SlopworksHost
            {
                Paths = paths,
                Config = config,
                Profiles = profiles,
                Logger = logger,
                Journal = journal,
                Wsl = new WindowsWslBackend(probes),
                SystemInfo = new WindowsSystemInfo(paths, probes),
                ProcessRunner = runner,
                CommandLog = commandLog,
                Resolver = new ArtifactResolver(config, journal, http, logger),
                Downloader = new Downloader(http),
                ShellIntegration = new WindowsShellIntegration(runner),
                Linux = linux,
                Server = new VllmServerController(linux, config, http, paths),
                ModelInspector = new ModelInspector(http, config),
                NetworkExposure = new WindowsNetworkExposure(),
                Metrics = new WindowsSystemMetrics(),
                GpuMetrics = new WindowsGpuMetrics(),
            };
        }
        else
        {
            var runner = new PkexecProcessRunner(direct);
            var probes = new RecordingProcessRunner(runner, commandLog, "probe", "read-only");
            var linux = new HostLinuxCommandFactory();

            return new SlopworksHost
            {
                Paths = paths,
                Config = config,
                Profiles = profiles,
                Logger = logger,
                Journal = journal,
                Wsl = null,
                SystemInfo = new LinuxSystemInfo(paths, probes),
                ProcessRunner = runner,
                CommandLog = commandLog,
                Resolver = new ArtifactResolver(config, journal, http, logger),
                Downloader = new Downloader(http),
                ShellIntegration = new LinuxShellIntegration(runner),
                Linux = linux,
                Server = new VllmServerController(linux, config, http, paths),
                ModelInspector = new ModelInspector(http, config),
                NetworkExposure = new LinuxNetworkExposure(),
                Metrics = new LinuxSystemMetrics(),
                GpuMetrics = new LinuxGpuMetrics(),
            };
        }
    }

    public async Task<(ConvergenceEngine Engine, SystemProfile Profile)> CreateEngineAsync(CancellationToken ct)
    {
        var profile = await SystemInfo.GetProfileAsync(ct);

        var context = new StepContext
        {
            Paths = Paths,
            Config = Config,
            Profile = profile,
            Logger = Logger,
            Journal = Journal,
            Probes = new RecordingProcessRunner(ProcessRunner, CommandLog, "probe", "read-only"),
        };

        var steps = OperatingSystem.IsWindows()
            ? StepCatalog.CreateWindowsSteps(Wsl!, Resolver, Downloader, Linux, Server)
            : StepCatalog.CreateLinuxSteps(Linux, Server);

        var engine = new ConvergenceEngine(steps, new EngineServices
        {
            StepContext = context,
            Gate = BuildGate(),
            ProcessRunner = ProcessRunner,
            CommandLog = CommandLog,
            Logger = Logger,
        });

        return (engine, profile);
    }

    private IActionGate BuildGate()
    {
        if (Config.IsAutoMode)
        {
            InteractiveGate = null;
            return new AutoApproveGate(Logger);
        }

        InteractiveGate = new InteractiveGate();
        return new PolicyGate(InteractiveGate, Config.AutoApproveInsideRoot);
    }
}
