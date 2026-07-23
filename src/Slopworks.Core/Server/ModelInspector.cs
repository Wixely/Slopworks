using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Slopworks.Core.Config;

namespace Slopworks.Core.Server;

/// <summary>How confidently vLLM can serve a model, worst-to-best colour-coded in the UI.</summary>
public enum ModelVerdict
{
    /// <summary>Couldn't decide (network error, private repo, empty id).</summary>
    Unknown,

    /// <summary>vLLM cannot load this (GGUF-only, MLX, missing config/weights, nonexistent).</summary>
    Unservable,

    /// <summary>Probably loadable but with a caveat (unusual quant, hybrid arch, full precision).</summary>
    Caution,

    /// <summary>A HuggingFace safetensors checkpoint vLLM should serve.</summary>
    Servable,
}

/// <summary>The verdict plus a one-line summary, a fuller explanation, and structured metadata.</summary>
public sealed record ModelInspection(ModelVerdict Verdict, string Summary, string Detail)
{
    /// <summary>Quantization label: awq / gptq / gguf / mlx / none / … (null when unknown).</summary>
    public string? Quant { get; init; }
    public string? Architecture { get; init; }

    /// <summary>Total parameter count from the HF safetensors metadata, when published.</summary>
    public long? Parameters { get; init; }

    /// <summary>Max context length (config.json max_position_embeddings) — ties to the Context size setting.</summary>
    public int? MaxContext { get; init; }

    /// <summary>Weight dtype (config.json torch_dtype), e.g. bfloat16.</summary>
    public string? Dtype { get; init; }

    /// <summary>HF task, e.g. text-generation or image-text-to-text (multimodal).</summary>
    public string? Pipeline { get; init; }

    public string? License { get; init; }

    /// <summary>Whether the repo is gated (needs terms acceptance + a token).</summary>
    public bool Gated { get; init; }

    public long? Downloads { get; init; }

    /// <summary>Human parameter size, e.g. "32.8B" (or "—" when unknown).</summary>
    public string ParametersText => FormatCount(Parameters);

    public static string FormatCount(long? value) => value is { } p and > 0
        ? p >= 1_000_000_000 ? $"{p / 1e9:0.#}B" : p >= 1_000_000 ? $"{p / 1e6:0.#}M" : p.ToString("N0")
        : "—";
}

/// <summary>Raw facts gathered about a repo — the input to the (pure, testable) classifier.</summary>
public sealed record ModelProbe(
    bool Found,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Tags,
    string? ConfigJson,
    long? Parameters = null,
    string? Pipeline = null,
    string? License = null,
    bool Gated = false,
    long? Downloads = null);

/// <summary>The interesting fields from a HuggingFace /api/models/{id} response.</summary>
public sealed record HfApiMeta(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Tags,
    long? Parameters,
    string? Pipeline,
    string? License,
    bool Gated,
    long? Downloads);

/// <summary>
/// Pure decision logic: given a repo's files/tags/config.json, decide whether vLLM can serve it.
/// Separated from the HTTP so it can be unit-tested with fixture strings.
/// </summary>
public static class ModelConfigClassifier
{
    private const StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    // Quantization methods vLLM understands (via quantization_config.quant_method).
    private static readonly string[] KnownQuantMethods =
    [
        "awq", "awq_marlin", "gptq", "gptq_marlin", "marlin", "compressed-tensors", "compressed_tensors",
        "fp8", "modelopt", "modelopt_fp4", "nvfp4", "bitsandbytes", "experts_int8",
    ];

