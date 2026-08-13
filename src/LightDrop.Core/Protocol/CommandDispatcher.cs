namespace LightDrop.Core.Protocol;

/// <inheritdoc cref="ICommandDispatcher"/>
public sealed class CommandDispatcher(CommandRegistry registry) : ICommandDispatcher
{
    public async ValueTask<CommandResult> DispatchAsync(
        CommandEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!registry.TryGetHandler(envelope.Type, out var handler))
        {
            // An older peer asking for something we do not implement is expected, not exceptional.
            // It should have checked our advertised capabilities first, but we answer cleanly anyway.
            return CommandResult.Failure(
                envelope.Id,
                CommandError.UnsupportedCommand,
                $"This device does not support the command '{envelope.Type}'.");
        }

        return await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
    }
}
