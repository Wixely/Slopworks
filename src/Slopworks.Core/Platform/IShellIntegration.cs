namespace Slopworks.Core.Platform;

/// <summary>
/// OS shell hooks for the reboot-resume flow. File-based on purpose (a script in the
/// per-user Startup folder, no registry) so it is visible and trivially removable.
/// </summary>
public interface IShellIntegration
{
    /// <summary>Arrange for Slopworks to reopen after the next login.</summary>
    void InstallResumeOnStartup();

    void RemoveResumeOnStartup();

    bool ResumeOnStartupInstalled { get; }

    Task RequestRebootAsync(CancellationToken ct);
}
