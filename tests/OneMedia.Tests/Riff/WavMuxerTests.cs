using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.Remuxing;
using RecursiveAI.OneMedia.Riff;
using RecursiveAI.OneMedia.Tags;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>The WAVE/BWF/RF64 muxer: formats, representation changes, the 4 GiB switch, metadata chunks, the safe file writer and remux from AIFF.</summary>
public class WavMuxerTests
{
    private static MediaTrack Pcm(int channels, int rate, int bits, string? tag = null, CodecConfiguration config = default) => new()
    {
        Index = 0,
        Kind = TrackKind.Audio,
        Codec = CodecId.Pcm,
        CodecTag = tag,
        Configuration = config,
        Timescale = rate,
        Audio = new AudioTrackInfo(rate, channels, bits),
    };

    private static byte[] Ramp(int length, int seed = 0)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 7) + 3 + seed);
        }

        return data;
    }

    private static PacketInfo Packet(long pts, long frames, int rate, int size) =>
        new(0, new MediaTime(pts, rate), new MediaTime(pts, rate), new MediaTime(frames, rate), PacketAttributes.Keyframe, ByteRange.None, size);

    private static (byte[] File, MuxReport Report) Write(MediaTrack track, byte[] samples, int packetBytes, WavMuxOptions? options = null, WavTrackOptions? trackOptions = null)
    {
        using MemoryStream ms = new();
        using WavMuxer mux = new(ms, options);
        mux.AddTrack(track, trackOptions);
        int frame = track.Audio!.Value.Channels * Math.Max(1, (track.Audio.Value.BitsPerSample + 7) / 8);
        for (int at = 0; at < samples.Length; at += packetBytes)
        {
            int n = Math.Min(packetBytes, samples.Length - at);
            mux.WritePacket(Packet(at / frame, Math.Max(1, n / frame), (int)track.Timescale, n), samples.AsSpan(at, n));
        }

        MuxReport report = mux.Finish();
        return (ms.ToArray(), report);
    }

    private static byte[] ReadSamples(byte[] file, out WavDemuxer demuxer, out MediaTrack track)
    {
        demuxer = WavDemuxer.Open(new MemoryMediaSource(file));
        track = demuxer.Tracks[0];
        using MemoryStream all = new();
        while (demuxer.TryReadPacket(out PacketView v))
        {
            all.Write(v.Data);
        }

        return all.ToArray();
    }

    private static string Id(byte[] file, int at) => Encoding.ASCII.GetString(file, at, 4);

    [Fact]
    public void Pcm16StereoIsPlainRiffWithAnRf64Reservation()
    {
        byte[] samples = Ramp(4 * 1000);
        (byte[] file, MuxReport report) = Write(Pcm(2, 44100, 16), samples, 4096);
        Assert.Equal("RIFF", Id(file, 0));
        Assert.Equal((uint)(file.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal("JUNK", Id(file, 12));
        Assert.Equal("fmt ", Id(file, 48));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(52)));
        Assert.Equal(file.Length, report.BytesWritten);
        Assert.Equal(1000, report.Tracks[0].Duration.Ticks);
        byte[] back = ReadSamples(file, out WavDemuxer d, out MediaTrack t);
        using (d)
        {
            Assert.Equal(samples, back);
            Assert.Equal(44100, t.Audio!.Value.SampleRate);
            Assert.Equal(1000, d.Duration.Ticks);
            Assert.False(d.WaveFormat.IsExtensible);
            Assert.DoesNotContain(d.Defects, x => x.Severity >= DefectSeverity.Warning);
        }
    }

    [Fact]
    public void SixChannel24BitIsExtensibleWithTheDefaultMask()
    {
        byte[] samples = Ramp(18 * 500);
        (byte[] file, MuxReport report) = Write(Pcm(6, 48000, 24), samples, 18 * 100);
        byte[] back = ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(samples, back);
            Assert.True(d.WaveFormat.IsExtensible);
            Assert.Equal(0x3Fu, d.WaveFormat.ChannelMask);
            Assert.Equal(24, d.WaveFormat.ValidBitsPerSample);
            Assert.Equal(1, d.WaveFormat.EffectiveTag);
            Assert.Equal(PcmEncoding.SignedInteger, d.Pcm!.Value.Encoding);
        }

        Assert.Contains(report.Notes, n => n.Contains("channel mask", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitMaskAndValidBitsAreKept()
    {
        PcmSampleFormat p = new(PcmEncoding.SignedInteger, 32, 24, false);
        (byte[] file, _) = Write(Pcm(2, 48000, 32), Ramp(8 * 50), 80, trackOptions: new WavTrackOptions { Pcm = p, ChannelMask = 0xC });
        ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.True(d.WaveFormat.IsExtensible);
            Assert.Equal(0xCu, d.WaveFormat.ChannelMask);
            Assert.Equal(24, d.WaveFormat.ValidBitsPerSample);
            Assert.Equal(32, d.WaveFormat.BitsPerSample);
        }
    }

    [Fact]
    public void FloatGetsAFactChunkAndStaysPlainForStereo()
    {
        PcmSampleFormat f32 = new(PcmEncoding.FloatingPoint, 32, 32, false);
        byte[] samples = Ramp(8 * 300);
        (byte[] file, _) = Write(Pcm(2, 48000, 32), samples, 800, trackOptions: new WavTrackOptions { Pcm = f32 });
        byte[] back = ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(samples, back);
            Assert.Equal(3, d.WaveFormat.FormatTag);
            Assert.Equal(PcmEncoding.FloatingPoint, d.Pcm!.Value.Encoding);
            ChunkNode fact = d.Chunks.Single(c => c.Id == "fact");
            Assert.Equal(300u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)fact.Payload.Offset)));
        }
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, true)]
    public void BigEndianSamplesAreByteSwappedAcrossPacketBoundaries(int bits, bool isFloat)
    {
        int bytes = bits / 8;
        byte[] be = Ramp(bytes * 2 * 700);
        byte[] le = new byte[be.Length];
        for (int i = 0; i < be.Length; i += bytes)
        {
            for (int k = 0; k < bytes; k++)
            {
                le[i + k] = be[i + bytes - 1 - k];
            }
        }

        PcmSampleFormat p = new(isFloat ? PcmEncoding.FloatingPoint : PcmEncoding.SignedInteger, bits, bits, true);
        // 13-byte packets split samples in the middle (1 and 3 byte tails).
        (byte[] file, MuxReport report) = Write(Pcm(2, 8000, bits), be, 13 * 2 + 1, trackOptions: new WavTrackOptions { Pcm = p });
        byte[] back = ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(le, back);
            Assert.False(d.Pcm!.Value.BigEndian);
        }

        Assert.Contains(report.Notes, n => n.Contains("byte-swapped", StringComparison.Ordinal));
    }

    [Fact]
    public void SignedEightBitBecomesUnsigned()
    {
        byte[] signed = Ramp(2 * 400);
        (byte[] file, MuxReport report) = Write(Pcm(2, 8000, 8), signed, 300, trackOptions: new WavTrackOptions { Pcm = new PcmSampleFormat(PcmEncoding.SignedInteger, 8, 8, false) });
        byte[] back = ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(signed.Select(b => (byte)(b + 128)).ToArray(), back);
            Assert.Equal(PcmEncoding.UnsignedInteger, d.Pcm!.Value.Encoding);
        }

        Assert.Contains(report.Notes, n => n.Contains("unsigned", StringComparison.Ordinal));
    }

    [Fact]
    public void MatroskaAndQuickTimeTagsDescribeTheLayout()
    {
        Assert.Equal(new PcmSampleFormat(PcmEncoding.SignedInteger, 24, 24, true), WavMuxer.DescribePcm(Pcm(2, 48000, 24, "A_PCM/INT/BIG")));
        Assert.Equal(new PcmSampleFormat(PcmEncoding.FloatingPoint, 32, 32, false), WavMuxer.DescribePcm(Pcm(2, 48000, 32, "A_PCM/FLOAT/IEEE")));
        Assert.Equal(new PcmSampleFormat(PcmEncoding.UnsignedInteger, 8, 8, false), WavMuxer.DescribePcm(Pcm(1, 8000, 8, "A_PCM/INT/LIT")));
        Assert.Equal(new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, true), WavMuxer.DescribePcm(Pcm(1, 8000, 16, "twos")));
        Assert.Equal(new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, false), WavMuxer.DescribePcm(Pcm(1, 8000, 16, "sowt")));
        Assert.Equal(new PcmSampleFormat(PcmEncoding.FloatingPoint, 64, 64, true), WavMuxer.DescribePcm(Pcm(1, 8000, 64, "fl64")));
    }

    [Fact]
    public void ALawStaysG711()
    {
        byte[] samples = Ramp(2 * 200);
        (byte[] file, _) = Write(Pcm(2, 8000, 8), samples, 100, trackOptions: new WavTrackOptions { Pcm = new PcmSampleFormat(PcmEncoding.ALaw, 8, 8, false) });
        byte[] back = ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(samples, back);
            Assert.Equal(6, d.WaveFormat.FormatTag);
            Assert.Equal(PcmEncoding.ALaw, d.Pcm!.Value.Encoding);
        }
    }

    [Fact]
    public void AFormatRecordOfTheSourceIsKeptByteForByte()
    {
        // MS ADPCM-style tag 2 with a 32-byte extra block, which the muxer cannot describe from the track.
        byte[] fmt = new byte[18 + 32];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 22050);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 11155);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 32);
        MediaTrack t = new()
        {
            Index = 0,
            Kind = TrackKind.Audio,
            Codec = CodecId.AdpcmMs,
            Configuration = new CodecConfiguration(fmt, "fmt "),
            Timescale = 22050,
            Audio = new AudioTrackInfo(22050, 1, 4),
        };
        byte[] samples = Ramp(512 * 4);
        using MemoryStream ms = new();
        using (WavMuxer mux = new(ms))
        {
            mux.AddTrack(t);
            for (int i = 0; i < 4; i++)
            {
                mux.WritePacket(Packet(i * 500, 500, 22050, 512), samples.AsSpan(i * 512, 512));
            }

            mux.Finish();
        }

        byte[] file = ms.ToArray();
        byte[] back = ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(samples, back);
            ChunkNode node = d.Chunks.Single(c => c.Id == "fmt ");
            Assert.Equal(fmt, file.AsSpan((int)node.Payload.Offset, (int)node.PayloadSize).ToArray());
            ChunkNode fact = d.Chunks.Single(c => c.Id == "fact");
            Assert.Equal(2000u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)fact.Payload.Offset)));
        }
    }

    [Fact]
    public void RefusesWhatItCannotCarry()
    {
        using MemoryStream ms = new();
        using WavMuxer mux = new(ms);
        MediaTrack video = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.H264, Timescale = 90000 };
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(video)).Reason);
        MediaTrack aac = new() { Index = 0, Kind = TrackKind.Audio, Codec = CodecId.Aac, Timescale = 44100, Audio = new AudioTrackInfo(44100, 2) };
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(aac)).Reason);
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Pcm(2, 44100, 12, "x"), new WavTrackOptions { Pcm = new PcmSampleFormat(PcmEncoding.SignedInteger, 12, 12, false) })).Reason);
        mux.AddTrack(Pcm(2, 44100, 16));
        Assert.Equal(MuxRefusalReason.InvalidTrack, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Pcm(2, 44100, 16))).Reason);
        mux.WritePacket(Packet(100, 10, 44100, 40), Ramp(40));
        Assert.Equal(MuxRefusalReason.NonMonotonicDts, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Packet(50, 10, 44100, 40), Ramp(40))).Reason);
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Pcm(2, 44100, 16))).Reason);
        MuxReport r = mux.Finish();
        Assert.Contains(r.Notes, n => n.Contains("start offset", StringComparison.Ordinal));
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.Finish()).Reason);
    }

    [Fact]
    public void ADestinationThatCannotSeekIsRefused()
    {
        using NonSeekable s = new();
        Assert.Equal(MuxRefusalReason.OutputNotSuitable, Assert.Throws<MuxRefusedException>(() => new WavMuxer(s)).Reason);
    }

    [Fact]
    public void NoPacketsGiveAnEmptyValidFile()
    {
        using MemoryStream ms = new();
        using WavMuxer mux = new(ms);
        mux.AddTrack(Pcm(1, 8000, 16));
        mux.Finish();
        byte[] back = ReadSamples(ms.ToArray(), out WavDemuxer d, out _);
        using (d)
        {
            Assert.Empty(back);
        }
    }

    [Fact]
    public void Rf64FromTheStartPutsTheSizesInDs64()
    {
        byte[] samples = Ramp(4 * 321 + 0);
        (byte[] file, MuxReport report) = Write(Pcm(2, 44100, 16), samples, 1000, new WavMuxOptions { Layout = WavLayout.Rf64 });
        Assert.Equal("RF64", Id(file, 0));
        Assert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal("ds64", Id(file, 12));
        Assert.Equal((ulong)(file.Length - 8), BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(20)));
        Assert.Equal((ulong)samples.Length, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(28)));
        Assert.Equal(321UL, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(36)));
        byte[] back = ReadSamples(file, out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(samples, back);
            Assert.Equal(ChunkLayout.Rf64, d.ChunkFile.Layout);
        }

        Assert.Equal(file.Length, report.BytesWritten);
    }

    [Fact]
    public void Bw64UsesItsOwnFormId()
    {
        (byte[] file, _) = Write(Pcm(2, 44100, 16), Ramp(400), 100, new WavMuxOptions { Layout = WavLayout.Bw64 });
        Assert.Equal("BW64", Id(file, 0));
        ReadSamples(file, out WavDemuxer d, out _);
        d.Dispose();
    }

    [Fact]
    public void AFileThatGrowsPastFourGibIsPromotedToRf64InPlace()
    {
        using HeadStream s = new();
        using WavMuxer mux = new(s);
        mux.AddTrack(Pcm(2, 48000, 24));
        byte[] chunk = new byte[6 * 1_000_000];
        long frames = 0;
        for (int i = 0; i < 800; i++)
        {
            mux.WritePacket(Packet(frames, 1_000_000, 48000, chunk.Length), chunk);
            frames += 1_000_000;
        }

        MuxReport r = mux.Finish();
        Assert.True(r.BytesWritten > 4_800_000_000L);
        byte[] head = s.Head;
        Assert.Equal("RF64", Id(head, 0));
        Assert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)));
        Assert.Equal("ds64", Id(head, 12));
        Assert.Equal((ulong)(r.BytesWritten - 8), BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20)));
        Assert.Equal(4_800_000_000UL, BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(28)));
        Assert.Equal(800_000_000UL, BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(36)));
        // The data chunk header sits after fmt (40-byte extensible): 12 + 36 + 8 + 40.
        Assert.Equal("data", Id(head, 96));
        Assert.Equal(0xFFFF_FFFFu, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(100)));
        Assert.Equal(800_000_000L, r.Tracks[0].Duration.Ticks);
        Assert.Contains(r.Notes, n => n.Contains("RF64", StringComparison.Ordinal));
    }

    [Fact]
    public void RiffOnlyRefusesToPassFourGib()
    {
        using HeadStream s = new();
        using WavMuxer mux = new(s, new WavMuxOptions { Layout = WavLayout.RiffOnly });
        mux.AddTrack(Pcm(2, 48000, 16));
        byte[] chunk = new byte[4_000_000];
        MuxRefusedException e = Assert.Throws<MuxRefusedException>(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                mux.WritePacket(Packet(i * 1_000_000L, 1_000_000, 48000, chunk.Length), chunk);
            }
        });
        Assert.Equal(MuxRefusalReason.LimitExceeded, e.Reason);
        Assert.True(s.Length <= 0xFFFF_FFFFL + 8);
    }

    [Fact]
    public void BroadcastMetadataReadsBack()
    {
        BextChunk bext = new()
        {
            Description = "Take 4, scene 12",
            Originator = "OneMedia tests",
            OriginatorReference = "ref-0001",
            OriginationDate = new DateOnly(2026, 3, 4),
            OriginationTime = new TimeOnly(13, 5, 59),
            TimeReference = 0x1_0000_0002UL,
            LoudnessValue = -23.0,
            CodingHistory = "A=PCM,F=48000,W=16,M=stereo,T=OneMedia\r\n",
        };
        CartChunk cart = new() { Title = "Jingle", Artist = "Station", CutId = "0042", StartDate = "2026/03/04", LevelReference = 12000, TagText = "extra text" };
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><BWFXML><IXML_VERSION>1.5</IXML_VERSION><PROJECT>Pilot</PROJECT><SCENE>12</SCENE></BWFXML>";
        WavMuxOptions options = new()
        {
            Bext = bext,
            Cart = cart,
            IXml = xml,
            Info = [new("INAM", "Pilot ünïcode"), new("IART", "Someone")],
            Chunks = [new WavChunk("zzzz", new byte[] { 1, 2, 3 }), new WavChunk("tail", new byte[] { 9 }, AfterData: true)],
            PaddingBytes = 256,
        };
        byte[] samples = Ramp(4 * 900);
        (byte[] file, _) = Write(Pcm(2, 48000, 16), samples, 2048, options);
        using MediaFile f = Media.Open(file);
        Assert.Equal("Take 4, scene 12", f.Native[TagSystems.Bext, "Description"]!.Text);
        Assert.Equal("OneMedia tests", f.Native[TagSystems.Bext, "Originator"]!.Text);
        Assert.Equal("2026-03-04", f.Native[TagSystems.Bext, "OriginationDate"]!.Text);
        Assert.Equal("13:05:59", f.Native[TagSystems.Bext, "OriginationTime"]!.Text);
        Assert.Equal(0x1_0000_0002L, f.Native[TagSystems.Bext, "TimeReference"]!.Number);
        Assert.Equal(-23.0, f.Native[TagSystems.Bext, "LoudnessValue"]!.RealValue);
        Assert.Equal("Jingle", f.Native[TagSystems.Cart, "Title"]!.Text);
        Assert.Equal("0042", f.Native[TagSystems.Cart, "CutID"]!.Text);
        Assert.Equal(12000, f.Native[TagSystems.Cart, "LevelReference"]!.Number);
        Assert.Equal("Pilot", f.Native[TagSystems.IXml, "PROJECT"]!.Text);
        Assert.Equal("Pilot ünïcode", f.Native[TagSystems.RiffInfo, "INAM"]!.Text);
        Assert.Equal("Someone", f.Native[TagSystems.RiffInfo, "IART"]!.Text);
        WavDemuxer d = Assert.IsType<WavDemuxer>(f.Demuxer);
        Assert.Contains(d.Chunks, c => c.Id == "zzzz");
        Assert.Equal("tail", d.Chunks[^1].Id);
        Assert.Equal("JUNK", d.Chunks.First(c => c.Id == "JUNK" && c.PayloadSize == 248).Id);
        Assert.DoesNotContain(f.Defects, x => x.Severity >= DefectSeverity.Warning);
    }

    [Fact]
    public void BadOptionsAreRefusedBeforeAnythingIsWritten()
    {
        using MemoryStream ms = new();
        Assert.Throws<ArgumentException>(() => new WavMuxer(ms, new WavMuxOptions { IXml = "<a><b></a>" }));
        Assert.Throws<ArgumentException>(() => new WavMuxer(ms, new WavMuxOptions { Chunks = [new WavChunk("data", new byte[1])] }));
        Assert.Throws<ArgumentException>(() => new WavMuxer(ms, new WavMuxOptions { Chunks = [new WavChunk("bad", new byte[1])] }));
        Assert.Throws<ArgumentException>(() => new WavMuxer(ms, new WavMuxOptions { PaddingBytes = 7 }));
        Assert.Throws<ArgumentException>(() => new WavMuxer(ms, new WavMuxOptions { Bext = new BextChunk { Description = new string('x', 257) } }));
        Assert.Throws<ArgumentException>(() => new WavMuxer(ms, new WavMuxOptions { Bext = new BextChunk { Originator = "Zoë" } }));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void CreateFileAppearsOnlyWhenFinished()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onemedia-wav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "out.wav");
            using (WavMuxer mux = WavMuxer.CreateFile(target))
            {
                mux.AddTrack(Pcm(2, 8000, 16));
                mux.WritePacket(Packet(0, 100, 8000, 400), Ramp(400));
                Assert.False(File.Exists(target));
                Assert.Single(Directory.GetFiles(dir));
            }

            Assert.Empty(Directory.GetFiles(dir)); // disposed unfinished: the temporary file is gone and the target never appeared

            File.WriteAllText(target, "precious");
            Assert.Throws<IOException>(() => WavMuxer.CreateFile(target));
            using (WavMuxer mux = WavMuxer.CreateFile(target, overwrite: true))
            {
                mux.AddTrack(Pcm(2, 8000, 16));
                mux.WritePacket(Packet(0, 100, 8000, 400), Ramp(400));
                Assert.Equal("precious", File.ReadAllText(target));
                mux.Finish();
            }

            Assert.Single(Directory.GetFiles(dir));
            using MediaFile f = Media.Open(target);
            Assert.Equal(8000, f.Tracks[0].Audio!.Value.SampleRate);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A hand-built AIFF: big-endian 16-bit stereo with a NAME chunk.
    private static byte[] Aiff(byte[] sound, int rate)
    {
        using MemoryStream ms = new();
        void U32(uint v) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void U16(ushort v) { byte[] b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); ms.Write(b); }
        ms.Write("FORM"u8);
        U32(0);
        ms.Write("AIFF"u8);
        ms.Write("COMM"u8);
        U32(18);
        U16(2);
        U32((uint)(sound.Length / 4));
        U16(16);
        // 80-bit IEEE extended: exponent 0x400B..., mantissa for 8000 Hz.
        ushort exp = (ushort)(16383 + System.Numerics.BitOperations.Log2((uint)rate));
        ulong mant = (ulong)rate << (63 - System.Numerics.BitOperations.Log2((uint)rate));
        U16(exp);
        U32((uint)(mant >> 32));
        U32((uint)mant);
        ms.Write("NAME"u8);
        U32(4);
        ms.Write("Song"u8);
        ms.Write("SSND"u8);
        U32((uint)(sound.Length + 8));
        U32(0);
        U32(0);
        ms.Write(sound);
        byte[] all = ms.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(all.AsSpan(4), (uint)(all.Length - 8));
        return all;
    }

    [Fact]
    public void RemuxFromAiffSwapsBytesAndReportsIt()
    {
        byte[] be = Ramp(4 * 2000);
        byte[] le = new byte[be.Length];
        for (int i = 0; i < be.Length; i += 2)
        {
            le[i] = be[i + 1];
            le[i + 1] = be[i];
        }

        using MediaFile input = Media.Open(Aiff(be, 8000));
        using MemoryStream ms = new();
        RemuxReport report = Remuxer.Remux(input, ms, RemuxFormat.Wav);
        Assert.Contains(report.Mux.Notes, n => n.Contains("byte-swapped", StringComparison.Ordinal));
        byte[] back = ReadSamples(ms.ToArray(), out WavDemuxer d, out _);
        using (d)
        {
            Assert.Equal(le, back);
            Assert.Equal(2, d.WaveFormat.Channels);
            Assert.Equal(8000, d.WaveFormat.SampleRate);
        }
    }

    [Fact]
    public void PathRemuxFromWavCarriesChunksAndIsAtomic()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onemedia-wavremux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string source = Path.Combine(dir, "in.wav");
            byte[] samples = Ramp(4 * 1500);
            (byte[] file, _) = Write(Pcm(2, 48000, 16), samples, 4096, new WavMuxOptions
            {
                Bext = new BextChunk { Description = "kept", Originator = "me" },
                IXml = "<BWFXML><PROJECT>P</PROJECT></BWFXML>",
                Info = [new("INAM", "Name")],
                Chunks = [new WavChunk("cue ", new byte[4])],
            });
            File.WriteAllBytes(source, file);
            string output = Path.Combine(dir, "out.wav");
            RemuxReport report = Remuxer.Remux(source, output);
            Assert.DoesNotContain(report.Losses, l => l.Kind is LossKind.Tag);
            Assert.Equal(["in.wav", "out.wav"], Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToArray());
            using MediaFile f = Media.Open(output);
            Assert.Equal("kept", f.Native[TagSystems.Bext, "Description"]!.Text);
            Assert.Equal("P", f.Native[TagSystems.IXml, "PROJECT"]!.Text);
            Assert.Equal("Name", f.Native[TagSystems.RiffInfo, "INAM"]!.Text);
            Assert.Equal(samples, ReadSamples(File.ReadAllBytes(output), out WavDemuxer d, out _));
            d.Dispose();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A stream that cannot seek.
    private sealed class NonSeekable : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    // A seekable stream that keeps only its first bytes, so a file of several gigabytes can be written in a test.
    private sealed class HeadStream : Stream
    {
        private readonly byte[] _head = new byte[4096];
        private long _position;
        private long _length;

        public byte[] Head => _head;

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _position; set => _position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => _length + offset };
            return _position;
        }

        public override void SetLength(long value) => _length = value;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_position < _head.Length)
            {
                int n = (int)Math.Min(buffer.Length, _head.Length - _position);
                buffer[..n].CopyTo(_head.AsSpan((int)_position));
            }

            _position += buffer.Length;
            _length = Math.Max(_length, _position);
        }
    }
}
