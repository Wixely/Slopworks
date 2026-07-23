using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Slopworks.App.ViewModels;

namespace Slopworks.App.Views;

public partial class ServerView : UserControl
{
    private ScrollViewer? _logScroller;

    public ServerView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ServerViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// Live log UX: keep following the tail only if the user is already at the bottom, so a
    /// manual scroll-up to read history isn't yanked back down on the next update.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServerViewModel.Logs))
            return;

        _logScroller ??= this.FindControl<ScrollViewer>("LogScroller");
        if (_logScroller is null)
            return;

        // Measured against the pre-update layout — i.e. "was I at the bottom before this?"
        const double slack = 12;
        var atBottom = _logScroller.Offset.Y >= _logScroller.Extent.Height - _logScroller.Viewport.Height - slack;
        if (atBottom)
        {
            // Runs after the new text has been laid out, so Extent reflects the new content.
            Dispatcher.UIThread.Post(
                () => _logScroller.Offset = new Vector(_logScroller.Offset.X, _logScroller.Extent.Height),
                DispatcherPriority.Background);
        }
    }

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

    private async void CopyLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.ServerViewModel { Logs: { Length: > 0 } logs }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(logs);
        }
    }
}
