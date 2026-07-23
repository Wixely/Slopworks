namespace Slopworks.Core;

/// <summary>
/// Cleans up HuggingFace model ids people paste from Ollama or the HF model page, and flags
/// ids that won't serve well under vLLM.
/// </summary>
public static class ModelId
{
    /// <summary>
    /// Strips the hf.co/ (or huggingface.co/) host prefix and a leading scheme, which vLLM's
    /// HuggingFace validation rejects: "Repo id must be in the form namespace/repo_name".
    /// Leaves a plain "namespace/name" (or "namespace/name:tag") untouched otherwise.
    /// </summary>
    public static string Normalize(string? model)
    {
        var cleaned = (model ?? string.Empty).Trim();

        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (cleaned.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[scheme.Length..];
        }

        foreach (var host in new[] { "www.huggingface.co/", "www.hf.co/", "hf.co/", "huggingface.co/" })
        {
            if (cleaned.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[host.Length..];
                break;
            }
        }

        return cleaned;
    }

    /// <summary>A one-line caution when the id looks Ollama/GGUF-shaped, else null.</summary>
    public static string? Advisory(string? model)
    {
        var cleaned = Normalize(model);

        if (cleaned.Contains(':'))
            return "That ':tag' is an Ollama convention — vLLM uses plain HuggingFace ids. Remove it.";

        if (cleaned.Contains("GGUF", StringComparison.OrdinalIgnoreCase))
            return "GGUF is a llama.cpp/Ollama format; vLLM's support is experimental. Prefer an " +
                   "AWQ / GPTQ / FP8 safetensors repo instead.";

        if (cleaned.Contains("mlx", StringComparison.OrdinalIgnoreCase))
            return "MLX is Apple Silicon's format (mlx-lm) — vLLM can't read it and it won't use NVIDIA " +
                   "GPUs. Prefer an AWQ / GPTQ / FP8 safetensors repo instead.";

        return null;
    }
}
