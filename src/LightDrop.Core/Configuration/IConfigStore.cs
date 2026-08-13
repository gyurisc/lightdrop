namespace LightDrop.Core.Configuration;

/// <summary>
/// Read access to user-owned configuration. Implemented by an infrastructure adapter.
/// </summary>
/// <remarks>
/// Read-only by design: see <see cref="LightDropConfig"/> for why the application must not
/// write this file.
/// </remarks>
public interface IConfigStore
{
    ValueTask<LightDropConfig> LoadAsync(CancellationToken cancellationToken = default);
}
