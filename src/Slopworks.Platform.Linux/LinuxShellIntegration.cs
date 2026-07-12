using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Linux;

/// <summary>
/// Resume-after-reboot via a freedesktop autostart entry — file-based and visible, the
/// Linux analogue of the Windows Startup-folder script.
/// </summary>
public sealed class LinuxShellIntegration(IProcessRunner runner) : IShellIntegration
{
    public static string AutostartPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart", "slopworks-resume.desktop");

    public void InstallResumeOnStartup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartPath)!);
        File.WriteAllText(AutostartPath, BuildDesktopEntry(Environment.ProcessPath ?? "slopworks"));
    }

    public void RemoveResumeOnStartup()
    {
        if (File.Exists(AutostartPath))
            File.Delete(AutostartPath);
    }

    public bool ResumeOnStartupInstalled => File.Exists(AutostartPath);

    public Task RequestRebootAsync(CancellationToken ct)
        => runner.RunAsync(new ProcessSpec("systemctl", ["reboot"]), null, ct);

    public static string BuildDesktopEntry(string executablePath) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Slopworks (resume setup)
        Exec="{executablePath}"
        X-GNOME-Autostart-enabled=true
        """;
}
