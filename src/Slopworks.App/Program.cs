using Avalonia;
using Slopworks.Platform.Windows.Elevation;

namespace Slopworks.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Elevated worker mode: execute one command and exit — no UI.
        if (args is ["--elevated-worker", var requestFile])
            return ElevatedWorker.Run(requestFile);

        // Headless commands (start/stop/status/--help): run and exit without a window.
        if (CliRunner.IsCliCommand(args))
            return CliRunner.Run(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
