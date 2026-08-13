using LightDrop.Core.Devices;
using LightDrop.Core.Discovery;

namespace LightDrop.Core.Tests.Discovery;

public sealed class PeerTxtRecordTests
{
    private static string[] WellFormed(params string[] extra) =>
    [
        $"{PeerTxtRecord.TxtVersionKey}={PeerTxtRecord.TxtVersion}",
        $"{PeerTxtRecord.DeviceIdKey}=peer-1",
        $"{PeerTxtRecord.ProtocolVersionKey}=1",
        $"{PeerTxtRecord.PlatformKey}=macos",
        $"{PeerTxtRecord.DeviceNameKey}=MacBook Air",
        .. extra,
    ];

    [Fact]
    public void RoundTripsWhatItAdvertises()
    {
        var built = PeerTxtRecord.Build(new DeviceIdentity("device-1", "Work Laptop"))
            .Select(pair => $"{pair.Key}={pair.Value}");

        Assert.True(PeerTxtRecord.TryParse(built, 5533, out var announcement));
        Assert.Equal("device-1", announcement!.DeviceId);
        Assert.Equal("Work Laptop", announcement.DeviceName);
        Assert.Equal(LightDropVersion.Protocol, announcement.ProtocolVersion);
        Assert.Equal(DevicePlatform.Current, announcement.Platform);
        Assert.Equal(5533, announcement.Port);
    }

    [Fact]
    public void DoesNotAdvertiseTheApplicationVersion()
    {
        // An exact build number is a free fingerprint for a passive observer on an untrusted link,
        // and protocolVersion is the actual compatibility gate.
        var keys = PeerTxtRecord.Build(new DeviceIdentity("device-1", "Laptop")).Select(pair => pair.Key);

        Assert.DoesNotContain("version", keys);
        Assert.DoesNotContain("ver", keys);
    }

    [Fact]
    public void OmitsCapabilitiesEntirelyWhileEmpty()
    {
        // In DNS-SD an absent key means something different from a present-but-empty one.
        var keys = PeerTxtRecord.Build(new DeviceIdentity("device-1", "Laptop")).Select(pair => pair.Key);

        Assert.DoesNotContain(PeerTxtRecord.CapabilitiesKey, keys);
    }

    [Fact]
    public void ParsesCapabilities()
    {
        Assert.True(PeerTxtRecord.TryParse(
            WellFormed($"{PeerTxtRecord.CapabilitiesKey}=file.send, clipboard.text"), 5533, out var announcement));

        Assert.Equal(["file.send", "clipboard.text"], announcement!.Capabilities);
    }

    [Fact]
    public void RejectsARecordWithNoDeviceId()
    {
        string[] txt = [$"{PeerTxtRecord.DeviceNameKey}=Laptop", $"{PeerTxtRecord.PlatformKey}=macos"];

        Assert.False(PeerTxtRecord.TryParse(txt, 5533, out _));
    }

    [Fact]
    public void IgnoresEntriesWithNoSeparator()
    {
        // A bare key carries nothing; it must not derail the rest of the record.
        Assert.True(PeerTxtRecord.TryParse(WellFormed("justakey", "=novalue"), 5533, out var announcement));
        Assert.Equal("peer-1", announcement!.DeviceId);
    }

    [Fact]
    public void KeepsTheFirstValueWhenAKeyRepeats()
    {
        // RFC 6763: first occurrence wins. Otherwise a peer could append a duplicate key to
        // override an earlier value.
        Assert.True(PeerTxtRecord.TryParse(
            WellFormed($"{PeerTxtRecord.DeviceNameKey}=Impostor"), 5533, out var announcement));

        Assert.Equal("MacBook Air", announcement!.DeviceName);
    }

    [Fact]
    public void TreatsANonNumericProtocolVersionAsZeroRatherThanFailing()
    {
        string[] txt = [$"{PeerTxtRecord.DeviceIdKey}=peer-1", $"{PeerTxtRecord.ProtocolVersionKey}=not-a-number"];

        Assert.True(PeerTxtRecord.TryParse(txt, 5533, out var announcement));
        Assert.Equal(0, announcement!.ProtocolVersion);
    }

    [Fact]
    public void SanitizesValuesOnTheWayIn()
    {
        // Proof the parse path cannot bypass the ingestion chokepoint.
        Assert.True(PeerTxtRecord.TryParse(
            [$"{PeerTxtRecord.DeviceIdKey}=peer-1", $"{PeerTxtRecord.DeviceNameKey}=Evil\u001B[2m\r\nRow"],
            5533,
            out var announcement));

        Assert.DoesNotContain('\u001B', announcement!.DeviceName);
        Assert.DoesNotContain('\n', announcement.DeviceName);
    }

    [Fact]
    public void AcceptsAnEmptyRecordAsUnusableRatherThanThrowing()
    {
        Assert.False(PeerTxtRecord.TryParse([], 5533, out _));
    }
}
