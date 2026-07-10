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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(host),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
