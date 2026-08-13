namespace LightDrop.Core.Protocol;

/// <summary>
/// Routes a command envelope to its handler.
/// </summary>
public interface ICommandDispatcher
{
    ValueTask<CommandResult> DispatchAsync(CommandEnvelope envelope, CancellationToken cancellationToken = default);
}
