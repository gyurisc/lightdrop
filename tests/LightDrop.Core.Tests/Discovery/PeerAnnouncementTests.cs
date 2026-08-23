using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;

namespace LightDrop.Core.Tests.Discovery;

/// <summary>
/// Ingestion of untrusted announcement data. Anyone on the local network can send any of this.
/// </summary>
public sealed class PeerAnnouncementTests
{
    // Written as escapes, not literals: these are invisible or terminal-controlling characters
    // that no editor or diff renders reliably.
    private const string Escape = "\u001B";
    private const string ZeroWidthSpace = "\u200B";
    private const string RightToLeftOverride = "\u202E";

    private static PeerAnnouncement Create(
        string? deviceId = "peer-1",
        string? deviceName = "MacBook Air",
        string? platform = DevicePlatform.MacOS,
        int protocolVersion = 1,
        IEnumerable<string>? capabilities = null,
        int port = 5533,
        string? address = "192.168.0.149")
    {
        Assert.True(PeerAnnouncement.TryCreate(
            deviceId, deviceName, platform, protocolVersion, capabilities, port, address, out var announcement));
        return announcement!;
    }

    [Fact]
    public void StripsTheEscapeCharacterFromDeviceName()
    {
        // The concrete attack: a hostile peer names itself with terminal control sequences that
        // clear the screen or recolour output when `lightdrop peers` prints the table. Removing
        // the escape character defuses the sequence; the remaining literal text is harmless.
        var announcement = Create(deviceName: $"{Escape}[2JEvil{Escape}[1;31m Laptop");

        Assert.DoesNotContain(Escape, announcement.DeviceName, StringComparison.Ordinal);
        Assert.Equal("[2JEvil[1;31m Laptop", announcement.DeviceName);
    }

    [Fact]
    public void StripsNewlinesThatWouldFabricateExtraRows()
    {
        var announcement = Create(deviceName: "Real Laptop\r\n  fake-id   Fake Laptop");

        Assert.DoesNotContain('\n', announcement.DeviceName);
        Assert.DoesNotContain('\r', announcement.DeviceName);
    }

    [Fact]
    public void StripsBidiOverridesAndZeroWidthCharacters()
    {
        // U+202E reverses rendering order and U+200B is invisible; both let a name masquerade as
        // a different one in a terminal.
        var announcement = Create(deviceName: $"Work{RightToLeftOverride}{ZeroWidthSpace}Laptop");

        Assert.Equal("WorkLaptop", announcement.DeviceName);
    }

    [Fact]
    public void KeepsOrdinarySpacesAndNonAsciiLetters()
    {
        // Sanitizing must not mangle a legitimate name.
        var announcement = Create(deviceName: "Krisztián's MacBook");

        Assert.Equal("Krisztián's MacBook", announcement.DeviceName);
    }

    [Fact]
    public void TruncatesAnOverlongDeviceNameWithoutSplittingACharacter()
    {
        var announcement = Create(deviceName: new string('é', 200));

        // Two UTF-8 bytes per character, so the 63-byte bound allows 31 whole characters.
        Assert.Equal(31, announcement.DeviceName.Length);
    }

    [Fact]
    public void SubstitutesAPlaceholderWhenTheNameIsEmptyAfterSanitizing()
    {
        // A name made entirely of invisible characters must not render as a blank row.
        var announcement = Create(deviceId: "abcdef0123456789", deviceName: ZeroWidthSpace);

        Assert.Equal("Peer abcdef01", announcement.DeviceName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsAnAnnouncementWithNoUsableDeviceId(string? deviceId)
    {
        // Without an identifier there is nothing to deduplicate or display against.
        Assert.False(PeerAnnouncement.TryCreate(
            deviceId, "Laptop", DevicePlatform.MacOS, 1, null, 5533, "192.168.0.149", out var announcement));
        Assert.Null(announcement);
    }

    [Fact]
    public void RejectsADeviceIdMadeEntirelyOfControlCharacters()
    {
        Assert.False(PeerAnnouncement.TryCreate(
            $"{Escape}{ZeroWidthSpace}", "Laptop", DevicePlatform.MacOS, 1, null, 5533, "192.168.0.149", out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RejectsAnOutOfRangePort(int port)
    {
        Assert.False(PeerAnnouncement.TryCreate(
            "peer-1", "Laptop", DevicePlatform.MacOS, 1, null, port, "192.168.0.149", out _));
    }

    [Fact]
    public void BoundsTheNumberOfAdvertisedCapabilities()
    {
        // Otherwise a peer can make its announcement arbitrarily large.
        var announcement = Create(capabilities: Enumerable.Range(0, 500).Select(i => $"cap.{i}"));

        Assert.Equal(PeerAnnouncement.MaxCapabilities, announcement.Capabilities.Count);
    }

    [Fact]
    public void FallsBackToUnknownForAMissingPlatform()
    {
        var announcement = Create(platform: null);

        Assert.Equal(DevicePlatform.Unknown, announcement.Platform);
    }

    [Fact]
    public void AcceptsAnUnrecognisedPlatformRatherThanDiscardingIt()
    {
        // Restricting to today's known tokens would drop a peer running a future build.
        var announcement = Create(platform: "freebsd");

        Assert.Equal("freebsd", announcement.Platform);
    }

    [Fact]
    public void KeepsAnAddressOnTheLocalNetwork() =>
        Assert.Equal("10.0.0.5", Create(address: "10.0.0.5").Address);

    [Fact]
    public void RejectsAnAnnouncementNamingAHostBeyondTheLocalNetwork()
    {
        // Pairing dials this address, so a peer that could name any host would be able to point
        // this machine at a third party. Rejected at ingestion rather than before dialling: the
        // point of the chokepoint is that a later consumer cannot forget the check.
        Assert.False(PeerAnnouncement.TryCreate(
            "peer-1", "Evil", DevicePlatform.MacOS, 1, null, 5533, "203.0.113.7", out var announcement));
        Assert.Null(announcement);
    }

    [Fact]
    public void RejectsAnAnnouncementWithNoAddress()
    {
        // Presence with nowhere to go is not useful presence: such a peer could be listed but
        // never paired with, so the row would only mislead.
        Assert.False(PeerAnnouncement.TryCreate(
            "peer-1", "Nowhere", DevicePlatform.MacOS, 1, null, 5533, null, out var announcement));
        Assert.Null(announcement);
    }
}
