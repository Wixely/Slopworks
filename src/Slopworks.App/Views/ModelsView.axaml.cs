using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Slopworks.App.ViewModels;

namespace Slopworks.App.Views;

public partial class ModelsView : UserControl
{
    public ModelsView() => InitializeComponent();

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
