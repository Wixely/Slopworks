using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Config;

namespace Slopworks.App.ViewModels;

/// <summary>
/// The Platform tab: named platforms (container images + distro source), one marked default.
/// Editor on the left edits the selected platform; the list on the right manages them. A settings
/// profile picks a platform on the Settings tab (or uses the default).
/// </summary>
public partial class PlatformsViewModel : ObservableObject, IActivatableTab
{
    private readonly SlopworksHost _host;
    private bool _syncing;
    private bool _deleteArmed;

    public ObservableCollection<string> Platforms { get; } = [];
    [ObservableProperty] private string? _selectedPlatform;
    [ObservableProperty] private string _platformNameEdit = "";
    [ObservableProperty] private string _defaultPlatform = "";
    [ObservableProperty] private bool _isSelectedDefault;
    [ObservableProperty] private string _statusText =
        "Platforms bundle the container images and distro source. Choose one per profile on the Settings tab.";

    // Editor — the selected platform's images + distro source.
    [ObservableProperty] private string _gpuImage = "";
    [ObservableProperty] private string _cpuImage = "";
    [ObservableProperty] private bool _useWslCatalog = true;
    [ObservableProperty] private string _catalogDistroName = "";
    [ObservableProperty] private string _rootfsUrl = "";
    [ObservableProperty] private string _rootfsChecksumUrl = "";

    public PlatformsViewModel(SlopworksHost host)
    {
        _host = host;
        RefreshList();
        _host.Platforms.Changed += RefreshList;
    }

    public void Activate() => RefreshList();

    public void Deactivate() { }

    private void RefreshList()
    {
        _syncing = true;
        var keep = SelectedPlatform;
        Platforms.Clear();
        foreach (var name in _host.Platforms.Platforms)
            Platforms.Add(name);
        DefaultPlatform = _host.Platforms.Default;
        SelectedPlatform = keep is not null && Platforms.Contains(keep) ? keep : _host.Platforms.Default;
        _syncing = false;
        LoadEditor();
    }

    partial void OnSelectedPlatformChanged(string? value)
    {
        _deleteArmed = false;
        PlatformNameEdit = value ?? "";
        IsSelectedDefault = value is not null && value == _host.Platforms.Default;
        if (!_syncing)
            LoadEditor();
    }

    private void LoadEditor()
    {
        if (SelectedPlatform is null)
            return;
        var platform = _host.Platforms.Load(SelectedPlatform);
        GpuImage = platform.Images.Gpu;
        CpuImage = platform.Images.Cpu;
        UseWslCatalog = !platform.Distro.UsesTarball;
        CatalogDistroName = platform.Distro.OnlineName;
        RootfsUrl = platform.Rootfs.Url ?? "";
        RootfsChecksumUrl = platform.Rootfs.ChecksumUrl ?? "";
    }

    private PlatformProfile BuildPlatform()
    {
        var platform = new PlatformProfile();
        platform.Images.Gpu = GpuImage.Trim();
        platform.Images.Cpu = CpuImage.Trim();
        platform.Distro.Source = UseWslCatalog ? DistroConfig.SourceWslOnline : DistroConfig.SourceTarball;
        platform.Distro.OnlineName = CatalogDistroName.Trim();
        platform.Rootfs = new ArtifactSource
        {
            Url = string.IsNullOrWhiteSpace(RootfsUrl) ? null : RootfsUrl.Trim(),
            ChecksumUrl = string.IsNullOrWhiteSpace(RootfsChecksumUrl) ? null : RootfsChecksumUrl.Trim(),
        };
        return platform;
    }

    private static string NewPlatformName() => $"New-Platform-{DateTime.Now:yyyyMMdd-HHmmss}";

    [RelayCommand]
    private void Save()
    {
        if (SelectedPlatform is not { } name)
            return;
        _host.Platforms.Save(name, BuildPlatform());
        StatusText = $"Saved platform '{name}'. Restart the server / re-run setup to apply.";
    }

    [RelayCommand]
    private void CreatePlatform()
    {
        try
        {
            var name = _host.Platforms.Create(NewPlatformName());
            SelectedPlatform = name;
            StatusText = $"Created platform '{name}' — edit it and rename it above.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void DuplicatePlatform()
    {
        if (SelectedPlatform is not { } source)
            return;
        try
        {
            var name = _host.Platforms.Duplicate(source, NewPlatformName());
            SelectedPlatform = name;
            StatusText = $"Duplicated '{source}' into '{name}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void RenamePlatform()
    {
        if (SelectedPlatform is not { } current || !ProfileStore.IsValidName(PlatformNameEdit))
        {
            StatusText = "Enter a valid platform name.";
            return;
        }
        try
        {
            var renamed = _host.Platforms.Rename(current, PlatformNameEdit);
            SelectedPlatform = renamed;
            StatusText = $"Renamed to '{renamed}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void DeletePlatform()
    {
        if (SelectedPlatform is not { } name)
            return;
        if (Platforms.Count <= 1)
        {
            StatusText = "Can't delete the only platform — create another first.";
            return;
        }
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            StatusText = $"Press Delete again to permanently remove platform '{name}'.";
            return;
        }
        _deleteArmed = false;
        _host.Platforms.Delete(name);
        StatusText = $"Deleted platform '{name}'.";
    }

    [RelayCommand]
    private void SetDefault()
    {
        if (SelectedPlatform is not { } name)
            return;
        _host.Platforms.SetDefault(name);
        StatusText = $"'{name}' is now the default platform.";
    }
}