    public static ModelInspection Classify(string id, ModelProbe probe)
    {
        if (!probe.Found)
            return new(ModelVerdict.Unservable, "Repository not found",
                $"HuggingFace has no repo '{id}'. Check the id is 'namespace/name' (not an Ollama or URL form). " +
                "If it's private or gated, set a HuggingFace token and re-check.");

        var hasSafetensors = probe.Files.Any(f => f.EndsWith(".safetensors", Ic));
        var hasGguf = probe.Files.Any(f => f.EndsWith(".gguf", Ic));
        var hasConfig = probe.ConfigJson is not null || probe.Files.Any(f => f.Equals("config.json", Ic));
        var taggedMlx = probe.Tags.Any(t => t.Contains("mlx", Ic));
        var hasMmproj = probe.Files.Any(f => f.Contains("mmproj", Ic));

        var (quant, arch, modelType, hybrid, maxContext, dtype) = probe.ConfigJson is { } cfg
            ? ReadConfig(cfg)
            : (null, null, null, false, null, null);

        // Attach the shared metadata (arch, params, context, license, …) to every verdict.
        ModelInspection Make(ModelVerdict v, string summary, string detail, string? quantLabel) =>
            new(v, summary, detail)
            {
                Quant = quantLabel,
                Architecture = arch,
                Parameters = probe.Parameters,
                MaxContext = maxContext,
                Dtype = dtype,
                Pipeline = probe.Pipeline,
                License = probe.License,
                Gated = probe.Gated,
                Downloads = probe.Downloads,
            };

        // GGUF-only → llama.cpp/Ollama territory, no config.json, vLLM can't load it.
        if (hasGguf && !hasSafetensors)
            return Make(ModelVerdict.Unservable, "GGUF-only — vLLM can't serve this",
                "The repo ships .gguf files (llama.cpp/Ollama format) and no safetensors, so vLLM has no " +
                "config.json/checkpoint to load. Run it in Ollama or LM Studio, or find an AWQ / GPTQ / FP8 " +
                "safetensors build for vLLM." + (hasMmproj ? " (The mmproj file is llama.cpp's vision projector.)" : ""),
                "gguf");

        if (!hasSafetensors && !hasConfig)
            return Make(ModelVerdict.Unservable, "No safetensors / config.json",
                "vLLM needs a HuggingFace-format checkpoint (config.json + .safetensors); this repo has neither.", null);

        // MLX (Apple Silicon) — safetensors but a quantization scheme vLLM can't read.
        if (taggedMlx || (probe.ConfigJson is { } mlxCfg && LooksLikeMlx(mlxCfg)))
            return Make(ModelVerdict.Unservable, "MLX (Apple Silicon) — not vLLM",
                "This is an mlx-lm model quantized for Apple Silicon. vLLM can't read MLX quantization and it " +
                "won't use NVIDIA GPUs. Use an AWQ / GPTQ / FP8 safetensors build instead.", "mlx");

        var archNote = arch is null ? "" : $" Architecture: {arch}{(modelType is null ? "" : $" ({modelType})")}.";
        var hybridNote = hybrid
            ? " Note: this looks like a hybrid linear-attention/SSM model — vLLM support for those is " +
              "model-specific, so confirm your vLLM image lists this architecture."
            : "";

        if (quant is { Length: > 0 })
        {
            var supported = KnownQuantMethods.Contains(quant, StringComparer.OrdinalIgnoreCase);
            return Make(supported && !hybrid ? ModelVerdict.Servable : ModelVerdict.Caution,
                supported ? $"Servable — {quant} safetensors" : $"Quantization '{quant}' — verify vLLM support",
                $"Safetensors checkpoint, quantization method '{quant}'." +
                (supported ? " vLLM supports this method." : " That isn't a method vLLM commonly supports — check before serving.") +
                archNote + hybridNote,
                quant);
        }

        return Make(hybrid ? ModelVerdict.Caution : ModelVerdict.Servable, "Servable — full-precision safetensors",
            "Full-precision safetensors (no quantization_config). It'll load but needs the most VRAM — consider a " +
            "pre-quantized AWQ/GPTQ build, or set Quantization = bitsandbytes to compress it on the fly." + archNote + hybridNote,
            "none");
    }

