using LightDrop.Core.Protocol;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

public sealed class CommandDispatcherTests
{
    private static CommandEnvelope Envelope(string type) =>
        new() { Id = "envelope-1", Type = type };

    [Fact]
    public async Task RoutesToTheMatchingHandler()
    {
        var handler = new StubCommandHandler("file.send");
        var dispatcher = new CommandDispatcher(new CommandRegistry([handler]));

        var result = await dispatcher.DispatchAsync(Envelope("file.send"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, handler.HandleCount);
    }

    [Fact]
    public async Task EchoesTheEnvelopeIdSoResultsCanBeCorrelated()
    {
        var dispatcher = new CommandDispatcher(new CommandRegistry([new StubCommandHandler("file.send")]));

        var result = await dispatcher.DispatchAsync(Envelope("file.send"), CancellationToken.None);

        Assert.Equal("envelope-1", result.Id);
    }

    [Fact]
    public async Task FailsCleanlyOnAnUnsupportedCommand()
    {
        // A newer peer asking for something this build does not implement is expected traffic,
        // not an exception. It must get a structured answer it can branch on.
        var dispatcher = new CommandDispatcher(new CommandRegistry([]));

        var result = await dispatcher.DispatchAsync(Envelope("clipboard.text"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(CommandError.UnsupportedCommand, result.Error?.Code);
        Assert.Equal("envelope-1", result.Id);
    }
}
