using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Slopworks.App.ViewModels;

namespace Slopworks.App.Views;

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.Content);
    }

    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
            return;

        try
        {
            Directory.CreateDirectory(vm.LogsDir);
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(vm.LogsDir));
        }
        catch (Exception)
        {
            // Opening the file manager is a convenience; never surface an error for it.
        }
    }
}
