using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Slopworks.App.ViewModels;
using Slopworks.App.Views;

namespace Slopworks.App;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = SlopworksHost.Create();
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(host),
            };

            // Remember the window size (never position) across sessions.
            if (Slopworks.Core.Config.WindowSizeStore.Load(host.Paths) is { } size)
            {
                window.Width = size.Width;
                window.Height = size.Height;
            }

            // Persist the size (never position) when the window closes. Only a normal-state
            // size is stored — a maximized size would stick oddly on next launch.
            window.Closing += (_, _) =>
            {
                if (window.WindowState == Avalonia.Controls.WindowState.Normal)
                    Slopworks.Core.Config.WindowSizeStore.Save(host.Paths, window.ClientSize.Width, window.ClientSize.Height);
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
