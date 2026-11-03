using System.Globalization;
using RecursiveAI.OneMedia.Avchd;
using RecursiveAI.OneMedia.ElementaryStreams;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.MpegPs;
using RecursiveAI.OneMedia.MpegTs;

namespace RecursiveAI.OneMedia.Tests;

[Collection(AllocationMeasurements.Name)]
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

    [Fact]
    public void FrameDoublingPictureTimingSetsDurationsToTwoFramePeriods()
    {
        // The VUI declares 25 frames per second (3600 ticks) but every picture carries pic_struct 7 (frame doubling, H.264 Table D-1)
        // and the pictures are 7200 ticks apart; ffprobe 7.0.2 reports 7200 for each.
        using IDemuxer d = OpenFile("h264-pic-struct-doubling.ts");
        List<Got> ours = Drain(d);
        string[][] theirs = File.ReadAllLines(Path("h264-pic-struct-doubling.ts.packets.csv")).Select(l => l.Split(',')).ToArray();
        Assert.Equal(theirs.Length, ours.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.Equal(long.Parse(theirs[i][3], CultureInfo.InvariantCulture), ours[i].Duration);
        }

        VideoTrackInfo v = d.Tracks.First(t => t.Kind == TrackKind.Video).Video!.Value;
        Assert.Equal(25, v.FrameRateNumerator / v.FrameRateDenominator); // the declared rate stays visible
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
    [InlineData(0x00, 0, false, 0)]
    [InlineData(0x02, 60, false, 60)]
    [InlineData(0x42, 60, true, 120)]
    [InlineData(0x12, 540, false, 540)]
    [InlineData(0x2C, -360, false, -360)]
    [InlineData(0x6E, -420, true, -360)]
    public void MdpmFlagsByteNamesTheCameraZone(int flags, int standard, bool dst, int utcOffset)
    {
        // Flags byte layout observed against ExifTool 13.60 on camera recordings: 0x20 west, 0x40 daylight saving, low five bits in half hours.
        byte[] uuid = [0x17, 0xEE, 0x8C, 0x60, 0xF8, 0x4D, 0x11, 0xD9, 0x8C, 0xD6, 0x08, 0x00, 0x20, 0x0C, 0x9A, 0x66];
        byte[] body = [.. uuid, .. "MDPM"u8.ToArray(), 2, 0x18, (byte)flags, 0x20, 0x12, 0x01, 0x19, 0x26, 0x09, 0x18, 0x35];
        Assert.True(Mdpm.TryParse([5, (byte)body.Length, .. body, 0x80], out MdpmBlock? block));
        Assert.Equal((standard, dst), block!.TimeZone);
        Assert.Equal(utcOffset, block.UtcOffsetMinutes);
    }

    [Fact]
    public void MdpmHighBitInTheFlagsByteGivesNoZone()
    {
        byte[] uuid = [0x17, 0xEE, 0x8C, 0x60, 0xF8, 0x4D, 0x11, 0xD9, 0x8C, 0xD6, 0x08, 0x00, 0x20, 0x0C, 0x9A, 0x66];
        byte[] body = [.. uuid, .. "MDPM"u8.ToArray(), 1, 0x18, 0xFF, 0x20, 0x12, 0x01];
        Assert.True(Mdpm.TryParse([5, (byte)body.Length, .. body, 0x80], out MdpmBlock? block));
        Assert.Null(block!.TimeZone);
        Assert.Null(block.UtcOffsetMinutes);
    }

    [Fact]
    public void MdpmSonyModelIsSpreadOverThreeEntries()
    {
        byte[] uuid = [0x17, 0xEE, 0x8C, 0x60, 0xF8, 0x4D, 0x11, 0xD9, 0x8C, 0xD6, 0x08, 0x00, 0x20, 0x0C, 0x9A, 0x66];
        byte[] body = [.. uuid, .. "MDPM"u8.ToArray(), 3, 0xE4, (byte)'D', (byte)'S', (byte)'C', (byte)'-', 0xE5, (byte)'W', (byte)'X', (byte)'3', (byte)'5', 0xE6, (byte)'0', 0, 0, 0];
        Assert.True(Mdpm.TryParse([5, (byte)body.Length, .. body, 0x80], out MdpmBlock? block));
        Assert.Equal("DSC-WX350", block!.Model);
    }

    [Theory]
    [InlineData("mpeg2-mp2.ts")]
    [InlineData("h264-aac.ts")]
    [InlineData("h264-ac3.ts")]
    public void PullPathAllocatesNothingPerPacket(string name)
    {
        AllocationGuard.RequireOptimised();
        byte[] file = File.ReadAllBytes(Path(name));
        using (IDemuxer warm = MpegTsDemuxer.Open(new MemoryMediaSource(file)))
        {
            Drain(warm);
        }

        long allocated = AllocationGuard.Best(() =>
        {
            using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(file));
            Assert.True(d.TryReadPacket(out _));
            return Measure(d);
        });
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

    [Fact]
    public void H264InTransportStreamGetsItsRfc6381StringFromTheStream()
    {
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(Path("h264-aac.ts"))));
        Assert.StartsWith("avc1.", d.Tracks.First(t => t.Kind == TrackKind.Video).Rfc6381Codec(), StringComparison.Ordinal);
    }

    // At open only the front of the stream has been read, which must not stand for the whole: the figure comes from the length and the
    // duration, and agrees with the program clock figure once every packet has been read.
    [Fact]
    public void BitRateAtOpenIsTheWholeStreamsAndNotTheFrontsAlone()
    {
        byte[] bytes = File.ReadAllBytes(Path("h264-aac.ts"));
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(bytes));
        long atOpen = d.BitRate;
        Assert.InRange(atOpen, 1, long.MaxValue);
        Assert.Equal(bytes.Length * 8L * d.Duration.Timescale / d.Duration.Ticks, atOpen);
        while (d.TryReadPacket(out _))
        {
        }

        Assert.InRange(d.BitRate, atOpen * 8 / 10, atOpen * 12 / 10);
    }

    // ffmpeg writes the SDT ahead of the PAT and PMT, so the service is described before its program is known.
    [Fact]
    public void ServiceNamesFromAnSdtThatPrecedesThePatAreKept()
    {
        using MpegTsDemuxer d = MpegTsDemuxer.Open(new MemoryMediaSource(File.ReadAllBytes(Path("h264-aac.ts"))));
        TransportStreamProgram program = Assert.Single(d.Programs);
        Assert.Equal("Service01", program.ServiceName);
        Assert.Equal("FFmpeg", program.ServiceProvider);
        Assert.Contains(d.ReadMetadata().NativeTags, t => t.Key == "service_name");
    }

    [Fact]
    public void AvchdSidecarsBesideTheStreamAreListed()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onemedia-avchd-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(System.IO.Path.Combine(root, "BDMV", "STREAM"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "BDMV", "CLIPINF"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "BDMV", "PLAYLIST"));
            string stream = System.IO.Path.Combine(root, "BDMV", "STREAM", "00001.MTS");
            File.Copy(Path("h264-aac.m2ts"), stream);
            File.WriteAllBytes(System.IO.Path.Combine(root, "BDMV", "CLIPINF", "00001.CPI"), new byte[40]);
            File.WriteAllBytes(System.IO.Path.Combine(root, "BDMV", "CLIPINF", "00002.CPI"), new byte[40]);
            File.WriteAllBytes(System.IO.Path.Combine(root, "BDMV", "PLAYLIST", "00000.MPL"), new byte[24]);
            using MediaFile file = Media.Open(stream);
            Tags.ITagContainer sidecars = Assert.Single(file.Metadata.Containers, c => c.System == MpegTsTagSystems.Sidecars);
            Assert.Equal(["00000.MPL", "00001.CPI"], sidecars.Tags.Select(t => t.Key).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DvdSubpictureUnitIsJoinedFromItsPesPackets()
    {
        // The first two bytes give the unit's size (30 bytes here); the second PES packet carries the rest.
        Mpeg.PacketQueue queue = new(MediaLimits.Default);
        Mpeg.PesPassthroughAssembler a = new(0, queue, MediaLimits.Default, new DefectLog(), keyframes: true, dvdSubpicture: true);
        byte[] first = new byte[20];
        first[1] = 30;
        a.Push(first, new Mpeg.PesStamp(900, -1, 0, false), true);
        a.Push(new byte[10], new Mpeg.PesStamp(1800, -1, 100, false), true);
        a.Push(new byte[8], new Mpeg.PesStamp(2700, -1, 200, false), true); // a new unit
        a.Flush();
        Assert.True(queue.TryDequeue(out PacketInfo one, out ReadOnlySpan<byte> data));
        Assert.Equal(30, data.Length);
        Assert.Equal(900, one.Pts.Ticks);
        Assert.True(queue.TryDequeue(out PacketInfo two, out data));
        Assert.Equal(8, data.Length);
        Assert.Equal(2700, two.Pts.Ticks);
    }

    [Fact]
    public void Ac3IsFoundWhenTheStreamStartsInsideAFrame()
    {
        // A private stream with no descriptor whose first PES packet holds 300 bytes of the tail of a frame, then whole frames.
        byte[] ts = File.ReadAllBytes(Path("h264-ac3.ts"));
        using IDemuxer d = MediaFormats.Default.Open(new MemoryMediaSource(ts), new MediaOpenOptions());
        MediaTrack audio = d.Tracks.First(t => t.Kind == TrackKind.Audio);
        Assert.Equal("ac3", audio.Codec.Name);
        int frame = 0;
        while (d.TryReadPacket(out PacketView v))
        {
            if (v.Info.TrackIndex == audio.Index)
            {
                frame = v.Data.Length;
                break;
            }
        }

        Assert.True(frame > 0);
        byte[] bytes = new byte[300 + frame * 8];
        bytes.AsSpan(0, 300).Fill(0x55);
        int at = 300;
        using IDemuxer again = MediaFormats.Default.Open(new MemoryMediaSource(ts), new MediaOpenOptions());
        while (again.TryReadPacket(out PacketView v) && at + v.Data.Length <= bytes.Length)
        {
            if (v.Info.TrackIndex == audio.Index)
            {
                v.Data.CopyTo(bytes.AsSpan(at));
                at += v.Data.Length;
            }
        }

        Mpeg.PacketQueue queue = new(MediaLimits.Default);
        Mpeg.ProbingAssembler probe = new(0, queue, MediaLimits.Default, new DefectLog());
        probe.Push(bytes.AsSpan(0, 300), new Mpeg.PesStamp(1000, -1, 0, false), true);
        probe.Push(bytes.AsSpan(300, at - 300), default, false);
        probe.Flush();
        Assert.Equal("ac3", probe.Resolved?.Codec.Name);
        Assert.True(queue.Count >= 2);
    }
}
