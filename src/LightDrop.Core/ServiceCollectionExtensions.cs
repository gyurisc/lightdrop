using LightDrop.Core.Devices;
using LightDrop.Core.Health;
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

        // Registered as concrete types: each has a single implementation and nothing substitutes
        // them, including in tests. Interfaces here are reserved for genuine ports.
        services.AddSingleton<DeviceIdentityProvider>();
        services.AddSingleton<DeviceKeyProvider>();
        services.AddSingleton<HealthService>();

        return services;
    }
}
