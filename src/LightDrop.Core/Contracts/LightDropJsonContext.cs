using System.Text.Json.Serialization;
using LightDrop.Core.Configuration;
using LightDrop.Core.Protocol;

namespace LightDrop.Core.Contracts;

/// <summary>
/// Source-generated JSON metadata for everything LightDrop serializes.
/// </summary>
/// <remarks>
/// Source generation rather than reflection keeps trimming and NativeAOT viable, which is what
/// "small, single portable executable" eventually requires. Every serializable type must be
/// listed here — a missing entry fails at runtime, not at build.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(LightDropConfig))]
[JsonSerializable(typeof(LightDropState))]
[JsonSerializable(typeof(CommandEnvelope))]
[JsonSerializable(typeof(CommandResult))]
public partial class LightDropJsonContext : JsonSerializerContext;
