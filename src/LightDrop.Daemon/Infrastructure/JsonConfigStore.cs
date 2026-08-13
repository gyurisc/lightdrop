using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Infrastructure;

/// <summary>
/// Reads user-owned settings from <c>config.json</c>.
/// </summary>
public sealed class JsonConfigStore(IOptions<StorageOptions> options, ILogger<JsonConfigStore> logger) : IConfigStore
{
    private readonly string _path = options.Value.ConfigFilePath;

    public async ValueTask<LightDropConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Zero-config: a missing file is the normal case, not an error.
        if (!File.Exists(_path))
        {
            return new LightDropConfig();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var config = await JsonSerializer
                .DeserializeAsync(stream, LightDropJsonContext.Default.LightDropConfig, cancellationToken)
                .ConfigureAwait(false);

            return config ?? new LightDropConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A typo in a hand-edited file should not take down a background utility. Warn
            // loudly and fall back to defaults. Note the asymmetry with JsonStateStore, which
            // fails hard: config is replaceable, state is not.
            StorageLog.ConfigUnreadable(logger, _path, ex);
            return new LightDropConfig();
        }
    }
}
