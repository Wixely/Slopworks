using Slopworks.Core.Platform;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Platform.Windows;

public sealed class WindowsShellIntegration(IProcessRunner runner) : IShellIntegration
{
    public static string ResumeScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Slopworks-resume.cmd");

    public void InstallResumeOnStartup()
    {
        File.WriteAllText(ResumeScriptPath,
            $"""
            @echo off
            start "" "{Environment.ProcessPath}"
            """);
    }

    public void RemoveResumeOnStartup()
    {
        if (File.Exists(ResumeScriptPath))
            File.Delete(ResumeScriptPath);
    }

    public bool ResumeOnStartupInstalled => File.Exists(ResumeScriptPath);

    public Task RequestRebootAsync(CancellationToken ct)
        => runner.RunAsync(new ProcessSpec("shutdown.exe", ["/r", "/t", "5",
            "/c", "Slopworks: restarting to finish enabling WSL"]), null, ct);
}
