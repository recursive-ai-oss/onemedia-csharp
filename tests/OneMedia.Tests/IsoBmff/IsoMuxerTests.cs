using RecursiveAI.OneMedia.Codecs.Pcm;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Tests;

public class IsoMuxerTests
{
    private const int VideoFrames = 36;
    private const int AudioPackets = 60;
    private static readonly byte[] AvcC = [1, 0x64, 0, 0x1F, 0xFF, 0xE0, 0];
    private static readonly byte[] Asc = [0x12, 0x10]; // AAC-LC, 44.1 kHz, stereo

    private static MediaTrack Video(long id = 1) => new()
    {
        Index = 0,
        Id = id,
        Kind = TrackKind.Video,
        Codec = CodecId.H264,
        CodecTag = "avc1",
        Configuration = new CodecConfiguration(AvcC, "avcC"),
        Timescale = 12800,
        Video = new VideoTrackInfo(320, 180, RotationDegrees: 90),
        Language = "und",
    };

    private static MediaTrack Audio(long id = 2) => new()
    {
        Index = 1,
        Id = id,
        Kind = TrackKind.Audio,
        Codec = CodecId.Aac,
        Configuration = new CodecConfiguration(Asc, "asc"),
        Timescale = 44100,
        Audio = new AudioTrackInfo(44100, 2),
        Language = "eng",
        Edit = new TrackEditInfo(Priming: new MediaTime(1024, 44100)),
    };

    private sealed record Sent(int Track, long Dts, long Pts, long Duration, bool Key, byte[] Data);

    // Video: groups of three decoded I P B and shown I B P, starting at presentation time zero: composition offsets 0, 512 and -512 (a B picture
    // is shown before its decode time, which needs the signed form of the composition offset table).
    private static List<Sent> Packets()
    {
        List<Sent> list = [];
        for (int i = 0; i < VideoFrames; i++)
        {
            long dts = i * 512L;
            long offset = (i % 3) switch { 0 => 0, 1 => 512, _ => -512 };
            list.Add(new Sent(0, dts, dts + offset, 512, i % 12 == 0, Payload(0, i, 200 + (i * 13 % 90))));
            // Audio is interleaved on its own clock: about 1.7 audio packets per video frame.
            for (int a = list.Count(s => s.Track == 1); a < (i + 1) * AudioPackets / VideoFrames; a++)
            {
                list.Add(new Sent(1, a * 1024L, a * 1024L, 1024, true, Payload(1, a, 90 + (a % 7))));
            }
        }

        return list;
    }

    private static byte[] Payload(int track, int index, int size)
    {
        byte[] b = new byte[size];
        for (int i = 0; i < size; i++)
        {
            b[i] = (byte)((track * 77) + (index * 31) + i);
        }

        return b;
    }

    private static byte[] Mux(IsoMuxOptions options, bool seekable = true, List<Sent>? packets = null, Action<IsoMuxer>? declare = null)
    {
        using MemoryStream ms = new();
        Stream dest = seekable ? ms : new NonSeekableStream(ms);
        using (IsoMuxer mux = new(dest, options))
        {
            if (declare is null)
            {
                mux.AddTrack(Video());
                mux.AddTrack(Audio());
            }
            else
            {
                declare(mux);
            }

            foreach (Sent s in packets ?? Packets())
            {
                PacketAttributes flags = s.Key ? PacketAttributes.Keyframe : PacketAttributes.None;
                MediaTrack t = mux.Tracks[s.Track];
                mux.WritePacket(new PacketInfo(s.Track, new MediaTime(s.Pts, t.Timescale), new MediaTime(s.Dts, t.Timescale), new MediaTime(s.Duration, t.Timescale), flags, ByteRange.None, s.Data.Length), s.Data);
            }

            MuxReport report = mux.Finish();
            Assert.Equal(ms.Length, report.BytesWritten);
        }

        return ms.ToArray();
    }

