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

    /// <summary>
    /// Directory the chat-template file is mounted from inside the container. On Windows the file is
    /// copied into the distro at start (the Windows data folder isn't visible to the container); on a
    /// Linux host the native templates dir is mounted directly.
    /// </summary>
    public string TemplatesContainerDir => OperatingSystem.IsWindows()
        ? "/opt/slopworks/templates"
        : paths.TemplatesDir;

    /// <summary>
    /// The chat-template file to use for a given model — the template attached to that model in the
    /// library (models.json) — if one is set AND the file exists on disk; otherwise null (so a model
    /// with no template, or a dangling reference, just runs with vLLM's built-in template).
    /// </summary>
    private string? SelectedTemplateFile(string model)
    {
        var wanted = ModelId.Normalize(model);
        if (wanted.Length == 0)
            return null;

        string? templateName;
        try
        {
            var library = new ModelLibraryStore(paths).Load();
            templateName = library.Models
                .FirstOrDefault(m => ModelId.Normalize(m.Id).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?.ChatTemplate;
        }
        catch (Exception)
        {
            return null; // a missing/corrupt library just means no template override
        }

        if (string.IsNullOrWhiteSpace(templateName))
            return null;
        var file = ProfileStore.Clean(templateName) + ".jinja";
        return File.Exists(Path.Combine(paths.TemplatesDir, file)) ? file : null;
    }

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

        // Mount the model's chat template (read-only) so --chat-template can point at it.
        if (SelectedTemplateFile(model) is not null)
            args.Add($"-v {TemplatesContainerDir}:{TemplatesContainerDir}:ro");

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

        // A managed flag is skipped when the user already supplied it via Extra vLLM arguments, so
        // we never emit a duplicate (which argparse rejects or resolves ambiguously).
        bool UserSet(string flag) => config.Server.ExtraArgs.Any(a =>
            a.StartsWith(flag, StringComparison.OrdinalIgnoreCase)
            || a.Contains(" " + flag, StringComparison.OrdinalIgnoreCase));

        // "auto" (or blank) lets vLLM detect quantization from the checkpoint; anything else forces it.
        if (config.Server.Quantization is { Length: > 0 } quant && !quant.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !UserSet("--quantization"))
            args.Add($"--quantization {quant}");

        // Context window. Unset = the model's own (often huge) maximum.
        if (config.Server.MaxModelLen is { } maxLen && maxLen > 0 && !UserSet("--max-model-len"))
            args.Add($"--max-model-len {maxLen}");

        // KV cache quantization — fp8 roughly halves KV-cache VRAM. "auto" keeps the model dtype.
        if (config.Server.KvCacheDtype is { Length: > 0 } kvDtype && !kvDtype.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !UserSet("--kv-cache-dtype"))
            args.Add($"--kv-cache-dtype {kvDtype}");

        // Weight/compute precision. "auto" (or blank) matches the checkpoint — the usual choice.
        if (config.Server.Dtype is { Length: > 0 } dtype && !dtype.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !UserSet("--dtype"))
            args.Add($"--dtype {dtype}");

        // Some architectures ship custom modeling code and won't load without this.
        if (config.Server.TrustRemoteCode && !UserSet("--trust-remote-code"))
            args.Add("--trust-remote-code");

        // Disable CUDA graphs: frees the VRAM they reserve; a documented long-context requirement on WSL2.
        if (config.Server.EnforceEager && !UserSet("--enforce-eager"))
            args.Add("--enforce-eager");

        // Concurrency cap. Lower it so one long-context request can claim more KV cache.
        if (config.Server.MaxNumSeqs is { } maxSeqs && maxSeqs > 0 && !UserSet("--max-num-seqs"))
            args.Add($"--max-num-seqs {maxSeqs}");

        // Chunked-prefill token budget per step.
        if (config.Server.MaxNumBatchedTokens is { } maxBatched && maxBatched > 0 && !UserSet("--max-num-batched-tokens"))
            args.Add($"--max-num-batched-tokens {maxBatched}");

        // Prefix caching: null leaves vLLM's own default; true/false force the matching flag.
        if (config.Server.EnablePrefixCaching is { } prefixCaching
            && !UserSet("--enable-prefix-caching") && !UserSet("--no-enable-prefix-caching"))
            args.Add(prefixCaching ? "--enable-prefix-caching" : "--no-enable-prefix-caching");

        // Advertise a short, stable id at /v1/models instead of the full repo path.
        if (config.Server.ServedModelName is { Length: > 0 } servedName && !UserSet("--served-model-name"))
            args.Add($"--served-model-name {servedName}");

        // A corrected chat template (mounted above) overriding the model's built-in prompt format.
        if (SelectedTemplateFile(model) is { } chatTemplate && !UserSet("--chat-template"))
            args.Add($"--chat-template {TemplatesContainerDir}/{chatTemplate}");

        if (profile.GpuPresent)
        {
            if (!UserSet("--gpu-memory-utilization"))
                args.Add($"--gpu-memory-utilization {config.Server.GpuMemoryUtilization:0.##}");
            if (config.Server.TensorParallelSize > 1)
            {
                if (!UserSet("--tensor-parallel-size"))
                    args.Add($"--tensor-parallel-size {config.Server.TensorParallelSize}");

                // The custom all-reduce uses CUDA IPC memory handles (open_mem_handle), which
                // WSL doesn't support — "invalid resource handle". Fall back to NCCL.
                if (OperatingSystem.IsWindows() && !UserSet("--disable-custom-all-reduce"))
                    args.Add("--disable-custom-all-reduce");
            }
        }

        // OpenAI tool / function calling — without this, tool_choice="auto" returns 400.
        if (config.Server.EnableToolCalling)
        {
            if (!UserSet("--enable-auto-tool-choice"))
                args.Add("--enable-auto-tool-choice");
            if (config.Server.ToolCallParser is { Length: > 0 } toolParser && !UserSet("--tool-call-parser"))
                args.Add($"--tool-call-parser {toolParser}");
        }

        args.AddRange(config.Server.ExtraArgs);

        return string.Join(" ", args);
    }

    public async Task<ProcessResult> StartAsync(
        IProcessRunner processes, SystemProfile profile, string model,
        IProgress<string>? output, CancellationToken ct)
    {
        var prep = new List<string> { $"mkdir -p {HfCachePath}" };

        // On Windows the container can't see the Windows data folder, so copy the model's template
        // into the distro first. base64 over the command line sidesteps all Jinja/shell escaping.
        if (OperatingSystem.IsWindows() && SelectedTemplateFile(model) is { } tplFile)
        {
            var content = await File.ReadAllTextAsync(Path.Combine(paths.TemplatesDir, tplFile), ct);
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
            prep.Add($"mkdir -p {TemplatesContainerDir}");
            prep.Add($"printf %s '{b64}' | base64 -d > {TemplatesContainerDir}/{tplFile}");
        }

        var command = $"{string.Join(" && ", prep)} && {BuildRunCommand(profile, model)}";
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
