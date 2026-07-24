using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core;
using Slopworks.Core.Config;

namespace Slopworks.App.ViewModels;

/// <summary>
/// The Templates tab: a library of chat-template (*.jinja) files. A template is attached to the
/// current server model — selecting one here sets that model's ChatTemplate (saved in models.json),
/// the same value the Models tab's per-model dropdown edits. "None" leaves the model's built-in template.
/// </summary>
public partial class TemplatesViewModel : ObservableObject, IActivatableTab
{
    private readonly SlopworksHost _host;
    private readonly TemplateStore _store;
    private bool _syncing;

    // Default web proxy is honoured; the HF token (if any) is sent for gated repos.
    private static readonly HttpClient Http = new();

    /// <summary>List sentinel meaning "no override — use the model's built-in template".</summary>
    public const string NoneOption = "None — use the model's own template";

    public TemplatesViewModel(SlopworksHost host)
    {
        _host = host;
        _store = new TemplateStore(host.Paths);
        _host.Profiles.Changed += OnProfilesChanged;
        RebuildList();
    }

    public ObservableCollection<string> Templates { get; } = [];

    [ObservableProperty] private string? _selectedTemplate;
    [ObservableProperty] private string _templateContent = "";
    [ObservableProperty] private string _newTemplateName = "";
    [ObservableProperty] private string _renameName = "";
    [ObservableProperty] private string _importRepo = "";
    [ObservableProperty] private string _importPath = "chat_template.jinja";
    [ObservableProperty] private bool _deleteArmed;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _statusText =
        "Add a chat template, then select it to use it as the server's --chat-template.";

    /// <summary>The folder where the .jinja files live (for the "Open folder" button).</summary>
    public string TemplatesFolder => _store.Dir;

    /// <summary>The server model this template is being set for (whatever you select applies to it).</summary>
    public string CurrentModel => string.IsNullOrWhiteSpace(_host.Models.ActiveModelId)
        ? "(no model selected)"
        : _host.Models.ActiveModelId;

    /// <summary>A real template (not None) is selected — the editor/rename/delete apply to it.</summary>
    public bool HasSelection => SelectedTemplate is not null && SelectedTemplate != NoneOption;

    public void Activate() => RebuildList();

    public void Deactivate() { }

    private void OnProfilesChanged() => RebuildList();

    [RelayCommand]
    private void Refresh()
    {
        RebuildList(); // re-reads the folder (picks up .jinja files dropped in directly)
        StatusText = "Refreshed the template list from disk.";
    }

    private void RebuildList()
    {
        _syncing = true;
        Templates.Clear();
        Templates.Add(NoneOption);
        foreach (var name in _store.List())
            Templates.Add(name);

        // The selection tracks the current model's attached template.
        var active = _host.Models.ActiveModelTemplate;
        SelectedTemplate = !string.IsNullOrWhiteSpace(active) && Templates.Contains(active) ? active : NoneOption;
        OnPropertyChanged(nameof(CurrentModel));
        _syncing = false;
    }

    partial void OnSelectedTemplateChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        DeleteArmed = false;
        if (value is not null)
        {
            RenameName = value == NoneOption ? "" : value;
            TemplateContent = value == NoneOption ? "" : _store.Load(value);
        }

        if (_syncing || value is null)
            return;

        // Selecting here attaches the template to the current server model (saved in models.json).
        var template = value == NoneOption ? null : value;
        _host.Models.SetActiveModelTemplate(template);
        StatusText = template is null
            ? $"'{CurrentModel}' will use the model's built-in template."
            : $"'{template}' is now the chat template for '{CurrentModel}'. Restart the server to apply.";
    }

    [RelayCommand]
    private void SaveContent()
    {
        if (!HasSelection)
            return;
        _store.Save(SelectedTemplate!, TemplateContent);
        StatusText = $"Saved '{SelectedTemplate}'. Restart the server to apply.";
    }

    [RelayCommand]
    private void NewTemplate()
    {
        var name = string.IsNullOrWhiteSpace(NewTemplateName)
            ? $"template-{DateTime.Now:yyyyMMdd-HHmmss}"
            : NewTemplateName.Trim();
        try
        {
            var clean = _store.Create(name, "");
            NewTemplateName = "";
            RebuildList();
            SelectedTemplate = clean; // selecting also makes it the server's template
            StatusText = $"Created '{clean}'. Paste or edit the Jinja on the right, then Save.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>Called by the view once a file picker has read a .jinja from disk.</summary>
    public void ImportFromFile(string suggestedName, string content)
    {
        try
        {
            var clean = _store.Create(UniqueName(suggestedName), content);
            RebuildList();
            SelectedTemplate = clean;
            StatusText = $"Imported '{clean}' ({content.Length:N0} chars).";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportFromHfAsync()
    {
        var repo = ModelId.Normalize(ImportRepo).Trim();
        var path = string.IsNullOrWhiteSpace(ImportPath) ? "chat_template.jinja" : ImportPath.Trim();
        if (repo.Length == 0)
        {
            StatusText = "Enter a HuggingFace repo id to import from.";
            return;
        }

        IsImporting = true;
        StatusText = $"Downloading {path} from {repo}…";
        try
        {
            var content = await DownloadHfFileAsync(repo, path, CancellationToken.None);
            var clean = _store.Create(UniqueName($"{repo.Replace('/', '-')}-{Path.GetFileNameWithoutExtension(path)}"), content);
            RebuildList();
            SelectedTemplate = clean;
            StatusText = $"Imported '{clean}' from {repo}. Review it before use, then Save.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    private async Task<string> DownloadHfFileAsync(string repo, string path, CancellationToken ct)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(_host.Config.Server.HuggingFaceEndpoint)
            ? "https://huggingface.co"
            : _host.Config.Server.HuggingFaceEndpoint).TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{repo}/resolve/main/{path}");
        if (_host.Config.Server.HfToken is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    [RelayCommand]
    private void DuplicateTemplate()
    {
        if (!HasSelection)
            return;
        try
        {
            var clean = _store.Duplicate(SelectedTemplate!, UniqueName(SelectedTemplate!));
            RebuildList();
            SelectedTemplate = clean;
            StatusText = $"Duplicated into '{clean}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void RenameTemplate()
    {
        if (!HasSelection)
            return;
        if (string.IsNullOrWhiteSpace(RenameName))
        {
            StatusText = "Enter a new name.";
            return;
        }
        var old = SelectedTemplate!;
        try
        {
            var clean = _store.Rename(old, RenameName);
            _host.Models.RenameTemplateReferences(old, clean); // repoint every model that used it
            RebuildList(); // selection re-syncs to the current model's (now renamed) template
            StatusText = $"Renamed to '{clean}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (!HasSelection)
            return;
        if (!DeleteArmed)
        {
            DeleteArmed = true;
            StatusText = $"Press Delete again to permanently remove '{SelectedTemplate}'.";
            return;
        }
        var name = SelectedTemplate!;
        _store.Delete(name);
        _host.Models.ClearTemplateReferences(name); // any model using it falls back to its built-in template
        RebuildList(); // selection falls back to None
        StatusText = $"Deleted '{name}'.";
    }

    /// <summary>A clean file base that doesn't collide with an existing template (appends -2, -3, …).</summary>
    private string UniqueName(string baseName)
    {
        var clean = ProfileStore.Clean(baseName);
        if (clean.Length == 0)
            clean = "template";
        var name = clean;
        var n = 2;
        while (_store.Exists(name))
            name = $"{clean}-{n++}";
        return name;
    }
}
