using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Logging;
using Slopworks.Core.Uninstall;
using Slopworks.Platform.Abstractions;

namespace Slopworks.App.ViewModels;

public partial class CleanupItemViewModel(CleanupStatus status, Func<string, Task> remove) : ObservableObject
{
    public string Id { get; } = status.Id;
    public string Title { get; } = status.Title;
    public string Description { get; } = status.Description;
    public string? Warning { get; } = status.Warning;
    public bool HasWarning => Warning is not null;
    public bool IsWsl => status.Id == UninstallService.WslId;

    [ObservableProperty]
    private bool _present = status.Present;

    [ObservableProperty]
    private string _detail = status.Detail;

    [RelayCommand]
    private Task RemoveAsync() => remove(Id);
}

/// <summary>
/// The undo page: everything Slopworks has changed on this machine, each individually
/// removable, plus remove-everything. Button clicks are the consent; commands are audited
/// and elevation prompts via UAC where needed.
/// </summary>
public partial class MaintenanceViewModel(SlopworksHost host) : ObservableObject
{
    public ObservableCollection<CleanupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _statusText = "Everything Slopworks changes is listed here and can be undone.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _includeWslInFullRemoval;

    public string WhatRemains => UninstallService.WhatRemains;

    private UninstallService Service => new(
        host.Paths, host.Config, host.Linux, host.NetworkExposure, host.ShellIntegration, host.Wsl);

    private IProcessRunner Runner => new RecordingProcessRunner(
        host.ProcessRunner, host.CommandLog, "maintenance", "user-click");

    [RelayCommand]
    private async Task RefreshAsync()
        => await RunBusyAsync(async () =>
        {
            var statuses = await Task.Run(() => Service.GetStatusAsync(Runner, CancellationToken.None));
            Items.Clear();
            foreach (var status in statuses)
                Items.Add(new CleanupItemViewModel(status, RemoveOneAsync));
            StatusText = "Everything Slopworks changes is listed here and can be undone.";
        });

    private async Task RemoveOneAsync(string id)
        => await RunBusyAsync(async () =>
        {
            var result = await Task.Run(() => Service.RemoveAsync(id, Runner, null, CancellationToken.None));
            StatusText = result.Message;
            await ReloadItemsAsync();
        });

    [RelayCommand]
    private async Task RemoveEverythingAsync()
        => await RunBusyAsync(async () =>
        {
            var include = IncludeWslInFullRemoval;
            var results = await Task.Run(() =>
                Service.RemoveEverythingAsync(include, Runner, null, CancellationToken.None));

            var failures = results.Where(r => !r.Succeeded).ToList();
            StatusText = failures.Count == 0
                ? "Everything removed. " + (include ? "" : "WSL itself was kept (tick the checkbox to include it).")
                : $"Finished with issues: {string.Join(" · ", failures.Select(f => f.Message))}";
            await ReloadItemsAsync();
        });

    private async Task ReloadItemsAsync()
    {
        var statuses = await Task.Run(() => Service.GetStatusAsync(Runner, CancellationToken.None));
        Items.Clear();
        foreach (var status in statuses)
            Items.Add(new CleanupItemViewModel(status, RemoveOneAsync));
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
