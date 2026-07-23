using Slopworks.Core.Config;

namespace Slopworks.App;

/// <summary>
/// Coordinates settings profiles across the UI. Wraps the file-level <see cref="ProfileStore"/> and
/// swaps the <em>shared</em> live config in place on switch, so every holder of the config reference
/// (server controller, model inspector, VMs) picks up the new values. Raises <see cref="Changed"/>
/// so pages can refresh their dropdowns and reload the editor.
/// </summary>
public sealed class ProfileManager
{
    private readonly SlopworksPaths _paths;
    private readonly SlopworksConfig _config; // the shared host.Config instance
    private readonly ProfileStore _store;

    /// <summary>The active profile or the profile list changed — pages should re-sync.</summary>
    public event Action? Changed;

    /// <summary>The System page asked to edit the active profile — the shell should open Settings.</summary>
    public event Action? EditRequested;

    public ProfileManager(SlopworksPaths paths, SlopworksConfig config)
    {
        _paths = paths;
        _config = config;
        _store = new ProfileStore(paths);
        _store.EnsureInitialized(config); // migrate an existing config.json into a "default" profile
    }

    public IReadOnlyList<string> Profiles => _store.List();
    public string Active => _store.Active;

    /// <summary>Persist the current live config into the active profile file (called on Settings save).</summary>
    public void SaveActive() => _store.Save(Active, _config);

    /// <summary>Switch the active profile, loading it into the live config and syncing the working copy.</summary>
    public void Switch(string name)
    {
        if (string.IsNullOrEmpty(name) || name == Active || !_store.Exists(name))
            return;

        _store.Save(Active, _config);   // flush any pending edits to the outgoing profile
        Apply(name);
        Changed?.Invoke();
    }

    /// <summary>Create a new profile from defaults and switch to it. Returns the clean name.</summary>
    public string Create(string name)
    {
        _store.Save(Active, _config);   // don't lose current edits when we switch away
        var created = _store.Create(name, new SlopworksConfig());
        Apply(created);
        Changed?.Invoke();
        return created;
    }

    /// <summary>Duplicate the active profile under a new name and switch to the copy.</summary>
    public string Duplicate(string newName)
    {
        _store.Save(Active, _config);   // ensure the copy reflects current edits
        var created = _store.Duplicate(Active, newName);
        Apply(created);
        Changed?.Invoke();
        return created;
    }

    /// <summary>Rename the active profile.</summary>
    public string Rename(string newName)
    {
        _store.Save(Active, _config); // flush pending edits before the file moves
        var renamed = _store.Rename(Active, newName);
        Changed?.Invoke();
        return renamed;
    }

    /// <summary>Delete a profile. If it was active, fall back to another (or a fresh default).</summary>
    public void Delete(string name)
    {
        var wasActive = name == Active;
        _store.Delete(name);

        if (wasActive)
        {
            var next = _store.List().FirstOrDefault() ?? _store.Create(ProfileStore.DefaultName, new SlopworksConfig());
            Apply(next);
        }

        Changed?.Invoke();
    }

    public void RequestEdit() => EditRequested?.Invoke();

    /// <summary>Load a profile into the shared config, point the pointer at it, and sync config.json.</summary>
    private void Apply(string name)
    {
        _config.CopyFrom(_store.Load(name));
        _store.SetActive(name);
        ConfigStore.Save(_paths, _config); // keep the working copy (config.json) consistent
    }
}
