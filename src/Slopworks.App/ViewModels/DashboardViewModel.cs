using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Slopworks.Core.Engine;

namespace Slopworks.App.ViewModels;

public partial class DashboardViewModel(SlopworksHost host) : ObservableObject
{
    public const string BypassKind = "bypass";
    public const string ForceKind = "force";

    public ObservableCollection<StepStatusItemViewModel> Steps { get; } = [];

    /// <summary>Active bypass/force overrides, each removable to restore the original check.</summary>
    public ObservableCollection<OverrideChipViewModel> Overrides { get; } = [];

    [ObservableProperty]
    private string _profileSummary = "Press Refresh to check setup status.";

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
            RebuildOverrideChips();
            var (engine, profile) = await host.CreateEngineAsync(CancellationToken.None);

            ProfileSummary = profile.Gpu is { } gpu
                ? $"{profile.OsDescription} · {gpu.Name} ({gpu.MemoryMiB / 1024} GiB, driver {gpu.DriverVersion}) · {profile.FreeDiskBytes / (1024 * 1024 * 1024)} GB free"
                : $"{profile.OsDescription} · no NVIDIA GPU (CPU mode) · {profile.FreeDiskBytes / (1024 * 1024 * 1024)} GB free";

            Steps.Clear();
            var items = new Dictionary<string, StepStatusItemViewModel>();
            foreach (var step in engine.Steps.Where(s => s.AppliesTo(profile)))
            {
                var item = new StepStatusItemViewModel(step.Id, step.Title, BypassCheck, ForceCheck);
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

    /// <summary>Records the bypass in config and re-detects; the check downgrades to a warning.</summary>
    private void BypassCheck(string bypassKey)
    {
        if (!host.Config.Bypasses.Contains(bypassKey))
        {
            host.Config.Bypasses.Add(bypassKey);
            Slopworks.Core.Config.ConfigStore.Save(host.Paths, host.Config);
        }

        RefreshCommand.Execute(null);
    }

    /// <summary>User overrides a passing heuristic ("no NVIDIA card found") they know is wrong.</summary>
    private void ForceCheck(string forceKey)
    {
        if (!host.Config.Forces.Contains(forceKey))
        {
            host.Config.Forces.Add(forceKey);
            Slopworks.Core.Config.ConfigStore.Save(host.Paths, host.Config);
        }

        RefreshCommand.Execute(null);
    }

    private void RebuildOverrideChips()
    {
        Overrides.Clear();
        foreach (var key in host.Config.Bypasses)
            Overrides.Add(new OverrideChipViewModel(BypassKind, key, RemoveOverride));
        foreach (var key in host.Config.Forces)
            Overrides.Add(new OverrideChipViewModel(ForceKind, key, RemoveOverride));
    }

    /// <summary>Un-bypass / un-force: restores the original check and re-detects.</summary>
    private void RemoveOverride(string kind, string key)
    {
        var list = kind == BypassKind ? host.Config.Bypasses : host.Config.Forces;
        if (list.Remove(key))
            Slopworks.Core.Config.ConfigStore.Save(host.Paths, host.Config);

        RefreshCommand.Execute(null);
    }
}
