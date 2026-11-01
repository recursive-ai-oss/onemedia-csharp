using RecursiveAI.OneMedia.Editing;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Tests.Editing;

public class IsoCutterTests
{
    private const int GopLength = 10;
    private const int Gops = 6;
    private const int Frames = GopLength * Gops;
    private const int FrameTicks = 512; // 25 frames per second at 12800 ticks per second
    private const int VideoDelay = 2;   // frames the decode order runs ahead of the display order
    private const int AudioPackets = 115;
    private static readonly byte[] AvcC = [1, 0x64, 0, 0x1F, 0xFF, 0xE0, 0];
    private static readonly byte[] AvcCOther = [1, 0x64, 0, 0x28, 0xFF, 0xE0, 0];
    private static readonly byte[] Asc = [0x11, 0x90]; // AAC-LC, 48 kHz, stereo
    private static readonly int[] DecodeOrder = [0, 3, 1, 2, 6, 4, 5, 9, 7, 8];

    private static MediaTrack VideoTrack(byte[]? avcC = null) => new()
    {
        Index = 0,
        Id = 1,
        Kind = TrackKind.Video,
        Codec = CodecId.H264,
        CodecTag = "avc1",
        Configuration = new CodecConfiguration(avcC ?? AvcC, "avcC"),
        Timescale = 12800,
        Video = new VideoTrackInfo(320, 180, FrameRateNumerator: 25, FrameRateDenominator: 1),
        Edit = new TrackEditInfo(
            Priming: new MediaTime(VideoDelay * FrameTicks, 12800),
            Edits: [new EditSegment(new MediaTime(VideoDelay * FrameTicks, 12800), new MediaTime(Frames * FrameTicks, 12800))]),
    };

    private static MediaTrack AudioTrack(int index = 1) => new()
    {
        Index = index,
        Id = 2,
        Kind = TrackKind.Audio,
        Codec = CodecId.Aac,
        Configuration = new CodecConfiguration(Asc, "asc"),
        Timescale = 48000,
        Audio = new AudioTrackInfo(48000, 2),
        Edit = new TrackEditInfo(
            Priming: new MediaTime(1024, 48000),
            Edits: [new EditSegment(new MediaTime(1024, 48000), new MediaTime(Frames * FrameTicks, 12800))]),
    };

    // A payload names the packet it belongs to: track, then the display index (video) or packet index (audio).
    private static byte[] Payload(int track, int index, int size) => [(byte)track, (byte)(index & 0xFF), (byte)(index >> 8), .. new byte[size - 3]];

    private static (int Track, int Index) Identify(ReadOnlySpan<byte> data) => (data[0], data[1] | (data[2] << 8));

    private static byte[] BuildSource(bool video = true, bool audio = true, byte[]? avcC = null)
    {
        using MemoryStream ms = new();
        using (IsoMuxer mux = new(ms, new IsoMuxOptions { MovieTimescale = 12800 }))
        {
            int v = video ? mux.AddTrack(VideoTrack(avcC)) : -1;
            int a = audio ? mux.AddTrack(AudioTrack()) : -1;
            int audioSent = 0;
            for (int g = 0; g < Gops; g++)
            {
                for (int k = 0; k < GopLength; k++)
                {
                    int display = g * GopLength + DecodeOrder[k];
                    int position = g * GopLength + k;
                    if (video)
                    {
                        long dts = position * (long)FrameTicks;
                        long pts = (display + VideoDelay) * (long)FrameTicks;
                        PacketAttributes flags = k == 0 ? PacketAttributes.Keyframe : PacketAttributes.None;
                        byte[] data = Payload(0, display, 120 + (display * 7 % 50));
                        mux.WritePacket(new PacketInfo(v, new MediaTime(pts, 12800), new MediaTime(dts, 12800), new MediaTime(FrameTicks, 12800), flags, ByteRange.None, data.Length), data);
                    }

                    // About 1.9 audio packets per video frame, on the audio clock.
                    while (audio && audioSent < (position + 1) * AudioPackets / Frames)
                    {
                        byte[] ad = Payload(1, audioSent, 60 + (audioSent % 5));
                        MediaTime t = new(audioSent * 1024L, 48000);
                        mux.WritePacket(new PacketInfo(a, t, t, new MediaTime(1024, 48000), PacketAttributes.Keyframe, ByteRange.None, ad.Length), ad);
                        audioSent++;
                    }
                }
            }

            while (audio && audioSent < AudioPackets)
            {
                byte[] ad = Payload(1, audioSent, 60);
                MediaTime t = new(audioSent * 1024L, 48000);
                mux.WritePacket(new PacketInfo(a, t, t, new MediaTime(1024, 48000), PacketAttributes.Keyframe, ByteRange.None, ad.Length), ad);
                audioSent++;
            }

            mux.Finish();
        }

        return ms.ToArray();
    }

