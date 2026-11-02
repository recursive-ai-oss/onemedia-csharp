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

    private static string TempFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "recon-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ftyp, the whole media data box, then the first bytes of the movie box: a recording cut while the movie box at the end was being written.
    private static byte[] CutInsideMovieBoxAtTheEnd(byte[] file)
    {
        int pos = 0;
        int ftypEnd = 0, mdatStart = 0, mdatEnd = 0, moov = 0;
        while (pos + 8 <= file.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            switch (type)
            {
                case "ftyp": ftypEnd = pos + (int)size; break;
                case "mdat": mdatStart = pos; mdatEnd = pos + (int)size; break;
                case "moov": moov = pos; break;
            }

            pos += (int)size;
        }

        using MemoryStream ms = new();
        ms.Write(file, 0, ftypEnd);
        ms.Write(file, mdatStart, mdatEnd - mdatStart);
        ms.Write(file, moov, 40);
        return ms.ToArray();
    }

    [Fact]
    public void MediaRepairRebuildsACutOffRecordingWithAReferenceAndReportsPerTrack()
    {
        byte[] healthy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "avi", "avcc-odd-source.mp4"));
        string input = TempFile(Cut(healthy, 0.7));
        string reference = TempFile(healthy);
        string output = input + ".fixed.mp4";
        try
        {
            RecursiveAI.OneMedia.Repair.RepairReport r = Media.Repair(input, output, reference);
            Assert.True(r.Status is RecursiveAI.OneMedia.Repair.RepairStatus.Repaired or RecursiveAI.OneMedia.Repair.RepairStatus.PartiallyRepaired, r.Status.ToString());
            Assert.NotNull(r.Reconstruction);
            Assert.Contains(r.Items, i => i.Structure == "moov" && i.Action == RecursiveAI.OneMedia.Repair.RepairAction.Rebuilt);
            Assert.Equal(2, r.Tracks.Count);
            Assert.All(r.Tracks, t => Assert.True(t.Packets > 0 && t.Ranges.Count > 0 && t.Confidence >= RecursiveAI.OneMedia.Repair.RepairConfidence.Medium));
            List<Packet> original = Packets(healthy);
            List<Packet> rebuilt = Packets(File.ReadAllBytes(output));
            AssertPrefix(original, rebuilt, TrackKind.Video);
            AssertPrefix(original, rebuilt, TrackKind.Audio);
        }
        finally
        {
            File.Delete(input);
            File.Delete(reference);
            File.Delete(output);
        }
    }

    [Fact]
    public void MediaRepairRebuildsAFileWhoseMovieBoxAtTheEndIsCutOff()
    {
        byte[] healthy = File.ReadAllBytes(Fixture("mp3.mp4"));
        string input = TempFile(CutInsideMovieBoxAtTheEnd(healthy));
        string output = input + ".fixed.mp4";
        try
        {
            RecursiveAI.OneMedia.Repair.RepairReport r = Media.Repair(input, output);
            Assert.NotNull(r.Reconstruction);
            Assert.True(r.OutputWritten);
            Assert.Contains(r.Items, i => i.Action == RecursiveAI.OneMedia.Repair.RepairAction.Dropped && i.Range.Offset + i.Range.Length == new FileInfo(input).Length);
            List<Packet> rebuilt = Packets(File.ReadAllBytes(output));
            AssertPrefix(Packets(healthy), rebuilt, TrackKind.Audio);
            Assert.True(OfKind(rebuilt, TrackKind.Audio).Count > 0);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void MediaRepairOfAnUnframeableFileIsUnrepairableWithAReason()
    {
        byte[] healthy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "avi", "avcc-odd-source.mp4"));
        byte[] cut = Cut(healthy, 0.7);
        Array.Fill(cut, (byte)0x55, 40, cut.Length - 40); // nothing but noise after the file type box
        string input = TempFile(cut);
        string output = input + ".fixed.mp4";
        try
        {
            RecursiveAI.OneMedia.Repair.RepairReport r = Media.Repair(input, output);
            Assert.Equal(RecursiveAI.OneMedia.Repair.RepairStatus.Unrepairable, r.Status);
            Assert.False(File.Exists(output));
            Assert.NotEmpty(r.Warnings);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
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
    public void AVideoTrackWithoutASizeSaysTheParameterSetsAreMissingBeforeTheMuxerDetail()
    {
        MediaTrack noSize = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.H264, Timescale = 90000, Video = new VideoTrackInfo(0, 0) };
        MuxRefusedException refusal = new(MuxRefusalReason.InvalidTrack, "Track 0: a video track needs a width and height between 1 and 65535.", 0, CodecId.H264);
        ReconstructionException e = IsoReconstructor.RefusalToException(refusal, hasReference: false, noSize);
        Assert.StartsWith("The stream carries no video parameter sets (SPS/PPS)", e.Message, StringComparison.Ordinal);
        Assert.Contains("reference file from the same device is needed. Detail: Track 0: a video track needs", e.Message, StringComparison.Ordinal);

        // With a reference file the advice would be wrong, and other refusals keep their own text.
        Assert.StartsWith("The rebuilt tracks cannot be written", IsoReconstructor.RefusalToException(refusal, hasReference: true, noSize).Message, StringComparison.Ordinal);
        MuxRefusedException offset = new(MuxRefusalReason.InvalidTrack, "Track 0: a time offset or duration does not fit 32 bits.", 0, CodecId.H264);
        MediaTrack sized = noSize with { Video = new VideoTrackInfo(640, 360) };
        Assert.StartsWith("The rebuilt tracks cannot be written", IsoReconstructor.RefusalToException(offset, hasReference: false, sized).Message, StringComparison.Ordinal);
    }

    // A 64x64 AV1 sequence header OBU (reduced still-picture header, AV1 specification §5.5) followed by a shown key frame OBU: one temporal unit
    // that parses on its own. Bit by bit so the layout can be checked against the specification.
    private static byte[] FakeAv1TemporalUnit()
    {
        List<bool> bits = [];
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        Put(0, 3);      // seq_profile
        Put(0, 1);      // still_picture
        Put(1, 1);      // reduced_still_picture_header
        Put(0, 5);      // seq_level_idx[0]
        Put(7, 4);      // frame_width_bits_minus_1
        Put(7, 4);      // frame_height_bits_minus_1
        Put(63, 8);     // max_frame_width_minus_1
        Put(63, 8);     // max_frame_height_minus_1
        Put(0, 3);      // use_128x128_superblock, enable_filter_intra, enable_intra_edge_filter
        Put(0, 3);      // enable_superres, enable_cdef, enable_restoration
        Put(0, 1);      // high_bitdepth
        Put(0, 1);      // mono_chrome
        Put(0, 1);      // color_description_present_flag
        Put(0, 1);      // color_range
        Put(0, 2);      // chroma_sample_position
        Put(0, 1);      // separate_uv_delta_q
        Put(0, 1);      // film_grain_params_present
        Put(1, 1);      // trailing_one_bit
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        byte[] seq = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                seq[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        // obu_header: type 1 (sequence header), has_size_field; then type 6 (frame) whose first byte says show_existing_frame 0, KEY_FRAME, show_frame 1.
        return [0x0A, (byte)seq.Length, .. seq, 0x32, 4, 0x10, 0, 0, 0];
    }

    [Fact]
    public void H264DataThatHoldsOneStrayAv1UnitIsNotRebuiltAsAnAv1Track()
    {
        // A recording whose video parameter sets lived only in the lost movie box (x264 writes them nowhere else) and no reference file.
        // Somewhere in the video bytes is a run that parses as one AV1 temporal unit; the scan used to take it for the video track, and the
        // rebuild then wrote a bogus AV1 track (or died on its made-up frame size) instead of saying a reference file is needed.
        byte[] healthy = File.ReadAllBytes(Fixture("bframes_aac.mp4"));
        byte[] cut = Cut(healthy, 0.7);
        int mdat = cut.AsSpan().IndexOf("mdat"u8) + 4;
        FakeAv1TemporalUnit().CopyTo(cut, mdat + 48);

        // Nothing but a bogus AV1 track could have come out of this file; the right answer is the typed failure that asks for a reference file.
        ReconstructionException e = Assert.Throws<ReconstructionException>(() => Rebuild(cut, null));
        Assert.Equal(ReconstructionFailure.NoSamples, e.Failure);
        Assert.StartsWith("No video parameter sets were found", e.Message, StringComparison.Ordinal);
        Assert.Contains("reference file from the same device is needed", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(65536, 64)]
    [InlineData(64, 70000)]
    public void AVideoSizeTheSampleEntryCannotHoldSaysTheParameterSetsAreMissing(int width, int height)
    {
        // A size made up from stray bytes is as unusable as none, and the muxer refuses both with the same text.
        MediaTrack track = new() { Index = 0, Kind = TrackKind.Video, Codec = CodecId.Av1, Timescale = 90000, Video = new VideoTrackInfo(width, height) };
        MuxRefusedException refusal = new(MuxRefusalReason.InvalidTrack, "Track 0: a video track needs a width and height between 1 and 65535.", 0, CodecId.Av1);
        Assert.StartsWith("The stream carries no video parameter sets", IsoReconstructor.RefusalToException(refusal, hasReference: false, track).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnyMediaDataTheFailureIsTyped()
    {
        byte[] junk = new byte[4096];
        using MemoryStream output = new();
        ReconstructionException e = Assert.Throws<ReconstructionException>(() => IsoReconstructor.Reconstruct(new MemoryMediaSource(junk), output));
        Assert.NotEqual(default, e.Failure);
    }

    // Inputs the fuzzer found (parameter sets in the stream that made no usable codec configuration) and the seeds it started from:
    // each must end in a rebuilt file or a ReconstructionException, nothing else.
    [Fact]
    public void FuzzFindingsAndSeedsEndInAFileOrATypedFailure()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "reconstruct-iso");
        string[] files = Directory.GetFiles(dir);
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.EndsWith(".bin", StringComparison.Ordinal));
        foreach (string file in files)
        {
            try
            {
                Rebuild(File.ReadAllBytes(file), null);
            }
            catch (ReconstructionException)
            {
            }
        }
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
