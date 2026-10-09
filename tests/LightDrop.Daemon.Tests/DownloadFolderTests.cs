using System.Text.Json;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Daemon.Infrastructure;
using LightDrop.Daemon.Tests.TestSupport;
using LightDrop.Daemon.Transfers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Where the download folder is, and the last check before a received file is created in it.
/// </summary>
/// <remarks>
/// The path rules are pure arithmetic, built from a rooted temp path so the same vectors hold on
/// Windows and macOS. The save tests use a real folder inside a temp directory.
/// </remarks>
public sealed class DownloadFolderTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "home-of-test-user");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultsToDownloadsInTheHomeDirectory(string? configured) =>
        Assert.Equal(Path.Combine(Home, "Downloads"), DownloadFolder.Resolve(configured, Home));

    [Fact]
    public void ExpandsATilde()
    {
        Assert.Equal(Path.Combine(Home, "Inbox"), DownloadFolder.Resolve("~/Inbox", Home));
        Assert.Equal(Path.Combine(Home, "Inbox"), DownloadFolder.Resolve("~\\Inbox".Replace('\\', Path.DirectorySeparatorChar), Home));
        Assert.Equal(Path.GetFullPath(Home), DownloadFolder.Resolve("~", Home));
    }

    [Fact]
    public void TakesARelativePathFromTheHomeDirectoryNotTheWorkingDirectory() =>
        Assert.Equal(Path.Combine(Home, "Inbox", "LightDrop"), DownloadFolder.Resolve(Path.Combine("Inbox", "LightDrop"), Home));

    [Fact]
    public void KeepsAnAbsolutePath()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "drops");

        Assert.Equal(absolute, DownloadFolder.Resolve(absolute + Path.DirectorySeparatorChar, Home));
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("report (1).pdf", true)]
    [InlineData("..", false)]
    [InlineData("../escape.txt", false)]
    [InlineData("sub/file.txt", false)]
    [InlineData("sub/../../escape.txt", false)]
    [InlineData("sub/../file.txt", true)]
    public void AcceptsOnlyAnEntryDirectlyInsideTheFolder(string relative, bool inside)
    {
        var folder = Path.Combine(Home, "Downloads");
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(inside, DownloadFolder.IsDirectlyInside(folder, path));
        Assert.Equal(inside, DownloadFolder.IsDirectlyInside(folder + Path.DirectorySeparatorChar, path));
    }

    [Fact]
    public void ASiblingFolderSharingAPrefixIsNotInside()
    {
        // "Downloads-evil" starts with "Downloads": a prefix comparison would let it through.
        var folder = Path.Combine(Home, "Downloads");

        Assert.False(DownloadFolder.IsDirectlyInside(folder, Path.Combine(Home, "Downloads-evil", "x.txt")));
        Assert.False(DownloadFolder.IsDirectlyInside(folder, folder));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public async Task ABodyOfTheWrongLengthLeavesNothingBehind(int actual)
    {
        // Kestrel already holds a body to its Content-Length, so over HTTP this check is never
        // the one that fires. It is tested on its own so it stays true if that ever changes --
        // a different server, a chunked body let through by mistake.
        using var directory = new TempDataDirectory();
        var downloads = CreateDownloadFolder(directory, out var folder);

        await Assert.ThrowsAsync<IOException>(
            () => downloads.SaveAsync("short.bin", new MemoryStream(new byte[actual]), length: 20, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public async Task ABodyOfTheDeclaredLengthIsSaved()
    {
        using var directory = new TempDataDirectory();
        var downloads = CreateDownloadFolder(directory, out var folder);

        var (savedAs, savedIn) = await downloads.SaveAsync(
            "exact.bin", new MemoryStream(new byte[20]), length: 20, CancellationToken.None);

        Assert.Equal("exact.bin", savedAs);
        Assert.Equal(folder, savedIn);
        Assert.Equal(20, new FileInfo(Path.Combine(folder, "exact.bin")).Length);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    public async Task ANameThatLeavesTheFolderIsRefusedEvenIfItGotPastSanitizing(string fileName)
    {
        // The second line of defence, called the way SendFileHandler calls it: if
        // ReceivedFileName.Sanitize ever let a separator or ".." through, the save must still not
        // land outside the folder -- nor leave its temp file behind on the way out.
        using var directory = new TempDataDirectory();
        var downloads = CreateDownloadFolder(directory, out var folder);
        var before = Directory.EnumerateFileSystemEntries(directory.FullPath, "*", SearchOption.AllDirectories).ToList();

        await Assert.ThrowsAnyAsync<Exception>(
            () => downloads.SaveAsync(fileName, new MemoryStream(new byte[5]), length: 5, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.Equal(
            before.Append(folder).Order(StringComparer.Ordinal),
            Directory.EnumerateFileSystemEntries(directory.FullPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }

    private static DownloadFolder CreateDownloadFolder(TempDataDirectory directory, out string folder)
    {
        folder = Path.Combine(directory.FullPath, "Downloads");
        directory.WriteConfig(JsonSerializer.Serialize(
            new LightDropConfig { DownloadFolder = folder }, LightDropJsonContext.Default.LightDropConfig));

        return new DownloadFolder(new JsonConfigStore(
            Options.Create(new StorageOptions { DataDirectory = directory.FullPath }),
            NullLogger<JsonConfigStore>.Instance));
    }
}
