using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Slopworks.App.ViewModels;

namespace Slopworks.App.Views;

public partial class TemplatesView : UserControl
{
    public TemplatesView() => InitializeComponent();

    /// <summary>Open the templates folder in the file manager (users can drop .jinja files in directly).</summary>
    private async void OpenTemplatesFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TemplatesViewModel vm || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
            return;

        var path = vm.TemplatesFolder;
        try { Directory.CreateDirectory(path); }
        catch (Exception) { }
        try { await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path)); }
        catch (Exception) { }
    }

    /// <summary>Pick a .jinja file from disk and hand its contents to the view-model to import.</summary>
    private async void ImportFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TemplatesViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import chat template",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Chat templates") { Patterns = ["*.jinja", "*.j2", "*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0)
            return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            vm.ImportFromFile(Path.GetFileNameWithoutExtension(files[0].Name), content);
        }
        catch (Exception)
        {
            // A failed read leaves the library unchanged; the picker is the only side effect.
        }
    }
}
