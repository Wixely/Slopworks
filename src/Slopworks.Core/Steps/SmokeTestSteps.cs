using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;

namespace Slopworks.Core.Steps;

/// <summary>
/// GPU-only: proves the GPU is reachable from inside a container (reusing the already-pulled
/// vLLM image — no extra download). Marker is keyed to the Windows driver version so driver
/// updates invalidate it.
/// </summary>
public sealed class GpuSmokeTestStep(ILinuxCommandFactory linux) : ISetupStep
{
    public string Id => "gpu.smoke";
    public string Title => "GPU-in-container check";
    public IReadOnlyList<string> DependsOn => ["distro.nvidia", "image.pull"];

    public bool AppliesTo(SystemProfile profile) => profile.GpuPresent;

    private static string MarkerPath(StepContext ctx)
        => Path.Combine(ctx.Paths.SmokeDir, $"gpu-{ctx.Profile.Gpu!.DriverVersion}.ok");

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
        => Task.FromResult(File.Exists(MarkerPath(ctx))
            ? StepDetection.Ok($"GPU verified in-container with driver {ctx.Profile.Gpu!.DriverVersion}.")
            : StepDetection.Missing("GPU has not been verified inside a container yet (or the driver changed)."));

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var image = ImagePullStep.SelectImage(ctx);
        var command = $"podman run --rm --device nvidia.com/gpu=all --entrypoint nvidia-smi {image} -L";

        var action = new PlannedAction(
            ActionId: "gpu.smoke.run",
            StepId: Id,
            Kind: ActionKind.Execute,
            Description: "Run nvidia-smi inside a container to prove GPU passthrough works end to end",
            Detail: command,
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                var result = await exec.Processes.RunAsync(linux.Command(command), exec.Output, token);
                if (!result.Succeeded || !result.Stdout.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                {
                    return ActionResult.Failure(
                        $"GPU not visible inside the container (exit {result.ExitCode}): {DistroBaseStep.Tail(result)} " +
                        "— if nvidia-smi works in the distro but not in a container, repair the NVIDIA toolkit step.");
                }

                Directory.CreateDirectory(exec.Paths.SmokeDir);
                await File.WriteAllTextAsync(MarkerPath(ctx), result.Stdout.Trim(), token);
                return ActionResult.Success(result.Stdout.Trim().Split('\n')[0]);
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }
}

/// <summary>
/// The end-to-end proof: start vLLM with the configured model, wait for the OpenAI API to
/// answer, do one completion round-trip, stop the server. Marker keyed to image + model.
/// </summary>
public sealed class VllmSmokeTestStep(VllmServerController server) : ISetupStep
{
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(15);

    public string Id => "vllm.smoke";
    public string Title => "vLLM end-to-end check";
    public IReadOnlyList<string> DependsOn => ["image.pull", "gpu.smoke"];

    public bool AppliesTo(SystemProfile profile) => true;

    private static string MarkerPath(StepContext ctx) => Path.Combine(ctx.Paths.SmokeDir, "vllm.ok");

    private static string MarkerContent(StepContext ctx)
        => $"{ImagePullStep.SelectImage(ctx)}|{ctx.Config.Server.Model}";

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var marker = MarkerPath(ctx);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == MarkerContent(ctx))
            return Task.FromResult(StepDetection.Ok(
                $"vLLM verified end-to-end with {ctx.Config.Server.Model}."));

        return Task.FromResult(StepDetection.Missing(
            $"vLLM has not been proven working with {ctx.Config.Server.Model} yet " +
            "(first run downloads the model into the distro's cache)."));
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        var model = ctx.Config.Server.Model;
        var action = new PlannedAction(
            ActionId: "vllm.smoke.run",
            StepId: Id,
            Kind: ActionKind.Execute,
            Description: $"Start vLLM with {model}, wait for the API, run one completion, stop the server",
            Detail: server.BuildRunCommand(ctx.Profile, model),
            InsideSlopworksRoot: false,
            Execute: async (exec, token) =>
            {
                var start = await server.StartAsync(exec.Processes, ctx.Profile, model, exec.Output, token);
                if (!start.Succeeded)
                    return ActionResult.Failure($"Server failed to start: {DistroBaseStep.Tail(start)}");

                try
                {
                    exec.Output.Report($"Waiting for {server.BaseUrl}/v1/models (model download can take a while)…");
                    var deadline = DateTimeOffset.UtcNow + StartupTimeout;
                    while (!await server.IsApiHealthyAsync(token))
                    {
                        if (DateTimeOffset.UtcNow > deadline)
                            return ActionResult.Failure(await DiagnoseStartupFailureAsync(exec, token));

                        var state = await server.GetHealthAsync(exec.Processes, token);
                        if (state.ContainerState is "exited" or "absent")
                            return ActionResult.Failure(await DiagnoseStartupFailureAsync(exec, token));

                        await Task.Delay(TimeSpan.FromSeconds(10), token);
                    }

                    exec.Output.Report("API is up; running a test completion…");
                    var completion = await server.CompleteAsync(model, "Hello from Slopworks! Reply with one word:", token);
                    exec.Output.Report(completion.Length > 400 ? completion[..400] + "…" : completion);

                    Directory.CreateDirectory(exec.Paths.SmokeDir);
                    await File.WriteAllTextAsync(MarkerPath(ctx), MarkerContent(ctx), token);
                    return ActionResult.Success("vLLM answered a completion request — the stack works end to end.");
                }
                finally
                {
                    exec.Output.Report("Stopping the smoke-test server…");
                    await server.StopAsync(exec.Processes, null, CancellationToken.None);
                }
            });

