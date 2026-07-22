using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;

namespace Slopworks.App.ViewModels;

/// <summary>One GPU checkbox in the Settings visible-GPUs list.</summary>
public partial class GpuCheckItemViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public GpuCheckItemViewModel(int index, string label, bool selected, Action onChanged)
    {
        Index = index;
        Label = label;
        _isSelected = selected;
        _onChanged = onChanged;
    }

    public int Index { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

/// <summary>
/// Every knob that shapes the vLLM run, editable, with a live preview of the exact podman
/// command the current values would produce. Nothing applies until Save.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IActivatableTab
{
    private readonly SlopworksHost _host;
    private bool _loading;
    private bool _profileProbed;
    private SystemProfile _profile = SystemProfile.Unknown;

    public SettingsViewModel(SlopworksHost host)
    {
        _host = host;
        LoadFromConfig();
    }

    /// <summary>Probes the machine profile (for the live command preview) the first time the tab is viewed.</summary>
    public void Activate()
    {
        if (_profileProbed)
            return;
        _profileProbed = true;
        _ = InitProfileAsync();
    }

    // Server
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _gpuMemoryUtilization = "";
    [ObservableProperty] private string _vllmLogLevel = "INFO";
    [ObservableProperty] private bool _enableToolCalling = true;
    [ObservableProperty] private string _toolCallParser = "hermes";
    [ObservableProperty] private string _hfToken = "";
    [ObservableProperty] private string _extraVllmArgs = "";
    [ObservableProperty] private string _extraContainerArgs = "";

    public IReadOnlyList<string> LogLevelOptions { get; } = ["DEBUG", "INFO", "WARNING", "ERROR"];

    // GPUs (populated from nvidia-smi when the tab is first viewed)
    public ObservableCollection<GpuCheckItemViewModel> Gpus { get; } = [];
    public ObservableCollection<int> TensorParallelOptions { get; } = [1];
    public IReadOnlyList<string> DeviceOrderOptions { get; } =
        ["Fastest first (CUDA default)", "PCI bus order (recommended for mixed GPUs)"];

    [ObservableProperty] private int _selectedTensorParallel = 1;
    [ObservableProperty] private int _selectedDeviceOrderIndex;
    [ObservableProperty] private bool _disableGpuP2P;
    [ObservableProperty] private bool _hasGpus;
    [ObservableProperty] private string _gpuHint = "Viewing this tab lists your GPUs.";
    [ObservableProperty] private string _nvLinkHint = "";

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
        IReadOnlyList<GpuDevice> gpus = [];
        try
        {
            _profile = await _host.SystemInfo.GetProfileAsync(CancellationToken.None);
            gpus = await _host.SystemInfo.EnumerateGpusAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Preview falls back to CPU shape; settings editing must never be blocked by probes.
        }

        BuildGpuControls(gpus);
    }

    /// <summary>Builds the GPU checkboxes and tensor-parallel options from the enumerated devices.</summary>
    private void BuildGpuControls(IReadOnlyList<GpuDevice> gpus)
    {
        _loading = true;

        var chosen = ParseVisibleGpus(_host.Config.Server.VisibleGpus, gpus);
        Gpus.Clear();
        foreach (var gpu in gpus)
            Gpus.Add(new GpuCheckItemViewModel(gpu.Index, gpu.Describe(), chosen.Contains(gpu.Index), OnGpuSelectionChanged));

        var maxParallel = Math.Max(1, gpus.Count);
        TensorParallelOptions.Clear();
        for (var n = 1; n <= maxParallel; n++)
            TensorParallelOptions.Add(n);
        SelectedTensorParallel = Math.Clamp(_host.Config.Server.TensorParallelSize, 1, maxParallel);

        SelectedDeviceOrderIndex =
            string.Equals(_host.Config.Server.CudaDeviceOrder, "PCI_BUS_ID", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        // Auto default: disable P2P only when there's no NVLink to use.
        DisableGpuP2P = _host.Config.Server.DisableGpuP2P ?? !_profile.HasNvLink;
        NvLinkHint = _profile.HasNvLink
            ? "NVLink detected — leave this unchecked to use it (much faster). Only check it if multi-GPU " +
              "still errors with 'invalid resource handle'."
            : "No NVLink detected — keep this checked (WSL doesn't support PCIe peer-to-peer across GPUs).";

        HasGpus = gpus.Count > 0;
        GpuHint = gpus.Count > 0
            ? "Unchecked GPUs are hidden from vLLM (CUDA_VISIBLE_DEVICES). All checked = use all."
            : "No NVIDIA GPUs detected (nvidia-smi unavailable). GPU options are hidden — this is normal in CPU mode.";

        _loading = false;
        UpdatePreview();
    }

    private void OnGpuSelectionChanged()
    {
        if (!_loading)
            UpdatePreview();
    }

    private static HashSet<int> ParseVisibleGpus(string? configured, IReadOnlyList<GpuDevice> gpus)
    {
        // Blank = all GPUs selected.
        if (string.IsNullOrWhiteSpace(configured))
            return [.. gpus.Select(g => g.Index)];

        return [.. configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var i) ? i : -1)
            .Where(i => i >= 0)];
    }

    private string? ComposeVisibleGpus()
    {
        if (Gpus.Count == 0)
            return _host.Config.Server.VisibleGpus; // no list available; keep whatever was configured

        var selected = Gpus.Where(g => g.IsSelected).Select(g => g.Index).ToList();
        // All (or none) selected means "use all" — represented as blank.
        return selected.Count == 0 || selected.Count == Gpus.Count ? null : string.Join(",", selected);
    }

    private string? ComposeDeviceOrder() => SelectedDeviceOrderIndex == 1 ? "PCI_BUS_ID" : null;

    private void LoadFromConfig()
    {
        _loading = true;
        var config = _host.Config;

        Port = config.Server.Port.ToString();
        Model = config.Server.Model;
        GpuMemoryUtilization = config.Server.GpuMemoryUtilization.ToString("0.##");
        VllmLogLevel = string.IsNullOrWhiteSpace(config.Server.VllmLogLevel) ? "INFO" : config.Server.VllmLogLevel;
        EnableToolCalling = config.Server.EnableToolCalling;
        ToolCallParser = config.Server.ToolCallParser;
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

        // Re-apply GPU selections when the list already exists (e.g. Discard changes).
        if (Gpus.Count > 0)
        {
            var chosen = ParseVisibleGpus(config.Server.VisibleGpus, [.. Gpus.Select(g => new GpuDevice(g.Index, "", "", 0))]);
            foreach (var gpu in Gpus)
                gpu.IsSelected = chosen.Contains(gpu.Index);
            SelectedTensorParallel = Math.Clamp(config.Server.TensorParallelSize, 1, Math.Max(1, TensorParallelOptions.Count));
            SelectedDeviceOrderIndex =
                string.Equals(config.Server.CudaDeviceOrder, "PCI_BUS_ID", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            DisableGpuP2P = config.Server.DisableGpuP2P ?? !_profile.HasNvLink;
        }

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
        config.Server.VllmLogLevel = VllmLogLevel;
        config.Server.EnableToolCalling = EnableToolCalling;
        config.Server.ToolCallParser = string.IsNullOrWhiteSpace(ToolCallParser) ? "hermes" : ToolCallParser.Trim();
        config.Server.TensorParallelSize = SelectedTensorParallel > 0 ? SelectedTensorParallel : 1;
        config.Server.VisibleGpus = ComposeVisibleGpus();
        config.Server.CudaDeviceOrder = ComposeDeviceOrder();
        config.Server.DisableGpuP2P = DisableGpuP2P;
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
                VllmLogLevel = VllmLogLevel,
                EnableToolCalling = EnableToolCalling,
                ToolCallParser = string.IsNullOrWhiteSpace(ToolCallParser) ? "hermes" : ToolCallParser.Trim(),
                TensorParallelSize = SelectedTensorParallel > 0 ? SelectedTensorParallel : 1,
                VisibleGpus = ComposeVisibleGpus(),
                CudaDeviceOrder = ComposeDeviceOrder(),
                DisableGpuP2P = DisableGpuP2P,
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
