using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Server;

namespace Slopworks.App.ViewModels;

/// <summary>
/// Every knob that shapes the vLLM run, editable, with a live preview of the exact podman
/// command the current values would produce. Nothing applies until Save.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SlopworksHost _host;
    private bool _loading;
    private SystemProfile _profile = SystemProfile.Unknown;

    public SettingsViewModel(SlopworksHost host)
    {
        _host = host;
        LoadFromConfig();
        _ = InitProfileAsync();
    }

    // Server
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _gpuMemoryUtilization = "";
    [ObservableProperty] private string _hfToken = "";
    [ObservableProperty] private string _extraVllmArgs = "";
    [ObservableProperty] private string _extraContainerArgs = "";

    // Images
    [ObservableProperty] private string _gpuImage = "";
    [ObservableProperty] private string _cpuImage = "";

    // Distro
    [ObservableProperty] private bool _useWslCatalog = true;
    [ObservableProperty] private string _catalogDistroName = "";
    [ObservableProperty] private string _rootfsUrl = "";
    [ObservableProperty] private string _rootfsChecksumUrl = "";

    // Network / behavior
    [ObservableProperty] private string _proxy = "";
    [ObservableProperty] private bool _allowSystemProxy = true;
    [ObservableProperty] private bool _autoApproveInsideRoot = true;

    [ObservableProperty] private string _commandPreview = "";
    [ObservableProperty] private string _previewLabel = "";
    [ObservableProperty] private string _statusText = "Changes apply when you press Save.";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(CommandPreview) or nameof(PreviewLabel) or nameof(StatusText)))
            UpdatePreview();
    }

    private async Task InitProfileAsync()
    {
        try
        {
            _profile = await _host.SystemInfo.GetProfileAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Preview falls back to CPU shape; settings editing must never be blocked by probes.
        }

        UpdatePreview();
    }

    private void LoadFromConfig()
    {
        _loading = true;
        var config = _host.Config;

        Port = config.Server.Port.ToString();
        Model = config.Server.Model;
        GpuMemoryUtilization = config.Server.GpuMemoryUtilization.ToString("0.##");
        HfToken = config.Server.HfToken ?? "";
        ExtraVllmArgs = string.Join(Environment.NewLine, config.Server.ExtraArgs);
        ExtraContainerArgs = string.Join(Environment.NewLine, config.Server.ExtraContainerArgs);

        GpuImage = config.Images.Gpu;
        CpuImage = config.Images.Cpu;

        UseWslCatalog = !config.Distro.UsesTarball;
        CatalogDistroName = config.Distro.OnlineName;
        var rootfs = config.Artifacts.TryGetValue("rootfs", out var source) ? source : new ArtifactSource();
        RootfsUrl = rootfs.Url ?? "";
        RootfsChecksumUrl = rootfs.ChecksumUrl ?? "";

        Proxy = config.Network.Proxy ?? "";
        AllowSystemProxy = config.Network.AllowSystemProxy;
        AutoApproveInsideRoot = config.AutoApproveInsideRoot;

        _loading = false;
        UpdatePreview();
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryValidate(out var port, out var gpuMem, out var error))
        {
            StatusText = error;
            return;
        }

        var config = _host.Config;
        config.Server.Port = port;
        config.Server.Model = Model.Trim();
        config.Server.GpuMemoryUtilization = gpuMem;
        config.Server.HfToken = string.IsNullOrWhiteSpace(HfToken) ? null : HfToken.Trim();
        config.Server.ExtraArgs = SplitArgs(ExtraVllmArgs);
        config.Server.ExtraContainerArgs = SplitArgs(ExtraContainerArgs);

        config.Images.Gpu = GpuImage.Trim();
        config.Images.Cpu = CpuImage.Trim();

        config.Distro.Source = UseWslCatalog ? DistroConfig.SourceWslOnline : DistroConfig.SourceTarball;
        config.Distro.OnlineName = CatalogDistroName.Trim();
        config.Artifacts["rootfs"] = new ArtifactSource
        {
            Url = string.IsNullOrWhiteSpace(RootfsUrl) ? null : RootfsUrl.Trim(),
            ChecksumUrl = string.IsNullOrWhiteSpace(RootfsChecksumUrl) ? null : RootfsChecksumUrl.Trim(),
        };

        config.Network.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        config.Network.AllowSystemProxy = AllowSystemProxy;
        config.AutoApproveInsideRoot = AutoApproveInsideRoot;

        ConfigStore.Save(_host.Paths, config);
        StatusText = "Saved. Restart the server (and re-run affected setup steps) to apply.";
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        LoadFromConfig();
        StatusText = "Edits discarded.";
    }

    private void UpdatePreview()
    {
        if (!TryValidate(out var port, out var gpuMem, out var error))
        {
            CommandPreview = "";
            PreviewLabel = error;
            return;
        }

        var preview = new SlopworksConfig
        {
            Server = new ServerConfig
            {
                Port = port,
                Model = Model.Trim(),
                GpuMemoryUtilization = gpuMem,
                HfToken = string.IsNullOrWhiteSpace(HfToken) ? null : "***",
                ExtraArgs = SplitArgs(ExtraVllmArgs),
                ExtraContainerArgs = SplitArgs(ExtraContainerArgs),
            },
            Images = new ImagesConfig { Gpu = GpuImage.Trim(), Cpu = CpuImage.Trim() },
            Network = new NetworkConfig { Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim() },
        };

        var controller = new VllmServerController(_host.Linux, preview, new HttpClient(), _host.Paths);
        CommandPreview = controller.BuildRunCommand(_profile, preview.Server.Model);
        PreviewLabel = _profile.GpuPresent
            ? "Command this machine will run (GPU mode):"
            : "Command this machine will run (CPU mode — no NVIDIA GPU detected):";
    }

    private bool TryValidate(out int port, out double gpuMem, out string error)
    {
        gpuMem = 0.9;
        error = "";

        if (!int.TryParse(Port, out port) || port is < 1 or > 65535)
        {
            error = "Port must be a number between 1 and 65535.";
            return false;
        }

        if (!double.TryParse(GpuMemoryUtilization, out gpuMem) || gpuMem is <= 0 or > 1)
        {
            error = "GPU memory utilization must be between 0 and 1 (e.g. 0.9).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            error = "Model id cannot be empty.";
            return false;
        }

        return true;
    }

    /// <summary>One argument per line (spaces within a line are kept, so flags with values work).</summary>
    private static List<string> SplitArgs(string text) =>
        [.. text.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0)];
}
