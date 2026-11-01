using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Codecs.Dv;
using RecursiveAI.OneMedia.Dv;
using RecursiveAI.OneMedia.Metadata;

namespace RecursiveAI.OneMedia.Tests.Avi;

public class DvTests
{
    // One DV frame of 25 Mbit/s video: 10 (525/60) or 12 (625/50) DIF sequences; only the first carries packs here, as in the standard's
    // layout the others repeat header, subcode and VAUX blocks (which the reader does not need).
    internal static byte[] Frame(bool pal, bool wide = false, int stype = 0, DateTime? recorded = null, int fill = 0, int audioSmp = 0, int audioQu = 0)
    {
        int sequences = (pal ? 12 : 10) * (stype == 4 ? 2 : 1);
        byte[] frame = new byte[sequences * 12000];
        for (int s = 0; s < sequences; s++)
        {
            int o = s * 12000;
            frame[o] = 0x1F;
            frame[o + 1] = (byte)((s % (pal ? 12 : 10) << 4) | 0x07 | (s >= (pal ? 12 : 10) ? 0x08 : 0));
            frame[o + 2] = 0;
            frame[o + 3] = (byte)(pal ? 0xBF : 0x3F);
            frame[o + 4] = 0xF8;
            frame[o + 5] = frame[o + 6] = frame[o + 7] = 0x78;
            frame.AsSpan(o + 8, 72).Fill(0xFF);
            for (int b = 1; b < 150; b++)
            {
                int bo = o + (b * 80);
                int section = b <= 2 ? 1 : b <= 5 ? 2 : (b - 6) % 16 == 0 ? 3 : 4;
                frame[bo] = (byte)((section << 5) | 0x16);
                frame[bo + 1] = frame[o + 1];
                frame[bo + 2] = (byte)(section == 1 ? b - 1 : section == 2 ? b - 3 : (b - 6) / 16);
                frame.AsSpan(bo + 3, 77).Fill(section == 4 ? (byte)fill : (byte)0xFF);
            }
        }

        // VAUX block 0: source (0x60), source control (0x61), recording date (0x62) and time (0x63).
        int vaux = 3 * 80 + 3;
        Pack(frame, vaux, 0x60, 0xFF, 0xFF, (byte)(0xC0 | (pal ? 0x20 : 0) | stype), 0xFF);
        Pack(frame, vaux + 5, 0x61, 0x3F, (byte)(0xC8 | (wide ? 2 : 0)), 0xFC, 0xFF);
        if (recorded is { } t)
        {
            Pack(frame, vaux + 10, 0x62, 0xFF, (byte)(0xC0 | Bcd(t.Day)), (byte)Bcd(t.Month), (byte)Bcd(t.Year % 100));
            Pack(frame, vaux + 15, 0x63, 0xFF, (byte)(0x80 | Bcd(t.Second)), (byte)(0x80 | Bcd(t.Minute)), (byte)(0xC0 | Bcd(t.Hour)));
        }

        // Subcode block 0: a time code pack in the first sync block.
        Pack(frame, (1 * 80) + 3 + 3, 0x13, 0x04, 0x83, 0x82, 0xC1);

        // Audio block 3 of the first sequence: the audio source pack.
        Pack(frame, (6 + (3 * 16)) * 80 + 3, 0x50, 0xD8, 0x00, 0xE0, (byte)(0x80 | (audioSmp << 3) | audioQu));
        return frame;
    }

    private static int Bcd(int v) => ((v / 10) << 4) | (v % 10);

    private static void Pack(byte[] frame, int at, byte type, byte a, byte b, byte c, byte d)
    {
        frame[at] = type;
        frame[at + 1] = a;
        frame[at + 2] = b;
        frame[at + 3] = c;
        frame[at + 4] = d;
    }

    [Fact]
    public void ParsesTheHeaderAndPacksOfAPalFrame()
    {
        byte[] frame = Frame(pal: true, wide: true, recorded: new DateTime(2019, 3, 4, 5, 6, 7), audioSmp: 1);
        Assert.True(DvDif.TryParseFrame(frame, out DvFrameInfo? info));
        Assert.True(info.IsPal);
        Assert.Equal(DvStreamClass.Dv25, info.StreamClass);
        Assert.Equal(144000, info.FrameSize);
        Assert.Equal(720, info.Width);
        Assert.Equal(576, info.Height);
        Assert.Equal(25, info.FrameRateNumerator);
        Assert.Equal((16, 9), (info.AspectWidth, info.AspectHeight));
        Assert.Equal(new DateTime(2019, 3, 4, 5, 6, 7), info.RecordedAt);
        Assert.Equal("01:02:03:04", info.Timecode);
        Assert.Equal(44100, info.AudioSampleRate);
        Assert.Equal(16, info.AudioBits);
        Assert.Equal(2, info.AudioChannels);
        Assert.Contains(info.Packs, p => p.Type == 0x62 && p.Source == DvPackSource.Vaux && p.Offset == (3 * 80) + 3 + 10);
    }

