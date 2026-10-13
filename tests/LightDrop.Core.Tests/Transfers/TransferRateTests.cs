using LightDrop.Core.Transfers;

namespace LightDrop.Core.Tests.Transfers;

/// <summary>
/// The speed and time left that <c>lightdrop send</c> shows, from the samples it polls.
/// </summary>
public sealed class TransferRateTests
{
    private const long MB = 1_000_000;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    /// <summary>Samples every half second at a steady speed, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static void Steady(TransferRate rate, double from, double to, long startBytes, long bytesPerSecond)
    {
        for (var at = from; at <= to; at += 0.5)
        {
            rate.Observe(Seconds(at), startBytes + (long)((at - from) * bytesPerSecond));
        }
    }

    [Fact]
    public void SaysNothingFromASingleSample()
    {
        var rate = new TransferRate(Window);
        rate.Observe(Seconds(1), 5 * MB);

        Assert.Null(rate.BytesPerSecond);
        Assert.Null(rate.TimeLeft(100 * MB));
    }

    [Fact]
    public void SaysNothingUntilTheSamplesSpanASecond()
    {
        var rate = new TransferRate(Window);
        rate.Observe(Seconds(0), 0);
        rate.Observe(Seconds(0.5), 5 * MB);

        Assert.Null(rate.BytesPerSecond);

        rate.Observe(Seconds(1), 10 * MB);

        Assert.Equal(10 * MB, rate.BytesPerSecond);
    }

    [Fact]
    public void ASteadySpeedGivesTheTimeTheRestWillTake()
    {
        var rate = new TransferRate(Window);
        Steady(rate, from: 0, to: 3, startBytes: 0, bytesPerSecond: 10 * MB);

        Assert.Equal(10 * MB, rate.BytesPerSecond);
        Assert.Equal(Seconds(5), rate.TimeLeft(80 * MB));
    }

    [Fact]
    public void FollowsTheRecentSpeedRatherThanTheAverageSinceTheStart()
    {
        // Ten seconds at 10 MB/s, then the link drops to 1 MB/s. Averaged since the start the
        // figure would still say about 7 MB/s; over the five-second window it says 1.
        var rate = new TransferRate(Window);
        Steady(rate, from: 0, to: 10, startBytes: 0, bytesPerSecond: 10 * MB);
        Steady(rate, from: 10.5, to: 15, startBytes: 100 * MB + MB / 2, bytesPerSecond: 1 * MB);

        Assert.Equal(1 * MB, rate.BytesPerSecond);
    }

    [Fact]
    public void ATransferThatHasStalledHasNoTimeLeftRatherThanForever()
    {
        var rate = new TransferRate(Window);
        rate.Observe(Seconds(0), 40 * MB);
        rate.Observe(Seconds(2), 40 * MB);

        Assert.Equal(0, rate.BytesPerSecond);
        Assert.Null(rate.TimeLeft(100 * MB));
    }

    [Fact]
    public void AFinishedTransferHasNoTimeLeft()
    {
        var rate = new TransferRate(Window);
        rate.Observe(Seconds(0), 100 * MB);

        Assert.Equal(TimeSpan.Zero, rate.TimeLeft(100 * MB));
    }

    [Fact]
    public void AnEstimateOfMoreThanAMonthIsNoEstimate()
    {
        var rate = new TransferRate(Window);
        rate.Observe(Seconds(0), 0);
        rate.Observe(Seconds(5), 1);

        Assert.Null(rate.TimeLeft(long.MaxValue));
    }

    [Fact]
    public void IgnoresASampleThatGoesBackInTimeOrInBytes()
    {
        var rate = new TransferRate(Window);
        rate.Observe(Seconds(0), 0);
        rate.Observe(Seconds(2), 20 * MB);
        rate.Observe(Seconds(1), 50 * MB);
        rate.Observe(Seconds(3), 10 * MB);

        Assert.Equal(10 * MB, rate.BytesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RefusesAWindowThatIsNotPositive(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferRate(TimeSpan.FromSeconds(seconds)));
}