    private sealed record Read(int Track, int Index, MediaTime Pts, MediaTime Dts, MediaTime Presented, bool Key);

    // What a player shows: the packets whose presentation time falls inside the track's edit list.
    private static (List<Read> All, MediaTime Shown, IReadOnlyList<MediaTrack> Tracks) ReadAll(byte[] file)
    {
        using MemoryMediaSource source = new(file);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(source);
        List<Read> list = [];
        while (d.TryReadPacket(out PacketView v))
        {
            (int track, int index) = Identify(v.Data);
            MediaTrack t = d.Tracks[v.Info.TrackIndex];
            list.Add(new Read(track, index, v.Info.Pts, v.Info.Dts, t.PresentationTime(v.Info.Pts), v.Info.IsKeyframe));
        }

        MediaTime shown = MediaTime.Zero;
        foreach (EditSegment e in d.Tracks[0].Edit.Edits ?? [])
        {
            shown = new MediaTime(shown.Ticks + e.Duration.Rescale(12800, MediaRounding.NearestAwayFromZero).Ticks, 12800);
        }

        return (list, shown, d.Tracks);
    }

    private static List<int> ShownVideo(List<Read> all, MediaTime length) =>
        [.. all.Where(r => r.Track == 0 && r.Presented >= MediaTime.Zero && r.Presented < length).OrderBy(r => r.Presented.Ticks * 1.0 / r.Presented.Timescale).Select(r => r.Index)];

    private static MediaTime Frame(int n) => new(n * (long)FrameTicks, 12800);

    private static byte[] Trim(byte[] source, MediaTime from, MediaTime to, TrimMode mode, out ClipReport report)
    {
        using MemoryMediaSource src = new(source);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        using MemoryStream ms = new();
        report = IsoCutter.Trim(d, ms, from, to, mode);
        return ms.ToArray();
    }

    [Fact]
    public void FrameAccurateTrimShowsExactlyTheRequestedFrames()
    {
        byte[] clip = Trim(BuildSource(), Frame(17), Frame(41), TrimMode.FrameAccurate, out ClipReport report);
        (List<Read> all, MediaTime shown, _) = ReadAll(clip);

        Assert.Equal(Frame(24), shown);
        Assert.Equal(Enumerable.Range(17, 24), ShownVideo(all, shown));
        Assert.Equal(Frame(17), report.SourceStart);
        Assert.Equal(Frame(24), report.Duration);

        // Decoding starts at the keyframe of the group (display frame 10); the frames before 17 are written but hidden.
        Read first = all.First(r => r.Track == 0);
        Assert.Equal(10, first.Index);
        Assert.True(first.Key);
        Assert.Equal(all.Count(r => r.Track == 0 && r.Presented < MediaTime.Zero), report.HiddenVideoPackets);
        Assert.True(report.HiddenVideoPackets >= 7);

        // Packets past the end that no kept frame needs are not written: the last group ends at display frame 40.
        Assert.DoesNotContain(all, r => r.Track == 0 && r.Index > 41);
    }

    [Fact]
    public void PayloadBytesAreCopiedUnchanged()
    {
        byte[] source = BuildSource();
        byte[] clip = Trim(source, Frame(17), Frame(41), TrimMode.FrameAccurate, out _);
        Dictionary<(int, int), byte[]> original = Payloads(source);
        Dictionary<(int, int), byte[]> cut = Payloads(clip);
        Assert.NotEmpty(cut);
        foreach (((int Track, int Index) key, byte[] data) in cut)
        {
            Assert.Equal(original[key], data);
        }
    }

    private static Dictionary<(int, int), byte[]> Payloads(byte[] file)
    {
        using MemoryMediaSource source = new(file);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(source);
        Dictionary<(int, int), byte[]> map = [];
        while (d.TryReadPacket(out PacketView v))
        {
            map[Identify(v.Data)] = v.Data.ToArray();
        }

        return map;
    }

    [Fact]
    public void AudioIsCutToTheSampleAndLinesUpWithTheVideo()
    {
        byte[] clip = Trim(BuildSource(), Frame(17), Frame(41), TrimMode.FrameAccurate, out _);
        (List<Read> all, MediaTime shown, _) = ReadAll(clip);

        // Source presentation time of audio packet i is i*1024 - 1024 samples (priming); in the clip it is that minus the clip start (0.68 s = 32640 samples).
        foreach (Read r in all.Where(r => r.Track == 1))
        {
            Assert.Equal(new MediaTime(r.Index * 1024L - 1024 - 32640, 48000), r.Presented);
        }

        // The packets in the clip cover [start, end): the first reaches back at most one packet of pre-roll, the last reaches past the end by less than a packet.
        List<Read> audio = [.. all.Where(r => r.Track == 1)];
        Assert.True(audio[0].Presented <= MediaTime.Zero);
        Assert.True(audio[0].Presented > new MediaTime(-2048, 48000));
        Assert.True(audio[^1].Presented >= shown.Rescale(48000, MediaRounding.Floor).Subtract(new MediaTime(1024, 48000), MediaRounding.NearestAwayFromZero));
        // Audio past the end is read up to one video frame further, because the end of the last frame is only known once it has been seen; the edit list hides it.
        Assert.True(audio[^1].Presented < shown.Add(Frame(1), MediaRounding.NearestAwayFromZero));
    }

