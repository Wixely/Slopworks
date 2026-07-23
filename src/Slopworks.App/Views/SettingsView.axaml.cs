using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Slopworks.App.ViewModels;

namespace Slopworks.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Open the folder that holds the profile .json files in the system file manager.</summary>
    private async void OpenProfilesFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            Directory.CreateDirectory(vm.ProfilesFolder);
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(vm.ProfilesFolder));
        }
    }
}