    private static void AssertSame(byte[] file, List<Sent> sent, bool videoShift = false)
    {
        using MemoryMediaSource src = new(file);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        Assert.DoesNotContain(d.Defects, x => x.Severity >= DefectSeverity.Warning);
        int[] seen = new int[2];
        List<Sent> video = sent.Where(s => s.Track == 0).ToList();
        List<Sent> audio = sent.Where(s => s.Track == 1).ToList();
        while (d.TryReadPacket(out PacketView v))
        {
            List<Sent> list = v.Info.TrackIndex == 0 ? video : audio;
            Sent expected = list[seen[v.Info.TrackIndex]++];
            MediaTrack t = d.Tracks[v.Info.TrackIndex];
            Assert.Equal(expected.Dts, v.Info.Dts.Rescale(t.Timescale, MediaRounding.Floor).Ticks);
            Assert.Equal(expected.Pts, v.Info.Pts.Rescale(t.Timescale, MediaRounding.Floor).Ticks);
            Assert.Equal(expected.Duration, v.Info.Duration.Rescale(t.Timescale, MediaRounding.Floor).Ticks);
            Assert.Equal(expected.Key, v.Info.IsKeyframe);
            Assert.True(expected.Data.AsSpan().SequenceEqual(v.Data));
        }

        Assert.Equal(video.Count, seen[0]);
        Assert.Equal(audio.Count, seen[1]);
        Assert.Equal(2, d.Tracks.Count);
        Assert.Equal(CodecId.H264, d.Tracks[0].Codec);
        Assert.Equal(90, d.Tracks[0].Video!.Value.RotationDegrees);
        Assert.Equal(320, d.Tracks[0].Video!.Value.Width);
        Assert.Equal(CodecId.Aac, d.Tracks[1].Codec);
        Assert.Equal("eng", d.Tracks[1].Language);
        Assert.Equal(new MediaTime(1024, 44100), d.Tracks[1].Edit.Priming);
        _ = videoShift;
    }

    [Theory]
    [InlineData(IsoLayout.MoovFirst, true)]
    [InlineData(IsoLayout.MoovFirst, false)]
    [InlineData(IsoLayout.MoovLast, true)]
    [InlineData(IsoLayout.MoovLast, false)]
    [InlineData(IsoLayout.Fragmented, true)]
    [InlineData(IsoLayout.Fragmented, false)]
    public void PacketsComeBackEqual(IsoLayout layout, bool seekable)
    {
        List<Sent> sent = Packets();
        AssertSame(Mux(new IsoMuxOptions { Layout = layout, FragmentDuration = new MediaTime(1, 2) }, seekable, sent), sent);
    }

    [Theory]
    [InlineData(IsoFileKind.Mov)]
    [InlineData(IsoFileKind.ThreeGp)]
    [InlineData(IsoFileKind.M4a)]
    [InlineData(IsoFileKind.M4v)]
    public void FlavoursReadBack(IsoFileKind kind)
    {
        List<Sent> sent = Packets();
        AssertSame(Mux(new IsoMuxOptions { Kind = kind }, packets: sent), sent);
    }

    [Fact]
    public void SpoolMovesToTheFactoryStreamPastTheBudget()
    {
        // Three packets of 200 KB are more than the single 256 KB block the budget allows.
        byte[] Run(IsoMuxOptions options)
        {
            using MemoryStream ms = new();
            using (IsoMuxer mux = new(new NonSeekableStream(ms), options))
            {
                mux.AddTrack(Audio());
                for (int i = 0; i < 3; i++)
                {
                    mux.WritePacket(Audio1(i * 1024L), Payload(1, i, 200_000));
                }

                mux.Finish();
            }

            return ms.ToArray();
        }

        int made = 0;
        byte[] spilled = Run(new IsoMuxOptions { SpoolMemoryBudget = 1, SpoolFactory = () => { made++; return new MemoryStream(); } });
        Assert.Equal(1, made);
        Assert.Equal(Run(new IsoMuxOptions()), spilled);
        using MemoryMediaSource src = new(spilled);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        int n = 0;
        while (d.TryReadPacket(out PacketView v))
        {
            Assert.True(Payload(1, n++, 200_000).AsSpan().SequenceEqual(v.Data));
        }

        Assert.Equal(3, n);
    }

    [Fact]
    public void FragmentsStartAtKeyframesAndOptionalBoxesAreWritten()
    {
        byte[] file = Mux(new IsoMuxOptions { Layout = IsoLayout.Fragmented, FragmentDuration = new MediaTime(1, 2), WriteSegmentIndex = true, WriteRandomAccess = true });
        List<string> top = TopLevel(file);
        // 36 video frames of 512/12800 s = 1.44 s with a keyframe every 12 frames (0.48 s): the keyframe at 0.48 s is short of the half
        // second, the one at 0.96 s ends the first fragment, so there are two.
        Assert.Equal(["ftyp", "moov", "sidx", "moof", "mdat", "moof", "mdat", "mfra"], top);
        AssertSame(file, Packets());
    }

