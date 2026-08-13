using LightDrop.Core.Contracts;

namespace LightDrop.Core.Health;

/// <summary>
/// Builds this device's health snapshot.
/// </summary>
public interface IHealthService
{
    ValueTask<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default);
}
