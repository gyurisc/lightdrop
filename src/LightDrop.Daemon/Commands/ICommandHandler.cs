using LightDrop.Core.Configuration;
using Microsoft.AspNetCore.Http;

namespace LightDrop.Daemon.Commands;

/// <summary>
/// One command a paired peer can send: <c>POST /commands/{Name}</c> on the peer listener.
/// </summary>
/// <remarks>
/// An interface because DI resolves it as a collection, the same reason <c>ICliCommand</c> is
/// one: the dispatcher looks a handler up by name, and the advertised capabilities are projected
/// from the registered set (<see cref="Core.Commands.CommandCapabilities"/>), so registering a
/// handler is all it takes to both accept and advertise a command.
/// <para>
/// <strong>Sized to one handler.</strong> An envelope, dispatcher and registry were built once
/// with no handler and deleted (<c>docs/DECISIONS.md</c> #11). This is what <c>SendFile</c> needs
/// and no more: metadata in a header, the body as a raw stream, the caller already proven to be a
/// pinned peer. When a second handler needs something else -- a persistent session, a reply
/// stream -- that is the moment to grow it.
/// </para>
/// </remarks>
internal interface ICommandHandler
{
    /// <summary>The route segment, and the capability name advertised for it.</summary>
    string Name { get; }

    Task<IResult> HandleAsync(CommandRequest request);
}

/// <summary>A command as it reaches its handler, after both trust checks.</summary>
/// <param name="Peer">
/// The pinned peer whose key the connection presented -- matched by the key, never by anything
/// the request claims. Its name is the one pinned at pairing.
/// </param>
/// <param name="Metadata">
/// The decoded <c>LightDrop-Metadata</c> header, or null if there was none. Each handler parses
/// its own shape out of it.
/// </param>
/// <param name="Http">
/// For the body stream and its limits. The body is untouched when the handler gets it: nothing
/// ahead of the handler reads it, which is what lets a handler still raise the size limit.
/// </param>
internal sealed record CommandRequest(TrustedPeer Peer, byte[]? Metadata, HttpContext Http);
