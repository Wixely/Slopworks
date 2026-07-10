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

    public ServerConfig Server { get; set; } = new();
    public ImagesConfig Images { get; set; } = new();

    /// <summary>Per-artifact download sources; every URL overridable, GitHub sources auto-resolved to latest.</summary>
    public Dictionary<string, ArtifactSource> Artifacts { get; set; } = DefaultArtifacts();

    public AptRepoOverrides AptRepos { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();

    public bool IsAutoMode => string.Equals(Mode, "auto", StringComparison.OrdinalIgnoreCase);

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
    public string Model { get; set; } = "Qwen/Qwen2.5-0.5B-Instruct";
    public List<string> ExtraArgs { get; set; } = [];
    public string? HfToken { get; set; }
    public double GpuMemoryUtilization { get; set; } = 0.90;
}

public sealed class ImagesConfig
{
    // Podman needs fully-qualified image names — it does not default to Docker Hub.
    public string Gpu { get; set; } = "docker.io/vllm/vllm-openai:latest";
    public string Cpu { get; set; } = "public.ecr.aws/q9t5s3a7/vllm-cpu-release-repo:latest";
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