    /// <summary>Read a JSON element as a long, tolerating integers, floats (e.g. 32768.0) and quoted strings.</summary>
    internal static long? ReadInt64(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var l)) return l;
                if (e.TryGetDouble(out var d) && d is > 0 and < 9.2e18) return (long)d;
                return null;
            case JsonValueKind.String:
                return long.TryParse(e.GetString(), out var s) ? s : null;
            default:
                return null;
        }
    }

    /// <summary>Read a positive Int32 (for context length), tolerating floats/strings.</summary>
    internal static int? ReadPositiveInt32(JsonElement e)
        => ReadInt64(e) is >= 1 and <= int.MaxValue and { } v ? (int)v : null;

    /// <summary>MLX writes a top-level "quantization" object (not "quantization_config") with group_size/bits.</summary>
    internal static bool LooksLikeMlx(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.TryGetProperty("quantization", out var q)
                && q.ValueKind == JsonValueKind.Object
                && (q.TryGetProperty("group_size", out _) || q.TryGetProperty("mode", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static (string? Quant, string? Arch, string? ModelType, bool Hybrid, int? MaxContext, string? Dtype) ReadConfig(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;

            string? quant = null;
            if (root.TryGetProperty("quantization_config", out var qc) && qc.ValueKind == JsonValueKind.Object
                && qc.TryGetProperty("quant_method", out var qm) && qm.ValueKind == JsonValueKind.String)
                quant = qm.GetString();

            string? arch = null;
            if (root.TryGetProperty("architectures", out var arr) && arr.ValueKind == JsonValueKind.Array
                && arr.GetArrayLength() > 0)
                arch = arr[0].GetString();

            string? modelType = null;
            if (root.TryGetProperty("model_type", out var mt) && mt.ValueKind == JsonValueKind.String)
                modelType = mt.GetString();

            int? maxContext = null;
            if (root.TryGetProperty("max_position_embeddings", out var mpe))
                maxContext = ReadPositiveInt32(mpe);

            string? dtype = null;
            if (root.TryGetProperty("torch_dtype", out var dt) && dt.ValueKind == JsonValueKind.String)
                dtype = dt.GetString();

            var hybrid = configJson.Contains("mamba", Ic) || configJson.Contains("linear_attention", Ic);
            return (quant, arch, modelType, hybrid, maxContext, dtype);
        }
        catch (JsonException)
        {
            return (null, null, null, false, null, null);
        }
    }
}

/// <summary>
/// Fetches a HuggingFace repo's file list and config.json and asks <see cref="ModelConfigClassifier"/>
/// whether vLLM can serve it — so the user learns "GGUF-only, use Ollama" before downloading 30 GB.
/// </summary>
public sealed class ModelInspector(HttpClient http, SlopworksConfig config)
{
    public async Task<ModelInspection> InspectAsync(string modelId, CancellationToken ct)
    {
        var id = ModelId.Normalize(modelId).Trim().Trim('/');
        if (id.Length == 0)
            return new(ModelVerdict.Unknown, "Enter a model id",
                "Type a HuggingFace repo id first, e.g. Qwen/Qwen2.5-7B-Instruct-AWQ.");
        if (id.Contains(':'))
            return new(ModelVerdict.Unservable, "Ollama-style tag",
                $"'{id}' has a ':tag' (an Ollama convention). Use the bare HuggingFace repo id — drop the ':' and everything after it.");

        var baseUrl = (string.IsNullOrWhiteSpace(config.Server.HuggingFaceEndpoint)
            ? "https://huggingface.co"
            : config.Server.HuggingFaceEndpoint).TrimEnd('/');

        // The shared client has no timeout (built for large downloads); a metadata check must not hang.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ct = timeout.Token;

        HttpResponseMessage api;
        try
        {
            api = await SendAsync($"{baseUrl}/api/models/{id}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(ModelVerdict.Unknown, "Couldn't reach HuggingFace",
                $"Network error contacting {baseUrl}: {TextUtil.Condense(ex.Message, 160)}. Check your connection or proxy.");
        }

        using (api)
        {
            if (api.StatusCode == HttpStatusCode.NotFound)
                return ModelConfigClassifier.Classify(id, new ModelProbe(false, [], [], null));
            if (api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(ModelVerdict.Unknown, "Private or gated repo",
                    "HuggingFace refused access. If the model is gated, accept its terms on the model page and set a " +
                    "HuggingFace token in Settings, then re-check.");
            if (!api.IsSuccessStatusCode)
                return new(ModelVerdict.Unknown, $"HuggingFace returned {(int)api.StatusCode}",
                    "Couldn't read the repo metadata — try again shortly.");

            var meta = ParseModelApi(await api.Content.ReadAsStringAsync(ct));

            string? configJson = null;
            if (meta.Files.Any(f => f.Equals("config.json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var cfg = await SendAsync($"{baseUrl}/{id}/resolve/main/config.json", ct);
                    if (cfg.IsSuccessStatusCode)
                        configJson = await cfg.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // config.json is a nice-to-have; classify off the file list if it can't be fetched.
                }
            }

            return ModelConfigClassifier.Classify(id, new ModelProbe(
                true, meta.Files, meta.Tags, configJson, meta.Parameters, meta.Pipeline, meta.License, meta.Gated, meta.Downloads));
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (config.Server.HfToken is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Extract the interesting fields from a HuggingFace /api/models/{id} response.</summary>
    public static HfApiMeta ParseModelApi(string json)
    {
        var files = new List<string>();
        var tags = new List<string>();
        long? parameters = null, downloads = null;
        string? pipeline = null, license = null;
        var gated = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
                foreach (var s in siblings.EnumerateArray())
                    if (s.TryGetProperty("rfilename", out var f) && f.GetString() is { } name)
                        files.Add(name);

            if (root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var tag in t.EnumerateArray())
                    if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { } value)
                    {
                        tags.Add(value);
                        if (value.StartsWith("license:", StringComparison.OrdinalIgnoreCase))
                            license = value["license:".Length..];
                    }

            // HF publishes a total param count for safetensors models under "safetensors.total".
            if (root.TryGetProperty("safetensors", out var st) && st.ValueKind == JsonValueKind.Object
                && st.TryGetProperty("total", out var total)
                && ModelConfigClassifier.ReadInt64(total) is > 0 and { } count)
                parameters = count;

            if (root.TryGetProperty("pipeline_tag", out var pt) && pt.ValueKind == JsonValueKind.String)
                pipeline = pt.GetString();

            if (root.TryGetProperty("downloads", out var dl))
                downloads = ModelConfigClassifier.ReadInt64(dl);

            // "gated" is false, "auto", or "manual" — any non-false value means access is gated.
            if (root.TryGetProperty("gated", out var g))
                gated = g.ValueKind == JsonValueKind.True
                    || (g.ValueKind == JsonValueKind.String && !string.Equals(g.GetString(), "false", StringComparison.OrdinalIgnoreCase));

            // cardData.license wins over a license:* tag when present.
            if (root.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object
                && card.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.String)
                license = lic.GetString();
        }
        catch (JsonException)
        {
            // A malformed body just yields an empty probe → "no safetensors/config" verdict.
        }

        return new HfApiMeta(files, tags, parameters, pipeline, license, gated, downloads);
    }
}
