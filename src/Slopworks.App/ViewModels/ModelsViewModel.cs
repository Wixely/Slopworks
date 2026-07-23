using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Config;
using Slopworks.Core.Server;

namespace Slopworks.App.ViewModels;

/// <summary>One saved model in the library — wraps a <see cref="ModelEntry"/> with observable notes/metadata.</summary>
public partial class ModelItemViewModel : ObservableObject
{
    public ModelEntry Entry { get; }

    public ModelItemViewModel(ModelEntry entry, string hfBaseUrl)
    {
        Entry = entry;
        _notes = entry.Notes;
        Url = $"{hfBaseUrl}/{entry.Id}";
        RefreshMetadata();
    }

    public string Id => Entry.Id;

    /// <summary>The model's HuggingFace page, for the "Open" link.</summary>
    public string Url { get; }

    [ObservableProperty] private string _notes;
    partial void OnNotesChanged(string value) => Entry.Notes = value; // mirror edits back to the model

    // Cached HF metadata, shown in the list and detail panel.
    [ObservableProperty] private string _verdictLabel = "";
    [ObservableProperty] private string _metadata = "";
    [ObservableProperty] private string _facts = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private IBrush _verdictBrush = Brushes.Gray;
    [ObservableProperty] private bool _hasMetadata;

    /// <summary>Update from a fresh HF check and cache the full results into the entry (persisted to models.json).</summary>
    public void ApplyInspection(ModelInspection result, string checkedAt)
    {
        Entry.Verdict = result.Verdict.ToString();
        Entry.Summary = result.Summary;
        Entry.Detail = result.Detail;
        Entry.Quant = result.Quant;
        Entry.Architecture = result.Architecture;
        Entry.Parameters = result.Parameters;
        Entry.MaxContext = result.MaxContext;
        Entry.Dtype = result.Dtype;
        Entry.Pipeline = result.Pipeline;
        Entry.License = result.License;
        Entry.Gated = result.Gated;
        Entry.Downloads = result.Downloads;
        Entry.CheckedAt = checkedAt;
        RefreshMetadata();
    }

    private void RefreshMetadata()
    {
        var line1 = new List<string>();
        if (!string.IsNullOrEmpty(Entry.Quant)) line1.Add($"quant: {Entry.Quant}");
        if (Entry.Parameters is > 0) line1.Add($"{FormatCount(Entry.Parameters)} params");
        if (!string.IsNullOrEmpty(Entry.Architecture)) line1.Add(Entry.Architecture!);
        Metadata = string.Join("  ·  ", line1);

        var line2 = new List<string>();
        if (!string.IsNullOrEmpty(Entry.Pipeline)) line2.Add($"task: {Entry.Pipeline}");
        if (Entry.MaxContext is > 0) line2.Add($"ctx {Entry.MaxContext:N0}");
        if (!string.IsNullOrEmpty(Entry.Dtype)) line2.Add(Entry.Dtype!);
        if (!string.IsNullOrEmpty(Entry.License)) line2.Add(Entry.License!);
        if (Entry.Downloads is > 0) line2.Add($"{FormatCount(Entry.Downloads)} downloads");
        if (Entry.Gated) line2.Add("gated");
        if (!string.IsNullOrEmpty(Entry.CheckedAt)) line2.Add($"checked {Entry.CheckedAt}");
        Facts = string.Join("  ·  ", line2);

        VerdictLabel = Entry.Summary ?? Entry.Verdict ?? "";
        DetailText = Entry.Detail ?? "";
        VerdictBrush = BrushForVerdict(Entry.Verdict);
        HasMetadata = VerdictLabel.Length > 0 || Metadata.Length > 0 || Facts.Length > 0;
    }

    private static string FormatCount(long? value) => ModelInspection.FormatCount(value);

    private static IBrush BrushForVerdict(string? verdict) => verdict switch
    {
        nameof(ModelVerdict.Servable) => new SolidColorBrush(Color.Parse("#3FB950")),
        nameof(ModelVerdict.Caution) => new SolidColorBrush(Color.Parse("#E0A030")),
        nameof(ModelVerdict.Unservable) => new SolidColorBrush(Color.Parse("#F0603E")),
        _ => new SolidColorBrush(Color.Parse("#9AA0A6")),
    };
}

/// <summary>
/// The Models tab: a library of models with notes, plus (behind a remembered "advanced" toggle)
/// HF investigation — verdict, quant type, param size. The selected model is the server's model.
/// </summary>
public partial class ModelsViewModel : ObservableObject, IActivatableTab
{
    private readonly SlopworksHost _host;
    private bool _syncingSelection;

    public ObservableCollection<ModelItemViewModel> Models { get; } = [];

    [ObservableProperty] private ModelItemViewModel? _selectedModel;
    [ObservableProperty] private string _newModelId = "";
    [ObservableProperty] private string _statusText = "Save the models you use, add notes, and check them against HuggingFace.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckModelCommand))]
    private bool _isCheckingModel;

