using Slopworks.Core.Config;

namespace Slopworks.App;

/// <summary>
/// Named platforms (container images + distro source). A settings profile references a platform by
/// name (empty = the default platform); <see cref="Apply"/> resolves that into the live config's
/// Images/Distro so the rest of the app (server controller, setup steps) keeps reading config as-is.
/// </summary>
public sealed class PlatformManager
{
    private readonly SlopworksConfig _config;
    private readonly ProfileManager _profiles;
    private readonly PlatformStore _store;

    /// <summary>The platform list, default, or the profile's selection changed.</summary>
    public event Action? Changed;

    public PlatformManager(SlopworksPaths paths, SlopworksConfig config, ProfileManager profiles)
    {
        _config = config;
        _profiles = profiles;
        _store = new PlatformStore(paths);
        // Seed a "default" platform from the current images/distro on first run.
        _store.EnsureInitialized(SnapshotOfConfig());
        Apply();
    }

    public IReadOnlyList<string> Platforms => _store.List();
    public string Default => _store.Default;

    /// <summary>Name of the active profile's platform selection ("" = use the default).</summary>
    public string SelectedForProfile => _config.Platform;

    /// <summary>The platform actually in effect (the selection, or the default when unset/missing).</summary>
    public string ResolvedName =>
        !string.IsNullOrWhiteSpace(_config.Platform) && _store.Exists(_config.Platform) ? _config.Platform : Default;

    public PlatformProfile Load(string name) => _store.Load(name);

    /// <summary>Resolve the active profile's platform into the live config (images, distro, rootfs source).</summary>
    public void Apply()
    {
        var platform = _store.Load(ResolvedName);
        _config.Images = platform.Images;
        _config.Distro = platform.Distro;
        _config.Artifacts["rootfs"] = platform.Rootfs;
    }

    private PlatformProfile SnapshotOfConfig() => new()
    {
        Images = _config.Images,
        Distro = _config.Distro,
        Rootfs = _config.Artifacts.TryGetValue("rootfs", out var r) ? r : new ArtifactSource(),
    };

    /// <summary>Set which platform the active profile uses ("" = default), refresh config, persist.</summary>
    public void SelectForProfile(string platformNameOrEmpty)
    {
        _config.Platform = string.IsNullOrWhiteSpace(platformNameOrEmpty) ? "" : platformNameOrEmpty.Trim();
        Apply();
        _profiles.SaveActive();
        Changed?.Invoke();
    }

    public void SetDefault(string name)
    {
        _store.SetDefault(name);
        Apply();
        _profiles.SaveActive();
        Changed?.Invoke();
    }

    /// <summary>Save edits to a platform; if it's the one in effect, refresh the live config too.</summary>
    public void Save(string name, PlatformProfile platform)
    {
        var affectsCurrent = string.Equals(name, ResolvedName, StringComparison.OrdinalIgnoreCase);
        _store.Save(name, platform);
        if (affectsCurrent)
        {
            Apply();
            _profiles.SaveActive();
        }
        Changed?.Invoke();
    }

    public string Create(string name)
    {
        var created = _store.Create(name, new PlatformProfile { Images = new ImagesConfig(), Distro = new DistroConfig() });
        Changed?.Invoke();
        return created;
    }

    public string Duplicate(string source, string newName)
    {
        var created = _store.Duplicate(source, newName);
        Changed?.Invoke();
        return created;
    }

    public string Rename(string oldName, string newName)
    {
        var renamed = _store.Rename(oldName, newName);
        // The profile might have referenced the old name.
        if (string.Equals(_config.Platform, oldName, StringComparison.OrdinalIgnoreCase))
            _config.Platform = renamed;
        Apply();
        _profiles.SaveActive();
        Changed?.Invoke();
        return renamed;
    }

    public void Delete(string name)
    {
        _store.Delete(name);
        if (_store.List().Count == 0) // never leave zero platforms
            _store.Save(PlatformStore.DefaultName, SnapshotOfConfig());
        _store.SetDefault(_store.Default); // persist a valid default (the getter falls back)
        if (_config.Platform.Length > 0 && !_store.Exists(_config.Platform))
            _config.Platform = ""; // the profile's platform is gone → fall back to default
        Apply();
        _profiles.SaveActive();
        Changed?.Invoke();
    }
}
