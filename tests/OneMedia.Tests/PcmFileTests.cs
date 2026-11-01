using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMedia.Aiff;
using RecursiveAI.OneMedia.Caf;
using RecursiveAI.OneMedia.Metadata;
using RecursiveAI.OneMedia.Riff;

namespace RecursiveAI.OneMedia.Tests;

/// <summary>Hand-built WAVE, AIFF and CAF files: packetization, defects with offsets, forward-only reads, ranged reads, tags and the zero-allocation pull path.</summary>
public class PcmFileTests
{
    private static byte[] Bytes(params object[] parts)
    {
        using MemoryStream ms = new();
        foreach (object p in parts)
        {
            switch (p)
            {
                case string s: ms.Write(Encoding.Latin1.GetBytes(s)); break;
                case byte[] b: ms.Write(b); break;
                case uint u: { byte[] t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, u); ms.Write(t); break; }
                case ushort w: { byte[] t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, w); ms.Write(t); break; }
                default: throw new InvalidOperationException();
            }
        }

        return ms.ToArray();
    }

    private static byte[] Chunk(string id, byte[] payload) => Bytes(id, (uint)payload.Length, payload, payload.Length % 2 == 1 ? new byte[1] : []);

    private static byte[] Fmt(ushort tag, ushort channels, uint rate, ushort bits, byte[]? extra = null)
    {
        ushort align = (ushort)(channels * bits / 8);
        return Chunk("fmt ", Bytes(tag, channels, rate, rate * align, align, bits, extra ?? []));
    }

    private static byte[] Ramp(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 7 + 3);
        }

        return data;
    }

    private static byte[] Riff(string form, params byte[][] chunks)
    {
        byte[] body = Bytes(form, Bytes(chunks.Cast<object>().ToArray()));
        return Bytes("RIFF", (uint)body.Length, body);
    }

    private static byte[] Wav(int frames, uint rate = 8000, ushort channels = 2, params byte[][] more) =>
        Riff("WAVE", [Fmt(1, channels, rate, 16), .. more, Chunk("data", Ramp(frames * channels * 2))]);

    private static List<(long Pts, long Dur, int Size)> Drain(IDemuxer d, out byte[] all)
    {
        List<(long, long, int)> list = [];
        using MemoryStream ms = new();
        while (d.TryReadPacket(out PacketView p))
        {
            list.Add((p.Info.Pts.Ticks, p.Info.Duration.Ticks, p.Data.Length));
            ms.Write(p.Data);
        }

        all = ms.ToArray();
        return list;
    }

    [Fact]
    public void WavPcm16CutsPacketsLikeTheReferenceTool()
    {
        // 8000 Hz: the largest power of two not above 8000 x 4096 / 44100 (742) is 512 frames; 1200 frames = 512 + 512 + 176.
        byte[] file = Wav(1200);
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(file));
        Assert.Single(d.Tracks);
        Assert.Equal(CodecId.Pcm, d.Tracks[0].Codec);
        Assert.Equal(new MediaTime(1200, 8000), d.Duration);
        var packets = Drain(d, out byte[] all);
        Assert.Equal([(0L, 512L, 2048), (512L, 512L, 2048), (1024L, 176L, 704)], packets);
        Assert.Equal(Ramp(1200 * 4), all);
        Assert.Empty(d.Defects);
    }

    [Fact]
    public void WavExtensibleKeepsChannelMaskAndSubFormat()
    {
        byte[] extra = Bytes((ushort)22, (ushort)16, 0x3Fu, new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        byte[] fmt = Chunk("fmt ", Bytes((ushort)0xFFFE, (ushort)6, 48000u, 48000u * 12, (ushort)12, (ushort)16, extra));
        byte[] file = Riff("WAVE", fmt, Chunk("data", Ramp(12 * 100)));
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(6, d.Tracks[0].Audio!.Value.Channels);
        Assert.Equal(CodecId.Pcm, d.Tracks[0].Codec);
        Assert.Equal(0x3Fu, d.WaveFormat.ChannelMask);
        Assert.Equal(100, d.Duration.Rescale(48000, MediaRounding.Floor).Ticks);
    }

    [Fact]
    public void Rf64TakesSizesFromDs64()
    {
        byte[] audio = Ramp(4 * 300);
        byte[] ds64 = Chunk("ds64", Bytes(0u, 0u, (uint)audio.Length, 0u, 300u, 0u, 0u));
        byte[] body = Bytes("WAVE", ds64, Fmt(1, 2, 8000, 16), Bytes("data", 0xFFFFFFFFu, audio));
        byte[] file = Bytes("RF64", 0xFFFFFFFFu, body);
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(file));
        Assert.Equal(ChunkLayout.Rf64, d.ChunkFile.Layout);
        Assert.Equal(audio.Length, d.DataLength);
        Drain(d, out byte[] all);
        Assert.Equal(audio, all);
    }

    [Fact]
    public void TruncatedDataReportsTheOffsetWhereItEnds()
    {
        byte[] file = Wav(1000);
        byte[] cut = file[..(file.Length - 1001)];
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(cut));
        var packets = Drain(d, out byte[] all);
        Assert.Contains(d.Defects, x => x.Kind == DefectKind.SizeExceedsFile && x.Offset == 36);
        Assert.Equal(all.Length, packets.Sum(p => p.Size));
    }

    [Fact]
    public void ForwardOnlyReadsGiveTheSamePackets()
    {
        byte[] file = Wav(3000, 8000, 2, Chunk("LIST", Bytes("INFO", Chunk("INAM", Encoding.ASCII.GetBytes("Title\0")))));
        using WavDemuxer seekable = WavDemuxer.Open(new MemoryMediaSource(file));
        using WavDemuxer forward = WavDemuxer.Open(new StreamForwardSource(new MemoryStream(file)));
        var a = Drain(seekable, out byte[] da);
        var b = Drain(forward, out byte[] db);
        Assert.Equal(a, b);
        Assert.Equal(da, db);
        Assert.False(forward.CanSeek);
        Assert.Equal("Title", forward.ReadMetadata().Tags.Title);
    }

    [Fact]
    public void SeekLandsOnAPacketBoundary()
    {
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(Wav(4096)));
        MediaTime landed = d.Seek(new MediaTime(1100, 8000));
        Assert.Equal(new MediaTime(1024, 8000), landed);
        Assert.True(d.TryReadPacket(out PacketView p));
        Assert.Equal(1024, p.Info.Pts.Ticks);
    }

    [Fact]
    public void BwfAndInfoTagsAreRead()
    {
        byte[] bext = new byte[602];
        Encoding.ASCII.GetBytes("A description").CopyTo(bext, 0);
        Encoding.ASCII.GetBytes("2021-03-04").CopyTo(bext, 320);
        Encoding.ASCII.GetBytes("05:06:07").CopyTo(bext, 330);
        BinaryPrimitives.WriteUInt16LittleEndian(bext.AsSpan(346), 1);
        byte[] info = Chunk("LIST", Bytes("INFO", Chunk("INAM", Encoding.ASCII.GetBytes("Song\0")), Chunk("IART", Encoding.ASCII.GetBytes("Band\0"))));
        byte[] file = Wav(100, 8000, 2, Chunk("bext", bext), info);
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(file));
        MediaMetadata m = d.ReadMetadata();
        Assert.Equal("Song", m.Tags.Title);
        Assert.Equal("Band", m.Tags.Artist);
        Assert.Contains(m.NativeTags, t => t.Key == "Description" && t.Text == "A description");
        Assert.NotEmpty(m.Facts.CaptureTime.Candidates);
        Assert.DoesNotContain(d.Defects, x => x.Severity == DefectSeverity.Error);
    }

    [Fact]
    public async Task RangedOpenMatchesSyncAndReadsInBlocks()
    {
        byte[] file = Wav(200_000, 44100, 2, Chunk("LIST", Bytes("INFO", Chunk("INAM", Encoding.ASCII.GetBytes("Late\0")))));
        // The LIST chunk sits after 800 KB of sample data: fetching it must not read the data in between.
        int requests = 0;
        long bytesRead = 0;
        RangedMediaSource ranged = new(file.Length, (off, mem, ct) =>
        {
            requests++;
            int n = (int)Math.Min(mem.Length, file.Length - off);
            file.AsMemory((int)off, n).CopyTo(mem);
            bytesRead += n;
            return new ValueTask<int>(n);
        });
        MediaFormatRegistry registry = MediaFormats.Create();
        await using IAsyncDemuxer d = await ((IMediaFormat)registry.Formats.First(f => f.Id == MediaFormatId.Wav)).OpenAsync(ranged, MediaOpenOptions.Default);
        int openRequests = requests;
        MediaMetadata meta = await ((IAsyncMetadataProvider)d).ReadMetadataAsync();
        Assert.Equal("Late", meta.Tags.Title);
        Assert.True(bytesRead < 100_000, $"{bytesRead} bytes for open and tags");
        Assert.True(requests <= openRequests + 2);

        using WavDemuxer sync = WavDemuxer.Open(new MemoryMediaSource(file));
        var expected = Drain(sync, out byte[] all);
        List<(long, long, int)> got = [];
        using MemoryStream ms = new();
        while (await d.ReadPacketAsync())
        {
            got.Add((d.Current.Info.Pts.Ticks, d.Current.Info.Duration.Ticks, d.Current.Data.Length));
            ms.Write(d.Current.Data);
        }

        Assert.Equal(expected, got);
        Assert.Equal(all, ms.ToArray());
        Assert.Empty(d.Defects);
    }

    // AIFF and CAF ---------------------------------------------------------------------------------------------

    private static byte[] Be32(uint v) { byte[] t = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(t, v); return t; }

    private static byte[] Be16(ushort v) { byte[] t = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(t, v); return t; }

    private static byte[] IffChunk(string id, byte[] payload) => Bytes(id, Be32((uint)payload.Length), payload, payload.Length % 2 == 1 ? new byte[1] : []);

    // 44100 Hz as an IEEE 80-bit extended number: exponent 0x400E, mantissa 44100 << 48.
    private static readonly byte[] Rate44100 = [0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0];

    private static byte[] Aiff(int frames, string? compression = null)
    {
        byte[] comm = Bytes(Be16(2), Be32((uint)frames), Be16(16), Rate44100, compression is null ? [] : Bytes(compression, new byte[] { 0 }, new byte[1]));
        byte[] ssnd = Bytes(Be32(0), Be32(0), Ramp(frames * 4));
        byte[] body = Bytes(compression is null ? "AIFF" : "AIFC", IffChunk("COMM", comm), IffChunk("NAME", Encoding.ASCII.GetBytes("Aiff title")), IffChunk("SSND", ssnd));
        return Bytes(compression is null ? "FORM" : "FORM", Be32((uint)body.Length), body);
    }

    [Fact]
    public void AiffReadsExtendedRateAndText()
    {
        using AiffDemuxer d = AiffDemuxer.Open(new MemoryMediaSource(Aiff(5000)));
        Assert.Equal(44100, d.Tracks[0].Timescale);
        Assert.Equal(new MediaTime(5000, 44100), d.Duration);
        var packets = Drain(d, out byte[] all);
        Assert.Equal(1024, packets[0].Dur); // 4096 bytes of 4-byte frames
        Assert.Equal(5000 * 4, all.Length);
        Assert.Equal("Aiff title", d.ReadMetadata().Tags.Title);
        Assert.Empty(d.Defects);
    }

    private static byte[] CafChunk(string id, byte[] payload) { byte[] size = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(size, payload.Length); return Bytes(id, size, payload); }

    private static byte[] CafDesc(string format, uint flags, uint bytesPerPacket, uint framesPerPacket, uint channels, uint bits)
    {
        byte[] rate = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(rate, 44100.0);
        return CafChunk("desc", Bytes(rate, format, Be32(flags), Be32(bytesPerPacket), Be32(framesPerPacket), Be32(channels), Be32(bits)));
    }

    [Fact]
    public void CafPcmWithUnknownDataSizeRunsToTheEndOfFile()
    {
        byte[] desc = CafDesc("lpcm", 2, 4, 1, 2, 16);
        byte[] dataHeader = Bytes("data", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, Be32(0));
        byte[] file = Bytes("caff", Be16(1), Be16(0), desc, dataHeader, Ramp(4 * 3000));
        using CafDemuxer d = CafDemuxer.Open(new MemoryMediaSource(file));
        Drain(d, out byte[] all);
        Assert.Equal(4 * 3000, all.Length);
    }

    [Fact]
    public void CafPacketTableGivesEachPacketItsSize()
    {
        // Variable-size packets of 1024 frames: the table lists the byte size of each packet as a varint.
        byte[] pakt = Bytes(new byte[8].Select((_, i) => (byte)(i == 7 ? 3 : 0)).ToArray(), new byte[8].Select((_, i) => (byte)(i == 7 ? 3 * 1024 : 0)).ToArray(), Be32(0), Be32(0), new byte[] { 10, 20, 30 });
        byte[] desc = CafDesc("alac", 0, 0, 1024, 2, 0);
        byte[] file = Bytes("caff", Be16(1), Be16(0), desc, CafChunk("pakt", pakt), CafChunk("data", Bytes(Be32(0), Ramp(60))));
        using CafDemuxer d = CafDemuxer.Open(new MemoryMediaSource(file));
        var packets = Drain(d, out _);
        Assert.Equal([(0L, 1024L, 10), (1024L, 1024L, 20), (2048L, 1024L, 30)], packets);
        Assert.Equal(CodecId.Alac, d.Tracks[0].Codec);
    }

    [Fact]
    public async Task AiffAndCafOpenOverRangedSources()
    {
        byte[] aiff = Aiff(20000);
        byte[] caf = Bytes("caff", Be16(1), Be16(0), CafDesc("lpcm", 2, 4, 1, 2, 16), CafChunk("data", Bytes(Be32(0), Ramp(4 * 20000))));
        foreach (byte[] file in new[] { aiff, caf })
        {
            RangedMediaSource ranged = new(file.Length, (off, mem, ct) =>
            {
                int n = (int)Math.Min(mem.Length, file.Length - off);
                file.AsMemory((int)off, n).CopyTo(mem);
                return new ValueTask<int>(n);
            });
            await using MediaFile media = await Media.OpenAsync(file.Length, (off, mem, ct) =>
            {
                int n = (int)Math.Min(mem.Length, file.Length - off);
                file.AsMemory((int)off, n).CopyTo(mem);
                return new ValueTask<int>(n);
            });
            Assert.Equal(new MediaTime(20000, 44100), media.ContainerDuration);
            await ranged.DisposeAsync();
        }
    }

    // Allocations ----------------------------------------------------------------------------------------------

    [Fact]
    public void PullPathAllocatesNothingPerPacket()
    {
        byte[] file = Wav(500_000, 44100, 2);
        using (WavDemuxer warm = WavDemuxer.Open(new MemoryMediaSource(file)))
        {
            DrainCount(warm, out _);
        }

        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(file));
        Assert.True(d.TryReadPacket(out _));
        long allocated = DrainCount(d, out long sum);
        Assert.NotEqual(0, sum);
        Assert.Equal(0, allocated);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long DrainCount(WavDemuxer d, out long sum)
    {
        sum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        while (d.TryReadPacket(out PacketView v))
        {
            sum += v.Data[0] + v.Info.Pts.Ticks;
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void IXmlWithAMisleadingEncodingDeclarationIsRejectedAsXmlNotCrashed()
    {
        // Found by fuzzing: the XML reader threw ArgumentOutOfRangeException while switching encodings; the chunk is now decoded before parsing.
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "riff-wav", "ixml-encoding-switch.bin"));
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(input.AsMemory(1)));
        _ = d.ReadMetadata();
    }

    [Fact]
    public void EveryCraftedFixtureOpensOrIsRejectedWithATypedError()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "pcm");
        MediaFormatRegistry registry = MediaFormats.Create();
        int opened = 0;
        foreach (string file in Directory.GetFiles(dir))
        {
            try
            {
                using IDemuxer d = registry.Open(new MemoryMediaSource(File.ReadAllBytes(file)));
                while (d.TryReadPacket(out _))
                {
                }

                _ = ((IMetadataProvider)d).ReadMetadata();
                opened++;
            }
            catch (MediaException)
            {
            }
        }

        Assert.True(opened >= 24, $"{opened} fixtures opened");
    }

    [Fact]
    public void SeekFarBeyondTheEndDoesNotOverflow()
    {
        // Found by fuzzing: a header with a huge rate made the seek arithmetic wrap around.
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "riff-wav", "seek-overflow.bin"));
        using WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(input.AsMemory(1)));
        if (d.CanSeek)
        {
            d.Seek(new MediaTime(60_000, 1000));
        }

        Assert.False(d.TryReadPacket(out _));
    }

    [Fact]
    public async Task FramedStreamsGiveTheSamePacketsOverRangedSources()
    {
        // Found by fuzzing: resynchronising over junk in MPEG-audio-in-WAVE restarted wrongly when a ranged read stopped the step half-way.
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "riff-wav", "framed-ranged-retry.bin"))[1..];
        long plain = 0;
        using (WavDemuxer d = WavDemuxer.Open(new MemoryMediaSource(input)))
        {
            while (d.TryReadPacket(out PacketView p))
            {
                plain += p.Data.Length;
            }
        }

        long ranged = 0;
        await using IAsyncDemuxer a = await new WavFileFormat().OpenAsync(new MemoryMediaSource(input), MediaOpenOptions.Default);
        while (await a.ReadPacketAsync())
        {
            ranged += a.Current.Data.Length;
        }

        Assert.Equal(plain, ranged);
    }
}
