namespace LightDrop.Core.Configuration;

/// <summary>
/// Read and write access to application-owned state. Implemented by an infrastructure adapter.
/// </summary>
public interface IStateStore
{
    ValueTask<LightDropState> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(LightDropState state, CancellationToken cancellationToken = default);
}
