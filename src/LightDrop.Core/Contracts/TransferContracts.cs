namespace LightDrop.Core.Contracts;

// ---- Loopback API: what `lightdrop send` asks the local daemon. ----

/// <summary>Body of <c>POST /api/send</c>.</summary>
public sealed record SendRequest
{
    /// <summary>
    /// The file to send, as an absolute path. The CLI resolves it: the daemon's working directory
    /// is not the user's.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>A paired device's name or device-id prefix.</summary>
    public required string Peer { get; init; }

    /// <summary>
    /// Chosen by the caller, so it can ask <c>GET /api/send/{id}</c> how far this send has got
    /// while this request is still waiting. Optional: without one, the send is not reported.
    /// </summary>
    public Guid? TransferId { get; init; }
}

/// <summary>Response of <c>GET /api/send/{id}</c>: how far a running send has got.</summary>
public sealed record SendProgress
{
    /// <summary>The file's length, as declared to the receiver.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// Bytes read from the file so far. Runs ahead of what the receiver has by whatever the network
    /// is buffering, so it can reach <see cref="Length"/> a moment before the send completes.
    /// </summary>
    public required long Sent { get; init; }
}

/// <summary>Response of <c>POST /api/send</c> once the other device has the whole file.</summary>
public sealed record SendResponse
{
    /// <summary>
    /// The name the file was saved under on the other device -- not necessarily its name here,
    /// after sanitizing and collision numbering. A name only, never the receiver's path.
    /// </summary>
    public required string SavedAs { get; init; }

    /// <summary>Bytes sent.</summary>
    public required long Length { get; init; }

    public required string PeerDeviceId { get; init; }

    /// <summary>The name pinned at pairing.</summary>
    public required string PeerName { get; init; }
}

// ---- Peer protocol: the SendFile command between two paired daemons. ----

/// <summary>
/// The metadata of a <c>SendFile</c> command, carried in the <c>LightDrop-Metadata</c> header.
/// The file's bytes are the request body, streamed; never in here.
/// </summary>
public sealed record SendFileMetadata
{
    /// <summary>
    /// What the sender calls the file. Attacker-controlled: the receiver keeps only what
    /// <see cref="Transfers.ReceivedFileName.Sanitize"/> leaves of it.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>The exact body length. The request's <c>Content-Length</c> must equal it.</summary>
    public required long Length { get; init; }
}

/// <summary>The receiver's answer to a completed <c>SendFile</c>.</summary>
public sealed record SendFileResult
{
    /// <summary>The name the file landed under in the download folder. Never a path.</summary>
    public required string SavedAs { get; init; }
}
