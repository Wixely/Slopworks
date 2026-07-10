using System.Text.Json;
using Slopworks.Core.Serialization;

namespace Slopworks.Core.Config;

public static class ConfigStore
{
    /// <summary>Loads config.json, creating it with defaults on first run.</summary>
    public static SlopworksConfig LoadOrCreate(SlopworksPaths paths)
    {
        if (File.Exists(paths.ConfigFile))
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(paths.ConfigFile), SlopworksJsonContext.Default.SlopworksConfig);
            if (loaded is not null)
                return loaded;
        }

        var config = new SlopworksConfig();
        Save(paths, config);
        return config;
    }

    public static void Save(SlopworksPaths paths, SlopworksConfig config)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, SlopworksJsonContext.Default.SlopworksConfig));
        File.Move(tmp, paths.ConfigFile, overwrite: true);
    }
}
