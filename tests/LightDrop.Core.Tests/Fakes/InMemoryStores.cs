using LightDrop.Core.Configuration;

namespace LightDrop.Core.Tests.Fakes;

/// <summary>
/// In-memory stand-in for the state adapter.
/// </summary>
/// <remarks>
/// Core holds the get-or-create logic and keeps file I/O behind <see cref="IStateStore"/>,
/// which is precisely what lets these tests run without touching a real filesystem.
/// </remarks>
internal sealed class InMemoryStateStore(LightDropState? initial = null) : IStateStore
{
    public LightDropState Current { get; private set; } = initial ?? new LightDropState();

    public int LoadCount { get; private set; }

    public int SaveCount { get; private set; }

    public ValueTask<LightDropState> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCount++;
        return ValueTask.FromResult(Current);
    }

    public ValueTask SaveAsync(LightDropState state, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Current = state;
        return ValueTask.CompletedTask;
    }
}

internal sealed class InMemoryConfigStore(LightDropConfig? config = null) : IConfigStore
{
    private readonly LightDropConfig _config = config ?? new LightDropConfig();

    public ValueTask<LightDropConfig> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_config);
}
