using System.Text.Json.Serialization;
using LightDrop.Core.Configuration;

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
[JsonSerializable(typeof(IReadOnlyList<DiscoveredPeer>))]
[JsonSerializable(typeof(PeerListResponse))]
[JsonSerializable(typeof(LightDropConfig))]
[JsonSerializable(typeof(LightDropState))]
[JsonSerializable(typeof(StartPairingRequest))]
[JsonSerializable(typeof(PairingStatus))]
[JsonSerializable(typeof(PairingDecision))]
[JsonSerializable(typeof(TrustedPeerListResponse))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(PairHelloRequest))]
[JsonSerializable(typeof(PairHelloResponse))]
[JsonSerializable(typeof(PairingNonce))]
[JsonSerializable(typeof(SendRequest))]
[JsonSerializable(typeof(SendResponse))]
[JsonSerializable(typeof(SendFileMetadata))]
[JsonSerializable(typeof(SendFileResult))]
public partial class LightDropJsonContext : JsonSerializerContext;
