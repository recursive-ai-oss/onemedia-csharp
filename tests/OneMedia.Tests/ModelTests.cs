using RecursiveAI.OneMedia.Audio;

namespace RecursiveAI.OneMedia.Tests;

public class ModelTests
{
    private sealed class Node(ulong key, long offset, params Node[] children) : IStructureNode
    {
        public ulong TypeKey => key;

        public long Offset => offset;

        public int HeaderSize => 8;

        public long PayloadSize => 100;

        public bool IsSizeInferred => false;

        public IReadOnlyList<IStructureNode> Children => children;
    }

    private sealed class EndlessNode : IStructureNode
    {
        public ulong TypeKey => 1;

        public long Offset => 0;

        public int HeaderSize => 8;

        public long PayloadSize => 8;

        public bool IsSizeInferred => false;

        public IReadOnlyList<IStructureNode> Children => [new EndlessNode()];
    }

    [Fact]
    public void StructureNodesExposeRangesWithoutContent()
    {
        IStructureNode node = new Node(0x6D6F6F76, 40);
        Assert.Equal(new ByteRange(40, 108), node.Range);
        Assert.Equal(new ByteRange(48, 100), node.Payload);
        Assert.Equal(148, node.Range.End);
        Assert.True(ByteRange.None.IsNone);
    }

    [Fact]
    public void DescendantsWalkInFileOrderWithDepths()
    {
        Node tree = new(1, 0, new Node(2, 10, new Node(3, 20), new Node(4, 30)), new Node(5, 40));
        (ulong, int)[] walked = tree.Descendants().Select(n => (n.Node.TypeKey, n.Depth)).ToArray();
        Assert.Equal([(1UL, 1), (2UL, 2), (3UL, 3), (4UL, 3), (5UL, 2)], walked);
        Assert.Equal(40, tree.FindChild(5)?.Offset);
        Assert.Null(tree.FindChild(99));
    }

    [Fact]
    public void DescendantsEnforceDepthAndCountLimits()
    {
        Assert.Throws<MediaLimitExceededException>(() => new EndlessNode().Descendants(new MediaLimits { MaxStructureDepth = 10 }).Count());
        Node wide = new(1, 0, Enumerable.Range(0, 20).Select(i => new Node(2, i)).ToArray());
        MediaLimitExceededException ex = Assert.Throws<MediaLimitExceededException>(() => wide.Descendants(new MediaLimits { MaxElementCount = 10 }).Count());
        Assert.Equal(nameof(MediaLimits.MaxElementCount), ex.LimitName);
    }

    [Fact]
    public void TrackCarriesWhatTheAudioAdapterNeeds()
    {
        MediaTrack track = new()
        {
            Index = 1,
            Id = 2,
            Kind = TrackKind.Audio,
            Codec = CodecId.Aac,
            CodecTag = "mp4a",
            Configuration = new CodecConfiguration(new byte[] { 0x12, 0x10 }, "asc"),
            Timescale = 44_100,
            Audio = new AudioTrackInfo(44_100, 2),
            Edit = new TrackEditInfo(Priming: new MediaTime(2112, 44_100), EndPadding: new MediaTime(500, 44_100)),
            Language = "eng",
            Disposition = TrackDisposition.Default,
        };
        Assert.False(track.Configuration.IsEmpty);
        Assert.Equal(2112, track.Edit.Priming.ToTicks(track.Audio!.Value.SampleRate, MediaRounding.Floor));
        Assert.False(track.Duration.IsKnown);
        Assert.Equal(-1, track.PacketCount);
        Assert.Equal(MediaTime.Zero, track.Edit.StartTime);
    }

    [Fact]
    public void EditSegmentsMarkEmptyEdits()
    {
        Assert.True(new EditSegment(MediaTime.Unknown, new MediaTime(1000, 1000)).IsEmpty);
        Assert.False(new EditSegment(MediaTime.Zero, new MediaTime(1000, 1000)).IsEmpty);
    }

    [Fact]
    public void CodecNamesMatchOneAudio()
    {
        foreach (CodecId codec in new[] { CodecId.Aac, CodecId.Mp3, CodecId.Opus, CodecId.Vorbis, CodecId.Flac, CodecId.AmrNb, CodecId.AmrWb })
        {
            Assert.Equal(codec.Name, AudioCodecMap.ToOneAudio(codec).Name);
            Assert.Equal(codec, AudioCodecMap.FromOneAudio(AudioCodecMap.ToOneAudio(codec)));
        }

        Assert.Equal(global::OneAudio.CodecId.Aac, AudioCodecMap.ToOneAudio(CodecId.Aac));
        Assert.Equal(global::OneAudio.CodecId.Opus, AudioCodecMap.ToOneAudio(CodecId.Opus));
        Assert.True(CodecId.Unknown.IsUnknown);
    }

    [Fact]
    public void PacketInfoDefaultsAreUsable()
    {
        PacketInfo info = new(0, MediaTime.Unknown, MediaTime.Unknown, MediaTime.Unknown, PacketAttributes.None, ByteRange.None, 0);
        Assert.False(info.IsKeyframe);
        Assert.True(info.DiscardPadding.IsZero);
    }
}
