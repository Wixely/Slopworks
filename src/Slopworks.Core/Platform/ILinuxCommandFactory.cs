using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Platform;

/// <summary>
/// Builds ProcessSpecs that run inside the managed Linux environment. A factory (not a
/// runner) so every invocation still flows through the gated/audited IProcessRunner.
/// Windows: wsl.exe -d slopworks; Linux: bash on the host.
///
/// Users: "root" = administrative work (apt, provisioning) — elevates on a host;
/// "operator" (the default for Command) = the identity that runs podman — distro root on
/// Windows (rootful inside our disposable distro), the invoking user on a Linux host
/// (rootless podman, never prompts).
/// </summary>
public interface ILinuxCommandFactory
{
    /// <summary>One shell command via bash -c.</summary>
    ProcessSpec Command(string bashCommand, string user = "operator");

    /// <summary>A whole script piped over stdin (bash -s) — no quoting hell, fully visible for approval.</summary>
    ProcessSpec Script(string scriptText, string user = "root");

    /// <summary>Stop the environment so boot-time config (wsl.conf/systemd) is re-read on next entry.</summary>
    ProcessSpec Terminate();
}

public sealed class WslLinuxCommandFactory(string distroName) : ILinuxCommandFactory
{
    // Inside our disposable distro both roles are root.
    private static string Map(string user) => user == "operator" ? "root" : user;

    public ProcessSpec Command(string bashCommand, string user = "operator")
        => new("wsl.exe", ["-d", distroName, "-u", Map(user), "--", "bash", "-c", bashCommand],
            StdoutEncoding: System.Text.Encoding.UTF8);

    public ProcessSpec Script(string scriptText, string user = "root")
        => new("wsl.exe", ["-d", distroName, "-u", Map(user), "--", "bash", "-s"],
            StdoutEncoding: System.Text.Encoding.UTF8,
            StdinText: scriptText.ReplaceLineEndings("\n"));

    public ProcessSpec Terminate()
        => new("wsl.exe", ["--terminate", distroName], StdoutEncoding: System.Text.Encoding.Unicode);
}