    [Fact]
    public void SegmentIndexPointsAtTheFragments()
    {
        byte[] file = Mux(new IsoMuxOptions { Layout = IsoLayout.Fragmented, FragmentDuration = new MediaTime(1, 2), WriteSegmentIndex = true });
        using MemoryMediaSource src = new(file);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        Assert.True(d.Movie.IsFragmented);
        // sidx offsets: first reference starts right after the index box, each size lands on the next moof.
        int at = 0;
        int sidxAt = -1;
        while (at + 8 <= file.Length)
        {
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            if (System.Text.Encoding.ASCII.GetString(file, at + 4, 4) == "sidx")
            {
                sidxAt = at;
                break;
            }

            at += (int)size;
        }

        Assert.True(sidxAt > 0);
        int sidxSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(sidxAt));
        int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(sidxAt + 38));
        Assert.Equal(2, count);
        int pos = sidxAt + sidxSize;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal("moof", System.Text.Encoding.ASCII.GetString(file, pos + 4, 4));
            pos += (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(sidxAt + 40 + (i * 12))) & 0x7FFFFFFF);
        }
    }

    [Fact]
    public void MovieFirstPutsTheMovieBoxBeforeTheMediaData()
    {
        Assert.Equal(["ftyp", "moov", "mdat"], TopLevel(Mux(new IsoMuxOptions { Layout = IsoLayout.MoovFirst })));
        Assert.Equal(["ftyp", "free", "mdat", "moov"], TopLevel(Mux(new IsoMuxOptions { Layout = IsoLayout.MoovLast })));
    }

    [Fact]
    public void OutputIsDeterministic() =>
        Assert.Equal(Mux(new IsoMuxOptions()), Mux(new IsoMuxOptions()));

    [Fact]
    public void PcmFlavoursKeepTheirSampleLayout()
    {
        foreach ((IsoFileKind kind, string tag, int bits, bool big) in new[] { (IsoFileKind.Mov, "sowt", 16, false), (IsoFileKind.Mov, "twos", 16, true), (IsoFileKind.Mov, "in24", 24, true), (IsoFileKind.Mp4, "ipcm", 16, false), (IsoFileKind.Mp4, "ipcm", 24, true) })
        {
            MediaTrack pcm = new()
            {
                Index = 0,
                Kind = TrackKind.Audio,
                Codec = CodecId.Pcm,
                Timescale = 48000,
                Audio = new AudioTrackInfo(48000, 2, bits),
            };
            IsoTrackOptions options = new() { Pcm = new PcmSampleFormat(PcmEncoding.SignedInteger, bits, bits, big) };
            using MemoryStream ms = new();
            using (IsoMuxer mux = new(ms, new IsoMuxOptions { Kind = kind }))
            {
                mux.AddTrack(pcm, options);
                byte[] data = new byte[1024 * 2 * (bits / 8)];
                for (int i = 0; i < 4; i++)
                {
                    mux.WritePacket(new PacketInfo(0, new MediaTime(i * 1024L, 48000), MediaTime.Unknown, new MediaTime(1024, 48000), PacketAttributes.Keyframe, ByteRange.None, data.Length), data);
                }

                mux.Finish();
            }

            using MemoryMediaSource src = new(ms.ToArray());
            using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
            Assert.Equal(CodecId.Pcm, d.Tracks[0].Codec);
            Assert.Equal(tag, d.Tracks[0].CodecTag);
            Assert.Equal(bits, d.Tracks[0].Audio!.Value.BitsPerSample);
            PcmSampleFormat back = IsoSampleEntriesPcm(d.Tracks[0]);
            Assert.Equal(big, back.BigEndian);
            int n = 0;
            while (d.TryReadPacket(out PacketView v))
            {
                Assert.Equal(1024 * 2 * (bits / 8), v.Data.Length);
                Assert.Equal(1024, v.Info.Duration.Ticks);
                n++;
            }

            Assert.Equal(4, n);
        }
    }

    private static PcmSampleFormat IsoSampleEntriesPcm(MediaTrack t)
    {
        // The sample entry code (and for ipcm the configuration box) says the byte order.
        return t.CodecTag switch
        {
            "sowt" => new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, false),
            "twos" or "in24" => new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, true),
            _ => t.Configuration.Data.Span[4] == 0 ? new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, true) : new PcmSampleFormat(PcmEncoding.SignedInteger, 16, 16, false),
        };
    }

    [Fact]
    public void EditListsKeepPrimingStartOffsetsAndEndPadding()
    {
        MediaTrack audio = Audio() with
        {
            Edit = new TrackEditInfo(Priming: new MediaTime(2112, 44100), EndPadding: new MediaTime(500, 44100)),
        };
        using MemoryStream ms = new();
        using (IsoMuxer mux = new(ms))
        {
            mux.AddTrack(audio);
            for (int i = 0; i < 20; i++)
            {
                mux.WritePacket(new PacketInfo(0, new MediaTime(i * 1024L, 44100), MediaTime.Unknown, new MediaTime(1024, 44100), PacketAttributes.Keyframe, ByteRange.None, 10), new byte[10]);
            }

            mux.Finish();
        }

        using MemoryMediaSource src = new(ms.ToArray());
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        Assert.Equal(new MediaTime(2112, 44100), d.Tracks[0].Edit.Priming);
        Assert.Equal(new MediaTime(500, 44100), d.Tracks[0].Edit.EndPadding);
    }

    [Fact]
    public void ADecodeTimeThatDoesNotStartAtZeroBecomesALeadingEmptyEdit()
    {
        MediaTrack audio = Audio() with { Edit = default };
        using MemoryStream ms = new();
        using (IsoMuxer mux = new(ms))
        {
            mux.AddTrack(audio);
            for (int i = 0; i < 5; i++)
            {
                mux.WritePacket(new PacketInfo(0, new MediaTime(44100 + (i * 1024L), 44100), MediaTime.Unknown, new MediaTime(1024, 44100), PacketAttributes.Keyframe, ByteRange.None, 10), new byte[10]);
            }

            mux.Finish();
        }

        using MemoryMediaSource src = new(ms.ToArray());
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        Assert.True(d.TryReadPacket(out PacketView first));
        Assert.Equal(0, first.Info.Pts.Ticks);
        // The track is shifted to zero and a one-second empty edit puts it back on the file's timeline.
        MediaTrack t = d.Tracks[0];
        Assert.Equal(new MediaTime(1, 1), t.Edit.StartTime);
        Assert.Equal(new MediaTime(44100, 44100), t.PresentationTime(first.Info.Pts));
    }

    [Fact]
    public void RefusesNonMonotonicDecodeTimes()
    {
        using MemoryStream ms = new();
        using IsoMuxer mux = new(ms);
        mux.AddTrack(Audio());
        mux.WritePacket(Audio1(2048), new byte[4]);
        MuxRefusedException e = Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Audio1(1024), new byte[4]));
        Assert.Equal(MuxRefusalReason.NonMonotonicDts, e.Reason);
        Assert.Equal(0, e.TrackIndex);
        // The refused packet changed nothing: the muxer carries on.
        mux.WritePacket(Audio1(3072), new byte[4]);
        Assert.Equal(2, mux.Finish().Tracks[0].Packets);
    }

    private static PacketInfo Audio1(long dts) => new(0, new MediaTime(dts, 44100), new MediaTime(dts, 44100), new MediaTime(1024, 44100), PacketAttributes.Keyframe, ByteRange.None, 4);

    [Fact]
    public void RefusesCodecsTheFormatCannotCarry()
    {
        using IsoMuxer mux = new(new MemoryStream());
        MediaTrack vorbis = Audio() with { Codec = CodecId.Vorbis, CodecTag = null, Configuration = new CodecConfiguration(new byte[] { 1, 2, 3 }, "CodecPrivate") };
        MuxRefusedException e = Assert.Throws<MuxRefusedException>(() => mux.AddTrack(vorbis));
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, e.Reason);
        Assert.Equal(CodecId.Vorbis, e.Codec);
        MediaTrack unknown = Audio() with { Codec = CodecId.Unknown, CodecTag = null };
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(unknown)).Reason);
        MediaTrack encrypted = Audio() with { IsProtected = true };
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(encrypted)).Reason);
        MediaTrack subtitle = new() { Index = 0, Kind = TrackKind.Subtitle, Codec = CodecId.SubRip, CodecTag = "S_TEXT/UTF8", Timescale = 1000 };
        Assert.Equal(MuxRefusalReason.UnsupportedCodec, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(subtitle)).Reason);
        Assert.Empty(mux.Tracks);
    }

    [Fact]
    public void RefusesMissingOrBrokenConfiguration()
    {
        using IsoMuxer mux = new(new MemoryStream());
        Assert.Equal(MuxRefusalReason.MissingConfiguration, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Video() with { Configuration = default })).Reason);
        Assert.Equal(MuxRefusalReason.MissingConfiguration, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Audio() with { Configuration = default })).Reason);
        Assert.Equal(MuxRefusalReason.InvalidConfiguration, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Audio() with { Configuration = new CodecConfiguration(new byte[] { 0 }, "asc") })).Reason);
        // A configuration laid out as another container's record is not guessed at.
        Assert.Equal(MuxRefusalReason.InvalidConfiguration, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Video() with { Configuration = new CodecConfiguration(AvcC, "annexb") })).Reason);
        // E-AC-3 and AC-3 take their record from the first frame: a track that never gets one is refused at Finish.
        mux.AddTrack(Audio() with { Codec = CodecId.Ac3, CodecTag = null, Configuration = default });
        Assert.Equal(MuxRefusalReason.MissingConfiguration, Assert.Throws<MuxRefusedException>(() => mux.Finish()).Reason);
    }

    [Fact]
    public void RefusesBadTracksAndBadCalls()
    {
        using IsoMuxer mux = new(new MemoryStream());
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Audio1(0), new byte[1])).Reason);
        Assert.Equal(MuxRefusalReason.InvalidTrack, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Audio() with { Timescale = 0 })).Reason);
        Assert.Equal(MuxRefusalReason.InvalidTrack, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Audio() with { Audio = null })).Reason);
        Assert.Equal(MuxRefusalReason.InvalidTrack, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Video() with { Video = null })).Reason);
        mux.AddTrack(Audio());
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Audio1(0) with { TrackIndex = 3 }, new byte[1])).Reason);
        Assert.Equal(MuxRefusalReason.MissingTimestamp, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Audio1(0) with { Pts = MediaTime.Unknown, Dts = MediaTime.Unknown }, new byte[1])).Reason);
        mux.WritePacket(Audio1(0), new byte[1]);
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Audio())).Reason);
        mux.Finish();
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Audio1(2048), new byte[1])).Reason);
        Assert.Equal(MuxRefusalReason.InvalidState, Assert.Throws<MuxRefusedException>(() => mux.Finish()).Reason);
    }

    [Fact]
    public void LimitsApply()
    {
        MediaLimits limits = MediaLimits.Default with { MaxSampleTableEntries = 3, MaxTracks = 1, MaxPacketBytes = 8 };
        using IsoMuxer mux = new(new MemoryStream(), new IsoMuxOptions { Limits = limits });
        mux.AddTrack(Audio());
        Assert.Equal(MuxRefusalReason.LimitExceeded, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Audio(5))).Reason);
        Assert.Equal(MuxRefusalReason.LimitExceeded, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Audio1(0), new byte[9])).Reason);
        for (int i = 0; i < 3; i++)
        {
            mux.WritePacket(Audio1(i * 1024L), new byte[4]);
        }

        Assert.Equal(MuxRefusalReason.LimitExceeded, Assert.Throws<MuxRefusedException>(() => mux.WritePacket(Audio1(4096), new byte[4])).Reason);
    }

    [Fact]
    public void CmafHoldsOneTrack()
    {
        using IsoMuxer mux = new(new MemoryStream(), new IsoMuxOptions { Layout = IsoLayout.Fragmented, Cmaf = true });
        mux.AddTrack(Video());
        Assert.Equal(MuxRefusalReason.InvalidTrack, Assert.Throws<MuxRefusedException>(() => mux.AddTrack(Audio())).Reason);
        Assert.Throws<ArgumentException>(() => new IsoMuxer(new MemoryStream(), new IsoMuxOptions { Cmaf = true }));
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        using IsoMuxer mux = new(new MemoryStream());
        mux.AddTrack(Audio());
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => mux.WritePacket(Audio1(0), new byte[1], cts.Token));
        Assert.Throws<OperationCanceledException>(() => mux.Finish(cts.Token));
    }

    [Fact]
    public void TimesInOtherTimescalesAreRescaledAndTheRoundingIsReported()
    {
        using MemoryStream ms = new();
        using IsoMuxer mux = new(ms);
        mux.AddTrack(Audio() with { Edit = default });
        // Nanosecond clock, as Matroska delivers: 1024 samples at 44100 Hz is 23219954.648... ns.
        for (int i = 0; i < 4; i++)
        {
            long ns = (long)Math.Round(i * 1024 * 1e9 / 44100);
            mux.WritePacket(new PacketInfo(0, new MediaTime(ns, 1_000_000_000), MediaTime.Unknown, MediaTime.Unknown, PacketAttributes.Keyframe, ByteRange.None, 2), new byte[2]);
        }

        MuxReport report = mux.Finish();
        Assert.Contains(report.Notes, n => n.Contains("rounded", StringComparison.Ordinal));
        using MemoryMediaSource src = new(ms.ToArray());
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        long expected = 0;
        while (d.TryReadPacket(out PacketView v))
        {
            Assert.Equal(expected, v.Info.Dts.Ticks);
            expected += 1024;
        }
    }

    [Fact]
    public void SteadyStateWritesAllocateNothing()
    {
        byte[] payload = new byte[96];
        foreach (IsoLayout layout in new[] { IsoLayout.MoovFirst, IsoLayout.MoovLast, IsoLayout.Fragmented })
        {
            // The smallest of three runs: a per-packet allocation shows in all of them, while a one-off (a runtime type load or a
            // tiered-compilation step landing on this thread) shows in one.
            _ = Run(layout, payload, 30_000);
            long allocated = Math.Min(Run(layout, payload, 30_000), Math.Min(Run(layout, payload, 30_000), Run(layout, payload, 30_000)));
            Assert.True(allocated == 0, $"{layout}: {allocated} bytes");
        }
    }

    // Writes N packets of one video track and one audio track into a stream that discards, and returns the bytes the thread allocated
    // between the first packet and the last.
    private static long Run(IsoLayout layout, byte[] payload, int count)
    {
        using IsoMuxer mux = new(Stream.Null, new IsoMuxOptions { Layout = layout, SpoolMemoryBudget = 1L << 30, FragmentDuration = new MediaTime(1, 1) });
        mux.AddTrack(Video());
        mux.AddTrack(Audio());
        long before = 0;
        PacketInfo video = new(0, default, default, new MediaTime(512, 12800), PacketAttributes.Keyframe, ByteRange.None, payload.Length);
        PacketInfo audio = new(1, default, default, new MediaTime(1024, 44100), PacketAttributes.Keyframe, ByteRange.None, payload.Length);
        // The first packets of each track allocate (first-packet capture, buffers), and so does the first fragment (its box buffer
        // grows to the fragment's size once); measure after a few seconds of media.
        mux.WritePacket(video with { Pts = new MediaTime(0, 12800), Dts = new MediaTime(0, 12800) }, payload);
        mux.WritePacket(audio with { Pts = new MediaTime(0, 44100), Dts = new MediaTime(0, 44100) }, payload);
        const int warm = 200;
        for (int i = 1; i < count; i++)
        {
            if (i == warm)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
            }

            MediaTime vt = new(i * 512L, 12800);
            mux.WritePacket(video with { Pts = vt, Dts = vt, Flags = i % 25 == 0 ? PacketAttributes.Keyframe : PacketAttributes.None }, payload);
            MediaTime at = new(i * 1024L, 44100);
            mux.WritePacket(audio with { Pts = at, Dts = at }, payload);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        mux.Finish();
        return allocated;
    }

    private static List<string> TopLevel(byte[] file)
    {
        List<string> names = [];
        int at = 0;
        while (at + 8 <= file.Length)
        {
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            names.Add(System.Text.Encoding.ASCII.GetString(file, at + 4, 4));
            if (size == 1)
            {
                size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(at + 8));
            }

            at += (int)size;
        }

        Assert.Equal(file.Length, at);
        return names;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