    [Fact]
    public void BetweenFramesTheNextFrameIsTheFirstShown()
    {
        MediaTime between = new(17 * FrameTicks - 200, 12800);
        byte[] clip = Trim(BuildSource(), between, Frame(30), TrimMode.FrameAccurate, out ClipReport report);
        (List<Read> all, MediaTime shown, _) = ReadAll(clip);
        Assert.Equal(Enumerable.Range(17, 13), ShownVideo(all, shown));
        Assert.Contains(report.Notes, n => n.Contains("after the requested start", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyframeModeStartsOnTheKeyframeAndHidesNothing()
    {
        byte[] clip = Trim(BuildSource(), Frame(17), Frame(41), TrimMode.Keyframe, out ClipReport report);
        (List<Read> all, MediaTime shown, _) = ReadAll(clip);
        Assert.Equal(0, report.HiddenVideoPackets);
        Assert.Equal(Enumerable.Range(10, 31), ShownVideo(all, shown));
        Assert.Equal(Frame(10), report.SourceStart);
        Assert.True(all.First(r => r.Track == 0).Key);
    }

    [Fact]
    public void StartAndEndOutsideTheMediaAreHandled()
    {
        byte[] clip = Trim(BuildSource(), MediaTime.Zero, Frame(500), TrimMode.FrameAccurate, out ClipReport report);
        (List<Read> all, MediaTime shown, _) = ReadAll(clip);
        Assert.Equal(Enumerable.Range(0, Frames), ShownVideo(all, shown));
        Assert.Equal(0, report.HiddenVideoPackets);
    }

    [Fact]
    public void EmptyRangesAreRefused()
    {
        byte[] source = BuildSource();
        Assert.Equal(EditRefusalReason.EmptyRange, Assert.Throws<EditRefusedException>(() => Trim(source, Frame(20), Frame(20), TrimMode.FrameAccurate, out _)).Reason);
        Assert.Equal(EditRefusalReason.EmptyRange, Assert.Throws<EditRefusedException>(() => Trim(source, Frame(900), Frame(950), TrimMode.FrameAccurate, out _)).Reason);
    }

    [Fact]
    public void AudioOnlyTrimIsCutToTheSample()
    {
        byte[] source = BuildSource(video: false);
        MediaTime from = new(24000, 48000);
        byte[] clip = Trim(source, from, new MediaTime(48000, 48000), TrimMode.FrameAccurate, out ClipReport report);
        (List<Read> all, _, IReadOnlyList<MediaTrack> tracks) = ReadAll(clip);
        Assert.Equal(new MediaTime(24000, 48000), report.Duration);
        EditSegment edit = tracks[0].Edit.Edits!.Single();
        Assert.Equal(new MediaTime(24000, 48000), edit.Duration);
        // The first packet is the one before the one that holds the start (pre-roll); the edit list starts the audio at the requested sample.
        foreach (Read r in all)
        {
            Assert.Equal(new MediaTime(r.Index * 1024L - 1024 - 24000, 48000), r.Presented);
        }

        Assert.True(all[0].Presented <= MediaTime.Zero);
        Assert.True(all[0].Presented > new MediaTime(-2048, 48000));
    }

    [Fact]
    public void SplitAndConcatGiveBackTheSourcePackets()
    {
        byte[] source = BuildSource();
        List<MemoryStream> parts = [];
        using (MemoryMediaSource src = new(source))
        using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(src))
        {
            IReadOnlyList<ClipReport> reports = IsoCutter.Split(d, _ =>
            {
                MemoryStream ms = new();
                parts.Add(ms);
                return ms;
            }, [Frame(18), Frame(43)]);
            Assert.Equal(3, reports.Count);
        }

        // Cuts land on keyframes at or before the requested times: display frames 10 and 40.
        Assert.Equal(3, parts.Count);
        List<IsoBmffDemuxer> inputs = [];
        List<MemoryMediaSource> sources = [];
        foreach (MemoryStream p in parts)
        {
            MemoryMediaSource s = new(p.ToArray());
            sources.Add(s);
            inputs.Add(IsoBmffDemuxer.Open(s));
        }

        using MemoryStream joined = new();
        IsoJoiner.Concat(inputs, joined);
        foreach (IsoBmffDemuxer d in inputs)
        {
            d.Dispose();
        }

        (List<Read> original, MediaTime originalShown, IReadOnlyList<MediaTrack> originalTracks) = ReadAll(source);
        (List<Read> again, MediaTime againShown, IReadOnlyList<MediaTrack> againTracks) = ReadAll(joined.ToArray());
        Assert.Equal(original.Count, again.Count);
        foreach (int track in new[] { 0, 1 })
        {
            List<Read> x = [.. original.Where(r => r.Track == track)];
            List<Read> y = [.. again.Where(r => r.Track == track)];
            Assert.Equal(x.Count, y.Count);
            for (int i = 0; i < x.Count; i++)
            {
                Assert.Equal((x[i].Index, x[i].Key), (y[i].Index, y[i].Key));
                Assert.Equal(x[i].Dts, y[i].Dts);
                Assert.Equal(x[i].Pts, y[i].Pts);
            }
        }

        Assert.Equal(originalShown, againShown);
        for (int t = 0; t < originalTracks.Count; t++)
        {
            Assert.Equal(originalTracks[t].Edit.Edits!.Select(e => (e.MediaStart, e.Duration)), againTracks[t].Edit.Edits!.Select(e => (e.MediaStart, e.Duration)));
        }

        Assert.Equal(Payloads(source).Count, Payloads(joined.ToArray()).Count);
    }

    [Fact]
    public void SplitPartsStartOnKeyframesAndPartitionTheAudio()
    {
        byte[] source = BuildSource();
        List<MemoryStream> parts = [];
        using MemoryMediaSource src = new(source);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        IsoCutter.Split(d, _ =>
        {
            MemoryStream ms = new();
            parts.Add(ms);
            return ms;
        }, [Frame(25)]);
        Assert.Equal(2, parts.Count);
        (List<Read> second, MediaTime shown, _) = ReadAll(parts[1].ToArray());
        Assert.Equal(Enumerable.Range(20, Frames - 20), ShownVideo(second, shown));
        Assert.True(second.First(r => r.Track == 0).Key);
        (List<Read> first, _, _) = ReadAll(parts[0].ToArray());
        List<int> audio = [.. first.Where(r => r.Track == 1).Select(r => r.Index), .. second.Where(r => r.Track == 1).Select(r => r.Index)];
        Assert.Equal(Enumerable.Range(0, AudioPackets), audio);
    }

    [Fact]
    public void SplitBySizeCutsOnKeyframes()
    {
        byte[] source = BuildSource();
        List<MemoryStream> parts = [];
        using MemoryMediaSource src = new(source);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        IReadOnlyList<ClipReport> reports = IsoCutter.SplitBySize(d, _ =>
        {
            MemoryStream ms = new();
            parts.Add(ms);
            return ms;
        }, 3000);
        Assert.True(reports.Count > 2);
        foreach (MemoryStream p in parts)
        {
            (List<Read> all, _, _) = ReadAll(p.ToArray());
            Assert.True(all.First(r => r.Track == 0).Key);
        }
    }

    [Fact]
    public void ConcatRefusesDifferentConfigurationNamingTheTrack()
    {
        using MemoryMediaSource a = new(BuildSource());
        using MemoryMediaSource b = new(BuildSource(avcC: AvcCOther));
        using IsoBmffDemuxer da = IsoBmffDemuxer.Open(a);
        using IsoBmffDemuxer db = IsoBmffDemuxer.Open(b);
        EditRefusedException e = Assert.Throws<EditRefusedException>(() => IsoJoiner.Concat([da, db], new MemoryStream()));
        Assert.Equal(EditRefusalReason.NeedsReencode, e.Reason);
        Assert.Equal(0, e.TrackIndex);
        Assert.Equal(CodecId.H264, e.Codec);
    }

    [Fact]
    public void ConcatOfTheSameFileTwiceDoublesTheShownLength()
    {
        byte[] source = BuildSource();
        using MemoryMediaSource a = new(source);
        using MemoryMediaSource b = new(source);
        using IsoBmffDemuxer da = IsoBmffDemuxer.Open(a);
        using IsoBmffDemuxer db = IsoBmffDemuxer.Open(b);
        using MemoryStream ms = new();
        ClipReport report = IsoJoiner.Concat([da, db], ms);
        Assert.Equal(Frame(2 * Frames), report.Duration);
        (List<Read> all, MediaTime shown, _) = ReadAll(ms.ToArray());
        Assert.Equal(Frame(2 * Frames), shown);
        List<int> expected = [.. Enumerable.Range(0, Frames), .. Enumerable.Range(0, Frames)];
        Assert.Equal(expected, ShownVideo(all, shown));
    }
}
