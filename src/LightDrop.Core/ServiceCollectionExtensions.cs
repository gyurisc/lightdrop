using LightDrop.Core.Devices;
using LightDrop.Core.Health;
using LightDrop.Core.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-independent LightDrop services.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for registering the infrastructure adapters this depends on
    /// (<see cref="Configuration.IConfigStore"/> and <see cref="Configuration.IStateStore"/>),
    /// because those perform file I/O and therefore live outside Core.
    /// </remarks>
    public static IServiceCollection AddLightDropCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDeviceIdentityProvider, DeviceIdentityProvider>();
        services.AddSingleton<IHealthService, HealthService>();

        // Resolves every ICommandHandler registered by any layer. None yet.
        services.AddSingleton<CommandRegistry>();
        services.AddSingleton<ICommandDispatcher, CommandDispatcher>();

        return services;
    }
}
