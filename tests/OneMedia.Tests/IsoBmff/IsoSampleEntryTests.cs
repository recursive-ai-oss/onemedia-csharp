using System.Text;
using RecursiveAI.OneMedia.IsoBmff;
using static RecursiveAI.OneMedia.Tests.IsoBmffSupport.Mp4Builder;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Crafted-file tests for QuickTime and 3GPP sample entries that need special reading.</summary>
public class IsoSampleEntryTests
{
    // QuickTime File Format, sound sample description version 1: IMA 4:1 stores 64 samples in 34 bytes per channel.
    private static byte[] Ima4Entry(int channels) =>
        Box("ima4", new byte[6], U16(1), U16(1), U16(0), U32(0), U16((ushort)channels), U16(16), U16(0), U16(0), U32(22050u << 16), U32(64), U32((uint)(34 * channels)), U32((uint)(34 * channels)), U32(2));

    [Fact]
    public void PacketisedQuickTimeAudioIsJoinedPerChunkLikeOtherTools()
    {
        // Two chunks of 1088 samples (17 packets of 68 bytes); the table lists every audio sample with a one-byte size.
        TrackSpec audio = new()
        {
            Id = 1,
            Handler = "soun",
            Timescale = 22050,
            SampleCount = 2176,
            SizeOf = _ => 1,
            DurationOf = _ => 1,
            ConstantSizeBox = true,
            SamplesPerChunk = 1088,
            SampleEntries = Ima4Entry(2),
        };
        Layout layout = Build([audio], moovFirst: false, brand: "isom");
        byte[] file = layout.ToArray([audio]);
        List<Got> got = IsoBmffTestHelpers.Demux(file, out IReadOnlyList<MediaTrack> tracks, out DefectLog defects);
        Assert.Empty(defects);
        Assert.Equal(-1, tracks[0].PacketCount);
        Assert.True(got.Count == 4, string.Join(";", got.Select(g => $"{g.Size}/{g.Duration}/{g.Pos}")));

        // 1024 samples = 16 packets = 1088 bytes, then the remaining 64 samples = one packet of 68 bytes; the same in the second chunk.
        Assert.Equal([1088, 68, 1088, 68], got.Select(g => g.Size));
        Assert.Equal([1024L, 64, 1024, 64], got.Select(g => g.Duration));
        Assert.Equal([0L, 1024, 1088, 2112], got.Select(g => g.Dts));
        long chunk1 = layout.SampleOffsets[0][0], chunk2 = layout.SampleOffsets[0][1088];
        Assert.Equal([chunk1, chunk1 + 1088, chunk2, chunk2 + 1088], got.Select(g => g.Pos));

        // A seek into the middle of a packet lands on the packet's first sample (a multiple of 64 samples into the chunk).
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        d.Seek(new MediaTime(500, 22050), SeekMode.PreviousKeyframe);
        Assert.True(d.TryReadPacket(out PacketView first));
        Assert.Equal(448, first.Info.Dts.Ticks);
        Assert.Equal(chunk1 + (7 * 68), first.Info.SourceRange.Offset);
        Assert.Equal(10 * 68, first.Info.Size);
        d.Seek(new MediaTime(1100, 22050), SeekMode.PreviousKeyframe);
        Assert.True(d.TryReadPacket(out first));
        Assert.Equal(1088, first.Info.Dts.Ticks);
        Assert.Equal(chunk2, first.Info.SourceRange.Offset);
    }

    // The real entry of a QuickTime AAC file: sound description version 1 followed by a 'wave' atom that holds the 'esds' box.
    private const string QuickTimeAacEntry =
        "0000009d6d7034610000000000000001000100000000000000020010fffe0000562200000000040000000001000000020000000200000069776176650000000c66726d616d7034610000000c6d703461000000000000003965736473000000000380808028000000048080801a401500180000007d0000009c40058080800813800484000020000680808001020000000800000000000000004005800d";

    [Fact]
    public void QuickTimeAacConfigurationInsideWaveIsFound()
    {
        TrackSpec audio = new() { Id = 1, Handler = "soun", Timescale = 22050, SampleCount = 4, SizeOf = _ => 200, DurationOf = _ => 1024, SampleEntries = Convert.FromHexString(QuickTimeAacEntry) };
        byte[] file = Build([audio], moovFirst: true, brand: "mp42").ToArray([audio]);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        MediaTrack t = d.Tracks[0];
        Assert.Equal(CodecId.Aac, t.Codec);
        Assert.Equal("esds", t.Configuration.Format);
        Assert.True(t.Configuration.Data.Length > 30);
        Assert.True(RecursiveAI.OneMedia.Codecs.Aac.EsDescriptor.TryParse(t.Configuration.Data, out RecursiveAI.OneMedia.Codecs.Aac.EsDescriptor? es));
        Assert.Equal("1380048400002000", Convert.ToHexString(es!.DecoderSpecificInfo.Span).ToLowerInvariant());
    }

    [Theory]
    [InlineData("samr", 8000, "damr")]
    [InlineData("sawb", 16000, "damr")]
    public void AmrIsMonoAtTheCodecRateWhateverTheEntryStates(string format, int rate, string config)
    {
        // 3GPP TS 26.244 §8.3: the channel count and sample rate fields of an AMR sample entry are not meaningful; AMR is mono at 8 or 16 kHz.
        byte[] entry = Box(format, new byte[6], U16(1), new byte[8], U16(2), U16(16), U16(0), U16(0), U32(1000u << 16), Box(config, "noki"u8.ToArray(), [0, 0x80, 0, 0, 0, 1]));
        TrackSpec audio = new() { Id = 1, Handler = "soun", Timescale = 1000, SampleCount = 3, SizeOf = _ => 32, DurationOf = _ => 20, SampleEntries = entry };
        byte[] file = Build([audio], moovFirst: true, brand: "3gp4").ToArray([audio]);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(1, d.Tracks[0].Audio!.Value.Channels);
        Assert.Equal(rate, d.Tracks[0].Audio!.Value.SampleRate);
    }

    [Fact]
    public void QuickTimeTextEntryIsTimedText()
    {
        TrackSpec text = new() { Id = 1, Handler = "text", Timescale = 1000, SampleCount = 2, SizeOf = _ => 12, DurationOf = _ => 500, SampleEntries = Box("text", new byte[6], U16(1), new byte[43]) };
        byte[] file = Build([text], moovFirst: true, brand: "qt  ").ToArray([text]);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(CodecId.TimedText, d.Tracks[0].Codec);
    }

    [Theory]
    [InlineData("tapt")]
    [InlineData("gmhd")]
    [InlineData("hinf")]
    [InlineData("hnti")]
    public void QuickTimeAndHintContainersHoldBoxes(string type)
    {
        uint code = BitConverter.ToUInt32(Encoding.ASCII.GetBytes(type).Reverse().ToArray());
        Assert.True(BoxReader.IsContainer(code, 0));
    }
}
