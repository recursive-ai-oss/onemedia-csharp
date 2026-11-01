using RecursiveAI.OneMedia.Editing;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Tests.Editing;

public class MatroskaCutterTests
{
    private const int GopLength = 10;
    private const int Gops = 6;
    private const int Frames = GopLength * Gops;
    private const int AudioPackets = 120;
    private static readonly byte[] AvcC = [1, 0x64, 0, 0x1F, 0xFF, 0xE0, 0];
    private static readonly byte[] AvcCOther = [1, 0x64, 0, 0x28, 0xFF, 0xE0, 0];
    private static readonly byte[] Asc = [0x11, 0x90];
    private static readonly int[] DecodeOrder = [0, 3, 1, 2, 6, 4, 5, 9, 7, 8];

    private static MediaTrack VideoTrack(byte[]? avcC = null, bool vp9 = false) => new()
    {
        Index = 0,
        Id = 1,
        Kind = TrackKind.Video,
        Codec = vp9 ? CodecId.Vp9 : CodecId.H264,
        CodecTag = vp9 ? "vp09" : "avc1",
        Configuration = vp9 ? default : new CodecConfiguration(avcC ?? AvcC, "avcC"),
        Timescale = 1000,
        Video = new VideoTrackInfo(320, 180, FrameRateNumerator: 25, FrameRateDenominator: 1),
    };

    private static MediaTrack AudioTrack() => new()
    {
        Index = 1,
        Id = 2,
        Kind = TrackKind.Audio,
        Codec = CodecId.Aac,
        Configuration = new CodecConfiguration(Asc, "asc"),
        Timescale = 48000,
        Audio = new AudioTrackInfo(48000, 2),
    };

    // A payload names its packet: track, then the display index (video) or packet index (audio).
    private static byte[] Payload(int track, int index, int size) => [(byte)track, (byte)(index & 0xFF), (byte)(index >> 8), .. new byte[size - 3]];

    private static (int Track, int Index) Identify(ReadOnlySpan<byte> data) => (data[0], data[1] | (data[2] << 8));

    // VP9 stands in for a codec whose frames can carry negative timestamps; the packets are reordered like B-frames either way.
    private static byte[] BuildSource(byte[]? avcC = null, bool vp9 = false)
    {
        using MemoryStream ms = new();
        using (MatroskaMuxer mux = new(ms))
        {
            int v = mux.AddTrack(VideoTrack(avcC, vp9));
            int a = mux.AddTrack(AudioTrack());
            int audioSent = 0;
            for (int g = 0; g < Gops; g++)
            {
                for (int k = 0; k < GopLength; k++)
                {
                    int display = g * GopLength + DecodeOrder[k];
                    byte[] data = Payload(0, display, 120 + (display * 7 % 50));
                    MediaTime pts = new(display * 40L, 1000);
                    mux.WritePacket(new PacketInfo(v, pts, MediaTime.Unknown, new MediaTime(40, 1000), k == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, data.Length), data);
                    int position = g * GopLength + k;
                    while (audioSent < (position + 1) * AudioPackets / Frames)
                    {
                        byte[] ad = Payload(1, audioSent, 60 + (audioSent % 5));
                        MediaTime t = new(audioSent * 1024L, 48000);
                        mux.WritePacket(new PacketInfo(a, t, MediaTime.Unknown, new MediaTime(1024, 48000), PacketAttributes.Keyframe, ByteRange.None, ad.Length), ad);
                        audioSent++;
                    }
                }
            }

            mux.Finish();
        }

        return ms.ToArray();
    }

    private sealed record Read(int Track, int Index, MediaTime Pts, bool Key, byte[] Data);

    private static List<Read> ReadAll(byte[] file)
    {
        using MemoryMediaSource source = new(file);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(source);
        List<Read> list = [];
        while (d.TryReadPacket(out PacketView v))
        {
            (int track, int index) = Identify(v.Data);
            list.Add(new Read(track, index, v.Info.Pts, v.Info.IsKeyframe, v.Data.ToArray()));
        }

        return list;
    }

    private static ClipReport TrimFile(byte[] source, out byte[] output, double from, double to, TrimMode mode)
    {
        using MemoryMediaSource src = new(source);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(src);
        using MemoryStream ms = new();
        ClipReport report = MatroskaCutter.Trim(d, ms, MediaTime.FromTimeSpan(TimeSpan.FromSeconds(from)), MediaTime.FromTimeSpan(TimeSpan.FromSeconds(to)), mode);
        output = ms.ToArray();
        return report;
    }