    [ObservableProperty] private bool _isTokenWarningVisible;

    public ModelsViewModel(SlopworksHost host)
    {
        _host = host;
        RebuildList();
    }

    /// <summary>Remembered across sessions in the model library file.</summary>
    public bool ShowAdvanced
    {
        get => _host.Models.ShowAdvanced;
        set
        {
            _host.Models.ShowAdvanced = value;
            OnPropertyChanged();
            // The first time advanced is switched on without a token, warn about the HF quota — once, ever.
            if (value && !HasHfToken && !_host.Models.TokenWarningShown)
            {
                IsTokenWarningVisible = true;
                _host.Models.TokenWarningShown = true;
            }
        }
    }

    /// <summary>The HuggingFace token now lives here; it's used for gated models and HF checks.</summary>
    public string HfToken
    {
        get => _host.Config.Server.HfToken ?? "";
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_host.Config.Server.HfToken == trimmed)
                return;
            _host.Config.Server.HfToken = trimmed;
            _host.Profiles.SaveActive();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHfToken));
            if (trimmed is not null)
                IsTokenWarningVisible = false;
        }
    }

    public bool HasHfToken => !string.IsNullOrWhiteSpace(_host.Config.Server.HfToken);

    /// <summary>
    /// The folder where downloaded models are cached. On Windows this is inside the WSL distro (kept
    /// on the fast Linux filesystem, not /mnt/c), browsable from Explorer via its \\wsl.localhost path;
    /// on Linux it's under the Slopworks data folder.
    /// </summary>
    public string ModelsFolder => OperatingSystem.IsWindows()
        ? $@"\\wsl.localhost\{SlopworksPaths.DistroName}" + _host.Server.HfCachePath.Replace('/', '\\')
        : _host.Server.HfCachePath;

    private string HfBase => (string.IsNullOrWhiteSpace(_host.Config.Server.HuggingFaceEndpoint)
        ? "https://huggingface.co"
        : _host.Config.Server.HuggingFaceEndpoint).TrimEnd('/');

    /// <summary>HuggingFace page URL of the selected model, for the Open link.</summary>
    public string? SelectedModelUrl => SelectedModel?.Url;

    public void Activate()
    {
        RebuildList();
        // The token lives in the active profile — re-read it in case the profile changed while away.
        OnPropertyChanged(nameof(HfToken));
        OnPropertyChanged(nameof(HasHfToken));
    }

    public void Deactivate() { }

    private void RebuildList()
    {
        _syncingSelection = true;
        Models.Clear();
        foreach (var entry in _host.Models.Models)
            Models.Add(new ModelItemViewModel(entry, HfBase));
        SelectedModel = Models.FirstOrDefault(m => m.Id == _host.Models.ActiveModelId) ?? Models.FirstOrDefault();
        _syncingSelection = false;
    }

    public bool HasSelection => SelectedModel is not null;

    partial void OnSelectedModelChanged(ModelItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedModelUrl));
        CheckModelCommand.NotifyCanExecuteChanged();
        if (_syncingSelection || value is null)
            return;
        _host.Models.Select(value.Id); // becomes the Server tab's model
        StatusText = $"'{value.Id}' is now the server model.";
    }

    [RelayCommand]
    private void AddModel()
    {
        var id = Slopworks.Core.ModelId.Normalize(NewModelId).Trim();
        if (id.Length == 0)
        {
            StatusText = "Enter a model id to add.";
            return;
        }
        if (Models.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "That model is already in the library.";
            return;
        }
        var vm = new ModelItemViewModel(_host.Models.Add(id), HfBase);
        Models.Add(vm);
        SelectedModel = vm; // selecting also makes it the server model
        NewModelId = "";
        StatusText = $"Added '{id}'.";
    }

    [RelayCommand]
    private void DeleteModel()
    {
        if (SelectedModel is not { } vm)
            return;
        _host.Models.Remove(vm.Entry);
        Models.Remove(vm);
        SelectedModel = Models.FirstOrDefault();
        StatusText = $"Removed '{vm.Id}'.";
    }

    [RelayCommand]
    private void SaveNotes()
    {
        _host.Models.Save();
        StatusText = "Notes saved.";
    }

    private bool CanCheck => !IsCheckingModel && SelectedModel is not null;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckModelAsync()
    {
        if (SelectedModel is not { } vm)
            return;

        IsCheckingModel = true;
        StatusText = $"Checking {vm.Id} on HuggingFace…";
        try
        {
            var result = await _host.ModelInspector.InspectAsync(vm.Id, CancellationToken.None);
            vm.ApplyInspection(result, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            _host.Models.Save(); // persist the metadata into models.json for this model
            StatusText = $"{vm.Id}: {result.Summary}";
        }
        catch (Exception ex)
        {
            StatusText = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingModel = false;
        }
    }

    [RelayCommand]
    private void DismissTokenWarning() => IsTokenWarningVisible = false;
}
