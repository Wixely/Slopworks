using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slopworks.Core.Config;

public sealed record WindowSize(double Width, double Height);

/// <summary>
/// Remembers the main window size (never position) across sessions, in a tiny JSON file
/// inside the Slopworks state folder. Corrupt or silly values fall back to defaults.
/// </summary>
public static class WindowSizeStore
{
    public const double MinWidth = 600;
    public const double MinHeight = 400;

    public static string FilePath(SlopworksPaths paths) => Path.Combine(paths.StateDir, "window.json");

    public static WindowSize? Load(SlopworksPaths paths)
    {
        try
        {
            var file = FilePath(paths);
            if (!File.Exists(file))
                return null;

            var size = JsonSerializer.Deserialize(File.ReadAllText(file), WindowSizeJsonContext.Default.WindowSize);
            return size is { Width: >= MinWidth, Height: >= MinHeight } && double.IsFinite(size.Width) && double.IsFinite(size.Height)
                ? size
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public static void Save(SlopworksPaths paths, double width, double height)
    {
        if (width < MinWidth || height < MinHeight || !double.IsFinite(width) || !double.IsFinite(height))
            return;

        try
        {
            Directory.CreateDirectory(paths.StateDir);
            File.WriteAllText(FilePath(paths),
                JsonSerializer.Serialize(new WindowSize(width, height), WindowSizeJsonContext.Default.WindowSize));
        }
        catch (IOException)
        {
            // Losing a window-size save is never worth surfacing an error for.
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WindowSize))]
internal sealed partial class WindowSizeJsonContext : JsonSerializerContext;
