using System.Text.Json;

namespace LightDrop.Core.Protocol;

/// <summary>
/// The single message shape every LightDrop command travels in.
/// </summary>
/// <remarks>
/// LightDrop is command-oriented rather than endpoint-oriented: adding clipboard, screenshots,
/// notifications or remote actions means registering another handler, not designing another
/// endpoint with its own client code and versioning surface.
/// <para>
/// The envelope carries <em>metadata only</em>. Bulk payloads such as file contents must stream
/// on a separate channel referenced by <see cref="Id"/> — base64 inside JSON costs roughly a
/// third in overhead and forces the whole payload to be buffered in memory.
/// </para>
/// </remarks>
public sealed record CommandEnvelope
{
    /// <summary>Correlates a result with its command, and names any associated data stream.</summary>
    public required string Id { get; init; }

    /// <summary>The command name, matching <see cref="ICommandHandler.CommandName"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Command-specific metadata. Never bulk data.</summary>
    public JsonElement? Payload { get; init; }
}