    [Fact]
    public void NtscAndFiftyMegabitFramesHaveTheirSizes()
    {
        Assert.True(DvDif.TryParseFrame(Frame(pal: false), out DvFrameInfo? ntsc));
        Assert.Equal(120000, ntsc.FrameSize);
        Assert.Equal(480, ntsc.Height);
        Assert.Equal(30000, ntsc.FrameRateNumerator);
        Assert.Equal(1001, ntsc.FrameRateDenominator);
        Assert.True(DvDif.TryParseFrame(Frame(pal: true, stype: 4), out DvFrameInfo? dv50));
        Assert.Equal(DvStreamClass.Dv50, dv50.StreamClass);
        Assert.Equal(288000, dv50.FrameSize);
    }

    [Fact]
    public void UnsetClocksAndInvalidDatesAreNotRecordingTimes()
    {
        Assert.True(DvDif.TryParseFrame(Frame(pal: true, recorded: new DateTime(1970, 1, 1)), out DvFrameInfo? info));
        Assert.Null(info.RecordedAt);
    }

    [Fact]
    public void NonDvDataIsNotAFrame()
    {
        Assert.False(DvDif.TryParseFrame(new byte[12000], out _));
        Assert.False(DvDif.TryParseFrame([0x1F, 0x07], out _));
        Assert.False(DvDif.LooksLikeFrameStart(new byte[100], out _));
    }

    [Fact]
    public void RawDvIsReadFrameByFrameAndSniffed()
    {
        byte[] data = [.. Frame(pal: true, recorded: new DateTime(2020, 5, 6, 7, 8, 9), fill: 1), .. Frame(pal: true, fill: 2), .. Frame(pal: true, fill: 3)];
        Assert.True(MediaFormats.Create().TrySniff(data.AsSpan(0, 4096), out FormatMatch match));
        Assert.Equal(MediaFormatId.Dv, match.Format.Id);
        Assert.Equal(SniffConfidence.Certain, match.Result.Confidence);
        using DvDemuxer d = DvDemuxer.Open(new MemoryMediaSource(data));
        Assert.Equal(new MediaTime(3, 25), d.Duration);
        MediaTrack t = Assert.Single(d.Tracks);
        Assert.Equal("dvvideo", t.Codec.Name);
        Assert.Equal((16, 15), (t.Video!.Value.PixelAspectNumerator, t.Video!.Value.PixelAspectDenominator));
        int n = 0;
        while (d.TryReadPacket(out PacketView p))
        {
            Assert.Equal(new MediaTime(n, 25), p.Info.Pts);
            Assert.Equal(144000, p.Data.Length);
            Assert.True(p.Info.IsKeyframe);
            Assert.Equal(n * 144000L, p.Info.SourceRange.Offset);
            n++;
        }

        Assert.Equal(3, n);
        Assert.Empty(d.Defects);
        MediaMetadata m = d.ReadMetadata();
        Assert.Equal(new DateTime(2020, 5, 6, 7, 8, 9), m.Facts.CaptureTime.Candidates[0].Value.Value);
        Assert.NotNull(m["dv", "Vaux:62"]);
    }

