using System.Text.Json.Serialization;
using Exo.Models;

namespace Exo.Serialization;

/// <summary>
/// Compile-time JSON metadata for Exo-owned settings and benchmark models.
/// This removes runtime reflection from hot persistence paths and keeps them
/// compatible with trimming and future Native AOT publishing.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(NvidiaPanelSettings))]
[JsonSerializable(typeof(NetworkBenchmarkResult))]
internal sealed partial class ExoJsonContext : JsonSerializerContext;