    [Fact]
    public void FrameAccurateTrimGivesNegativeTimestampsToTheFramesBeforeTheStart()
    {
        byte[] source = BuildSource(vp9: true);
        // 0.53 s falls between frames 13 (0.52) and 14 (0.56): frame 14 is the first one shown. The keyframe before it is frame 10.
        ClipReport report = TrimFile(source, out byte[] clip, 0.53, 1.65, TrimMode.FrameAccurate);
        List<Read> video = ReadAll(clip).Where(r => r.Track == 0).ToList();
        List<Read> shown = video.Where(r => r.Pts >= MediaTime.Zero).OrderBy(r => r.Pts).ToList();

        Assert.Equal(14, shown[0].Index);
        Assert.Equal(MediaTime.Zero, shown[0].Pts);
        Assert.Equal(report.HiddenVideoPackets, video.Count(r => r.Pts < MediaTime.Zero));
        Assert.Equal(10, video.OrderBy(r => r.Pts).First().Index); // decoding starts at the keyframe
        Assert.True(video.First().Key);

        // Shown frames are consecutive, spaced one frame apart, and stop before the end (they may stop earlier: frames coded after the cut frame go with it).
        for (int i = 1; i < shown.Count; i++)
        {
            Assert.Equal(shown[i - 1].Index + 1, shown[i].Index);
            Assert.Equal(new MediaTime(40, 1000), shown[i].Pts - shown[i - 1].Pts);
        }

        Assert.True(shown[^1].Index < 1.65 * 25);
        Assert.True(shown[^1].Index >= 1.65 * 25 - 3);
    }

    [Fact]
    public void KeyframeTrimStartsOnTheKeyframeAtTimeZero()
    {
        byte[] source = BuildSource();
        TrimFile(source, out byte[] clip, 0.53, 1.65, TrimMode.Keyframe);
        List<Read> video = ReadAll(clip).Where(r => r.Track == 0).ToList();

        Assert.True(video[0].Key);
        Assert.Equal(10, video[0].Index);
        Assert.Equal(MediaTime.Zero, video[0].Pts);
        Assert.DoesNotContain(video, r => r.Pts < MediaTime.Zero);
    }

    [Fact]
    public void TrimCopiesPayloadBytesUnchanged()
    {
        byte[] source = BuildSource(vp9: true);
        Dictionary<(int, int), byte[]> original = ReadAll(source).ToDictionary(r => (r.Track, r.Index), r => r.Data);
        TrimFile(source, out byte[] clip, 0.53, 1.65, TrimMode.FrameAccurate);
        foreach (Read r in ReadAll(clip))
        {
            Assert.Equal(original[(r.Track, r.Index)], r.Data);
        }
    }

    [Fact]
    public void SplitPartsJoinBackIntoTheSourcePackets()
    {
        byte[] source = BuildSource();
        List<MemoryStream> parts = [];
        using (MemoryMediaSource src = new(source))
        using (MatroskaDemuxer d = MatroskaDemuxer.Open(src))
        {
            IReadOnlyList<ClipReport> reports = MatroskaCutter.Split(d, _ =>
            {
                MemoryStream ms = new();
                parts.Add(ms);
                return ms;
            }, [MediaTime.FromTimeSpan(TimeSpan.FromSeconds(0.9)), MediaTime.FromTimeSpan(TimeSpan.FromSeconds(1.7))]);
            Assert.Equal(3, reports.Count);
        }

        List<MatroskaDemuxer> opened = [];
        List<MemoryMediaSource> sources = [];
        try
        {
            foreach (MemoryStream p in parts)
            {
                MemoryMediaSource s = new(p.ToArray());
                sources.Add(s);
                opened.Add(MatroskaDemuxer.Open(s));
            }

            using MemoryStream joined = new();
            MatroskaJoiner.Concat(opened, joined);
            List<Read> a = ReadAll(source);
            List<Read> b = ReadAll(joined.ToArray());
            Assert.Equal(a.Count, b.Count);
            foreach (int track in new[] { 0, 1 })
            {
                List<Read> ta = a.Where(r => r.Track == track).ToList();
                List<Read> tb = b.Where(r => r.Track == track).ToList();
                Assert.Equal(ta.Select(r => (r.Index, r.Key)), tb.Select(r => (r.Index, r.Key)));
                for (int i = 0; i < ta.Count; i++)
                {
                    Assert.Equal(ta[i].Pts, tb[i].Pts); // splitting then joining restores the timeline
                }
            }
        }
        finally
        {
            foreach (MatroskaDemuxer d in opened)
            {
                d.Dispose();
            }

            foreach (MemoryMediaSource s in sources)
            {
                s.Dispose();
            }
        }
    }

    [Fact]
    public void ConcatRefusesAChangedCodecConfigurationNamingTheTrack()
    {
        byte[] one = BuildSource();
        byte[] two = BuildSource(AvcCOther);
        using MemoryMediaSource s1 = new(one);
        using MemoryMediaSource s2 = new(two);
        using MatroskaDemuxer d1 = MatroskaDemuxer.Open(s1);
        using MatroskaDemuxer d2 = MatroskaDemuxer.Open(s2);
        using MemoryStream output = new();
        EditRefusedException e = Assert.Throws<EditRefusedException>(() => MatroskaJoiner.Concat([d1, d2], output));
        Assert.Equal(EditRefusalReason.NeedsReencode, e.Reason);
        Assert.Equal(0, e.TrackIndex);
    }

    [Fact]
    public void FrameAccurateStartInsideAGroupOfPicturesIsRefusedForH264()
    {
        byte[] source = BuildSource();
        EditRefusedException e = Assert.Throws<EditRefusedException>(() => TrimFile(source, out _, 0.53, 1.65, TrimMode.FrameAccurate));
        Assert.Equal(EditRefusalReason.ExactStartNotSupported, e.Reason);
        TrimFile(source, out byte[] onKey, 0.4, 1.65, TrimMode.FrameAccurate); // on a keyframe nothing is hidden
        Assert.NotEmpty(onKey);
    }
}
