using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Slopworks.Core.Engine;

namespace Slopworks.App.ViewModels;

public partial class DashboardViewModel(SlopworksHost host) : ObservableObject
{
    public ObservableCollection<StepStatusItemViewModel> Steps { get; } = [];

    [ObservableProperty]
    private string _profileSummary = "Probing machine…";

    [ObservableProperty]
    private string _rootSummary = $"Data root: {host.Paths.Root}";

    [ObservableProperty]
    private bool _isRefreshing;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing)
            return;

        IsRefreshing = true;
        try
        {
            var (engine, profile) = await host.CreateEngineAsync(CancellationToken.None);

            ProfileSummary = profile.Gpu is { } gpu
                ? $"{profile.OsDescription} · {gpu.Name} ({gpu.MemoryMiB / 1024} GiB, driver {gpu.DriverVersion}) · {profile.FreeDiskBytes / (1024 * 1024 * 1024)} GB free"
                : $"{profile.OsDescription} · no NVIDIA GPU (CPU mode) · {profile.FreeDiskBytes / (1024 * 1024 * 1024)} GB free";

            Steps.Clear();
            var items = new Dictionary<string, StepStatusItemViewModel>();
            foreach (var step in engine.Steps.Where(s => s.AppliesTo(profile)))
            {
                var item = new StepStatusItemViewModel(step.Id, step.Title);
                items[step.Id] = item;
                Steps.Add(item);
            }

            var results = await Task.Run(() => engine.DetectAllAsync(null, CancellationToken.None));
            foreach (var (stepId, detection) in results)
            {
                if (items.TryGetValue(stepId, out var item))
                    item.Update(detection);
            }

            // A pending reboot resolves itself once the step it was waiting on detects Ok.
            if (host.Journal.Data.PendingReboot is { } pending
                && results.TryGetValue(pending.AfterStep, out var afterReboot)
                && afterReboot.State == StepState.Ok)
            {
                host.Journal.Data.PendingReboot = null;
                await host.Journal.SaveAsync();
                host.ShellIntegration.RemoveResumeOnStartup();
            }
        }
        catch (Exception ex)
        {
            host.Logger.LogError(ex, "Dashboard refresh failed");
            ProfileSummary = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}
