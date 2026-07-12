using System.Text.Json.Serialization;

namespace Slopworks.App;

/// <summary>Machine-readable server state for `status --json` (consumed by embedding apps).</summary>
public sealed record CliServerState(
    string ContainerState,
    bool ApiHealthy,
    string Endpoint,
    string Model,
    int Port);

/// <summary>Machine-readable model list for `models --json`.</summary>
public sealed record CliModelsReport(bool ApiHealthy, IReadOnlyList<string> Models);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliServerState))]
[JsonSerializable(typeof(CliModelsReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
