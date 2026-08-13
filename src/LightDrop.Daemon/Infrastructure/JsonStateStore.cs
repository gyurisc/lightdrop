using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Infrastructure;

/// <summary>
/// Reads and writes application-owned state in <c>state.json</c>.
/// </summary>
public sealed class JsonStateStore(IOptions<StorageOptions> options) : IStateStore
{
    private readonly StorageOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<LightDropState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = _options.StateFilePath;

        if (!File.Exists(path))
        {
            return new LightDropState();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer
                .DeserializeAsync(stream, LightDropJsonContext.Default.LightDropState, cancellationToken)
                .ConfigureAwait(false);

            return state ?? new LightDropState();
        }
        catch (JsonException ex)
        {
            // Deliberately fatal, unlike config. Silently starting from a blank state would mint
            // a new device id and quietly invalidate every pairing on every other machine. Better
            // to stop and let the user decide.
            throw new InvalidOperationException(
                $"LightDrop state file '{path}' is corrupt. Device identity and pairings cannot be " +
                "recovered from it. Move the file aside to start fresh, understanding that this " +
                "device will get a new identity and must be paired again.",
                ex);
        }
    }

    public async ValueTask SaveAsync(LightDropState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);

            var path = _options.StateFilePath;
            var temporaryPath = path + ".tmp";

            // Write-then-rename. A crash mid-write leaves the previous state intact rather than a
            // truncated file, which would be unrecoverable given the failure mode described above.
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, state, LightDropJsonContext.Default.LightDropState, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
