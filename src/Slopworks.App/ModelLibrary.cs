using Slopworks.Core.Config;

namespace Slopworks.App;

/// <summary>
/// The user's saved model library (global, not per-profile) plus the Models-tab "show advanced"
/// preference. Selecting a model writes it into the live config + active profile so the Server tab
/// picks it up — the mirror of how <see cref="ProfileManager"/> works for settings.
/// </summary>
public sealed class ModelLibrary
{
    private readonly ModelLibraryStore _store;
    private readonly SlopworksConfig _config;
    private readonly ProfileManager _profiles;
    private readonly ModelLibraryDoc _doc;

    public ModelLibrary(SlopworksPaths paths, SlopworksConfig config, ProfileManager profiles)
    {
        _store = new ModelLibraryStore(paths);
        _config = config;
        _profiles = profiles;
        _doc = _store.Load();
        EnsureContains(config.Server.Model); // the default/active model is always in the library
    }

    /// <summary>Make sure a model id exists in the library (adds it if missing), so pickers can show it.</summary>
    public void EnsureContains(string id)
    {
        var trimmed = (id ?? "").Trim();
        if (trimmed.Length == 0 || _doc.Models.Any(m => m.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return;
        _doc.Models.Add(new ModelEntry { Id = trimmed });
        Save();
    }

    public IReadOnlyList<ModelEntry> Models => _doc.Models;

    /// <summary>The model the server is currently set to use (config.Server.Model).</summary>
    public string ActiveModelId => _config.Server.Model;

    /// <summary>Whether the Models tab shows the advanced HF-investigation features. Persisted.</summary>
    public bool ShowAdvanced
    {
        get => _doc.ShowAdvanced;
        set
        {
            if (_doc.ShowAdvanced == value)
                return;
            _doc.ShowAdvanced = value;
            Save();
        }
    }

    /// <summary>Whether the no-token quota warning has already been shown once (persisted).</summary>
    public bool TokenWarningShown
    {
        get => _doc.TokenWarningShown;
        set
        {
            if (_doc.TokenWarningShown == value)
                return;
            _doc.TokenWarningShown = value;
            Save();
        }
    }

    public ModelEntry Add(string id)
    {
        var entry = new ModelEntry { Id = id.Trim() };
        _doc.Models.Add(entry);
        Save();
        return entry;
    }

    public void Remove(ModelEntry entry)
    {
        _doc.Models.Remove(entry);
        Save();
    }

    /// <summary>Persist the library (called after edits to notes or cached metadata).</summary>
    public void Save() => _store.Save(_doc);

    /// <summary>Point the server/config at this model and persist it to the active profile.</summary>
    public void Select(string id)
    {
        _config.Server.Model = id.Trim();
        _profiles.SaveActive();
    }
}
