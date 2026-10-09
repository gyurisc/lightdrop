using LightDrop.Core.Commands;
using LightDrop.Daemon.Transfers;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Daemon.Commands;

internal static class CommandServices
{
    /// <summary>
    /// Registers the commands paired peers may send, and the capabilities advertised for them.
    /// </summary>
    /// <remarks>
    /// Registering a handler here is the whole of adding one: the peer listener dispatches to this
    /// set, and the capabilities <c>/health</c> and the TXT record advertise are projected from it
    /// rather than listed anywhere by hand. Projected from <em>every</em> registered
    /// <see cref="ICommandHandler"/>, including any registered before this call -- which is what
    /// lets a test prove the projection is real by adding one.
    /// </remarks>
    public static IServiceCollection AddCommandHandlers(this IServiceCollection services)
    {
        services.AddSingleton<DownloadFolder>();
        services.AddSingleton<ICommandHandler, SendFileHandler>();
        services.AddSingleton(provider => new CommandCapabilities(
            provider.GetServices<ICommandHandler>().Select(handler => handler.Name)));

        return services;
    }
}
