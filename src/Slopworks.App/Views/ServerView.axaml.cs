using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Slopworks.App.Views;

public partial class ServerView : UserControl
{
    public ServerView() => InitializeComponent();

    private async void CopyUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string url }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(url);
        }
    }

    private async void CopyModel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.ServerViewModel vm
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.Model);
        }
    }
}
