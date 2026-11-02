using System.Buffers.Binary;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Repair.Reconstruction;

namespace RecursiveAI.OneMedia.Tests.Repair;

// Cut-off recordings: a healthy fixture loses its tail (and with it the movie box), the media data is walked, and the rebuilt file's
// packets must be the original's first packets, byte for byte.
public sealed class ReconstructionTests
{
    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "testdata", "isobmff", .. parts]);

    private sealed record Packet(TrackKind Kind, byte[] Data, bool Key);

    private static List<Packet> Packets(byte[] file)
    {
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(new MemoryMediaSource(file));
        Dictionary<int, TrackKind> kinds = d.Tracks.ToDictionary(t => t.Index, t => t.Kind);
        List<Packet> all = [];
        while (d.TryReadPacket(out PacketView v))
        {
            all.Add(new Packet(kinds[v.Info.TrackIndex], v.Data.ToArray(), v.Info.IsKeyframe));
        }

        return all;
    }

    private static List<Packet> OfKind(List<Packet> p, TrackKind kind) => p.Where(x => x.Kind == kind).ToList();

    // Keeps the file type box, then a media data box whose size is zero ("to the end of the file", as a recorder leaves it), then
    // the first `fraction` of the media data plus a few bytes, so the cut rarely lands on a sample boundary.
    private static byte[] Cut(byte[] file, double fraction, int jitter = 7)
    {
        int pos = 0;
        int ftypEnd = 0;
        while (pos + 8 <= file.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(pos + 8));
                header = 16;
            }

            if (type == "ftyp")
            {
                ftypEnd = pos + (int)size;
            }

            if (type == "mdat")
            {
                int payload = pos + header;
                int payloadLength = (int)(size - header);
                int keep = (int)(payloadLength * fraction) + jitter;
                using MemoryStream ms = new();
                ms.Write(file, 0, ftypEnd);
                byte[] mdatHeader = new byte[8];
                System.Text.Encoding.ASCII.GetBytes("mdat", mdatHeader.AsSpan(4));
                ms.Write(mdatHeader);
                ms.Write(file, payload, keep);
                return ms.ToArray();
            }

            pos += (int)size;
        }

        throw new InvalidOperationException("fixture has no mdat");
    }

    private static (ReconstructionReport Report, byte[] Output) Rebuild(byte[] cut, byte[]? reference, ReconstructionOptions? options = null)
    {
        using MemoryStream output = new();
        ReconstructionReport r = IsoReconstructor.Reconstruct(new MemoryMediaSource(cut), output, reference is null ? null : new MemoryMediaSource(reference), options);
        return (r, output.ToArray());
    }

    // The rebuilt packets of one kind must be a prefix of the original's, and nothing may be invented.
    private static void AssertPrefix(List<Packet> original, List<Packet> rebuilt, TrackKind kind, bool keysToo = true)
    {
        List<Packet> o = OfKind(original, kind);
        List<Packet> r = OfKind(rebuilt, kind);
        Assert.NotEmpty(r);
        Assert.True(r.Count <= o.Count, $"{kind}: {r.Count} rebuilt, {o.Count} original");
        for (int i = 0; i < r.Count; i++)
        {
            Assert.True(o[i].Data.AsSpan().SequenceEqual(r[i].Data), $"{kind} packet {i} differs");
            if (keysToo)
            {
                Assert.Equal(o[i].Key, r[i].Key);
            }
        }
    }

    // AMR-NB in a 3GP file, written by our own muxer: mode 7 (12.2 kbit/s, 31 bytes + table of contents byte) with a few mode 0 frames mixed in
    // (RFC 4867 §5.3: F=0, FT, Q=1, two padding bits zero). Payload bytes are pseudo-random.
    private static byte[] AmrFile(int frames)
    {
        Random rnd = new(99);
        using MemoryStream ms = new();
        MediaTrack t = new()
        {
            Index = 0,
            Kind = TrackKind.Audio,
            Codec = CodecId.AmrNb,
            CodecTag = "samr",
            Configuration = new CodecConfiguration(new byte[] { 0x46, 0x46, 0x4D, 0x50, 0, 0x81, 0xFF, 0, 1 }, "damr"),
            Timescale = 8000,
            Audio = new AudioTrackInfo(8000, 1),
        };
        using (IsoMuxer mux = new(ms, new IsoMuxOptions { Kind = IsoFileKind.ThreeGp, Layout = IsoLayout.MoovLast }))
        {
            mux.AddTrack(t);
            for (int i = 0; i < frames; i++)
            {
                int ft = i % 5 == 4 ? 0 : 7;
                int size = ft == 7 ? 32 : 13;
                byte[] f = new byte[size];
                rnd.NextBytes(f);
                f[0] = (byte)((ft << 3) | 4);
                mux.WritePacket(new PacketInfo(0, new MediaTime(i * 160L, 8000), new MediaTime(i * 160L, 8000), new MediaTime(160, 8000), PacketAttributes.Keyframe, ByteRange.None, size), f);
            }

            mux.Finish();
        }

        return ms.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmrFramesAreFoundWithAndWithoutAReference(bool withReference)
    {
        byte[] healthy = AmrFile(300);
        List<Packet> original = Packets(healthy);
        byte[] cut = Cut(healthy, 0.55);
        (ReconstructionReport report, byte[] output) = Rebuild(cut, withReference ? healthy : null);
        List<Packet> rebuilt = Packets(output);
        AssertPrefix(original, rebuilt, TrackKind.Audio);
        Assert.InRange(OfKind(rebuilt, TrackKind.Audio).Count, 140, 170);
        Assert.Equal(withReference ? ReconstructionSetupSource.Reference : ReconstructionSetupSource.Guessed, report.Tracks[0].Setup);
        Assert.Equal(CodecId.AmrNb, report.Tracks[0].Codec);
        Assert.Contains(report.SkippedRanges, s => s.Reason == SkippedReason.PartialSample);
    }

    // A recorder that interleaves a small descriptor track (16-byte samples nobody can frame) with the sound: the sound is still found.
    [Fact]
    public void SamplesOfAnUnknownSmallTrackBetweenFramesDoNotHideTheFrames()
    {
        byte[] healthy = AmrFile(300);
        List<Packet> original = Packets(healthy);
        using MemoryStream mdat = new();
        byte[] junk = new byte[16];
        junk[0] = 0x47; // a table-of-contents byte with non-zero padding bits: not an AMR frame
        junk[1] = 0x50;
        foreach (Packet p in original)
        {
            mdat.Write(junk);
            mdat.Write(p.Data);
        }

        using MemoryStream file = new();
        file.Write(healthy, 0, 28); // the file type box of the fixture builder
        byte[] header = new byte[8];
        System.Text.Encoding.ASCII.GetBytes("mdat", header.AsSpan(4));
        file.Write(header);
        file.Write(mdat.ToArray(), 0, (int)(mdat.Length * 6 / 10));

        (ReconstructionReport report, byte[] output) = Rebuild(file.ToArray(), healthy);
        List<Packet> rebuilt = Packets(output);
        AssertPrefix(original, rebuilt, TrackKind.Audio);
        Assert.InRange(OfKind(rebuilt, TrackKind.Audio).Count, 150, 190);
        Assert.True(report.UsedBytes > 0);
    }

    // The H.264 fixture has five frames and an MP3 track: video comes from the reference's avcC, the MP3 frames from their own headers.
    [Fact]
    public void VideoAndMp3ComeBackFromTheMediaDataWithAReference()
    {
        byte[] healthy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "avi", "avcc-odd-source.mp4"));
        List<Packet> original = Packets(healthy);
        byte[] cut = Cut(healthy, 0.8);
        (ReconstructionReport report, byte[] output) = Rebuild(cut, healthy);
        List<Packet> rebuilt = Packets(output);
        AssertPrefix(original, rebuilt, TrackKind.Video);
        AssertPrefix(original, rebuilt, TrackKind.Audio);
        Assert.True(report.RecoveredFraction > 0.5, $"recovered {report.RecoveredFraction}");
    }

    [Fact]
    public void AnMp3FileIsFoundFromItsFrameHeadersAlone()
    {
        byte[] healthy = File.ReadAllBytes(Fixture("mp3.mp4"));
        (ReconstructionReport report, byte[] output) = Rebuild(Cut(healthy, 0.5), healthy);
        AssertPrefix(Packets(healthy), Packets(output), TrackKind.Audio);
        Assert.True(report.RecoveredFraction > 0.4);
    }

    [Fact]
    public void ThePartialLastSampleIsDroppedAndReported()
    {
        byte[] healthy = AmrFile(200);
        byte[] cut = Cut(healthy, 0.5, jitter: 3);
        (ReconstructionReport report, _) = Rebuild(cut, healthy);
        Assert.Contains(report.SkippedRanges, s => s.Reason == SkippedReason.PartialSample || s.Reason == SkippedReason.AfterMediaData || s.Reason == SkippedReason.Unframed);
        Assert.True(report.UsedBytes < cut.Length);
    }

    [Fact]
    public void AnAudioOnlyFileNeedsNoReferenceWhenItsFramesAreSelfDelimiting()
    {
        byte[] healthy = File.ReadAllBytes(Fixture("mp3.mp4"));
        byte[] cut = Cut(healthy, 0.6);
        (ReconstructionReport report, byte[] output) = Rebuild(cut, null);
        AssertPrefix(Packets(healthy), Packets(output), TrackKind.Audio);
        Assert.All(report.Tracks, t => Assert.Equal(ReconstructionSetupSource.Guessed, t.Setup));
    }

    [Fact]
    public void WithoutAnyMediaDataTheFailureIsTyped()
    {
        byte[] junk = new byte[4096];
        using MemoryStream output = new();
        ReconstructionException e = Assert.Throws<ReconstructionException>(() => IsoReconstructor.Reconstruct(new MemoryMediaSource(junk), output));
        Assert.NotEqual(default, e.Failure);
    }

    [Fact]
    public void RandomBytesAfterAFileTypeBoxNeverThrowAnythingUntyped()
    {
        Random rnd = new(1234);
        byte[] healthy = AmrFile(200);
        for (int i = 0; i < 20; i++)
        {
            byte[] cut = Cut(healthy, 0.8);
            int at = rnd.Next(40, cut.Length);
            for (int k = 0; k < 16 && at + k < cut.Length; k++)
            {
                cut[at + k] = (byte)rnd.Next(256);
            }

            try
            {
                Rebuild(cut, healthy);
            }
            catch (ReconstructionException)
            {
            }
        }
    }
}
