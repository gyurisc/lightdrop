using LightDrop.Core.Discovery;
using LightDrop.Core.Tests.Fakes;

namespace LightDrop.Core.Tests.Discovery;

public sealed class DiscoveryWatchdogTests
{
    private static readonly TimeSpan DeafAfter = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan Announcement = TimeSpan.FromSeconds(60);

    private static (DiscoveryWatchdog Watchdog, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider();
        return (new DiscoveryWatchdog(clock, DeafAfter), clock);
    }

    [Fact]
    public void LeavesATransportThatKeepsHearingItselfAlone()
    {
        var (watchdog, clock) = Create();

        for (var refresh = 0; refresh < 100; refresh++)
        {
            Assert.Equal(DiscoveryHealth.Healthy, watchdog.Check());
            watchdog.HeardSelf();
            clock.Advance(Announcement);
        }
    }

    [Fact]
    public void GivesAFreshTransportAFullWindow()
    {
        var (watchdog, clock) = Create();

        clock.Advance(DeafAfter - TimeSpan.FromSeconds(1));

        Assert.Equal(DiscoveryHealth.Healthy, watchdog.Check());
    }

    [Fact]
    public void CallsATransportDeafOnceItHasMissedAWholeWindow()
    {
        var (watchdog, clock) = Create();

        clock.Advance(DeafAfter);

        Assert.Equal(DiscoveryHealth.WentDeaf, watchdog.Check());
    }

    [Fact]
    public void GivesARebuiltTransportAFullWindowToo()
    {
        // Otherwise a machine with no network at all would be rebuilt on every refresh.
        var (watchdog, clock) = Create();
        clock.Advance(DeafAfter);
        watchdog.Check();

        clock.Advance(DeafAfter - TimeSpan.FromSeconds(1));

        Assert.Equal(DiscoveryHealth.Healthy, watchdog.Check());
    }

    [Fact]
    public void ReportsWentDeafOnlyOncePerSilence()
    {
        // WentDeaf is logged at Information; a laptop offline all afternoon must not repeat it.
        var (watchdog, clock) = Create();
        clock.Advance(DeafAfter);
        Assert.Equal(DiscoveryHealth.WentDeaf, watchdog.Check());

        clock.Advance(DeafAfter);
        Assert.Equal(DiscoveryHealth.StillDeaf, watchdog.Check());

        clock.Advance(DeafAfter);
        Assert.Equal(DiscoveryHealth.StillDeaf, watchdog.Check());
    }

    [Fact]
    public void ReportsRecoveryOnTheFirstEchoAfterGoingDeaf()
    {
        var (watchdog, clock) = Create();
        clock.Advance(DeafAfter);
        watchdog.Check();

        Assert.True(watchdog.HeardSelf());
        Assert.False(watchdog.HeardSelf());
    }

    [Fact]
    public void AnEchoWhileHealthyIsNotARecovery()
    {
        var (watchdog, _) = Create();

        Assert.False(watchdog.HeardSelf());
    }

    [Fact]
    public void ASecondSilenceAfterRecoveringIsReportedAgain()
    {
        var (watchdog, clock) = Create();
        clock.Advance(DeafAfter);
        watchdog.Check();
        watchdog.HeardSelf();

        clock.Advance(DeafAfter);

        Assert.Equal(DiscoveryHealth.WentDeaf, watchdog.Check());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsAWindowThatCouldNeverBeMet(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DiscoveryWatchdog(new FakeTimeProvider(), TimeSpan.FromSeconds(seconds)));
    }
}
