using Slopworks.Core.Config;

namespace Slopworks.Core.Steps;

/// <summary>
/// The idempotent shell scripts that provision the distro. Kept as source constants (not
/// resources) so the exact text is shown verbatim on safe-mode confirmation cards. Every
/// mutation is guarded so re-running a script converges instead of failing.
/// </summary>
public static class ProvisionScripts
{
    public const string BaseMarker = "base-v1";
    public const string PodmanMarker = "podman-v1";
    public const string NvidiaMarkerPrefix = "nvidia-v1";

    /// <summary>Prepended to every script; wires the configured proxy into apt and the shell.</summary>
    private const string ProxyPreamble = """
        PROXY="{{PROXY}}"
        if [ -n "$PROXY" ]; then
          printf 'Acquire::http::Proxy "%s";\nAcquire::https::Proxy "%s";\n' "$PROXY" "$PROXY" > /etc/apt/apt.conf.d/95slopworks-proxy
          export http_proxy="$PROXY" https_proxy="$PROXY" HTTP_PROXY="$PROXY" HTTPS_PROXY="$PROXY"
        fi
        """;

    public const string Base = """
        #!/usr/bin/env bash
        set -euo pipefail
        export DEBIAN_FRONTEND=noninteractive
        {{PROXY_PREAMBLE}}

        cat > /etc/wsl.conf <<'EOF'
        [boot]
        systemd=true

        [user]
        default=slop

        [interop]
        appendWindowsPath=false
        EOF

        id -u slop >/dev/null 2>&1 || useradd -m -s /bin/bash slop

        apt-get update
        apt-get install -y ca-certificates curl gnupg

        mkdir -p /etc/slopworks
        echo "base-v1" > /etc/slopworks/provisioned-base
        echo "PROVISION_BASE_OK"
        """;

    public const string Podman = """
        #!/usr/bin/env bash
        set -euo pipefail
        export DEBIAN_FRONTEND=noninteractive
        {{PROXY_PREAMBLE}}

        if ! command -v podman >/dev/null 2>&1; then
          apt-get update
          apt-get install -y podman
        fi

        mkdir -p /etc/slopworks
        echo "podman-v1" > /etc/slopworks/provisioned-podman
        podman --version
        echo "PROVISION_PODMAN_OK"
        """;

    public const string Nvidia = """
        #!/usr/bin/env bash
        set -euo pipefail
        export DEBIAN_FRONTEND=noninteractive
        {{PROXY_PREAMBLE}}

        REPO_BASE="{{NVIDIA_REPO_BASE}}"
        KEYRING=/etc/apt/keyrings/nvidia-container-toolkit-keyring.gpg

        if ! command -v nvidia-ctk >/dev/null 2>&1; then
          mkdir -p /etc/apt/keyrings
          curl -fsSL "$REPO_BASE/gpgkey" | gpg --dearmor --yes -o "$KEYRING"
          curl -fsSL "$REPO_BASE/stable/deb/nvidia-container-toolkit.list" \
            | sed "s#deb https://#deb [signed-by=$KEYRING] https://#g" \
            > /etc/apt/sources.list.d/nvidia-container-toolkit.list
          apt-get update
          apt-get install -y nvidia-container-toolkit
        fi

        mkdir -p /etc/cdi
        nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml

        mkdir -p /etc/slopworks
        echo "nvidia-v1 driver={{DRIVER_VERSION}}" > /etc/slopworks/provisioned-nvidia
        echo "PROVISION_NVIDIA_OK"
        """;

    public static string Render(string template, SlopworksConfig config, IReadOnlyDictionary<string, string>? extra = null)
    {
        var script = template
            .Replace("{{PROXY_PREAMBLE}}", ProxyPreamble)
            .Replace("{{PROXY}}", config.Network.Proxy ?? "")
            .Replace("{{NVIDIA_REPO_BASE}}",
                config.AptRepos.NvidiaContainerToolkit ?? "https://nvidia.github.io/libnvidia-container");

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                script = script.Replace("{{" + key + "}}", value);
        }

        return script;
    }
}
