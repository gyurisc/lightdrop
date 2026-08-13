using LightDrop.Core.Protocol;

namespace LightDrop.Core.Tests.Fakes;

internal sealed class StubCommandHandler(string commandName) : ICommandHandler
{
    public string CommandName { get; } = commandName;

    public int HandleCount { get; private set; }

    public ValueTask<CommandResult> HandleAsync(
        CommandEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        HandleCount++;
        return ValueTask.FromResult(CommandResult.Success(envelope.Id));
    }
}
