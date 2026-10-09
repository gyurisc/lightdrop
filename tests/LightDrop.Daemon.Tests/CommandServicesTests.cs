using LightDrop.Core.Commands;
using LightDrop.Core.Configuration;
using LightDrop.Daemon.Commands;
using LightDrop.Daemon.Infrastructure;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Capabilities are derived from the registered handler set, never hand-maintained.
/// </summary>
/// <remarks>
/// With one real handler, a hand-written <c>["SendFile"]</c> is indistinguishable from the
/// projection over HTTP. A second handler, registered the way the next real one will be, is what
/// tells them apart.
/// </remarks>
public sealed class CommandServicesTests
{
    [Fact]
    public void AdvertisesEveryRegisteredHandlerIncludingOneAddedLater()
    {
        using var directory = new TempDataDirectory();
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<StorageOptions>(storage => storage.DataDirectory = directory.FullPath)
            .AddSingleton<IConfigStore, JsonConfigStore>()
            .AddSingleton<ICommandHandler>(new NamedHandler("ShowNotification"))
            .AddCommandHandlers();

        using var provider = services.BuildServiceProvider();

        Assert.Equal(["SendFile", "ShowNotification"], provider.GetRequiredService<CommandCapabilities>().Names);
    }

    private sealed class NamedHandler(string name) : ICommandHandler
    {
        public string Name => name;

        public Task<IResult> HandleAsync(CommandRequest request) => throw new NotSupportedException();
    }
}
