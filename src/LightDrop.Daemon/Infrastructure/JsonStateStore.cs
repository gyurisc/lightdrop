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
        // Gated on the same semaphore as SaveAsync. File.OpenRead does not share delete access,
        // so a read in flight makes the save's File.Move fail with an access violation on
        // Windows. These files are tiny; serialising reads against writes costs nothing.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnsynchronizedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<LightDropState> LoadUnsynchronizedAsync(CancellationToken cancellationToken)
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
            //
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
            };

            // Restrict the file to its owner. The default umask would make it 0644 —
            // world-readable inside a 755 home directory on macOS. It holds device identity today
            // and paired-peer key material later, so other local accounts must not be able to read
            // it. Windows needs no equivalent: the user profile ACL already denies other
            // non-admin users, and setting UnixCreateMode there throws.
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, streamOptions))
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
