using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Logging;

namespace Slopworks.App.ViewModels;

public sealed class LogFileItemViewModel(LogFileInfo info)
{
    public string FullPath { get; } = info.FullPath;
    public string Display { get; } = $"{info.Name}  ·  {info.SizeBytes / 1024} KB  ·  {info.Modified:yyyy-MM-dd HH:mm}";
}

/// <summary>
/// Shows Slopworks' own logs (app, command audit, vLLM server) for diagnosing issues on any
/// machine. Read-only tail view with a file picker, refresh, copy, and open-folder.
/// </summary>
public partial class LogsViewModel(SlopworksHost host) : ObservableObject, IActivatableTab
{
    /// <summary>Re-reads the log list each time the tab is shown (cheap file IO; logs grow).</summary>
    public void Activate() => RefreshCommand.Execute(null);

    public ObservableCollection<LogFileItemViewModel> Files { get; } = [];

    public string LogsDir => host.Paths.LogsDir;

    [ObservableProperty]
    private LogFileItemViewModel? _selectedFile;

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private string _statusText = "";

    partial void OnSelectedFileChanged(LogFileItemViewModel? value)
    {
        Content = value is null ? "" : LogReader.ReadTail(value.FullPath);
    }

    [RelayCommand]
    private void Refresh()
    {
        var previous = SelectedFile?.FullPath;

        Files.Clear();
        foreach (var file in LogReader.ListLogFiles(host.Paths))
            Files.Add(new LogFileItemViewModel(file));

        if (Files.Count == 0)
        {
            SelectedFile = null;
            StatusText = "No logs yet. They appear here once Slopworks does something worth recording.";
            return;
        }

        StatusText = $"{Files.Count} log file(s) in {host.Paths.LogsDir}";
        SelectedFile = Files.FirstOrDefault(f => f.FullPath == previous) ?? Files[0];

        // Re-read even if the same file is reselected (it may have grown).
        if (SelectedFile is { } current)
            Content = LogReader.ReadTail(current.FullPath);
    }
}
