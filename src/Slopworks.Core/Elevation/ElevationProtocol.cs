using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slopworks.Core.Elevation;

/// <summary>
/// File-based round trip for commands that need administrator rights: the app writes a
/// request file, relaunches itself elevated as "--elevated-worker &lt;file&gt;", and the
/// worker writes the result next to it. Keeps elevation auditable and avoids trying to
/// redirect output across the UAC boundary (which Windows does not allow).
/// </summary>
public sealed class ElevatedRequest
{
    public string Exe { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public string? WorkingDir { get; set; }
    public string? StdinText { get; set; }
    public bool Utf16Output { get; set; }

    /// <summary>Extra environment for the elevated child (e.g. WSL_UTF8=1 for wsl.exe).</summary>
    public Dictionary<string, string>? Env { get; set; }
}

public sealed class ElevatedResponse
{
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public double DurationMs { get; set; }
}

public static class ElevationProtocol
{
    public static string ResponsePath(string requestPath) => requestPath + ".result";

    public static void WriteRequest(string path, ElevatedRequest request)
        => File.WriteAllText(path, JsonSerializer.Serialize(request, ElevationJsonContext.Default.ElevatedRequest));

    public static ElevatedRequest ReadRequest(string path)
        => JsonSerializer.Deserialize(File.ReadAllText(path), ElevationJsonContext.Default.ElevatedRequest)
            ?? throw new InvalidOperationException($"Empty elevation request at {path}");

    public static void WriteResponse(string requestPath, ElevatedResponse response)
    {
        var tmp = ResponsePath(requestPath) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(response, ElevationJsonContext.Default.ElevatedResponse));
        File.Move(tmp, ResponsePath(requestPath), overwrite: true);
    }

    public static ElevatedResponse? TryReadResponse(string requestPath)
    {
        var path = ResponsePath(requestPath);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize(File.ReadAllText(path), ElevationJsonContext.Default.ElevatedResponse);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ElevatedRequest))]
[JsonSerializable(typeof(ElevatedResponse))]
internal sealed partial class ElevationJsonContext : JsonSerializerContext;
