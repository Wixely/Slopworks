using System.Text.Json.Serialization;

namespace Slopworks.Core.Logging;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandLogEntry))]
internal sealed partial class CommandLogJsonContext : JsonSerializerContext;
