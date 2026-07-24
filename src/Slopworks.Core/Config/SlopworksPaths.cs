namespace Slopworks.Core.Config;

/// <summary>
/// Every path Slopworks touches, derived from the single root directory. Nothing outside
/// this root is written except the pointer file in %APPDATA%/Slopworks (see rootpath docs)
/// and system-level WSL registration.
/// </summary>
public sealed class SlopworksPaths(string root)
{
    public const string DistroName = "slopworks";

    public string Root { get; } = Path.GetFullPath(root);

    public string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>Named settings files (profiles) live here; ConfigFile is the active working copy.</summary>
    public string ProfilesDir => Path.Combine(Root, "profiles");

    /// <summary>Records which profile name is currently active.</summary>
    public string ActiveProfileFile => Path.Combine(ProfilesDir, "active.txt");

    /// <summary>Named platform files (container images + distro source).</summary>
    public string PlatformsDir => Path.Combine(Root, "platforms");

    /// <summary>Records which platform is the default (used by profiles that don't pick one).</summary>
    public string DefaultPlatformFile => Path.Combine(PlatformsDir, "default.txt");

    /// <summary>User chat-template files (*.jinja), mounted into the container as --chat-template.</summary>
    public string TemplatesDir => Path.Combine(Root, "templates");

    public string StateDir => Path.Combine(Root, "state");
    public string JournalFile => Path.Combine(StateDir, "journal.json");
    public string SmokeDir => Path.Combine(StateDir, "smoke");
    public string ElevatedDir => Path.Combine(StateDir, "elevated");
    public string DownloadsDir => Path.Combine(Root, "downloads");
    public string RootfsDir => Path.Combine(DownloadsDir, "rootfs");
    public string WslDir => Path.Combine(Root, "wsl");
    public string DistroDir => Path.Combine(WslDir, DistroName);
    public string LogsDir => Path.Combine(Root, "logs");
    public string VllmLogsDir => Path.Combine(LogsDir, "vllm");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, ProfilesDir, PlatformsDir, TemplatesDir, StateDir, SmokeDir, ElevatedDir, RootfsDir, WslDir, LogsDir, VllmLogsDir })
            Directory.CreateDirectory(dir);
    }

    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase);
    }
}
