using LightDrop.Core.Protocol;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests;

public sealed class CommandRegistryTests
{
    [Fact]
    public void AdvertisesNoCapabilitiesWhenNoHandlersAreRegistered()
    {
        // The current daemon's real state: it accepts no commands, and says so honestly.
        var registry = new CommandRegistry([]);

        Assert.Empty(registry.Capabilities);
    }

    [Fact]
    public void DerivesCapabilitiesFromRegisteredHandlers()
    {
        // The whole point of projecting the list from DI: registering a handler is the only
        // step needed to advertise it, so the advertised list cannot drift from reality.
        var registry = new CommandRegistry([
            new StubCommandHandler("clipboard.text"),
            new StubCommandHandler("file.send"),
        ]);

        Assert.Equal(["clipboard.text", "file.send"], registry.Capabilities);
    }

    [Fact]
    public void SortsCapabilitiesSoTheAdvertisedListIsStable()
    {
        var registry = new CommandRegistry([
            new StubCommandHandler("notification.show"),
            new StubCommandHandler("clipboard.text"),
            new StubCommandHandler("file.send"),
        ]);

        Assert.Equal(["clipboard.text", "file.send", "notification.show"], registry.Capabilities);
    }

    [Fact]
    public void RejectsDuplicateCommandNames()
    {
        // Fail at startup rather than dispatching to whichever registration happened to win.
        var exception = Assert.Throws<InvalidOperationException>(() => new CommandRegistry([
            new StubCommandHandler("file.send"),
            new StubCommandHandler("file.send"),
        ]));

        Assert.Contains("file.send", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesRegisteredHandlersByName()
    {
        var handler = new StubCommandHandler("file.send");
        var registry = new CommandRegistry([handler]);

        Assert.True(registry.TryGetHandler("file.send", out var resolved));
        Assert.Same(handler, resolved);
    }

    [Fact]
    public void DoesNotResolveUnknownCommands()
    {
        var registry = new CommandRegistry([new StubCommandHandler("file.send")]);

        Assert.False(registry.TryGetHandler("clipboard.text", out var resolved));
        Assert.Null(resolved);
    }
}
