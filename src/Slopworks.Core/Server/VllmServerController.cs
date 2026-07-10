using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Server;

public sealed record ServerHealth(string ContainerState, bool ApiHealthy);

/// <summary>
/// Lifecycle of the vLLM container. Methods take the IProcessRunner to use so callers keep
/// their own gating/attribution: wizard actions pass the gated runner, direct UI buttons
/// pass an audited one (the click is the consent).
/// </summary>
public sealed class VllmServerController(ILinuxCommandFactory linux, SlopworksConfig config, HttpClient http)
{
    public const string ContainerName = "slopworks-vllm";
    public const string HfCachePath = "/opt/slopworks/hf";

    public string BaseUrl => $"http://localhost:{config.Server.Port}";

    public string SelectImage(SystemProfile profile) => profile.GpuPresent ? config.Images.Gpu : config.Images.Cpu;

    /// <summary>The full podman command — also shown verbatim on confirmation cards.</summary>
    public string BuildRunCommand(SystemProfile profile, string model)
    {
        var image = SelectImage(profile);
        var args = new List<string>
        {
            "podman", "run", "-d", "--replace", $"--name {ContainerName}",
            $"-p {config.Server.Port}:8000",
            $"-v {HfCachePath}:/root/.cache/huggingface",
        };

        if (config.Server.HfToken is { Length: > 0 })
            args.Add("-e HUGGING_FACE_HUB_TOKEN=\"$HF_TOKEN\""); // injected via env, kept out of the visible command

        if (profile.GpuPresent)
            args.AddRange(["--device nvidia.com/gpu=all", "--ipc=host"]);
        else
            args.Add("-e VLLM_CPU_KVCACHE_SPACE=8");

        args.Add(image);
        args.Add($"--model {model}");
        if (profile.GpuPresent)
            args.Add($"--gpu-memory-utilization {config.Server.GpuMemoryUtilization:0.##}");
        args.AddRange(config.Server.ExtraArgs);

        return string.Join(" ", args);
    }

    public async Task<ProcessResult> StartAsync(
        IProcessRunner processes, SystemProfile profile, string model,
        IProgress<string>? output, CancellationToken ct)
    {
        var command = $"mkdir -p {HfCachePath} && {BuildRunCommand(profile, model)}";
        var spec = linux.Command(command);
        if (config.Server.HfToken is { Length: > 0 } token)
        {
            // WSLENV forwards the variable across the Windows→Linux boundary.
            spec = spec with
            {
                Env = new Dictionary<string, string> { ["HF_TOKEN"] = token, ["WSLENV"] = "HF_TOKEN" },
            };
        }

        return await processes.RunAsync(spec, output, ct);
    }

    public Task<ProcessResult> StopAsync(IProcessRunner processes, IProgress<string>? output, CancellationToken ct)
        => processes.RunAsync(linux.Command($"podman rm -f {ContainerName} 2>/dev/null || true"), output, ct);

    public Task<ProcessResult> GetLogsAsync(IProcessRunner processes, int tailLines, CancellationToken ct)
        => processes.RunAsync(linux.Command($"podman logs --tail {tailLines} {ContainerName} 2>&1 || true"), null, ct);

    public async Task<ServerHealth> GetHealthAsync(IProcessRunner processes, CancellationToken ct)
    {
        var state = await processes.RunAsync(
            linux.Command($"podman inspect --format '{{{{.State.Status}}}}' {ContainerName} 2>/dev/null || echo absent"),
            null, ct);

        var containerState = state.Succeeded && state.Stdout.Trim().Length > 0 ? state.Stdout.Trim() : "unknown";
        return new ServerHealth(containerState, await IsApiHealthyAsync(ct));
    }

    public async Task<bool> IsApiHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync($"{BaseUrl}/v1/models", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>One round-trip completion, used by the smoke test and the test panel.</summary>
    public async Task<string> CompleteAsync(string model, string prompt, CancellationToken ct)
    {
        using var response = await http.PostAsync($"{BaseUrl}/v1/completions",
            new StringContent(
                $$"""{"model": {{System.Text.Json.JsonSerializer.Serialize(model)}}, "prompt": {{System.Text.Json.JsonSerializer.Serialize(prompt)}}, "max_tokens": 16}""",
                System.Text.Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
