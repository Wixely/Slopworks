using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core;
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
    private bool _gpuControlsBuilt;
    private SystemProfile _profile = SystemProfile.Unknown;

    // One shared client for the (HTTP-free) command preview — avoids allocating an HttpClient per keystroke.
    private static readonly HttpClient PreviewHttp = new();

    public SettingsViewModel(SlopworksHost host)
    {
        _host = host;
        LoadFromConfig();
        RefreshProfiles();
        RefreshPlatformOptions();
        _host.Profiles.Changed += OnProfilesChanged;
        _host.Platforms.Changed += OnPlatformsChanged;
    }

    // Profiles (named settings files). The dropdown selects the active profile; the editor below
    // always edits whichever profile is active.
    public ObservableCollection<string> Profiles { get; } = [];
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _profileNameEdit = "";
    [ObservableProperty] private bool _deleteArmed;
    private bool _syncingProfiles;

    /// <summary>Folder where the profile .json files live (for the "Open folder" button).</summary>
    public string ProfilesFolder => _host.Paths.ProfilesDir;

    private void RefreshProfiles()
    {
        _syncingProfiles = true;
        Profiles.Clear();
        foreach (var name in _host.Profiles.Profiles)
            Profiles.Add(name);
        SelectedProfile = _host.Profiles.Active;
        ProfileNameEdit = _host.Profiles.Active;
        _syncingProfiles = false;
    }

    private void OnProfilesChanged()
    {
        RefreshProfiles();
        LoadFromConfig();       // the active config changed under us — reload the editor
        RefreshPlatformOptions();
        DeleteArmed = false;
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (_syncingProfiles || value is null || value == _host.Profiles.Active)
            return;
        DeleteArmed = false;
        _host.Profiles.Switch(value); // raises Changed → OnProfilesChanged reloads the editor
        StatusText = $"Switched to profile '{_host.Profiles.Active}'.";
    }

    /// <summary>Auto name for new/duplicated profiles: New-Profile-YYYYMMDD-HHMMSS.</summary>
    private static string NewProfileName() => $"New-Profile-{DateTime.Now:yyyyMMdd-HHmmss}";

    [RelayCommand]
    private void CreateProfile()
    {
        try
        {
            _host.Profiles.Create(NewProfileName());
            StatusText = $"Created profile '{_host.Profiles.Active}' from defaults — rename it on the left.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        try
        {
            _host.Profiles.Duplicate(NewProfileName());
            StatusText = $"Duplicated into '{_host.Profiles.Active}' — rename it on the left.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (!ProfileStore.IsValidName(ProfileNameEdit))
        {
            StatusText = "Enter a valid profile name.";
            return;
        }
        try
        {
            _host.Profiles.Rename(ProfileNameEdit);
            StatusText = $"Renamed to '{_host.Profiles.Active}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (Profiles.Count <= 1)
        {
            StatusText = "Can't delete the only profile — create another first.";
            return;
        }
        if (!DeleteArmed)
        {
            DeleteArmed = true;
            StatusText = $"Press Delete again to permanently remove '{_host.Profiles.Active}'.";
            return;
        }
        var name = _host.Profiles.Active;
        _host.Profiles.Delete(name);
        StatusText = $"Deleted profile '{name}'.";
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
    [ObservableProperty] private string _maxModelLen = "";
    [ObservableProperty] private string _vllmLogLevel = "INFO";
    [ObservableProperty] private string _quantization = "auto";
    [ObservableProperty] private string _kvCacheDtype = "auto";
    [ObservableProperty] private bool _enableToolCalling = true;
    [ObservableProperty] private string _toolCallParser = "hermes";
    [ObservableProperty] private string _extraVllmArgs = "";
    [ObservableProperty] private string _extraContainerArgs = "";

    // Precision / concurrency levers (from the dual-card parameter review).
    [ObservableProperty] private string _dtype = "auto";
    [ObservableProperty] private string _maxNumSeqs = "";
    [ObservableProperty] private bool _enforceEager;
    [ObservableProperty] private bool _trustRemoteCode;

    // Advanced group, revealed by ShowAdvancedServer.
    [ObservableProperty] private bool _showAdvancedServer;
    [ObservableProperty] private string _maxNumBatchedTokens = "";
    [ObservableProperty] private int _prefixCachingIndex; // 0 = vLLM default, 1 = on, 2 = off
    [ObservableProperty] private string _servedModelName = "";

    /// <summary>A caution shown under the model field when the id looks Ollama/GGUF-shaped.</summary>
    public string? ModelAdvisory => ModelId.Advisory(Model);
    public bool HasModelAdvisory => ModelAdvisory is not null;

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(ModelAdvisory));
        OnPropertyChanged(nameof(HasModelAdvisory));
    }

    public IReadOnlyList<string> LogLevelOptions { get; } = ["DEBUG", "INFO", "WARNING", "ERROR"];

    public IReadOnlyList<string> QuantizationOptions { get; } =
        ["auto", "awq", "gptq", "compressed-tensors", "fp8", "nvfp4", "modelopt_fp4", "bitsandbytes"];

    public IReadOnlyList<string> KvCacheOptions { get; } = ["auto", "fp8", "fp8_e5m2"];

    public IReadOnlyList<string> DtypeOptions { get; } = ["auto", "bfloat16", "float16", "float32"];

    public IReadOnlyList<string> PrefixCachingOptions { get; } = ["vLLM default", "On", "Off"];

    /// <summary>Maps the prefix-caching dropdown to the tri-state config value (default / on / off).</summary>
    private bool? PrefixCachingChoice() => PrefixCachingIndex switch { 1 => true, 2 => false, _ => null };

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

    // Platform — which platform (container images + distro source) this profile uses. Edited on
    // the Platform tab; here you only pick one (or leave it on the default).
    public const string DefaultPlatformOption = "(Default)";
    public ObservableCollection<string> PlatformOptions { get; } = [];
    [ObservableProperty] private string? _selectedPlatformOption;
    private bool _syncingPlatform;

    private void RefreshPlatformOptions()
    {
        _syncingPlatform = true;
        PlatformOptions.Clear();
        PlatformOptions.Add(DefaultPlatformOption);
        foreach (var name in _host.Platforms.Platforms)
            PlatformOptions.Add(name);
        var selected = _host.Platforms.SelectedForProfile;
        SelectedPlatformOption = string.IsNullOrWhiteSpace(selected) || !PlatformOptions.Contains(selected)
            ? DefaultPlatformOption
            : selected;
        _syncingPlatform = false;
    }

    partial void OnSelectedPlatformOptionChanged(string? value)
    {
        if (_syncingPlatform || value is null)
            return;
        _host.Platforms.SelectForProfile(value == DefaultPlatformOption ? "" : value);
        UpdatePreview();
        StatusText = value == DefaultPlatformOption
            ? $"Using the default platform ('{_host.Platforms.Default}')."
            : $"Using platform '{value}'.";
    }

    private void OnPlatformsChanged()
    {
        RefreshPlatformOptions();
        UpdatePreview();
    }

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
        if (!_loading && e.PropertyName is not (nameof(CommandPreview) or nameof(PreviewLabel) or nameof(StatusText)
                or nameof(ModelAdvisory) or nameof(HasModelAdvisory)
                or nameof(SelectedProfile) or nameof(ProfileNameEdit) or nameof(DeleteArmed)
                or nameof(SelectedPlatformOption) or nameof(ShowAdvancedServer)))
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
        _gpuControlsBuilt = true;

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
        MaxModelLen = config.Server.MaxModelLen?.ToString() ?? "";
        VllmLogLevel = string.IsNullOrWhiteSpace(config.Server.VllmLogLevel) ? "INFO" : config.Server.VllmLogLevel;
        Quantization = string.IsNullOrWhiteSpace(config.Server.Quantization) ? "auto" : config.Server.Quantization;
        KvCacheDtype = string.IsNullOrWhiteSpace(config.Server.KvCacheDtype) ? "auto" : config.Server.KvCacheDtype;
        EnableToolCalling = config.Server.EnableToolCalling;
        ToolCallParser = config.Server.ToolCallParser;
        ExtraVllmArgs = string.Join(Environment.NewLine, config.Server.ExtraArgs);
        ExtraContainerArgs = string.Join(Environment.NewLine, config.Server.ExtraContainerArgs);

        Dtype = string.IsNullOrWhiteSpace(config.Server.Dtype) ? "auto" : config.Server.Dtype;
        MaxNumSeqs = config.Server.MaxNumSeqs?.ToString() ?? "";
        EnforceEager = config.Server.EnforceEager;
        TrustRemoteCode = config.Server.TrustRemoteCode;
        MaxNumBatchedTokens = config.Server.MaxNumBatchedTokens?.ToString() ?? "";
        PrefixCachingIndex = config.Server.EnablePrefixCaching switch { true => 1, false => 2, _ => 0 };
        ServedModelName = config.Server.ServedModelName ?? "";
        // Auto-reveal the advanced group when the profile actually uses one of its options (only opens it).
        if (config.Server.MaxNumBatchedTokens is not null || config.Server.EnablePrefixCaching is not null
            || !string.IsNullOrWhiteSpace(config.Server.ServedModelName))
            ShowAdvancedServer = true;

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
        if (!TryValidate(out var port, out var gpuMem, out var maxModelLen, out var maxNumSeqs, out var maxNumBatchedTokens, out var error))
        {
            StatusText = error;
            return;
        }

        var config = _host.Config;
        config.Server.Port = port;
        config.Server.Model = Model.Trim();
        config.Server.GpuMemoryUtilization = gpuMem;
        config.Server.MaxModelLen = maxModelLen;
        config.Server.VllmLogLevel = VllmLogLevel;
        config.Server.Quantization = string.IsNullOrWhiteSpace(Quantization) ? "auto" : Quantization;
        config.Server.KvCacheDtype = string.IsNullOrWhiteSpace(KvCacheDtype) ? "auto" : KvCacheDtype;
        config.Server.Dtype = string.IsNullOrWhiteSpace(Dtype) ? "auto" : Dtype;
        config.Server.MaxNumSeqs = maxNumSeqs;
        config.Server.EnforceEager = EnforceEager;
        config.Server.TrustRemoteCode = TrustRemoteCode;
        config.Server.MaxNumBatchedTokens = maxNumBatchedTokens;
        config.Server.EnablePrefixCaching = PrefixCachingChoice();
        config.Server.ServedModelName = string.IsNullOrWhiteSpace(ServedModelName) ? null : ServedModelName.Trim();
        config.Server.EnableToolCalling = EnableToolCalling;
        config.Server.ToolCallParser = string.IsNullOrWhiteSpace(ToolCallParser) ? "hermes" : ToolCallParser.Trim();
        // Only persist GPU fields once the GPU list has actually loaded — otherwise a Save before
        // enumeration completes would clamp/overwrite the saved values (e.g. tensor-parallel → 1).
        if (_gpuControlsBuilt)
        {
            config.Server.TensorParallelSize = SelectedTensorParallel > 0 ? SelectedTensorParallel : 1;
            config.Server.VisibleGpus = ComposeVisibleGpus();
            config.Server.CudaDeviceOrder = ComposeDeviceOrder();
            config.Server.DisableGpuP2P = DisableGpuP2P;
        }
        config.Server.ExtraArgs = SplitArgs(ExtraVllmArgs);
        config.Server.ExtraContainerArgs = SplitArgs(ExtraContainerArgs);
        // Images/Distro are resolved from the selected platform (see the Platform tab), not saved here.

        config.Network.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        config.Network.AllowSystemProxy = AllowSystemProxy;
        config.AutoApproveInsideRoot = AutoApproveInsideRoot;

        ConfigStore.Save(_host.Paths, config);
        _host.Profiles.SaveActive(); // mirror the edits into the active profile file
        StatusText = $"Saved to profile '{_host.Profiles.Active}'. Restart the server to apply.";
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        LoadFromConfig();
        StatusText = "Edits discarded.";
    }

    private void UpdatePreview()
    {
        if (!TryValidate(out var port, out var gpuMem, out var maxModelLen, out var maxNumSeqs, out var maxNumBatchedTokens, out var error))
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
                MaxModelLen = maxModelLen,
                VllmLogLevel = VllmLogLevel,
                Quantization = string.IsNullOrWhiteSpace(Quantization) ? "auto" : Quantization,
                KvCacheDtype = string.IsNullOrWhiteSpace(KvCacheDtype) ? "auto" : KvCacheDtype,
                Dtype = string.IsNullOrWhiteSpace(Dtype) ? "auto" : Dtype,
                MaxNumSeqs = maxNumSeqs,
                EnforceEager = EnforceEager,
                TrustRemoteCode = TrustRemoteCode,
                MaxNumBatchedTokens = maxNumBatchedTokens,
                EnablePrefixCaching = PrefixCachingChoice(),
                ServedModelName = string.IsNullOrWhiteSpace(ServedModelName) ? null : ServedModelName.Trim(),
                EnableToolCalling = EnableToolCalling,
                ToolCallParser = string.IsNullOrWhiteSpace(ToolCallParser) ? "hermes" : ToolCallParser.Trim(),
                TensorParallelSize = SelectedTensorParallel > 0 ? SelectedTensorParallel : 1,
                VisibleGpus = ComposeVisibleGpus(),
                CudaDeviceOrder = ComposeDeviceOrder(),
                DisableGpuP2P = DisableGpuP2P,
                HfToken = string.IsNullOrWhiteSpace(_host.Config.Server.HfToken) ? null : "***",
                ExtraArgs = SplitArgs(ExtraVllmArgs),
                ExtraContainerArgs = SplitArgs(ExtraContainerArgs),
            },
            // Images come from the resolved platform (kept current by PlatformManager.Apply).
            Images = _host.Config.Images,
            Network = new NetworkConfig { Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim() },
        };

        var controller = new VllmServerController(_host.Linux, preview, PreviewHttp, _host.Paths);
        CommandPreview = controller.BuildRunCommand(_profile, preview.Server.Model);
        PreviewLabel = _profile.GpuPresent
            ? "Command this machine will run (GPU mode):"
            : "Command this machine will run (CPU mode — no NVIDIA GPU detected):";
    }

    private bool TryValidate(out int port, out double gpuMem, out int? maxModelLen,
        out int? maxNumSeqs, out int? maxNumBatchedTokens, out string error)
    {
        gpuMem = 0.9;
        maxModelLen = null;
        maxNumSeqs = null;
        maxNumBatchedTokens = null;
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

        if (!string.IsNullOrWhiteSpace(MaxModelLen))
        {
            if (!int.TryParse(MaxModelLen.Trim(), out var len) || len <= 0)
            {
                error = "Context size must be a positive whole number (or blank for the model default).";
                return false;
            }
            maxModelLen = len;
        }

        if (!TryParseOptionalPositiveInt(MaxNumSeqs, "Max concurrent sequences", out maxNumSeqs, out error))
            return false;
        if (!TryParseOptionalPositiveInt(MaxNumBatchedTokens, "Max batched tokens", out maxNumBatchedTokens, out error))
            return false;

        if (string.IsNullOrWhiteSpace(Model))
        {
            error = "Model id cannot be empty.";
            return false;
        }

        return true;
    }

    /// <summary>Parses an optional positive-int field: blank is valid (null); anything non-positive is an error.</summary>
    private static bool TryParseOptionalPositiveInt(string text, string label, out int? value, out string error)
    {
        value = null;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!int.TryParse(text.Trim(), out var n) || n <= 0)
        {
            error = $"{label} must be a positive whole number (or blank).";
            return false;
        }
        value = n;
        return true;
    }

    /// <summary>One argument per line (spaces within a line are kept, so flags with values work).</summary>
    private static List<string> SplitArgs(string text) =>
        [.. text.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0)];
}
