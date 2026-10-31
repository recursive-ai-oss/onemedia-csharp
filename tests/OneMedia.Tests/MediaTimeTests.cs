namespace RecursiveAI.OneMedia.Tests;

public class MediaTimeTests
{
    [Theory]
    [InlineData(1, 3, 1000, MediaRounding.Floor, 333)]
    [InlineData(1, 3, 1000, MediaRounding.Ceiling, 334)]
    [InlineData(1, 3, 1000, MediaRounding.TowardZero, 333)]
    [InlineData(1, 3, 1000, MediaRounding.AwayFromZero, 334)]
    [InlineData(1, 3, 1000, MediaRounding.NearestAwayFromZero, 333)]
    [InlineData(2, 3, 1000, MediaRounding.NearestAwayFromZero, 667)]
    [InlineData(-1, 3, 1000, MediaRounding.Floor, -334)]
    [InlineData(-1, 3, 1000, MediaRounding.Ceiling, -333)]
    [InlineData(-1, 3, 1000, MediaRounding.TowardZero, -333)]
    [InlineData(-1, 3, 1000, MediaRounding.AwayFromZero, -334)]
    [InlineData(-2, 3, 1000, MediaRounding.NearestAwayFromZero, -667)]
    [InlineData(1, 2, 1, MediaRounding.NearestAwayFromZero, 1)]
    [InlineData(-1, 2, 1, MediaRounding.NearestAwayFromZero, -1)]
    [InlineData(1, 2, 1, MediaRounding.NearestEven, 0)]
    [InlineData(3, 2, 1, MediaRounding.NearestEven, 2)]
    [InlineData(-3, 2, 1, MediaRounding.NearestEven, -2)]
    [InlineData(5, 2, 1, MediaRounding.NearestEven, 2)]
    [InlineData(90000, 90000, 1000, MediaRounding.Floor, 1000)]
    public void RescaleRounds(long ticks, long from, long to, MediaRounding rounding, long expected)
    {
        MediaTime result = new MediaTime(ticks, from).Rescale(to, rounding);
        Assert.Equal(expected, result.Ticks);
        Assert.Equal(to, result.Timescale);
    }

    [Fact]
    public void ExactRescaleIgnoresRounding()
    {
        foreach (MediaRounding mode in Enum.GetValues<MediaRounding>())
        {
            Assert.Equal(48000, new MediaTime(1000, 1000).Rescale(48000, mode).Ticks);
            Assert.Equal(-90, new MediaTime(-1, 1000).Rescale(90000, mode).Ticks);
        }
    }

    [Fact]
    public void RescaleDoesNotOverflowInTheIntermediate()
    {
        // long.MaxValue ticks at 10^9 is about 292 years; at 90 kHz it needs far fewer ticks.
        MediaTime nanos = new(long.MaxValue, 1_000_000_000);
        MediaTime ninety = nanos.Rescale(90_000, MediaRounding.Floor);
        Assert.Equal((long)((System.Int128)long.MaxValue * 90_000 / 1_000_000_000), ninety.Ticks);

        // A product of two near-maximal values still works: the result is long.MaxValue ticks of a nearly equal scale.
        MediaTime big = new(long.MaxValue, long.MaxValue);
        Assert.Equal(1, big.Rescale(1, MediaRounding.Floor).Ticks);
        Assert.Equal(long.MaxValue - 1, big.Rescale(long.MaxValue - 1, MediaRounding.Floor).Ticks);
    }

    [Fact]
    public void RescaleReportsResultOverflow()
    {
        MediaTime t = new(long.MaxValue, 1);
        Assert.False(t.TryRescale(2, MediaRounding.Floor, out _));
        Assert.Throws<OverflowException>(() => t.Rescale(2, MediaRounding.Floor));
        Assert.True(new MediaTime(long.MinValue, 1).TryRescale(1, MediaRounding.Floor, out MediaTime same));
        Assert.Equal(long.MinValue, same.Ticks);
    }

    [Fact]
    public void TimescaleMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTime(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTime(1, -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTime.Zero.Rescale(0, MediaRounding.Floor));
    }

    [Fact]
    public void DefaultIsZeroSeconds()
    {
        MediaTime zero = default;
        Assert.Equal(1, zero.Timescale);
        Assert.True(zero.IsZero);
        Assert.True(zero.IsKnown);
        Assert.Equal(zero, new MediaTime(0, 48000));
    }

