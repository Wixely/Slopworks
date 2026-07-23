using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Slopworks.App.ViewModels;

namespace Slopworks.App.Views;

public partial class ModelsView : UserControl
{
    public ModelsView() => InitializeComponent();

    /// <summary>Open the model-cache folder (WSL \\wsl.localhost path on Windows) in the file manager.</summary>
    private async void OpenModelsFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModelsViewModel vm || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
            return;

        var path = vm.ModelsFolder;
        try { Directory.CreateDirectory(path); } // best-effort so the folder exists to open (needs WSL running)
        catch (Exception) { }
        try { await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path)); }
        catch (Exception) { }
    }

    /// <summary>Opens a model's HuggingFace page — the clicked row's model, or the selected one.</summary>
    private async void OpenHf_Click(object? sender, RoutedEventArgs e)
    {
        var url = (sender as Control)?.DataContext switch
        {
            ModelItemViewModel item => item.Url,
            ModelsViewModel vm => vm.SelectedModelUrl,
            _ => null,
        };

        if (!string.IsNullOrEmpty(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }
}
