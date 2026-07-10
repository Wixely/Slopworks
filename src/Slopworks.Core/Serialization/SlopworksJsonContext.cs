using System.Text.Json.Serialization;
using Slopworks.Core.Config;
using Slopworks.Core.State;

namespace Slopworks.Core.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SlopworksConfig))]
[JsonSerializable(typeof(JournalData))]
internal sealed partial class SlopworksJsonContext : JsonSerializerContext;