    [Fact]
    public void PullPathAllocatesNothingPerFrame()
    {
        byte[] one = Frame(pal: true);
        byte[] data = new byte[one.Length * 200];
        for (int i = 0; i < 200; i++)
        {
            one.CopyTo(data, i * one.Length);
        }

        long best = long.MaxValue;
        for (int pass = 0; pass < 5 && best != 0; pass++)
        {
            using DvDemuxer d = DvDemuxer.Open(new MemoryMediaSource(data));
            Assert.True(d.TryReadPacket(out _));
            long allocated = Drain(d, out int count);
            Assert.Equal(199, count);
            if (pass > 0)
            {
                best = Math.Min(best, allocated);
            }
        }

        Assert.Equal(0, best);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long Drain(DvDemuxer d, out int count)
    {
        count = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (d.TryReadPacket(out PacketView p))
        {
            count += p.Data.Length > 0 ? 1 : 0;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void TruncatedLastFrameIsDeliveredCorruptWithADefect()
    {
        byte[] data = [.. Frame(pal: false), .. Frame(pal: false)];
        using DvDemuxer d = DvDemuxer.Open(new MemoryMediaSource(data.AsMemory(0, 120000 + 50000)));
        Assert.True(d.TryReadPacket(out _));
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(50000, p.Data.Length);
        Assert.True((p.Info.Flags & PacketAttributes.Corrupt) != 0);
        Assert.False(d.TryReadPacket(out _));
        Assert.Contains(d.Defects, x => x.Code == "dv.truncated-frame" && x.Offset == 170000);
    }

    [Fact]
    public void ForwardOnlyAndSeekableReadingAgree()
    {
        byte[] data = [.. Frame(pal: true, fill: 1), .. Frame(pal: true, fill: 2)];
        using DvDemuxer a = DvDemuxer.Open(new MemoryMediaSource(data));
        using DvDemuxer b = DvDemuxer.Open(new StreamForwardSource(new MemoryStream(data)));
        for (int i = 0; i < 2; i++)
        {
            Assert.True(a.TryReadPacket(out PacketView pa));
            Assert.True(b.TryReadPacket(out PacketView pb));
            Assert.True(pa.Data.SequenceEqual(pb.Data));
            Assert.Equal(pa.Info.Pts, pb.Info.Pts);
        }
    }

    [Fact]
    public void SeekGoesToTheFrame()
    {
        byte[] data = [.. Frame(pal: false, fill: 1), .. Frame(pal: false, fill: 2), .. Frame(pal: false, fill: 3)];
        using DvDemuxer d = DvDemuxer.Open(new MemoryMediaSource(data));
        Assert.Equal(new MediaTime(1001, 30000), d.Seek(new MediaTime(1500, 30000)));
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(new MediaTime(1001, 30000), p.Info.Pts);
        Assert.Equal(new MediaTime(2002, 30000), d.Seek(new MediaTime(1500, 30000), SeekMode.NextKeyframe));
        Assert.Equal(MediaTime.Unknown, d.Seek(new MediaTime(100, 1), SeekMode.NextKeyframe));
        Assert.Equal(new MediaTime(2002, 30000), d.Seek(new MediaTime(100, 1)));
    }

    [Fact]
    public void DvType1AviHasOneVideoTrackOfWholeFramesAndTheRecordingDate()
    {
        byte[] f0 = Frame(pal: true, recorded: new DateTime(2018, 12, 31, 23, 59, 58), fill: 1);
        byte[] f1 = Frame(pal: true, fill: 2);
        byte[] strf = new byte[32];
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("iavs", "dvsd", 1, 25, 0, strf));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, f0));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, f1));
        byte[] file = b.Build();
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(file));
        MediaTrack t = Assert.Single(d.Tracks);
        Assert.Equal(TrackKind.Video, t.Kind);
        Assert.Equal("dvvideo", t.Codec.Name);
        Assert.Equal(576, t.Video!.Value.Height);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.True(p.Data.SequenceEqual(f0));
        MediaMetadata m = d.ReadMetadata();
        Assert.Equal(new DateTime(2018, 12, 31, 23, 59, 58), m.Facts.CaptureTime.Candidates[0].Value.Value);
    }

    [Fact]
    public void DvType2AviIsAnOrdinaryVideoTrackBesideItsAudio()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "dvsd", 1, 25, 0, AviBuilder.Bitmap(720, 576, "dvsd")));
        b.Streams.Add(new AviBuilder.StreamSpec("auds", "\0\0\0\0", 1, 48000, 4, AviBuilder.Wave(1, 2, 48000, 4, 16)));
        b.Chunks.Add(new AviBuilder.ChunkSpec(0, Frame(pal: true)));
        b.Chunks.Add(new AviBuilder.ChunkSpec(1, new byte[7680], true, "wb"));
        using AviDemuxer d = AviDemuxer.Open(new MemoryMediaSource(b.Build()));
        Assert.Equal("dvvideo", d.Tracks[0].Codec.Name);
        Assert.Equal(2, d.Tracks.Count);
    }

    [Fact]
    public void DvFuzzTargetSurvivesDamagedFrames()
    {
        byte[] data = Frame(pal: true, recorded: new DateTime(2020, 1, 2, 3, 4, 5));
        Random rnd = new(7);
        for (int i = 0; i < 300; i++)
        {
            byte[] copy = (byte[])data.Clone();
            for (int k = 0; k < 20; k++)
            {
                copy[rnd.Next(copy.Length)] = (byte)rnd.Next(256);
            }

            _ = DvDif.TryParseFrame(copy.AsSpan(0, rnd.Next(1, copy.Length)), out _);
            try
            {
                using DvDemuxer d = DvDemuxer.Open(new MemoryMediaSource(copy));
                while (d.TryReadPacket(out _))
                {
                }
            }
            catch (MediaException)
            {
            }
        }
    }
}
