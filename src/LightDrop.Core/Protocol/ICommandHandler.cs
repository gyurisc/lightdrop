namespace LightDrop.Core.Protocol;

/// <summary>
/// Handles one LightDrop command.
/// </summary>
/// <remarks>
/// Deliberately transport-agnostic. Handlers know nothing about HTTP or WebSockets, so a
/// persistent socket transport can be added later without touching a single handler.
/// <para>
/// Registering an implementation in DI is all it takes to advertise the command: the name
/// flows into <see cref="CommandRegistry.Capabilities"/> and out through the health endpoint.
/// </para>
/// </remarks>
public interface ICommandHandler
{
    /// <summary>
    /// The wire name of this command, e.g. <c>file.send</c>. Namespaced with dots so related
    /// commands group naturally as the surface grows.
    /// </summary>
    string CommandName { get; }

    ValueTask<CommandResult> HandleAsync(CommandEnvelope envelope, CancellationToken cancellationToken = default);
}
