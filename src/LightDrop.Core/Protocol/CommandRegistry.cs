using System.Diagnostics.CodeAnalysis;

namespace LightDrop.Core.Protocol;

/// <summary>
/// The commands this device accepts, derived from the registered handlers.
/// </summary>
/// <remarks>
/// The capability list advertised over the wire is projected from DI rather than maintained by
/// hand, so it cannot drift from what the daemon actually implements. Register a handler and
/// the capability appears; remove it and the capability disappears.
/// </remarks>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers;

    public CommandRegistry(IEnumerable<ICommandHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = new Dictionary<string, ICommandHandler>(StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.CommandName, handler))
            {
                // Fail at startup rather than dispatching to whichever handler happened to win.
                throw new InvalidOperationException(
                    $"More than one command handler is registered for '{handler.CommandName}'.");
            }
        }

        // Sorted so the advertised capability list is stable between runs.
        Capabilities = [.. _handlers.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The command names advertised to peers. Empty until the first handler is registered.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; }

    public bool TryGetHandler(string commandName, [NotNullWhen(true)] out ICommandHandler? handler) =>
        _handlers.TryGetValue(commandName, out handler);
}
