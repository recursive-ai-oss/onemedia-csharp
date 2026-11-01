using System.Globalization;
using RecursiveAI.OneMedia.Avchd;
using RecursiveAI.OneMedia.ElementaryStreams;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.MpegPs;
using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Tests;

public class MpegTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "mpeg", name);

    private sealed record Got(int Track, long Pts, long Dts, long Duration, int Size, bool Key, long Pos, long Unused);

    private static List<Got> Drain(IDemuxer d)
    {
        List<Got> all = [];
        while (d.TryReadPacket(out PacketView v))
        {
            PacketInfo i = v.Info;
            all.Add(new Got(i.TrackIndex, i.Pts.IsKnown ? i.Pts.Ticks : long.MinValue, i.Dts.IsKnown ? i.Dts.Ticks : long.MinValue,
                i.Duration.IsKnown ? i.Duration.Ticks : long.MinValue, v.Data.Length, i.IsKeyframe, i.SourceRange.Offset, 0L));
        }

        return all;
    }

    private static IDemuxer OpenFile(string name) => MediaFormats.Default.Open(new MemoryMediaSource(File.ReadAllBytes(Path(name))), new MediaOpenOptions());

    [Theory]
    [InlineData("h264-aac.ts", "mpegts")]
    [InlineData("h264-aac.m2ts", "mpegts")]
    [InlineData("h264-ac3.ts", "mpegts")]
    [InlineData("mpeg2-mp2.ts", "mpegts")]
    [InlineData("mpeg2-mp2.vob", "mpegps")]
    [InlineData("mpeg1-mp2.mpg", "mpegps")]
    [InlineData("h264.264", "h264")]
    [InlineData("hevc.265", "hevc")]
    [InlineData("mpeg2.m2v", "mpegvideo")]
    [InlineData("vp8.ivf", "ivf")]
    public void PacketsMatchTheListingFfprobeWrote(string name, string format)
    {
        using IDemuxer d = OpenFile(name);
        Assert.Equal(format, d.Format.ToString(), ignoreCase: true);
        List<Got> ours = Drain(d);
        string[][] theirs = File.ReadAllLines(Path(name + ".packets.csv")).Select(l => l.Split(',')).ToArray();
        foreach (MediaTrack t in d.Tracks)
        {
            string[][] other = theirs.Where(f => f[0] == t.Index.ToString(CultureInfo.InvariantCulture)).ToArray();
            List<Got> mine = ours.Where(g => g.Track == t.Index).ToList();
            if (other.Length == 0)
            {
                continue; // a track ffprobe lists no packets for (a data stream)
            }

            Assert.Equal(other.Length, mine.Count);
            for (int i = 0; i < mine.Count; i++)
            {
                string[] o = other[i];
                Assert.Equal(long.Parse(o[4], CultureInfo.InvariantCulture), mine[i].Size);
                Assert.Equal(o[5].Contains('K', StringComparison.Ordinal), mine[i].Key);
                bool rawStream = format is "h264" or "hevc" or "mpegvideo" or "ivf";
                if (!rawStream && t.Kind != TrackKind.Audio && long.TryParse(o[1], out long pts) && mine[i].Pts != long.MinValue)
                {
                    Assert.Equal(pts, mine[i].Pts);
                }

                if (!rawStream && t.Kind != TrackKind.Audio && long.TryParse(o[2], out long dts) && mine[i].Dts != long.MinValue)
                {
                    Assert.Equal(dts, mine[i].Dts);
                }
            }
        }
    }

    private static IMediaFormat FormatFor(MediaFormatId id) => id.ToString() switch
    {
        "mpegts" => new MpegTsFormat(),
        "mpegps" => new MpegPsFormat(),
        "ivf" => new IvfFormat(),
        "h264" => new VideoStreamFormat(VideoStreamKind.H264),
        _ => throw new NotSupportedException(id.ToString()),
    };

    [Theory]
    [InlineData("h264-aac.ts")]
    [InlineData("h264-aac.m2ts")]
    [InlineData("mpeg2-mp2.vob")]
    [InlineData("h264.264")]
    [InlineData("vp8.ivf")]
    public void ForwardOnlyGivesTheSamePacketsAsSeekable(string name)
    {
        byte[] file = File.ReadAllBytes(Path(name));
        using IDemuxer seekable = MediaFormats.Default.Open(new MemoryMediaSource(file), new MediaOpenOptions());
        using IDemuxer forward = FormatFor(seekable.Format).Open(new StreamForwardSource(new MemoryStream(file)), new MediaOpenOptions());
        Assert.Equal(Drain(seekable), Drain(forward));
    }

    [Theory]
    [InlineData("h264-aac.ts", 188, true)]
    [InlineData("h264-aac.m2ts", 192, true)]
    public void PacketSizeIsDetected(string name, int size, bool seekable)
    {
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(Path(name))));
        Assert.Equal(size, d.PacketSize);
        Assert.Equal(seekable, d.CanSeek);
        Assert.Contains(d.Tracks, t => t.Kind == TrackKind.Video);
        Assert.Contains(d.Tracks, t => t.Kind == TrackKind.Audio);
        Assert.True(d.Duration.IsKnown);
    }

    [Fact]
    public void ContinuityGapIsReportedWithItsOffset()
    {
        byte[] file = File.ReadAllBytes(Path("h264-aac.ts"));
        // Remove one packet of the video PID from the middle: the counter then skips.
        int cut = -1;
        for (int at = 188 * 40; at < file.Length - 188; at += 188)
        {
            int pid = ((file[at + 1] & 0x1F) << 8) | file[at + 2];
            if (pid != 0 && pid != 0x1000 && pid != 17 && (file[at + 3] & 0x10) != 0 && (file[at + 1] & 0x40) == 0)
            {
                cut = at;
                break;
            }
        }

        Assert.True(cut > 0);
        byte[] damaged = [.. file.AsSpan(0, cut), .. file.AsSpan(cut + 188)];
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(damaged));
        Drain(d);
        MediaDefect defect = Assert.Single(d.Defects, x => x.Code == "ts.continuity");
        Assert.True(defect.Offset >= cut);
    }

    [Fact]
    public void SeekLandsOnAKeyframeAtOrBeforeTheTarget()
    {
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(Path("h264-aac.ts"))));
        int video = d.Tracks.First(t => t.Kind == TrackKind.Video).Index;
        MediaTime landed = d.Seek(new MediaTime(1, 2));
        Assert.True(landed.IsKnown);
        Assert.True(d.TryReadPacket(out PacketView v));
                while (v.Info.TrackIndex != video)
        {
            Assert.True(d.TryReadPacket(out v));
        }

        Assert.True(v.Info.IsKeyframe);
    }

    [Fact]
    public async Task OpenAsyncGivesTheSamePacketsAsOpen()
    {
        byte[] file = File.ReadAllBytes(Path("h264-aac.ts"));
        using IDemuxer sync = MediaFormats.Default.Open(new MemoryMediaSource(file), new MediaOpenOptions());
        List<Got> expected = Drain(sync);
        await using IAsyncDemuxer a = await new MpegTsFormat().OpenAsync(new MemoryMediaSource(file), new MediaOpenOptions());
        int n = 0;
        await foreach (OwnedPacket p in a.ReadPacketsAsync(null))
        {
            using (p)
            {
                Assert.Equal(expected[n].Size, p.Info.Size);
                n++;
            }
        }

        Assert.Equal(expected.Count, n);
    }

    [Fact]
    public void MdpmEntriesDecodeToRecordingTimeAndMaker()
    {
        // SEI payload: user_data_unregistered (type 5) with the MDPM identifier, "MDPM", a count and 5-byte entries.
        byte[] uuid = [0x17, 0xEE, 0x8C, 0x60, 0xF8, 0x4D, 0x11, 0xD9, 0x8C, 0xD6, 0x08, 0x00, 0x20, 0x0C, 0x9A, 0x66];
        byte[] body =
        [
            .. uuid, .. "MDPM"u8.ToArray(), 3,
            0x18, 0x00, 0x20, 0x12, 0x01,
            0x19, 0x26, 0x09, 0x18, 0x35,
            0xE0, 0x01, 0x03, 0x05, 0x91,
        ];
        byte[] sei = [5, (byte)body.Length, .. body, 0x80];
        Assert.True(Mdpm.TryParse(sei, out MdpmBlock? block));
        Assert.Equal(new DateTime(2012, 1, 26, 9, 18, 35), block!.RecordingTime);
        Assert.Equal("Panasonic", block.Make);
        Assert.False(Mdpm.TryParse([5, 2, 1, 2, 0x80], out _));
    }

    [Theory]
    [InlineData("mpeg2-mp2.ts")]
    [InlineData("h264-aac.ts")]
    [InlineData("h264-ac3.ts")]
    public void PullPathAllocatesNothingPerPacket(string name)
    {
        byte[] file = File.ReadAllBytes(Path(name));
        using (IDemuxer warm = MpegTsDemuxer.Open(new MemoryMediaSource(file)))
        {
            Drain(warm);
        }

        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(file));
        Assert.True(d.TryReadPacket(out _));
        long allocated = Measure(d);
        Assert.Equal(0, allocated);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long Measure(MpegTsDemuxer d)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (d.TryReadPacket(out _))
        {
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void MetadataOfAStreamWithoutServiceNamesIsStillReadable()
    {
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(Path("h264-aac.ts"))));
        MediaMetadata m = d.ReadMetadata();
        Assert.NotNull(m.Facts.Duration);
    }
}
