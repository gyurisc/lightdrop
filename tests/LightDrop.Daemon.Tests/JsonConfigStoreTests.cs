using LightDrop.Daemon.Infrastructure;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Tests;

public sealed class JsonConfigStoreTests
{
    private static (JsonConfigStore Store, CapturingLogger<JsonConfigStore> Logger) CreateStore(TempDataDirectory directory)
    {
        var logger = new CapturingLogger<JsonConfigStore>();
        var options = Options.Create(new StorageOptions { DataDirectory = directory.FullPath });
        return (new JsonConfigStore(options, logger), logger);
    }

    [Fact]
    public async Task ReturnsDefaultsWithoutComplainingWhenTheFileIsMissing()
    {
        // Zero-config is the normal case: no file must not be treated as a problem.
        using var directory = new TempDataDirectory();
        var (store, logger) = CreateStore(directory);

        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Null(config.DeviceName);
        Assert.Null(config.DownloadFolder);
        Assert.Empty(logger.Levels);
    }

    [Fact]
    public async Task ReadsValuesFromAnExistingFile()
    {
        using var directory = new TempDataDirectory();
        directory.WriteConfig("""
            {
              "deviceName": "Work Laptop",
              "downloadFolder": "D:\\Drops"
            }
            """);
        var (store, _) = CreateStore(directory);

        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Work Laptop", config.DeviceName);
        Assert.Equal("D:\\Drops", config.DownloadFolder);
    }

    [Fact]
    public async Task IgnoresUnknownPropertiesSoOlderBuildsCanReadNewerConfigFiles()
    {
        using var directory = new TempDataDirectory();
        directory.WriteConfig("""{ "deviceName": "Desktop", "somethingFromTheFuture": 42 }""");
        var (store, _) = CreateStore(directory);

        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Desktop", config.DeviceName);
    }

    [Fact]
    public async Task FallsBackToDefaultsAndWarnsWhenTheFileIsMalformed()
    {
        // Deliberately not fatal: a typo in a hand-edited file must not take down the daemon.
        // Contrast with JsonStateStore, which throws. Config is replaceable; state is not.
        using var directory = new TempDataDirectory();
        directory.WriteConfig("{ this is not json");
        var (store, logger) = CreateStore(directory);

        var config = await store.LoadAsync(CancellationToken.None);

        Assert.Null(config.DeviceName);
        Assert.Contains(LogLevel.Warning, logger.Levels);
    }
}
