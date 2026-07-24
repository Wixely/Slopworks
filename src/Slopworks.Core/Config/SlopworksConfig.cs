namespace Slopworks.Core.Config;

public sealed class SlopworksConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>"safe" (confirm every action) or "auto" (fully unattended).</summary>
    public string Mode { get; set; } = "safe";

    /// <summary>In safe mode, skip prompts for file writes/deletes confined to the Slopworks root.</summary>
    public bool AutoApproveInsideRoot { get; set; } = true;

    /// <summary>
    /// Bypassed opinionated checks (e.g. "preflight.disk"). Slopworks doesn't know what model
    /// you'll run — resource-headroom checks are advice, not requirements. Technical blockers
    /// (OS too old, virtualization off) are never bypassable.
    /// </summary>
    public List<string> Bypasses { get; set; } = [];

    /// <summary>
    /// Forced checks (e.g. "gpu.driver"): the user overriding a passing heuristic they know
    /// is wrong — "you say I have no NVIDIA card, but I do". The mirror of Bypasses.
    /// </summary>
    public List<string> Forces { get; set; } = [];

    /// <summary>
    /// Which platform (container images + distro source) this profile uses. Empty = the default
    /// platform. Images/Distro below are the resolved values for that platform (see PlatformManager).
    /// </summary>
    public string Platform { get; set; } = "";

    public ServerConfig Server { get; set; } = new();
    public ImagesConfig Images { get; set; } = new();
    public DistroConfig Distro { get; set; } = new();

    /// <summary>Per-artifact download sources; every URL overridable, GitHub sources auto-resolved to latest.</summary>
    public Dictionary<string, ArtifactSource> Artifacts { get; set; } = DefaultArtifacts();

    public AptRepoOverrides AptRepos { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();

    public bool IsAutoMode => string.Equals(Mode, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Overwrite this instance's contents from another (used to switch the active profile in place,
    /// so every holder of the shared config reference sees the new values). Add new top-level
    /// sections here if you introduce any.
    /// </summary>
    public void CopyFrom(SlopworksConfig other)
    {
        SchemaVersion = other.SchemaVersion;
        Mode = other.Mode;
        AutoApproveInsideRoot = other.AutoApproveInsideRoot;
        Bypasses = other.Bypasses;
        Forces = other.Forces;
        Platform = other.Platform;
        Server = other.Server;
        Images = other.Images;
        Distro = other.Distro;
        Artifacts = other.Artifacts;
        AptRepos = other.AptRepos;
        Network = other.Network;
    }

    public static Dictionary<string, ArtifactSource> DefaultArtifacts() => new()
    {
        ["rootfs"] = new ArtifactSource
        {
            Url = "https://cloudimages.ubuntu.com/wsl/noble/current/ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz",
            ChecksumUrl = "https://cloudimages.ubuntu.com/wsl/noble/current/SHA256SUMS",
        },
    };
}

public sealed class ServerConfig
{
    public int Port { get; set; } = 8000;

    /// <summary>
    /// When true, the vLLM port is reachable from other machines on the LAN (portproxy on
    /// 0.0.0.0 + a firewall allow rule for this one port). Off by default; the API has no
    /// authentication, so only enable on trusted networks.
    /// </summary>
    public bool ExposeToNetwork { get; set; }
    public string Model { get; set; } = "Qwen/Qwen2.5-0.5B-Instruct";

    /// <summary>Extra vLLM arguments, appended after the model (e.g. --max-model-len 8192).</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>Extra podman arguments, inserted before the image (e.g. --memory 24g, -v mounts).</summary>
    public List<string> ExtraContainerArgs { get; set; } = [];
    public string? HfToken { get; set; }
    public double GpuMemoryUtilization { get; set; } = 0.90;

    /// <summary>
    /// Context window (vLLM --max-model-len). null = use the model's own maximum, which can be
    /// very large (e.g. 262144) and demands a big KV cache. Lower it to cut VRAM and fit a model.
    /// </summary>
    public int? MaxModelLen { get; set; }

    /// <summary>
    /// KV cache quantization (vLLM --kv-cache-dtype). "auto" = the model dtype (fp16/bf16).
    /// "fp8" (e4m3) roughly halves KV-cache VRAM so longer context / bigger models fit;
    /// "fp8_e5m2" trades precision for range. The other main VRAM lever beside MaxModelLen.
    /// </summary>
    public string KvCacheDtype { get; set; } = "auto";

    /// <summary>
    /// HuggingFace host the model checker queries for repo metadata (files, tags, config.json).
    /// Overridable for a mirror/proxy; the container still downloads weights via its own HF settings.
    /// </summary>
    public string HuggingFaceEndpoint { get; set; } = "https://huggingface.co";

    /// <summary>
    /// vLLM logging level (VLLM_LOGGING_LEVEL): DEBUG (most verbose — shows request detail,
    /// useful for diagnosing 400s), INFO (default), WARNING, ERROR.
    /// </summary>
    public string VllmLogLevel { get; set; } = "INFO";

    /// <summary>
    /// vLLM --quantization method. "auto" (or blank) detects it from the checkpoint / runs
    /// full precision. Force one for weight compression: awq / gptq (pre-quantized 4-bit,
    /// Ampere-ok), nvfp4 / modelopt_fp4 / fp8 (Blackwell/Hopper), bitsandbytes (on-the-fly).
    /// </summary>
    public string Quantization { get; set; } = "auto";

    /// <summary>
    /// Enable OpenAI tool / function calling (--enable-auto-tool-choice). On by default;
    /// agents that send tools with tool_choice="auto" need it, else vLLM returns 400.
    /// </summary>
    public bool EnableToolCalling { get; set; } = true;

    /// <summary>
    /// vLLM --tool-call-parser, which must match the model's tool-call format. Common values:
    /// hermes (Qwen/Hermes), llama3_json, mistral, pythonic. Only used when tool calling is on.
    /// </summary>
    public string ToolCallParser { get; set; } = "hermes";

    /// <summary>
    /// Split the model across this many GPUs (vLLM --tensor-parallel-size). 1 = single GPU.
    /// Must divide the model's attention-head count, and the cards should match (same arch
    /// and memory) — mixed GPUs are unreliable.
    /// </summary>
    public int TensorParallelSize { get; set; } = 1;

    /// <summary>
    /// Which GPUs vLLM may use, as CUDA_VISIBLE_DEVICES (e.g. "0" or "1,2"). Blank = all.
    /// Use this to pick a specific card or restrict tensor parallelism to matching GPUs.
    /// </summary>
    public string? VisibleGpus { get; set; }

    /// <summary>
    /// CUDA_DEVICE_ORDER: "PCI_BUS_ID" makes CUDA indices follow PCI bus order (stable, and
    /// what nvidia-smi shows); "FASTEST_FIRST" is CUDA's default. Blank = leave unset.
    /// Recommended PCI_BUS_ID on mixed-GPU machines so device indices are unambiguous.
    /// </summary>
    public string? CudaDeviceOrder { get; set; }

    /// <summary>
    /// Force NCCL peer-to-peer off (NCCL_P2P_DISABLE=1) for multi-GPU. null = automatic:
    /// disabled when no NVLink is detected (WSL doesn't support PCIe peer-to-peer), left on
    /// when NVLink is present so the bridge is used. Disabling P2P also disables NVLink.
    /// </summary>
    public bool? DisableGpuP2P { get; set; }

    /// <summary>
    /// vLLM --dtype: weight/compute precision. "auto" (or blank) matches the checkpoint's own dtype
    /// and is what you want almost always. "bfloat16"/"float16" force half precision; "float32" is
    /// rarely wanted (doubles VRAM). Applies on CPU and GPU.
    /// </summary>
    public string Dtype { get; set; } = "auto";

    /// <summary>
    /// vLLM --trust-remote-code: allow the model repo to run its own modeling code. Required by some
    /// architectures that ship custom Python. Off by default; only enable for repos you trust — it
    /// executes code straight from the repo.
    /// </summary>
    public bool TrustRemoteCode { get; set; }

    /// <summary>
    /// vLLM --enforce-eager: run fully eager — turns off BOTH torch.compile and CUDA graphs. Frees the
    /// VRAM the graphs reserve (a known long-context requirement under WSL2) and is the most compatible,
    /// but the slowest. For just the CUDA-graph part while keeping torch.compile, use
    /// <see cref="CudaGraphModeNone"/> instead.
    /// </summary>
    public bool EnforceEager { get; set; }

    /// <summary>
    /// vLLM -cc.cudagraph_mode=NONE: disable CUDA-graph capture ONLY, while keeping torch.compile and
    /// its kernel optimizations. A lighter alternative to <see cref="EnforceEager"/> — it frees the same
    /// CUDA-graph VRAM and sidesteps graph-capture problems, but stays faster than full eager because
    /// compilation still runs. Off by default.
    /// </summary>
    public bool CudaGraphModeNone { get; set; }

    /// <summary>
    /// vLLM --max-num-seqs: cap on sequences batched concurrently. null = vLLM's default (256).
    /// Lower it (e.g. 1–2) so a single long-context request can claim more of the KV cache; it's a
    /// cap, not a reservation, so short requests are unaffected.
    /// </summary>
    public int? MaxNumSeqs { get; set; }

    /// <summary>
    /// vLLM --max-num-batched-tokens: chunked-prefill token budget per step. null = vLLM's default.
    /// Lower it to soften prefill/decode starvation when long prompts and active generations share
    /// the GPU; raise it to speed bulk prefill. Advanced.
    /// </summary>
    public int? MaxNumBatchedTokens { get; set; }

    /// <summary>
    /// vLLM prefix caching. null = leave vLLM's default (on in current versions); true/false force
    /// --enable-prefix-caching / --no-enable-prefix-caching. Caching shared prompt prefixes is a big
    /// win for agents that reuse a system prompt; force off only to isolate a caching issue. Advanced.
    /// </summary>
    public bool? EnablePrefixCaching { get; set; }

    /// <summary>
    /// vLLM --served-model-name: the id the API advertises at /v1/models instead of the full repo
    /// path, so an agent can target a short, stable name (e.g. "local"). null/blank = serve under the
    /// model id. Advanced.
    /// </summary>
    public string? ServedModelName { get; set; }
}

public sealed class ImagesConfig
{
    // Podman needs fully-qualified image names — it does not default to Docker Hub.
    public string Gpu { get; set; } = "docker.io/vllm/vllm-openai:latest";
    public string Cpu { get; set; } = "public.ecr.aws/q9t5s3a7/vllm-cpu-release-repo:latest";
}

public sealed class DistroConfig
{
    public const string SourceWslOnline = "wsl-online";
    public const string SourceTarball = "tarball";

    /// <summary>
    /// "wsl-online" (default): WSL downloads the distro itself from Microsoft's official
    /// catalog (wsl --list --online) — no guessed URLs, checksums handled by WSL.
    /// "tarball": download the rootfs from the configured artifacts.rootfs source instead.
    /// </summary>
    public string Source { get; set; } = SourceWslOnline;

    /// <summary>Catalog name as shown by wsl --list --online.</summary>
    public string OnlineName { get; set; } = "Ubuntu-24.04";

    public bool UsesTarball => string.Equals(Source, SourceTarball, StringComparison.OrdinalIgnoreCase);
}

public sealed class ArtifactSource
{
    public string? Url { get; set; }
    public string? ChecksumUrl { get; set; }
    public string? Sha256 { get; set; }
    public GitHubSource? GitHub { get; set; }
}

public sealed class GitHubSource
{
    /// <summary>"owner/name".</summary>
    public string Repo { get; set; } = "";

    /// <summary>Glob matched against release asset names, e.g. "*-x86_64.tar.gz".</summary>
    public string AssetPattern { get; set; } = "*";
}

public sealed class AptRepoOverrides
{
    public string? Podman { get; set; }
    public string? NvidiaContainerToolkit { get; set; }
}

public sealed class NetworkConfig
{
    public string? Proxy { get; set; }
    public bool AllowSystemProxy { get; set; } = true;
}
