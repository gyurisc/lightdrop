using LightDrop.Core.Configuration;
using LightDrop.Core.Devices;
using LightDrop.Core.Health;
using LightDrop.Core.Pairing;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-independent LightDrop services.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for registering the infrastructure adapters this depends on
    /// (<see cref="IConfigStore"/> and <see cref="IStateStore"/>), because those
    /// perform file I/O and therefore live outside Core.
    /// </remarks>
    public static IServiceCollection AddLightDropCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered as concrete types: each has a single implementation and nothing substitutes
        // them, including in tests. Interfaces here are reserved for genuine ports.
        services.AddSingleton<DeviceIdentityProvider>();
        services.AddSingleton<DeviceKeyProvider>();
        services.AddSingleton<HealthService>();

        // TimeProvider is supplied here rather than registered container-wide, matching how the
        // daemon hands one to DiscoveryStatus. Tests construct PairingService directly with a fake
        // clock, so the container never needs to know about time.
        services.AddSingleton(provider => new PairingService(
            provider.GetRequiredService<IStateStore>(), TimeProvider.System));

        return services;
    }
}
