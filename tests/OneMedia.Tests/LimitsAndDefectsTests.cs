namespace RecursiveAI.OneMedia.Tests;

public class LimitsAndDefectsTests
{
    [Fact]
    public void DefaultsAreSaneAndPositive()
    {
        MediaLimits limits = MediaLimits.Default;
        Assert.True(limits.MaxAllocationBytes is > 0 and <= 1L << 30);
        Assert.True(limits.MaxStructureDepth is > 8 and < 1000);
        Assert.True(limits.MaxTracks > 0);
        Assert.True(limits.MaxDefects > 0);
    }

    [Fact]
    public void NonPositiveValuesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLimits { MaxAllocationBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLimits { MaxStructureDepth = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLimits { MaxDefects = 0 });
    }

    [Fact]
    public void ChecksAcceptTheLimitAndRejectOneMore()
    {
        MediaLimits limits = new() { MaxAllocationBytes = 100, MaxStructureDepth = 3, MaxElementCount = 10, MaxTracks = 2, MaxSampleTableEntries = 50, MaxTagBytes = 20, MaxAttachmentBytes = 30, MaxPacketBytes = 40, MaxLacedFrames = 8 };
        limits.CheckAllocation(100, 0);
        limits.CheckDepth(3, 0);
        limits.CheckElementCount(10, 0);
        limits.CheckTracks(2, 0);
        limits.CheckSampleTable(50, 0);
        limits.CheckTag(20, 0);
        limits.CheckAttachment(30, 0);
        limits.CheckPacket(40, 0);
        limits.CheckLacedFrames(8, 0);

        Assert.Equal(nameof(MediaLimits.MaxAllocationBytes), Assert.Throws<MediaLimitExceededException>(() => limits.CheckAllocation(101, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxStructureDepth), Assert.Throws<MediaLimitExceededException>(() => limits.CheckDepth(4, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxElementCount), Assert.Throws<MediaLimitExceededException>(() => limits.CheckElementCount(11, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxTracks), Assert.Throws<MediaLimitExceededException>(() => limits.CheckTracks(3, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxSampleTableEntries), Assert.Throws<MediaLimitExceededException>(() => limits.CheckSampleTable(51, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxTagBytes), Assert.Throws<MediaLimitExceededException>(() => limits.CheckTag(21, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxAttachmentBytes), Assert.Throws<MediaLimitExceededException>(() => limits.CheckAttachment(31, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxPacketBytes), Assert.Throws<MediaLimitExceededException>(() => limits.CheckPacket(41, 7)).LimitName);
        Assert.Equal(nameof(MediaLimits.MaxLacedFrames), Assert.Throws<MediaLimitExceededException>(() => limits.CheckLacedFrames(9, 7)).LimitName);
    }

    [Fact]
    public void ExceptionCarriesTheNumbers()
    {
        MediaLimits limits = new() { MaxAllocationBytes = 100 };
        MediaLimitExceededException ex = Assert.Throws<MediaLimitExceededException>(() => limits.CheckAllocation(5000, 1234));
        Assert.Equal(100, ex.Limit);
        Assert.Equal(5000, ex.Requested);
        Assert.Equal(1234, ex.Offset);
        Assert.IsAssignableFrom<MediaException>(ex);
        Assert.Contains("5000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeSizesFromHostileInputAreLimitBreaches()
    {
        // A size read as a signed value from a corrupt file must not slip past a check that only looks at the upper bound.
        Assert.Throws<MediaLimitExceededException>(() => MediaLimits.Default.CheckAllocation(-1, 0));
        Assert.Throws<MediaLimitExceededException>(() => MediaLimits.Default.CheckSampleTable(long.MinValue, 0));
    }

    [Fact]
    public void LimitsCanBeCopiedWithChanges()
    {
        MediaLimits strict = MediaLimits.Default with { MaxTracks = 4 };
        Assert.Equal(4, strict.MaxTracks);
        Assert.Equal(MediaLimits.Default.MaxStructureDepth, strict.MaxStructureDepth);
    }

    [Fact]
    public void ExceptionFamilyIsTypedAndCarriesOffsets()
    {
        MediaFormatException format = new(new MediaDefect(DefectKind.SizeExceedsFile, DefectSeverity.Error, 99, "box too long"));
        Assert.Equal(99, format.Offset);
        Assert.Equal(DefectKind.SizeExceedsFile, format.Kind);
        Assert.IsAssignableFrom<MediaException>(format);
        Assert.IsAssignableFrom<MediaException>(new UnsupportedMediaException("x"));
        Assert.Equal(-1, new MediaFormatException("x").Offset);
    }

    [Fact]
    public void DefectLogKeepsOrderAndCapsItsSize()
    {
        DefectLog log = new(new MediaLimits { MaxDefects = 3 });
        log.Add(DefectKind.Truncated, DefectSeverity.Warning, 10, "a");
        log.Add(new MediaDefect(DefectKind.InvalidValue, DefectSeverity.Info, 20, "b", "x.y"));
        Assert.False(log.HasErrors);
        log.Add(DefectKind.Duplicate, DefectSeverity.Error, 30, "c");
        log.Add(DefectKind.Duplicate, DefectSeverity.Error, 40, "dropped");
        log.Add(DefectKind.Duplicate, DefectSeverity.Warning, 50, "dropped too");

        Assert.Equal(3, log.Count);
        Assert.Equal(2, log.Dropped);
        Assert.True(log.HasErrors);
        Assert.Equal([10L, 20L, 30L], log.Select(d => d.Offset).ToArray());
        Assert.Equal("x.y", log[1].Code);
        Assert.Contains("at 10", log[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DroppedErrorsStillMarkTheLog()
    {
        DefectLog log = new(new MediaLimits { MaxDefects = 1 });
        log.Add(DefectKind.Other, DefectSeverity.Info, -1, "first");
        log.Add(DefectKind.Other, DefectSeverity.Error, -1, "lost");
        Assert.True(log.HasErrors);
        Assert.Equal(1, log.Dropped);
    }
}
