using Slopworks.Core.Actions;
using Slopworks.Core.Artifacts;
using Slopworks.Core.Engine;

namespace Slopworks.Core.Steps;

/// <summary>
/// Downloads the Linux rootfs tarball into downloads/rootfs with resume + checksum
/// verification. Detection is fully offline: it trusts the sidecar marker written after a
/// verified download, so multi-GB files are never rehashed on dashboard refresh.
/// </summary>
public sealed class RootfsDownloadStep(IArtifactResolver resolver, Downloader downloader) : ISetupStep
{
    public const string ArtifactKey = "rootfs";

    public string Id => "rootfs.download";
    public string Title => "Linux rootfs image";
    public IReadOnlyList<string> DependsOn => ["preflight"];

    public bool AppliesTo(SystemProfile profile) => OperatingSystem.IsWindows();

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        if (!ctx.Config.Distro.UsesTarball)
        {
            return Task.FromResult(StepDetection.Ok(
                $"Not needed — the distro comes straight from the official WSL catalog ({ctx.Config.Distro.OnlineName}). " +
                "Set distro.source to 'tarball' in config.json to use a custom rootfs URL instead."));
        }

        if (!ctx.Config.Artifacts.TryGetValue(ArtifactKey, out var source))
            return Task.FromResult(StepDetection.Broken("config.json has no 'rootfs' artifact source."));

        var fileName = source.Url is { } url
            ? ArtifactResolver.FileNameFromUrl(url)
            : ctx.Journal.Data.ResolvedArtifacts.TryGetValue(ArtifactKey, out var cached) ? cached.FileName : null;

        if (fileName is null)
        {
            return Task.FromResult(StepDetection.Missing(
                "Rootfs not downloaded (exact file resolved from GitHub at install time)."));
        }

        var destination = Path.Combine(ctx.Paths.RootfsDir, fileName);
        if (!File.Exists(destination))
        {
            var partial = File.Exists(destination + ".part")
                ? $" A partial download ({new FileInfo(destination + ".part").Length / 1024 / 1024} MB) will be resumed."
                : "";
            return Task.FromResult(StepDetection.Missing($"Rootfs {fileName} not downloaded.{partial}"));
        }

        if (File.Exists(Downloader.MarkerPath(destination)))
        {
            return Task.FromResult(StepDetection.Ok(
                $"Rootfs present and verified ({fileName}, {new FileInfo(destination).Length / 1024 / 1024} MB).",
                File.ReadAllText(Downloader.MarkerPath(destination)).Trim()));
        }

        return Task.FromResult(StepDetection.Partial(
            $"Rootfs {fileName} exists but has not been checksum-verified."));
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var source = ctx.Config.Artifacts[ArtifactKey];
        var detail = source.Url
            ?? $"latest release of github.com/{source.GitHub?.Repo} matching '{source.GitHub?.AssetPattern}'";

        var action = new PlannedAction(
            ActionId: "rootfs.download.fetch",
            StepId: Id,
            Kind: ActionKind.Download,
            Description: "Download and verify the Linux rootfs image",
            Detail: detail,
            InsideSlopworksRoot: true,
            Execute: async (exec, token) =>
            {
                var resolved = await resolver.ResolveAsync(ArtifactKey, token);
                var destination = Path.Combine(exec.Paths.RootfsDir, resolved.FileName);

                // An existing-but-unverified file just needs hashing, not re-downloading.
                if (File.Exists(destination) && !File.Exists(Downloader.MarkerPath(destination)))
                {
                    exec.Output.Report($"Verifying existing file {resolved.FileName}…");
                    await Downloader.VerifyAsync(destination, resolved.Sha256, exec.Output, token);
                    return ActionResult.Success("Existing file verified.");
                }

                await downloader.DownloadAsync(resolved.Url, destination, resolved.Sha256, exec.Output, token);
                return ActionResult.Success($"Downloaded {resolved.FileName} from {resolved.Source}.");
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }
}
