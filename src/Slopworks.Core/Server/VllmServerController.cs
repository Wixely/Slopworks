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
public sealed class VllmServerController(ILinuxCommandFactory linux, SlopworksConfig config, HttpClient http, SlopworksPaths paths)
{
    public const string ContainerName = "slopworks-vllm";

    /// <summary>
    /// Model cache location: inside the vhdx on Windows (9p mounts are slow); directly in
    /// the Slopworks data root on a Linux host (native filesystem, one-directory principle).
    /// </summary>
    public string HfCachePath => OperatingSystem.IsWindows()
        ? "/opt/slopworks/hf"
        : Path.Combine(paths.Root, "hf");

    public string BaseUrl => $"http://localhost:{config.Server.Port}";

    public string SelectImage(SystemProfile profile) => profile.GpuPresent ? config.Images.Gpu : config.Images.Cpu;

    /// <summary>The full podman command — also shown verbatim on confirmation cards.</summary>
    public string BuildRunCommand(SystemProfile profile, string model)
    {
        // People paste ids straight from Ollama ("hf.co/org/repo") or the HF page; vLLM's
        // HuggingFace validation rejects the host prefix. Strip it so the id actually loads.
        model = ModelId.Normalize(model);

        var image = SelectImage(profile);

        // Windows: WSL NAT already confines reachability (host exposure is the portproxy's
        // job). Linux host: the publish address IS the exposure control.
        var publish = OperatingSystem.IsWindows()
            ? $"-p {config.Server.Port}:8000"
            : config.Server.ExposeToNetwork
                ? $"-p 0.0.0.0:{config.Server.Port}:8000"
                : $"-p 127.0.0.1:{config.Server.Port}:8000";

        var args = new List<string>
        {
            "podman", "run", "-d", "--replace", $"--name {ContainerName}",
            publish,
            $"-v {HfCachePath}:/root/.cache/huggingface",
        };

        if (config.Server.HfToken is { Length: > 0 })
            args.Add("-e HUGGING_FACE_HUB_TOKEN=\"$HF_TOKEN\""); // injected via env, kept out of the visible command

        if (config.Network.Proxy is { Length: > 0 } proxy)
        {
            // Model downloads happen inside the container; give it the same proxy.
            args.Add($"-e HTTP_PROXY={proxy} -e HTTPS_PROXY={proxy} -e http_proxy={proxy} -e https_proxy={proxy}");
            args.Add("-e NO_PROXY=localhost,127.0.0.1");
        }

        if (config.Server.VllmLogLevel is { Length: > 0 } logLevel)
            args.Add($"-e VLLM_LOGGING_LEVEL={logLevel}");

        if (profile.GpuPresent)
        {
            args.AddRange(["--device nvidia.com/gpu=all", "--ipc=host"]);

            // vLLM force-disables pinned memory when it detects WSL, which makes the V2 GPU
            // model runner fail with "UVA is not available". This is the sanctioned opt-in.
            if (OperatingSystem.IsWindows())
                args.Add("-e VLLM_WSL2_ENABLE_PIN_MEMORY=1");

            // Make CUDA index by PCI bus order (or the chosen order) so indices are stable
            // and match nvidia-smi — silences the mixed-GPU ordering warning.
            if (config.Server.CudaDeviceOrder is { Length: > 0 } order)
                args.Add($"-e CUDA_DEVICE_ORDER={order}");

            // Restrict which GPUs vLLM sees (all are exposed to the container via CDI).
            if (config.Server.VisibleGpus is { Length: > 0 } gpus)
                args.Add($"-e CUDA_VISIBLE_DEVICES={gpus}");

            // Multi-GPU under WSL: PCIe peer-to-peer isn't supported, so NCCL's P2P transport
            // fails — unless an NVLink bridge is present, which does work and is much faster.
            // Auto: disable P2P only without NVLink. Disabling P2P also disables NVLink.
            if (OperatingSystem.IsWindows() && config.Server.TensorParallelSize > 1
                && (config.Server.DisableGpuP2P ?? !profile.HasNvLink))
                args.Add("-e NCCL_P2P_DISABLE=1");
        }
        else
        {
            args.Add("-e VLLM_CPU_KVCACHE_SPACE=8");
        }

        args.AddRange(config.Server.ExtraContainerArgs);
        args.Add(image);
        args.Add($"--model {model}");

        // "auto" (or blank) lets vLLM detect quantization from the checkpoint; anything else forces it.
        if (config.Server.Quantization is { Length: > 0 } quant && !quant.Equals("auto", StringComparison.OrdinalIgnoreCase))
            args.Add($"--quantization {quant}");

        // Context window. Unset = the model's own (often huge) maximum. Skip if the user already
        // supplied it via extra args so we never pass a duplicate flag.
        if (config.Server.MaxModelLen is { } maxLen && maxLen > 0
            && !config.Server.ExtraArgs.Any(a => a.Contains("--max-model-len", StringComparison.OrdinalIgnoreCase)))
            args.Add($"--max-model-len {maxLen}");

        // KV cache quantization — fp8 roughly halves KV-cache VRAM. "auto" keeps the model dtype.
        if (config.Server.KvCacheDtype is { Length: > 0 } kvDtype && !kvDtype.Equals("auto", StringComparison.OrdinalIgnoreCase))
            args.Add($"--kv-cache-dtype {kvDtype}");

        if (profile.GpuPresent)
        {
            args.Add($"--gpu-memory-utilization {config.Server.GpuMemoryUtilization:0.##}");
            if (config.Server.TensorParallelSize > 1)
            {
                args.Add($"--tensor-parallel-size {config.Server.TensorParallelSize}");

                // The custom all-reduce uses CUDA IPC memory handles (open_mem_handle), which
                // WSL doesn't support — "invalid resource handle". Fall back to NCCL.
                if (OperatingSystem.IsWindows())
                    args.Add("--disable-custom-all-reduce");
            }
        }

        // OpenAI tool / function calling — without this, tool_choice="auto" returns 400.
        if (config.Server.EnableToolCalling)
        {
            args.Add("--enable-auto-tool-choice");
            if (config.Server.ToolCallParser is { Length: > 0 } toolParser)
                args.Add($"--tool-call-parser {toolParser}");
        }

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

    public const string ServerLogFileName = "server.log";

    /// <summary>
    /// Fetches the container log and, when there's real output, writes it to
    /// logs/vllm/server.log so the file-based Logs tab always has the vLLM output — not just
    /// after a failed smoke test. Returns the log text for a live view. No-op if the
    /// container is gone (so the last saved log is preserved).
    /// </summary>
    public async Task<string> SnapshotLogsAsync(IProcessRunner processes, int tailLines, CancellationToken ct)
    {
        var result = await GetLogsAsync(processes, tailLines, ct);
        var text = result.Stdout;

        var containerMissing = text.Contains("no container", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no such container", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(text) && !containerMissing)
        {
            try
            {
                Directory.CreateDirectory(paths.VllmLogsDir);
                await File.WriteAllTextAsync(Path.Combine(paths.VllmLogsDir, ServerLogFileName), text, ct);
            }
            catch (IOException)
            {
            }
        }

        return containerMissing ? "" : text;
    }

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

    /// <summary>Model ids the server currently reports at /v1/models; empty if unreachable.</summary>
    public async Task<IReadOnlyList<string>> GetServedModelsAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync($"{BaseUrl}/v1/models", timeout.Token);
            if (!response.IsSuccessStatusCode)
                return [];

            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];

            var ids = new List<string>();
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.GetString() is { } value)
                    ids.Add(value);
            }

            return ids;
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
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
