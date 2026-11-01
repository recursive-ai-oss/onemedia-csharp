using RecursiveAI.OneMedia.Editing;
using RecursiveAI.OneMedia.IsoBmff;
using RecursiveAI.OneMedia.Matroska;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>
/// Writes a small H.264-and-AAC file with the ISO muxer, trims it (frame-accurate and keyframe), splits it and joins the parts again.
/// Part of the NativeAOT and browser-WebAssembly smoke runs: the edit operations use only plain collections and the explicit muxer.
/// </summary>
internal static class EditSmoke
{
    private static readonly byte[] AvcC = [1, 0x64, 0, 0x1F, 0xFF, 0xE0, 0];

    public static void Run(Action<string, bool> check)
    {
        byte[] source = Build();

        using (MemoryMediaSource src = new(source))
        using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(src))
        using (MemoryStream clip = new())
        {
            ClipReport report = IsoCutter.Trim(d, clip, new MediaTime(7, 25), new MediaTime(19, 25));
            check("trim frame-accurate", report.Duration == new MediaTime(12, 25) && report.SourceStart == new MediaTime(7, 25) && report.HiddenVideoPackets > 0 && Count(clip.ToArray()) > 0);
        }

        using (MemoryMediaSource src = new(source))
        using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(src))
        using (MemoryStream clip = new())
        {
            ClipReport report = IsoCutter.Trim(d, clip, new MediaTime(7, 25), new MediaTime(19, 25), TrimMode.Keyframe);
            check("trim keyframe", report.SourceStart == new MediaTime(0, 25) && report.HiddenVideoPackets == 0);
        }

        List<MemoryStream> parts = [];
        using (MemoryMediaSource src = new(source))
        using (IsoBmffDemuxer d = IsoBmffDemuxer.Open(src))
        {
            IsoCutter.Split(d, _ =>
            {
                MemoryStream ms = new();
                parts.Add(ms);
                return ms;
            }, [new MediaTime(10, 25)]);
        }

        List<MemoryMediaSource> sources = [];
        List<IsoBmffDemuxer> inputs = [];
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

        foreach (MemoryMediaSource s in sources)
        {
            s.Dispose();
        }

        check("split and concat", parts.Count == 2 && Count(joined.ToArray()) == Count(source));
        RunMatroska(check);
    }

    // The same operations on a Matroska file: VP9 frames before the start get negative timestamps; two parts join back.
    private static void RunMatroska(Action<string, bool> check)
    {
        byte[] source = BuildMatroska();
        using MemoryMediaSource src = new(source);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(src);
        using MemoryStream clip = new();
        ClipReport report = MatroskaCutter.Trim(d, clip, new MediaTime(7, 25), new MediaTime(19, 25));
        check("matroska trim frame-accurate", report.SourceStart == new MediaTime(7, 25) && report.HiddenVideoPackets == 7);

        List<MemoryStream> parts = [];
        MatroskaCutter.Split(d, _ =>
        {
            MemoryStream ms = new();
            parts.Add(ms);
            return ms;
        }, [new MediaTime(10, 25)]);
        List<MemoryMediaSource> sources = [];
        List<MatroskaDemuxer> inputs = [];
        foreach (MemoryStream p in parts)
        {
            MemoryMediaSource s = new(p.ToArray());
            sources.Add(s);
            inputs.Add(MatroskaDemuxer.Open(s));
        }

        using MemoryStream joined = new();
        MatroskaJoiner.Concat(inputs, joined);
        foreach (MatroskaDemuxer m in inputs)
        {
            m.Dispose();
        }

        foreach (MemoryMediaSource s in sources)
        {
            s.Dispose();
        }

        check("matroska split and concat", parts.Count == 2 && CountMatroska(joined.ToArray()) == CountMatroska(source));
    }

    private static int CountMatroska(byte[] file)
    {
        using MemoryMediaSource src = new(file);
        using MatroskaDemuxer d = MatroskaDemuxer.Open(src);
        int n = 0;
        while (d.TryReadPacket(out _))
        {
            n++;
        }

        return n;
    }

    private static byte[] BuildMatroska()
    {
        using MemoryStream output = new();
        using (MatroskaMuxer mux = new(output))
        {
            int v = mux.AddTrack(new MediaTrack
            {
                Index = 0,
                Kind = TrackKind.Video,
                Codec = CodecId.Vp9,
                Timescale = 1000,
                Video = new VideoTrackInfo(320, 180, FrameRateNumerator: 25, FrameRateDenominator: 1),
            });
            byte[] payload = new byte[48];
            for (int i = 0; i < 30; i++)
            {
                MediaTime t = new(i * 40L, 1000);
                mux.WritePacket(new PacketInfo(v, t, MediaTime.Unknown, new MediaTime(40, 1000), i % 10 == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, payload.Length), payload);
            }

            mux.Finish();
        }

        return output.ToArray();
    }

    private static int Count(byte[] file)
    {
        using MemoryMediaSource src = new(file);
        using IsoBmffDemuxer d = IsoBmffDemuxer.Open(src);
        int n = 0;
        while (d.TryReadPacket(out _))
        {
            n++;
        }

        return n;
    }

    // 30 frames at 25 per second with a keyframe every 10, and 64 audio packets of 1024 samples at 48 kHz.
    private static byte[] Build()
    {
        using MemoryStream output = new();
        using (IsoMuxer mux = new(output, new IsoMuxOptions { MovieTimescale = 12800 }))
        {
            int v = mux.AddTrack(new MediaTrack
            {
                Index = 0,
                Kind = TrackKind.Video,
                Codec = CodecId.H264,
                Configuration = new CodecConfiguration(AvcC, "avcC"),
                Timescale = 12800,
                Video = new VideoTrackInfo(320, 180, FrameRateNumerator: 25, FrameRateDenominator: 1),
            });
            int a = mux.AddTrack(new MediaTrack
            {
                Index = 1,
                Kind = TrackKind.Audio,
                Codec = CodecId.Aac,
                Configuration = new CodecConfiguration(new byte[] { 0x11, 0x90 }, "asc"),
                Timescale = 48000,
                Audio = new AudioTrackInfo(48000, 2),
            });
            byte[] payload = new byte[48];
            int audio = 0;
            for (int i = 0; i < 30; i++)
            {
                MediaTime vt = new(i * 512L, 12800);
                mux.WritePacket(new PacketInfo(v, vt, vt, new MediaTime(512, 12800), i % 10 == 0 ? PacketAttributes.Keyframe : PacketAttributes.None, ByteRange.None, payload.Length), payload);
                while (audio < (i + 1) * 64 / 30)
                {
                    MediaTime at = new(audio * 1024L, 48000);
                    mux.WritePacket(new PacketInfo(a, at, at, new MediaTime(1024, 48000), PacketAttributes.Keyframe, ByteRange.None, payload.Length), payload);
                    audio++;
                }
            }

            mux.Finish();
        }

        return output.ToArray();
    }
}
