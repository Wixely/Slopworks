using System.IO.Compression;
using Slopworks.Core.Actions;
using Slopworks.Core.Artifacts;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;

namespace Slopworks.Core.Steps;

/// <summary>
/// Imports the downloaded rootfs as the dedicated "slopworks" distro, whose entire
/// filesystem is a single ext4.vhdx inside the Slopworks root. Repair of a broken
/// registration is destructive (unregister + reimport) and says so loudly.
/// </summary>
public sealed class DistroImportStep(IWslBackend wsl) : ISetupStep
{
    public string Id => "wsl.import";
    public string Title => "Slopworks distro";
    public IReadOnlyList<string> DependsOn => ["wsl.kernel", "rootfs.download"];

    public bool AppliesTo(SystemProfile profile) => OperatingSystem.IsWindows();

    public async Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var distros = await wsl.ListDistrosAsync(ct);
        var registered = distros.Any(d => string.Equals(d, SlopworksPaths.DistroName, StringComparison.OrdinalIgnoreCase));
        var vhdxPath = Path.Combine(ctx.Paths.DistroDir, "ext4.vhdx");
        var vhdxExists = File.Exists(vhdxPath);

        return (registered, vhdxExists) switch
        {
            (true, true) => StepDetection.Ok(
                $"Distro '{SlopworksPaths.DistroName}' registered, disk at {vhdxPath} " +
                $"({new FileInfo(vhdxPath).Length / 1024 / 1024} MB)."),
            (true, false) => StepDetection.Broken(
                $"Distro '{SlopworksPaths.DistroName}' is registered but its disk is not at {vhdxPath}. " +
                "It must be re-imported into the Slopworks folder."),
            (false, true) => StepDetection.Broken(
                $"A stale disk exists at {vhdxPath} but no '{SlopworksPaths.DistroName}' distro is registered."),
            _ => StepDetection.Missing($"Distro '{SlopworksPaths.DistroName}' not imported yet."),
        };
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var actions = new List<PlannedAction>();

        if (detection.State == StepState.Broken)
        {
            actions.Add(new PlannedAction(
                ActionId: "wsl.import.reset",
                StepId: Id,
                Kind: ActionKind.Execute,
                Description: $"DESTRUCTIVE: unregister broken '{SlopworksPaths.DistroName}' distro and delete its disk. " +
                             "Anything inside the distro (including cached models) is lost.",
                Detail: $"wsl.exe --unregister {SlopworksPaths.DistroName}; then delete {ctx.Paths.DistroDir}",
                InsideSlopworksRoot: false, // unregister affects WSL registration → always prompt
                Execute: async (exec, token) =>
                {
                    // Unregister may legitimately fail when nothing is registered; that's fine.
                    await exec.Processes.RunAsync(
                        WslCommands.Management(["--unregister", SlopworksPaths.DistroName]), exec.Output, token);

                    if (Directory.Exists(exec.Paths.DistroDir))
                        Directory.Delete(exec.Paths.DistroDir, recursive: true);
                    return ActionResult.Success("Old registration and disk removed.");
                }));
        }

        actions.Add(new PlannedAction(
            ActionId: "wsl.import.import",
            StepId: Id,
            Kind: ActionKind.Execute,
            Description: $"Import the rootfs as WSL distro '{SlopworksPaths.DistroName}' (disk stays inside the Slopworks folder)",
            Detail: $"wsl.exe --import {SlopworksPaths.DistroName} \"{ctx.Paths.DistroDir}\" \"<rootfs tarball>\" --version 2",
            InsideSlopworksRoot: false,
            Execute: (exec, token) => ImportAsync(ctx, exec, token)));

        return Task.FromResult<IReadOnlyList<PlannedAction>>(actions);
    }

    private static async Task<ActionResult> ImportAsync(StepContext ctx, ActionExecutionContext exec, CancellationToken ct)
    {
        var tarball = FindRootfsTarball(ctx);
        if (tarball is null)
            return ActionResult.Failure("No verified rootfs tarball found in downloads/rootfs — run the download step first.");

        Directory.CreateDirectory(exec.Paths.DistroDir);
        exec.Output.Report($"Importing {Path.GetFileName(tarball)} (this can take a few minutes)…");

        var result = await exec.Processes.RunAsync(
            WslCommands.Management(["--import", SlopworksPaths.DistroName, exec.Paths.DistroDir, tarball, "--version", "2"]),
            exec.Output, ct);

        // Older Store WSL builds reject .tar.gz; decompress with pure .NET and retry.
        if (!result.Succeeded && tarball.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            exec.Output.Report("Import of .tar.gz failed; decompressing to .tar and retrying…");
            var tarPath = tarball[..^3];
            if (!File.Exists(tarPath))
            {
                await using var input = new GZipStream(File.OpenRead(tarball), CompressionMode.Decompress);
                await using var output = File.Create(tarPath);
                await input.CopyToAsync(output, ct);
            }

            result = await exec.Processes.RunAsync(
                WslCommands.Management(["--import", SlopworksPaths.DistroName, exec.Paths.DistroDir, tarPath, "--version", "2"]),
                exec.Output, ct);
        }

        return result.Succeeded
            ? ActionResult.Success($"Distro '{SlopworksPaths.DistroName}' imported.")
            : ActionResult.Failure($"wsl --import failed: {result.Stderr}{result.Stdout}".Trim());
    }

    /// <summary>The verified tarball from the download step (marker present), newest first.</summary>
    private static string? FindRootfsTarball(StepContext ctx)
    {
        if (!Directory.Exists(ctx.Paths.RootfsDir))
            return null;

        return Directory.EnumerateFiles(ctx.Paths.RootfsDir, "*.tar*")
            .Where(f => !f.EndsWith(".part") && !f.EndsWith(".sha256.ok"))
            .Where(f => File.Exists(Downloader.MarkerPath(f)) || f.EndsWith(".tar")) // decompressed .tar inherits trust
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