    [Fact]
    public void UnknownIsDistinct()
    {
        Assert.False(MediaTime.Unknown.IsKnown);
        Assert.Equal("unknown", MediaTime.Unknown.ToString());
        Assert.True(new MediaTime(long.MinValue, 1000).IsKnown);
    }

    [Fact]
    public void ComparesAcrossTimescales()
    {
        MediaTime a = new(1, 1000);
        MediaTime b = new(90, 90_000);
        MediaTime c = new(44_100, 44_100);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a < c);
        Assert.True(c > b);
        Assert.True(new MediaTime(-1, 3) < new MediaTime(-1, 4));
        Assert.True(new MediaTime(1, 3) > new MediaTime(33, 100));
        Assert.False(new MediaTime(1, 3) == new MediaTime(33, 100));
        Assert.Equal(1, a.CompareTo((object?)null));
        Assert.Throws<ArgumentException>(() => a.CompareTo("x"));
    }

    [Fact]
    public void ComparesExtremesWithoutOverflow()
    {
        MediaTime max = new(long.MaxValue, 1);
        MediaTime nearMax = new(long.MaxValue, long.MaxValue - 1);
        Assert.True(max > nearMax);
        Assert.True(new MediaTime(long.MinValue + 1, 1) < new MediaTime(long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void AddAndSubtractUseTheLeftTimescale()
    {
        MediaTime a = new(1000, 1000);
        MediaTime b = new(45_000, 90_000);
        Assert.Equal(new MediaTime(1500, 1000), a + b);
        Assert.Equal(1000 + 500, (a + b).Ticks);
        Assert.Equal(new MediaTime(500, 1000), a - b);
        Assert.Equal(a, a - b + b);
    }

    [Fact]
    public void InexactSumNeedsARoundingMode()
    {
        MediaTime a = new(0, 1000);
        MediaTime third = new(1, 3);
        Assert.Throws<ArithmeticException>(() => a + third);
        Assert.Equal(333, a.Add(third, MediaRounding.Floor).Ticks);
        Assert.Equal(334, a.Add(third, MediaRounding.Ceiling).Ticks);
        Assert.Equal(-334, a.Subtract(third, MediaRounding.Floor).Ticks);
        Assert.Throws<OverflowException>(() => new MediaTime(long.MaxValue, 1).Add(new MediaTime(1, 1), MediaRounding.Floor));
    }

    [Fact]
    public void TimeSpanConversionIsAnEdgeHelper()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), new MediaTime(3, 2).ToTimeSpan());
        Assert.Equal(new MediaTime(15_000_000, 10_000_000), MediaTime.FromTimeSpan(TimeSpan.FromSeconds(1.5)));
        // 1 tick at 90 kHz is 111.11 ticks of 100 ns.
        Assert.Equal(111, new MediaTime(1, 90_000).ToTimeSpan(MediaRounding.Floor).Ticks);
        Assert.Equal(112, new MediaTime(1, 90_000).ToTimeSpan(MediaRounding.Ceiling).Ticks);
        Assert.Throws<OverflowException>(() => new MediaTime(long.MaxValue, 1).ToTimeSpan());
    }

    [Fact]
    public void ToTicksConvertsToFrames()
    {
        // 1024 AAC samples at 44.1 kHz expressed in a 90 kHz clock and back.
        MediaTime frame = new(1024, 44_100);
        Assert.Equal(2089, frame.ToTicks(90_000, MediaRounding.Floor));
        Assert.Equal(2090, frame.ToTicks(90_000, MediaRounding.Ceiling));
        Assert.Equal("1024/44100", frame.ToString());
    }

    [Fact]
    public void MinAndMax()
    {
        MediaTime a = new(1, 2);
        MediaTime b = new(1, 3);
        Assert.Equal(a, MediaTime.Max(a, b));
        Assert.Equal(b, MediaTime.Min(a, b));
    }

    [Fact]
    public void NegationOverflowIsReported()
    {
        Assert.Equal(new MediaTime(-5, 10), -new MediaTime(5, 10));
        Assert.Throws<OverflowException>(() => -new MediaTime(long.MinValue, 10));
    }
}
