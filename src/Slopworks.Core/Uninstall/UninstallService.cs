using Slopworks.Core.Config;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Uninstall;

public sealed record CleanupStatus(
    string Id,
    string Title,
    string Description,
    bool Present,
    string Detail,
    string? Warning = null);

public sealed record CleanupResult(string Id, bool Succeeded, string Message);

/// <summary>
/// The undo ledger: every change Slopworks makes to a machine, each individually reversible,
/// in a safe removal order. Nothing Slopworks cannot cleanly remove is hidden — WhatRemains
/// spells it out.
/// </summary>
public sealed class UninstallService(
    SlopworksPaths paths,
    SlopworksConfig config,
    ILinuxCommandFactory linux,
    INetworkExposure network,
    IShellIntegration shell,
    IWslBackend? wsl,
    string? appDataPointerDir = null)
{
    private string PointerDir => appDataPointerDir ?? Path.GetDirectoryName(RootLocator.PointerFile)!;

    public const string NetworkId = "network";
    public const string ServerId = "server";
    public const string DistroId = "distro";
    public const string DownloadsId = "downloads";
    public const string ImagesId = "images";
    public const string StartupId = "startup";
    public const string DataId = "data";
    public const string WslId = "wsl";

    /// <summary>Safe removal order for this platform; WSL last and only ever by explicit opt-in.</summary>
    public static IReadOnlyList<string> RemovalOrder => OperatingSystem.IsWindows()
        ? [NetworkId, ServerId, DistroId, DownloadsId, StartupId, DataId]
        : [NetworkId, ServerId, ImagesId, StartupId, DataId];

    public static string WhatRemains => OperatingSystem.IsWindows()
        ? "Things Slopworks deliberately does not remove:\n" +
          "• The NVIDIA Windows driver (removing display drivers can break your screen; uninstall via Windows Settings → Apps).\n" +
          "• The Windows optional features behind WSL (Virtual Machine Platform / Windows Subsystem for Linux) — other software may use them; disable via OptionalFeatures.exe if truly unwanted.\n" +
          "• Anything installed via winget at your request (e.g. Nvidia.App) — remove with winget uninstall.\n" +
          "• The Slopworks executable itself — delete it whenever you like; it stores nothing outside the folders listed above."
        : "Things Slopworks deliberately does not remove:\n" +
          "• The NVIDIA driver (removing it can break your display; use Software & Updates → Additional Drivers).\n" +
          "• apt packages installed during setup — other software may use them; remove manually with:\n" +
          "    sudo apt remove podman nvidia-container-toolkit ubuntu-drivers-common\n" +
          "• The Slopworks executable itself — delete it whenever you like; it stores nothing outside the folders listed above.";

    public async Task<IReadOnlyList<CleanupStatus>> GetStatusAsync(IProcessRunner runner, CancellationToken ct)
    {
        var statuses = new List<CleanupStatus>();

        var port = config.Server.Port;
        var networkOpen = false;
        try
        {
            networkOpen = await network.IsEnabledAsync(runner, port, ct);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
        }

        statuses.Add(new CleanupStatus(NetworkId, "Network exposure",
            $"Port forward (0.0.0.0:{port}) and firewall rule",
            networkOpen,
            networkOpen ? $"Port {port} is currently open to the network." : "Not active."));

        statuses.Add(await GetContainerStatusAsync(runner, ct));

        if (OperatingSystem.IsWindows())
        {
            statuses.Add(await GetDistroStatusAsync(ct));

            var downloadsPresent = Directory.Exists(paths.RootfsDir)
                && Directory.EnumerateFiles(paths.RootfsDir).Any();
            statuses.Add(new CleanupStatus(DownloadsId, "Downloaded files",
                "Rootfs tarballs in downloads/",
                downloadsPresent,
                downloadsPresent
                    ? $"{Directory.EnumerateFiles(paths.RootfsDir).Sum(f => new FileInfo(f).Length) / 1024 / 1024} MB in {paths.RootfsDir}"
                    : "Nothing downloaded."));
        }
        else
        {
            // On a Linux host images live in the user's podman storage, not inside a vhdx.
            statuses.Add(await GetImagesStatusAsync(runner, ct));
        }

        statuses.Add(new CleanupStatus(StartupId, "Startup resume script",
            "Reopens Slopworks after a mid-setup reboot",
            shell.ResumeOnStartupInstalled,
            shell.ResumeOnStartupInstalled ? "Installed in the Startup folder." : "Not installed."));

        statuses.Add(new CleanupStatus(DataId, "Slopworks data folder",
            $"Config, state, journal, logs{(OperatingSystem.IsWindows() ? "" : " and model cache")} at {paths.Root} (plus the pointer file)",
            Directory.Exists(paths.Root),
            Directory.Exists(paths.Root) ? $"Exists at {paths.Root}." : "Already removed."));

        if (OperatingSystem.IsWindows())
            statuses.Add(await GetWslStatusAsync(ct));
        return statuses;
    }

    /// <summary>Every container image referenced by any platform (plus the active config), de-duplicated.</summary>
    private IEnumerable<string> AllPlatformImages()
    {
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.Images.Gpu, config.Images.Cpu };
        var store = new PlatformStore(paths);
        foreach (var name in store.List())
        {
            var platform = store.Load(name);
            images.Add(platform.Images.Gpu);
            images.Add(platform.Images.Cpu);
        }
        return images.Where(i => !string.IsNullOrWhiteSpace(i));
    }

    private async Task<CleanupStatus> GetImagesStatusAsync(IProcessRunner runner, CancellationToken ct)
    {
        var present = false;
        var detail = "No vLLM images in podman storage.";
        try
        {
            var probe = await runner.RunAsync(
                linux.Command($"podman images --format '{{{{.Repository}}}}:{{{{.Tag}}}}' 2>/dev/null || true"), null, ct);
            var all = AllPlatformImages().ToList();
            var relevant = probe.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(i => all.Any(img => img.Contains(i, StringComparison.OrdinalIgnoreCase))
                         || i.Contains("vllm", StringComparison.OrdinalIgnoreCase))
                .ToList();
            present = relevant.Count > 0;
            if (present)
                detail = $"Present: {string.Join(", ", relevant)}.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return new CleanupStatus(ImagesId, "vLLM container images",
            "Pulled images in this user's podman storage", present, detail);
    }

    private async Task<CleanupStatus> GetContainerStatusAsync(IProcessRunner runner, CancellationToken ct)
    {
        var detail = "Not present.";
        var present = false;
        try
        {
            var probe = await runner.RunAsync(
                linux.Command($"podman inspect --format '{{{{.State.Status}}}}' {VllmServerController.ContainerName} 2>/dev/null || echo absent"),
                null, ct);
            var state = probe.Stdout.Trim();
            present = probe.Succeeded && state.Length > 0 && !state.Contains("absent");
            if (present)
                detail = $"Container is {state}.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return new CleanupStatus(ServerId, "vLLM server container",
            $"Podman container '{VllmServerController.ContainerName}' inside the distro", present, detail);
    }

    private async Task<CleanupStatus> GetDistroStatusAsync(CancellationToken ct)
    {
        var registered = (await (wsl?.ListDistrosAsync(ct) ?? Task.FromResult<IReadOnlyList<string>>([])))
            .Any(d => string.Equals(d, SlopworksPaths.DistroName, StringComparison.OrdinalIgnoreCase));
        var vhdx = Path.Combine(paths.DistroDir, "ext4.vhdx");
        var present = registered || File.Exists(vhdx);

        var detail = (registered, File.Exists(vhdx)) switch
        {
            (true, true) => $"Registered; disk is {new FileInfo(vhdx).Length / 1024 / 1024} MB (includes container images and model cache).",
            (true, false) => "Registered (disk elsewhere or missing).",
            (false, true) => "Unregistered stale disk on disk.",
            _ => "Not present.",
        };

        return new CleanupStatus(DistroId, "Slopworks Linux distro",
            "The WSL distro, its disk, container images and cached models — all in one vhdx",
            present, detail,
            present ? "Removing this deletes every cached model and image inside it." : null);
    }

    private async Task<CleanupStatus> GetWslStatusAsync(CancellationToken ct)
    {
        if (wsl is null)
            return new CleanupStatus(WslId, "WSL itself (system-wide)", "Not applicable on this platform", false, "n/a");

        var status = await wsl.GetStatusAsync(ct);
        var present = status.Kind != WslInstallKind.NotInstalled;
        var others = (await wsl.ListDistrosAsync(ct))

            .Where(d => !string.Equals(d, SlopworksPaths.DistroName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var warning = present
            ? others.Count > 0
                ? $"WSL IS IN USE BY OTHER SYSTEMS — these distros belong to something else and removing WSL breaks them: {string.Join(", ", others)}."
                : "Removing WSL affects the whole machine, not just Slopworks. Windows optional features stay enabled (see What remains)."
            : null;

        return new CleanupStatus(WslId, "WSL itself (system-wide)",
            "The Windows Subsystem for Linux app — only removable by explicit opt-in",
            present,
            present ? $"Installed ({status.Kind}, version {status.WslVersion ?? "unknown"})." : "Not installed.",
            warning);
    }

    public async Task<CleanupResult> RemoveAsync(
        string id, IProcessRunner runner, IProgress<string>? output, CancellationToken ct)
    {
        try
        {
            switch (id)
            {
                case NetworkId:
                    await network.DisableAsync(runner, config.Server.Port, ct);
                    return new CleanupResult(id, true,
                        $"Port forward and firewall rule for port {config.Server.Port} removed.");

                case ServerId:
                    await runner.RunAsync(
                        linux.Command($"podman rm -f {VllmServerController.ContainerName} 2>/dev/null || true"),
                        output, ct);
                    return new CleanupResult(id, true, "Server container removed.");

                case DistroId:
                    await runner.RunAsync(linux.Terminate(), output, ct);
                    await runner.RunAsync(
                        Steps.WslCommands.Management(["--unregister", SlopworksPaths.DistroName]), output, ct);
                    if (Directory.Exists(paths.WslDir))
                        Directory.Delete(paths.WslDir, recursive: true);
                    return new CleanupResult(id, true, "Distro unregistered and its disk deleted.");

                case DownloadsId:
                    if (Directory.Exists(paths.DownloadsDir))
                        Directory.Delete(paths.DownloadsDir, recursive: true);
                    return new CleanupResult(id, true, "Downloaded files deleted.");

                case ImagesId:
                    // Remove the images of every platform, not just the active one, so no platform's
                    // pulled images linger in podman storage.
                    await runner.RunAsync(
                        linux.Command($"podman rmi -f {string.Join(' ', AllPlatformImages())} 2>/dev/null || true"),
                        output, ct);
                    return new CleanupResult(id, true, "vLLM container images removed from podman storage.");

                case StartupId:
                    shell.RemoveResumeOnStartup();
                    return new CleanupResult(id, true, "Startup script removed.");

                case DataId:
                    if (Directory.Exists(paths.Root))
                        Directory.Delete(paths.Root, recursive: true);
                    if (Directory.Exists(PointerDir))
                        Directory.Delete(PointerDir, recursive: true);
                    return new CleanupResult(id, true,
                        "Data folder and %APPDATA% pointer removed. Slopworks recreates defaults on next launch.");

                case WslId:
                    var result = await runner.RunAsync(
                        Steps.WslCommands.Management(["--uninstall"]) with { RequiresElevation = true }, output, ct);
                    return result.Succeeded
                        ? new CleanupResult(id, true, "WSL uninstalled. Windows optional features remain (see What remains).")
                        : new CleanupResult(id, false, $"wsl --uninstall failed: {TextUtil.Condense(result.Stderr + result.Stdout, 200)}");

                default:
                    return new CleanupResult(id, false, $"Unknown component '{id}'.");
            }
        }
        catch (Exception ex)
        {
            return new CleanupResult(id, false, $"Failed: {ex.Message}");
        }
    }

    /// <summary>Best-effort full removal in safe order; keeps going past individual failures.</summary>
    public async Task<IReadOnlyList<CleanupResult>> RemoveEverythingAsync(
        bool includeWsl, IProcessRunner runner, IProgress<string>? output, CancellationToken ct)
    {
        var results = new List<CleanupResult>();
        IReadOnlyList<string> order = includeWsl && OperatingSystem.IsWindows()
            ? [.. RemovalOrder, WslId]
            : RemovalOrder;

        foreach (var id in order)
        {
            ct.ThrowIfCancellationRequested();
            output?.Report($"Removing {id}…");
            results.Add(await RemoveAsync(id, runner, output, ct));
        }

        return results;
    }
}