        return Task.FromResult<IReadOnlyList<PlannedAction>>([action]);
    }

    private async Task<string> DiagnoseStartupFailureAsync(ActionExecutionContext exec, CancellationToken ct)
    {
        // Grab a generous window — the real cause prints well above the downstream
        // "Engine core initialization failed" wrapper.
        var logs = await server.GetLogsAsync(exec.Processes, 400, ct);
        var text = logs.Stdout;

        // Persist the full log so the whole thing is readable later in the Logs tab.
        string? savedName = null;
        try
        {
            Directory.CreateDirectory(exec.Paths.VllmLogsDir);
            savedName = "smoke-failure.log";
            await File.WriteAllTextAsync(Path.Combine(exec.Paths.VllmLogsDir, savedName), text, ct);
        }
        catch (IOException)
        {
        }

        var hint = text switch
        {
            _ when text.Contains("UVA is not available", StringComparison.OrdinalIgnoreCase)
                => "vLLM force-disables pinned memory on WSL, so the V2 GPU runner fails. The fix is " +
                   "VLLM_WSL2_ENABLE_PIN_MEMORY=1, which Slopworks now sets automatically — update Slopworks " +
                   "and re-run setup (or add it under Settings → Extra container arguments).",
            _ when text.Contains("no kernel image is available", StringComparison.OrdinalIgnoreCase)
                || text.Contains("sm_120", StringComparison.OrdinalIgnoreCase)
                || text.Contains("kernel image", StringComparison.OrdinalIgnoreCase)
                => "The GPU is newer than the vLLM image's built-in CUDA kernels (common on RTX 50-series / Blackwell). " +
                   "Override Settings → GPU image with a newer or nightly build (CUDA 12.8+/Blackwell).",
            _ when text.Contains("401") || text.Contains("gated", StringComparison.OrdinalIgnoreCase)
                => "The model looks gated — set a HuggingFace token in Settings.",
            _ when text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                => "GPU ran out of memory — pick a smaller model or lower gpu-memory-utilization.",
            _ when text.Contains("CUDA error", StringComparison.OrdinalIgnoreCase)
                => "A CUDA error occurred — usually a driver/image mismatch; try a newer GPU image in Settings.",
            _ when text.Contains("No space left", StringComparison.OrdinalIgnoreCase)
                => "The distro disk is full — free space or move the Slopworks root to a bigger drive.",
            _ when text.Contains("/dev/shm", StringComparison.OrdinalIgnoreCase)
                || text.Contains("shared memory", StringComparison.OrdinalIgnoreCase)
                => "Shared memory looks too small for the engine — a WSL/container memory issue.",
            _ => "See the extracted error below.",
        };

        var where = savedName is null ? "" : $" Full log: Logs tab → vllm/{savedName}.";
        return $"vLLM did not become healthy. {hint}{where}\n--- error excerpt ---\n{ExtractRootCause(text)}";
    }

    /// <summary>Return the log window starting at the earliest error signature (the real cause).</summary>
    internal static string ExtractRootCause(string log, int window = 3000)
    {
        string[] markers =
        [
            "no kernel image is available", "CUDA error", "out of memory",
            "Traceback (most recent call last)", "Error:", "Exception", "raise ",
        ];

        var indices = markers
            .Select(m => log.IndexOf(m, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .ToList();

        if (indices.Count == 0)
            return log.Length <= window ? log.Trim() : "…" + log[^window..].Trim();

        var start = Math.Max(0, indices.Min() - 200);
        var slice = log[start..Math.Min(log.Length, start + window)];
        return (start > 0 ? "…" : "") + slice.Trim();
    }
}
